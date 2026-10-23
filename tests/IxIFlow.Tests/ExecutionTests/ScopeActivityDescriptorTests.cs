using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeActivityDescriptorTests
{
    [Theory, InlineData("Scope", "Concurrency"), InlineData("Scope", "Threading"), InlineData("Scope", "ValueType"),
        InlineData("RequiresScopes", "Type"), InlineData("RequiresScopes", "TypeName"), InlineData("Inputs", "Required"), InlineData("Outputs", "HasDefault")]
    public void NestedDescriptorsRejectMissingAndUnknownMembersBeforeDeserialization(string section, string member)
    {
        var descriptor = ActivityDescriptor.OfType(typeof(Container), "test", "1.0.0") with
        {
            RequiresScopes = ActivityDescriptor.OfType(typeof(Consumer), "test", "1.0.0").RequiresScopes,
            Inputs = WorkflowContract.OfType(typeof(ScopeBuildRuleTests.Data)).Inputs,
            Outputs = WorkflowContract.OfType(typeof(ScopeBuildRuleTests.Data)).Outputs,
            ActivityType = null
        };
        var node = JsonNode.Parse(descriptor.ToJson())!;
        var target = Assert.IsType<JsonObject>(section == "Scope" ? node[section] : node[section]![0]);
        var saved = target[member]?.DeepClone();
        Assert.True(target.Remove(member));
        Assert.Throws<JsonException>(() => ActivityDescriptor.Parse(node.ToJsonString()));
        target[member] = saved;
        target["Unknown"] = 0;
        Assert.Throws<JsonException>(() => ActivityDescriptor.Parse(node.ToJsonString()));
    }

    [Theory, InlineData("threading"), InlineData("concurrency"), InlineData("requirement"), InlineData("value")]
    public async Task PublisherRefusesMismatchedScopeReferenceAndRuntimeAssemblies(string mismatch)
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using IxIFlow.Core;
            using IxIFlow.ActivitySdk;
            namespace ScopePackage;
            public sealed class Data { public string Text { get; set; } = ""; }
            public sealed class Value;
            [WorkflowActivity("scope.package.container"), ScopeThreading(ScopeThreading.ThreadAffine), ScopeConcurrency(ScopeConcurrency.Shared)]
            public sealed class Container : IScopeActivity<Value> {
                public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token) => Task.FromResult(new Value());
                public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token) => Task.CompletedTask;
            }
            [WorkflowActivity("scope.package.noop")] public sealed class Noop : IAsyncActivity {
                public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask;
            }
            """;
        var runtime = mismatch switch
        {
            "threading" => source.Replace("ScopeThreading.ThreadAffine", "ScopeThreading.AnyThread"),
            "concurrency" => source.Replace("ScopeConcurrency.Shared", "ScopeConcurrency.Serialized"),
            "requirement" => source.Replace("WorkflowActivity(\"scope.package.container\")", "WorkflowActivity(\"scope.package.container\"), RequiresScope<Value>"),
            _ => source.Replace("IScopeActivity<Value>", "IScopeActivity<Data>").Replace("Task<Value>", "Task<Data>").Replace("new Value()", "new Data()").Replace("CloseAsync(Value", "CloseAsync(Data")
        };
        byte[] Emit(string code)
        {
            var references = WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("Scope.Package", [CSharpSyntaxTree.ParseText(code)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return bytes.ToArray();
        }
        var package = ActivityPackageFixture.Package("scope.package", "1.0.0", new()
        { ["ref/net10.0/Scope.Package.dll"] = Emit(source), ["lib/net10.0/Scope.Package.dll"] = Emit(runtime) });
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        await publisher.UploadPackageAsync(new MemoryStream(package));
        var draft = await store.SaveDefinitionAsync(new("ScopePackageAgreement", 1, "workflow", """
            imports:
              catalogs: [scope.package@1.0.0]
            workflow:
              name: ScopePackageAgreement
              version: 1
              dataType: ScopePackage.Data
            definitions:
              - kind: activity
                activity: scope.package.noop
            """), null);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("scope contract disagrees between reference and runtime assemblies"));
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
    }

    [Fact]
    public void MixedLocalRegistrationDerivesScopeAndRequirementsAndFingerprints()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivity<Container>().AddActivity<Consumer>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IActivityRegistry>();
        var scope = registry.Find("scope.descriptor.container")!;
        Assert.Equal(ScopeThreading.ThreadAffine, scope.Scope!.Threading);
        Assert.Equal(ScopeConcurrency.Shared, scope.Scope.Concurrency);
        Assert.Single(registry.Find("scope.descriptor.consumer")!.RequiresScopes);
        var json = scope.ToJson();
        Assert.Null(ActivityDescriptor.Parse(json).ActivityType);
        Assert.Equal(scope.ContractHash, ActivityDescriptor.Parse(json).ContractHash);
        Assert.DoesNotContain("ActivityType", json);
        Assert.Contains("RequiresScopes", Assert.Single(registry.Packages).ActivitiesJson);
    }

    [Fact]
    public void FalseScopeRequirementConcurrencyAndThreadingClaimsAreRefused()
    {
        var ordinary = ActivityDescriptor.OfType(typeof(Consumer), "test", "1.0.0");
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(ordinary with { RequiresScopes = [] }));
        var scope = ActivityDescriptor.OfType(typeof(Container), "test", "1.0.0");
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(scope with { Scope = null }));
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(scope with { Scope = scope.Scope! with { Threading = ScopeThreading.AnyThread } }));
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(scope with { Scope = scope.Scope! with { Concurrency = ScopeConcurrency.Serialized } }));
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptorRules.Validate(ordinary with { Scope = scope.Scope }));
    }

    [Fact]
    public void DualInterfacesAreRefusedByDescriptorAndRegistration()
    {
        Assert.Throws<WorkflowEngineException>(() => ActivityDescriptor.OfType(typeof(Dual), "test", "1.0.0"));
        Assert.Throws<WorkflowEngineException>(() => new ServiceCollection().AddIxIFlow().AddActivity<Dual>());
    }

    [Fact]
    public void MixedCollectibleExtractionDoesNotApplyLocalLoadContextRulesOrRetainTypes()
    {
        var (reference, descriptors) = Extract();
        Assert.Equal(2, descriptors.Length);
        Assert.Contains(descriptors, item => item.Scope != null);
        Assert.Contains(descriptors, item => item.RequiresScopes.Count == 1);
        Assert.All(descriptors, item => Assert.Null(item.ActivityType));
        for (var index = 0; index < 10 && reference.IsAlive; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, ActivityDescriptor[] Descriptors) Extract()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using IxIFlow.Core;
            using IxIFlow.ActivitySdk;
            [WorkflowActivity("mixed.scope")] public sealed class Container : IScopeActivity<object> {
                public Task<object> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token) => Task.FromResult(new object());
                public Task CloseAsync(object value, ScopeClosing closing, IScopeContext context, CancellationToken token) => Task.CompletedTask;
            }
            [WorkflowActivity("mixed.consumer"), RequiresScope<object>] public sealed class Consumer : IAsyncActivity {
                public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask;
            }
            """;
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(new[] { typeof(IAsyncActivity).Assembly.Location, typeof(WorkflowActivityAttribute).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("MixedScopePackage", [CSharpSyntaxTree.ParseText(source)],
            paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emitted = compilation.Emit(bytes);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        bytes.Position = 0;
        var context = new AssemblyLoadContext("scope-extraction", isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
        var assembly = context.LoadFromStream(bytes);
        var descriptors = ActivityDescriptor.Scan(assembly, "mixed", "1.0.0").Select(item => ActivityDescriptor.Parse(item.ToJson())).ToArray();
        var reference = new WeakReference(context);
        context.Unload();
        return (reference, descriptors);
    }

    [WorkflowActivity("scope.descriptor.container"), ScopeThreading(ScopeThreading.ThreadAffine), ScopeConcurrency(ScopeConcurrency.Shared)]
    public class Container : IScopeActivity<object>
    {
        public Task<object> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken) => Task.FromResult(new object());
        public Task CloseAsync(object value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    [WorkflowActivity("scope.descriptor.consumer"), RequiresScope<object>]
    public sealed class Consumer : IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    [WorkflowActivity("scope.descriptor.dual")]
    public sealed class Dual : Container, IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
}
