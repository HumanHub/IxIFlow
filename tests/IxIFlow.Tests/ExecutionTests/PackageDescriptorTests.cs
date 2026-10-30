using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-12")]
public sealed class PackageDescriptorTests
{
    [Fact]
    public void DescriptorCanonicalRoundtripHasPortableTypesAndPresentationIndependentContractHash()
    {
        var descriptor = ActivityDescriptor.OfType(typeof(Nested.Activity), "descriptor.test", "1.0.0");
        Assert.Equal("A nested activity", descriptor.Description);
        Assert.Contains("+Nested+Activity", descriptor.MetadataTypeName);
        Assert.Contains(".Nested.Activity", descriptor.CSharpTypeName);
        Assert.StartsWith("global::", descriptor.CSharpTypeName);
        Assert.DoesNotContain("ActivityType", descriptor.ToJson());
        var parsed = ActivityDescriptor.Parse(descriptor.ToJson());
        Assert.Null(parsed.ActivityType);
        Assert.Equal(descriptor.ToJson(), parsed.ToJson());
        Assert.Equal(descriptor.ContractHash, (descriptor with { Name = "Other", Description = "Other", Icon = "bolt" }).ContractHash);
        var member = Assert.Single(descriptor.Inputs);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(int)), member.Type);
        Assert.Equal("global::System.Int32", member.TypeName);
        Assert.Equal(descriptor.ContractHash, (descriptor with { Inputs = [member with { DisplayName = "Other" }] }).ContractHash);

        Assert.NotEqual(descriptor.ContractHash,
            (descriptor with { Inputs = [member with { DefaultJson = "8", HasDefault = true }] }).ContractHash);

        var json = JsonNode.Parse(descriptor.ToJson())!.AsObject();
        json.Remove("CSharpTypeName");
        Assert.Throws<JsonException>(() => ActivityDescriptor.Parse(json.ToJsonString()));
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(descriptor with { Icon = "<svg>" }));
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(descriptor with { Icon = new string('a', 101) }));
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(descriptor with { Designer = new string('a', 101) }));
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(descriptor with { MetadataTypeName = "Bad;Code" }));
        var contract = WorkflowContract.OfType(typeof(Nested.Activity));
        var contractJson = JsonNode.Parse(contract.ToJson())!;
        contractJson["Inputs"]![0]!.AsObject().Remove("TypeName");
        Assert.Throws<JsonException>(() => WorkflowContract.Parse(contractJson.ToJsonString()));
    }

    [Fact]
    public void NestedGenericArrayMemberNamesAreStructuralAndPortable()
    {
        var descriptor = ActivityDescriptor.OfType(typeof(StructuralNames), "descriptor.test", "1.0.0");
        var member = Assert.Single(descriptor.Inputs);

        Assert.Contains("Generic<global::System.Int32>.Inner<global::System.Collections.Generic.List<global::System.String[,]>>[]",
            member.TypeName);

        Assert.Equal(descriptor.ToJson(), ActivityDescriptor.Parse(descriptor.ToJson()).ToJson());
    }

    [Fact]
    public void ActivityDescriptorDefaultsUsePropertyConvertersNumberHandlingSavedDerivedTypeAndExplicitNull()
    {
        var descriptor = ActivityDescriptor.OfType(typeof(DefaultActivity), "descriptor.defaults", "1.0.0");
        Assert.Equal("\"number:8\"", descriptor.Inputs.Single(item => item.Name == "Converted").DefaultJson);
        Assert.Equal("\"5\"", descriptor.Inputs.Single(item => item.Name == "Number").DefaultJson);
        var explicitNull = descriptor.Inputs.Single(item => item.Name == "Null");
        Assert.True(explicitNull.HasDefault);
        Assert.Equal("null", explicitNull.DefaultJson);
        var absent = descriptor.Inputs.Single(item => item.Name == "Absent");
        Assert.False(absent.HasDefault);
        Assert.Null(absent.DefaultJson);
        using var json = JsonDocument.Parse(descriptor.Inputs.Single(item => item.Name == "Derived").DefaultJson!);
        Assert.Equal("kept", json.RootElement.GetProperty("Extra").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("Number").GetInt32());
    }

    [Fact]
    public async Task NestedActivitySourceNameCompilesAndRunsAConsumer()
    {
        var implementation = Emit("Descriptor.Nested", """
                                                       using System.Threading; using System.Threading.Tasks;
                                                       using IxIFlow.Core; using IxIFlow.ActivitySdk;
                                                       namespace NestedPackage;
                                                       public static class Outer {
                                                         public sealed class Data { public int Result {get;set;} }
                                                         [WorkflowActivity("nested.activity")]
                                                         public sealed class Inner : IAsyncActivity {
                                                           [ActivityOutput] public int Value {get;set;}
                                                           public Inner() { }
                                                           public Task ExecuteAsync(IActivityContext context, CancellationToken token=default) {Value=42;return Task.CompletedTask;}
                                                         }
                                                       }
                                                       """);

        var bytes = ActivityPackageFixture.Package("nested.package", "1.0.0",
            new() { ["lib/net10.0/Descriptor.Nested.dll"] = implementation });

        var store = new InMemoryWorkflowStateRepository();
        var receipt = await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(bytes));
        var descriptor = Assert.Single(receipt.Activities.Activities);
        Assert.Equal("NestedPackage.Outer+Inner", descriptor.MetadataTypeName);
        Assert.Equal("global::NestedPackage.Outer.Inner", descriptor.CSharpTypeName);

        var yaml = """
                   schemaVersion: "1.0"
                   workflow: {name: NestedConsumer, version: 1, dataType: NestedPackage.Outer.Data}
                   imports: {catalogs: ["nested.package@1.0.0"]}
                   definitions:
                     - kind: activity
                       id: nested
                       activity: nested.activity
                       output:
                         - source: Value
                           to: {kind: path, path: workflow.Result}
                   """;

        var draft = await store.SaveDefinitionAsync(new("NestedConsumer", 1, "workflow", yaml), null);
        var published = await new WorkflowPublisher(store, store).PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        var folder = Scratch();
        await using var host = PublishedBundleScenario.Host(store, folder);
        var started = await host.GetRequiredService<IWorkflowClient>().StartAsync("NestedConsumer", 1, new { Result = 0 });
        var result = await host.GetRequiredService<IWorkflowClient>().AwaitAsync(started, WorkflowAwaitCondition.Finished);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(42, result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
        using var definition = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync("NestedConsumer", 1);
        Assert.Equal("NestedPackage.Outer+Data", definition.Definition.WorkflowDataType!.FullName);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("entry")]
    [InlineData("expanded")]
    [InlineData("entries")]
    [InlineData("manifest")]
    public async Task UploadCandidatesEnforceEveryArchiveBoundBeforeAnyWrite(string bound)
    {
        var bytes = ActivityPackageFixture.Package("candidate.bounds", "1.0.0", new() { ["content/value.txt"] = new byte[128] });

        var limits = bound switch
        {
            "archive" => new WorkflowBundleReadLimits { MaxArchiveBytes = 10 },
            "entry" => new WorkflowBundleReadLimits { MaxEntryBytes = 10 },
            "expanded" => new WorkflowBundleReadLimits { MaxExpandedBytes = 10 },
            "entries" => new WorkflowBundleReadLimits { MaxEntries = 1 },
            _ => new WorkflowBundleReadLimits { MaxManifestBytes = 10 }
        };

        var store = new InMemoryWorkflowStateRepository();

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            new PackageIngestion(store, new ReadOnlyArtifacts(store), new() { PackageReadLimits = limits })
                .UploadPackageAsync(new MemoryStream(bytes)));

        Assert.Equal(WorkflowErrorKind.ResourceExhausted, error.Kind);
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Equal(0, await store.GetCatalogRevisionAsync());
    }

    [Fact]
    public async Task CandidateRevalidationChecksIdentityDigestAndEntryHashes()
    {
        var store = new InMemoryWorkflowStateRepository();
        var ingestion = new PackageIngestion(store, new ReadOnlyArtifacts(store));
        var bytes = ActivityPackageFixture.Package("candidate.test", "1.0.0", []);
        var candidate = await ingestion.ReadPackageAsync(new MemoryStream(bytes));
        Assert.Equal(ArtifactDigest.Of(bytes), candidate.Package.Digest);

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            ingestion.StorePackageAsync(candidate with { Package = candidate.Package with { Id = "other.id" } }));

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            ingestion.StorePackageAsync(candidate with { Package = candidate.Package with { Digest = ArtifactDigest.Of([9]) } }));

        await Assert.ThrowsAsync<WorkflowEngineException>(() => ingestion.StorePackageAsync(candidate with
        {
            Package = candidate.Package with { Entries = new Dictionary<string, ArtifactDigest>() }
        }));

        await Assert.ThrowsAsync<WorkflowEngineException>(() => ingestion.ReadPackageAsync(new MemoryStream([1, 2, 3])));
        Assert.Empty(await store.GetPackagesAsync());
    }

    [Fact]
    public async Task ScratchCanUseFeedsAndSeededRootsWithStrictlyReadOnlyStores()
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = DispatchProxy.Create<IWorkflowCatalogStore, ReadOnlyCatalog>();
        ((ReadOnlyCatalog)(object)catalog).Target = store;
        var artifacts = new ReadOnlyArtifacts(store);
        var (activity, dependency) = ActivityPackageFixture.Create();
        var folder = Scratch();
        await File.WriteAllBytesAsync(Path.Combine(folder, "fixture.dependency.1.0.0.nupkg"), dependency);
        var config = Path.Combine(folder, "NuGet.Config");

        await File.WriteAllTextAsync(config,
            "<configuration><packageSources><clear/><add key=\"offline\" value=\"" + SecurityElement.Escape(folder) +
            "\"/></packageSources></configuration>");

        var candidate = await new PackageIngestion(catalog, artifacts).ReadPackageAsync(new MemoryStream(activity));
        var imports = new List<string> { "fixture.activities@1.0.0" };

        using var resolved =
            await new WorkflowPackageResolver(catalog, artifacts, new() { NuGetConfigPath = config }).ResolveAsync(imports,
                PackageAcquisition.Scratch, [candidate], default);

        Assert.Equal(2, resolved.Pins.Count);
        Assert.Equal("fixture.echo", Assert.Single(resolved.Activities["fixture.activities"]).Key);
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Equal(0, await store.GetCatalogRevisionAsync());

        var error = await Assert.ThrowsAsync<WorkflowPublishException>(() =>
            new WorkflowPackageResolver(catalog, artifacts, new() { NuGetConfigPath = "no-such-config" })
                .ResolveAsync(imports, PackageAcquisition.CatalogOnly, [], default));

        Assert.Equal("PUB030", error.Code);
    }

    [Fact]
    public async Task InvalidDeclarationsAndTimeoutsNeverAcceptTheRoot()
    {
        foreach (var timeout in new[] { false, true })
        {
            var source = ActivitySource("string", timeout ? null : "Bad_ICON");

            if (timeout)
                source = source.Replace("public sealed class Activity", "[SlowDescription] public sealed class Activity") +
                    "public sealed class SlowDescriptionAttribute : System.ComponentModel.DescriptionAttribute { public SlowDescriptionAttribute() { System.Threading.Thread.Sleep(250); } }";

            var assembly = Emit("Descriptor.Invalid", source);

            var bytes = ActivityPackageFixture.Package("invalid.activity", "1.0.0",
                new() { ["lib/net10.0/Descriptor.Invalid.dll"] = assembly });

            var store = new InMemoryWorkflowStateRepository();

            var error = await Assert.ThrowsAsync<WorkflowPublishException>(() => new PackageIngestion(store, store,
                    new() { BuildTimeout = timeout ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromSeconds(30) })
                .UploadPackageAsync(new MemoryStream(bytes)));

            Assert.Equal(timeout ? "PUB043" : "PUB016", error.Code);
            Assert.Null(await store.GetPackageAsync("invalid.activity", "1.0.0"));
            Assert.Equal(0, await store.GetCatalogRevisionAsync());
        }
    }

    [Fact]
    public async Task ReferenceRuntimeMemberDisagreementIsPub014BeforeWrites()
    {
        var runtime = Emit("Descriptor.Agreement", ActivitySource("int"));
        var reference = Emit("Descriptor.Agreement", ActivitySource("string"));

        var bytes = ActivityPackageFixture.Package("agreement.activity", "1.0.0", new()
            { ["lib/net10.0/Descriptor.Agreement.dll"] = runtime, ["ref/net10.0/Descriptor.Agreement.dll"] = reference });

        var store = new InMemoryWorkflowStateRepository();

        var error = await Assert.ThrowsAsync<WorkflowPublishException>(() =>
            new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(bytes)));

        Assert.Equal("PUB014", error.Code);
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Equal(0, await store.GetCatalogRevisionAsync());
    }

    [Fact]
    public async Task ReferenceAgreementUsesStructuralNamesForNestedGenericsNullableArraysTuplesAndDynamic()
    {
        var implementation = Emit("Descriptor.Structural", """
                                                           using System.Threading; using System.Threading.Tasks; using System.Collections.Generic;
                                                           using System.ComponentModel; using System.ComponentModel.DataAnnotations;
                                                           using IxIFlow.Core; using IxIFlow.ActivitySdk;
                                                           namespace Structural;
                                                           public sealed class Outer<T> { public sealed class Inner<TValue> { } }
                                                           public sealed class CustomRequiredAttribute : RequiredAttribute { }
                                                           public class Base { [ActivityInput, CustomRequired] public virtual string Inherited {get;set;} }
                                                           [WorkflowActivity("structural.activity")]
                                                           public sealed class Activity : Base, IAsyncActivity {
                                                             public override string Inherited {get;set;}
                                                             [ActivityInput] public Outer<int>.Inner<List<string[,]>>[] Nested {get;set;}
                                                             [ActivityInput, DefaultValue(7)] public int? Nullable {get;set;}
                                                             [ActivityInput] public (int Number,string Text) Tuple {get;set;}
                                                             [ActivityInput] public dynamic Dynamic {get;set;}
                                                             public Task ExecuteAsync(IActivityContext c,CancellationToken t=default)=>Task.CompletedTask;
                                                           }
                                                           """);

        var store = new InMemoryWorkflowStateRepository();

        var receipt = await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package(
            "structural.activity", "1.0.0",
            new() { ["lib/net10.0/Descriptor.Structural.dll"] = implementation })));

        var descriptor = Assert.Single(receipt.Activities.Activities);
        Assert.Equal("global::System.Object", descriptor.Inputs.Single(item => item.Name == "Dynamic").TypeName);
        Assert.Equal("global::System.Nullable<global::System.Int32>", descriptor.Inputs.Single(item => item.Name == "Nullable").TypeName);
        Assert.Contains("global::System.ValueTuple<", descriptor.Inputs.Single(item => item.Name == "Tuple").TypeName);
        Assert.True(descriptor.Inputs.Single(item => item.Name == "Inherited").Required);
    }

    [Fact]
    public async Task DifferentDependencyDefaultsArePub015AndCollectibleContextsAreReleased()
    {
        var helper1 = Emit("Descriptor.Helper", HelperSource(1));
        var helper2 = Emit("Descriptor.Helper", HelperSource(2));

        var activity = Emit("Descriptor.Defaults", """
                                                   using System.Threading; using System.Threading.Tasks; using System.ComponentModel;
                                                   using System; using System.Text.Json; using System.Text.Json.Serialization;
                                                   using IxIFlow.Core; using IxIFlow.ActivitySdk;
                                                   namespace Defaults;
                                                   [WorkflowActivity("defaults.activity")]
                                                   public sealed class Activity : IAsyncActivity {
                                                     public Activity() { throw new System.Exception("An activity must not be constructed during extraction"); }
                                                     [ActivityInput, DefaultValue(typeof(Dependency.Value), "converted")]
                                                     public Dependency.Value Value {get;set;}
                                                     [ActivityInput, DefaultValue(3), JsonConverter(typeof(NumberConverter))]
                                                     public int Converted {get;set;}
                                                     public Task ExecuteAsync(IActivityContext c,CancellationToken t=default) => Task.CompletedTask;
                                                   }
                                                   public sealed class NumberConverter : JsonConverter<int> {
                                                     public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => 3;
                                                     public override void Write(Utf8JsonWriter writer,int value,JsonSerializerOptions options) => writer.WriteStringValue("converted:" + value);
                                                   }
                                                   """, helper1);

        var store = new InMemoryWorkflowStateRepository();
        var contexts = new System.Collections.Concurrent.ConcurrentBag<WeakReference>();

        void Observe(object? sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name is "Descriptor.Helper" or "Descriptor.Defaults" &&
                System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(args.LoadedAssembly) is { IsCollectible: true } context)
                contexts.Add(new(context));
        }

        AppDomain.CurrentDomain.AssemblyLoad += Observe;

        try
        {
            var ingestion = new PackageIngestion(store, store);

            await ingestion.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("defaults.helper", "1.0.0",
                new() { ["lib/net10.0/Descriptor.Helper.dll"] = helper1 })));

            var receipt = await ingestion.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("defaults.activity", "1.0.0",
                new() { ["lib/net10.0/Descriptor.Defaults.dll"] = activity },
                "<dependency id=\"defaults.helper\" version=\"[1.0.0,3.0.0)\" />")));

            Assert.Equal("{\"Number\":1}",
                Assert.Single(receipt.Activities.Activities).Inputs.Single(item => item.Name == "Value").DefaultJson);

            Assert.Equal("\"converted:3\"",
                Assert.Single(receipt.Activities.Activities).Inputs.Single(item => item.Name == "Converted").DefaultJson);

            await ingestion.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("defaults.helper", "2.0.0",
                new() { ["lib/net10.0/Descriptor.Helper.dll"] = helper2 })));

            var error = await Assert.ThrowsAsync<WorkflowPublishException>(() => new WorkflowPackageResolver(store, store, new())
                .ResolveAsync(new List<string> { "defaults.activity@1.0.0", "defaults.helper@2.0.0" }, PackageAcquisition.Scratch, [],
                    default));

            Assert.Equal("PUB015", error.Code);
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= Observe;
        }

        // System.Text.Json's process-wide emitted-accessor cache expires entries after one
        // idle second and evicts on subsequent metadata work. Exercise that public path;
        // an engine-owned strong type cache would still keep the context alive afterward.
        for (var attempt = 0; attempt < 100 && contexts.Any(item => item.IsAlive); attempt++)
        {
            _ = JsonSerializer.Serialize(new CacheProbe(), new JsonSerializerOptions
                { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(25);
        }

        Assert.NotEmpty(contexts);
        Assert.All(contexts, item => Assert.False(item.IsAlive));
    }

    private static string HelperSource(int value) => $$"""
                                                       using System; using System.ComponentModel; using System.Globalization;
                                                       namespace Dependency;
                                                       [TypeConverter(typeof(ValueConverter))] public sealed class Value { public int Number {get;set;} }
                                                       public sealed class ValueConverter : TypeConverter {
                                                         public override bool CanConvertFrom(ITypeDescriptorContext c,Type t)=>true;
                                                         public override object ConvertFrom(ITypeDescriptorContext c,CultureInfo culture,object value)=>new Value {Number={{value}}};
                                                       }
                                                       """;

    private sealed class CacheProbe
    {
        public int Value { get; set; }
    }

    private static string ActivitySource(string type, string? icon = null) => $$"""
                                                                                using System.Threading; using System.Threading.Tasks; using IxIFlow.Core; using IxIFlow.ActivitySdk;
                                                                                namespace Agreement;
                                                                                [WorkflowActivity("agreement.activity"{{(icon == null ? "" : ", Icon=\"" + icon + "\"")}})]
                                                                                public sealed class Activity : IAsyncActivity {
                                                                                  [ActivityInput] public {{type}} Value {get;set;}
                                                                                  public Task ExecuteAsync(IActivityContext c,CancellationToken t=default)=>Task.CompletedTask;
                                                                                }
                                                                                """;

    private static byte[] Emit(string name, string source, byte[]? dependency = null)
    {
        var references = WorkflowCompilation.ReferencePaths().Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        if (dependency != null)
            references = references.Append(MetadataReference.CreateFromImage(dependency));

        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        return output.ToArray();
    }

    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "IxIFlow.Descriptors", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }

    public class ReadOnlyCatalog : DispatchProxy
    {
        public IWorkflowCatalogStore Target { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method != null &&
            (method.Name is "get_CatalogArtifacts" or "set_CatalogArtifacts" || method.Name.StartsWith("Get", StringComparison.Ordinal) ||
                method.Name.StartsWith("List", StringComparison.Ordinal))
                ? method.Invoke(Target, args)
                : throw new XunitException("Scratch attempted a catalog mutation");
    }

    private sealed class ReadOnlyArtifacts(IArtifactStore inner) : IArtifactStore
    {
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => inner.OpenAsync(digest, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);

        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default) =>
            throw new XunitException("Scratch attempted an artifact put or age refresh");

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) =>
            throw new XunitException("Scratch attempted a delete");

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int size,
            CancellationToken token = default) => throw new XunitException("Scratch attempted a purge");
    }

    public static class Nested
    {
        [WorkflowActivity("descriptor.nested")]
        [Description("A nested activity")]
        public sealed class Activity : IAsyncActivity
        {
            [ActivityInput] public int Value { get; set; }
            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    public sealed class Generic<T>
    {
        public sealed class Inner<TValue>
        {
        }
    }

    [WorkflowActivity("descriptor.structural")]
    public sealed class StructuralNames : IAsyncActivity
    {
        [ActivityInput] public Generic<int>.Inner<List<string[,]>>[] Value { get; set; } = [];
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [WorkflowActivity("descriptor.defaults")]
    public sealed class DefaultActivity : IAsyncActivity
    {
        [ActivityInput, DefaultValue(8), JsonConverter(typeof(NumberConverter))]
        public int Converted { get; set; }

        [ActivityInput, DefaultValue(5), JsonNumberHandling(JsonNumberHandling.WriteAsString)]
        public int Number { get; set; }

        [ActivityInput, DefaultValue(null)] public string? Null { get; set; }
        [ActivityInput] public string? Absent { get; set; }

        [ActivityInput, DefaultValue(typeof(BaseValue), "converted")]
        public BaseValue? Derived { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class NumberConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            int.Parse(reader.GetString()![7..]);

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteStringValue("number:" + value);
    }

    [TypeConverter(typeof(DerivedValueConverter))]
    public class BaseValue
    {
        public int Number { get; set; }
    }

    public sealed class DerivedValue : BaseValue
    {
        public string Extra { get; set; } = "kept";
    }

    public sealed class DerivedValueConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => true;

        public override object ConvertFrom(ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object value) =>
            new DerivedValue { Number = 7 };
    }
}