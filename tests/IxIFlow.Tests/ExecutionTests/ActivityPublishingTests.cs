using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Packaging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-16")]
[Trait("Spec", "F-19")]
public sealed class ActivityPublishingTests
{
    internal const string Source = """
                                   # Authored source remains verbatim.
                                   activity: {key: acme.transform, version: 2, name: Transform}
                                   input:
                                     Value: {type: int, default: 3}
                                   output:
                                     Result: {type: int}
                                   inOut:
                                     Attempts: {type: int}
                                   body: |
                                     await Task.Yield();
                                     cancellationToken.ThrowIfCancellationRequested();
                                     Attempts++;
                                     Result = Value * 2;
                                   """;

    internal static async Task<WorkflowPackage> PublishActivity(IWorkflowStateRepository store, string source = Source,
        WorkflowPublisherOptions? options = null)
    {
        var identity = WorkflowDocumentKinds.Identify(source);
        var draft = await store.SaveDefinitionAsync(new(identity.Name, identity.Version, identity.Kind, source), null, "author");
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id, "publisher");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Null(result.Workflow);
        Assert.Null(result.Template);
        Assert.Equal(draft.Id, result.Package!.PublishedDraft);

        return result.Package;
    }

    internal static async Task<byte[]> Bytes(IArtifactStore store, ArtifactDigest digest)
    {
        await using var content = await store.OpenAsync(digest);
        Assert.NotNull(content);
        using var bytes = new MemoryStream();
        await content.CopyToAsync(bytes);

        return bytes.ToArray();
    }

    internal static string ConsumerYaml(string name = "ActivityConsumer", string key = "acme.transform", int version = 2) => $$"""
          workflow: {name: {{name}}, version: 1, dataType: IxIFlow.BundleFixtures.PublishedActivityData}
          imports: {catalogs: [{{key}}@{{version}}.0.0]}
          definitions:
            - kind: activity
              id: transform
              activity: {{key}}
              activityVersion: "{{version}}"
              input:
                - target: Attempts
                  from: {kind: path, path: workflow.Attempts}
              output:
                - source: Result
                  to: {kind: path, path: workflow.Result}
                - source: Attempts
                  to: {kind: path, path: workflow.Attempts}
          """;

    internal static async Task<WorkflowVersion> PublishWorkflow(IWorkflowStateRepository store, string yaml,
        WorkflowPublisherOptions? options = null)
    {
        var identity = WorkflowDocumentKinds.Identify(yaml);
        var draft = await store.SaveDefinitionAsync(new(identity.Name, identity.Version, identity.Kind, yaml), null);
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        return result.Workflow!;
    }

    [Theory, InlineData("local-memory"), InlineData("local-sqlite")]
    public async Task RealPackageReadbackHasWholeReceiptProfileSdkDependenciesAndVerbatimSymbols(string profile)
    {
        await using var database = await ActivityStore.CreateAsync(profile);
        var store = database.NewRepository();
        var package = await PublishActivity(store);
        Assert.Equal(WorkflowPackageOrigin.Published, package.Origin);
        Assert.Equal("publisher", package.PublishedBy);
        Assert.Empty(package.Closure!);
        var bytes = await Bytes(store, package.Digest);
        using var reader = new PackageArchiveReader(new MemoryStream(bytes));
        Assert.Equal("acme.transform", reader.GetIdentity().Id);
        Assert.Equal("2.0.0", reader.GetIdentity().Version.ToNormalizedString());
        var dependencies = Assert.Single(reader.GetPackageDependencies()).Packages.ToArray();
        Assert.Contains(dependencies, dependency => dependency.Id == "IxIFlow.Abstractions");

        // Every emitted SDK reference must have its actual package dependency.
        var entries = await WorkflowPackageArchive.ReadAsync(new MemoryStream(bytes), package.Digest, new());

        using var pe =
            new System.Reflection.PortableExecutable.PEReader(new MemoryStream(entries["lib/net10.0/acme.transform.dll"].ToArray()));

        var metadata = pe.GetMetadataReader();

        foreach (var reference in metadata.AssemblyReferences
                     .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
                     .Where(name => name.StartsWith("IxIFlow", StringComparison.Ordinal)))
            Assert.Contains(dependencies, dependency => dependency.Id == reference);

        Assert.Equal(Source, Encoding.UTF8.GetString(entries[ActivityCompilation.SourceResourceName].Span));
        var manifest = JsonSerializer.Deserialize<ActivityPackageMetadata>(entries["ixiflow/activity.json"].Span)!;
        Assert.Equal(package.PublishedDraft, manifest.Draft);
        Assert.NotEqual(Guid.Empty, manifest.Draft.Generation);
        Assert.Equal(WorkflowCompilation.Settings, manifest.Compiler);
        Assert.NotEmpty(manifest.SharedAssemblies);
        Assert.Empty(manifest.ActivityPackages);
        using var zip = new ZipArchive(new MemoryStream(bytes));

        Assert.Equal(zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal),
            zip.Entries.Select(entry => entry.FullName));

        Assert.All(zip.Entries, entry => Assert.Equal(1980, entry.LastWriteTime.Year));
        AssertEmbeddedSource(entries["lib/net10.0/acme.transform.pdb"].ToArray(), Source);
        var detail = await store.GetPackageDetailAsync(package.Id, package.Version, CatalogScope.Unrestricted);
        var descriptor = Assert.Single(detail!.Activities.Activities);
        Assert.Null(descriptor.ActivityType);
        Assert.Equal("3", Assert.Single(descriptor.Inputs, member => member.Name == "Value").DefaultJson);
        Assert.Contains(descriptor.Inputs, member => member.Name == "Attempts");
        Assert.Contains(descriptor.Outputs, member => member.Name == "Attempts");
        var replay = await new WorkflowPublisher(store, store).PublishAsync(package.PublishedDraft!, "retry");
        Assert.True(replay.Success);
        Assert.Equal("publisher", replay.Package!.PublishedBy);
        Assert.Equal(package.Digest, replay.Package.Digest);
        Assert.Null(await store.GetDefinitionAsync("activity", package.Id, 2));
    }

    [Fact]
    public async Task NupkgIsDeterministicForTheFullDraftAndVerbatimSourceCanBeRepublished()
    {
        var draft = new WorkflowDraft("acme.transform", 2, "activity", Source, 3) { Generation = Guid.NewGuid() };
        var options = new WorkflowPublisherOptions();
        using var profile = WorkflowSharedAssemblies.Enter(options.SharedProfile);
        using var packages = new ResolvedWorkflowPackages();
        var compilation = ActivityCompilation.Compile(Source);
        var shared = new Dictionary<string, string>();
        var identity = new ActivityPackageIdentity(draft.Name, draft.Version);
        var first = await ActivityPackageConstruction.CreateAsync(draft, identity, compilation, packages, shared, options, default);

        var second = await ActivityPackageConstruction.CreateAsync(draft, identity, ActivityCompilation.Compile(Source), packages, shared,
            options, default);

        Assert.Equal(first.Bytes.ToArray(), second.Bytes.ToArray());
        var store = new InMemoryWorkflowStateRepository();
        var published = await PublishActivity(store);

        var entries = await WorkflowPackageArchive.ReadAsync(new MemoryStream(await Bytes(store, published.Digest)), published.Digest,
            new());

        var source = Encoding.UTF8.GetString(entries[ActivityCompilation.SourceResourceName].Span);
        var edited = source.Replace("version: 2", "version: 3", StringComparison.Ordinal);
        var next = await PublishActivity(store, edited);
        var republished = await WorkflowPackageArchive.ReadAsync(new MemoryStream(await Bytes(store, next.Digest)), next.Digest, new());
        var compiled = ActivityCompilation.Compile(edited);
        Assert.Equal(compiled.Assembly, republished["lib/net10.0/acme.transform.dll"].ToArray());
        Assert.Equal(compiled.Pdb, republished["lib/net10.0/acme.transform.pdb"].ToArray());
        AssertEmbeddedSource(compiled.Pdb, edited);
    }

    [Theory, InlineData("local-memory"), InlineData("local-sqlite")]
    public async Task PackedActivityReferencesFrameworkContractsAndCompilesInAConsumer(string profile)
    {
        await using var database = await ActivityStore.CreateAsync(profile);
        var store = database.NewRepository();

        var source = Source.Replace("acme.transform", "acme.reference-proof")
            .Replace("Result = Value * 2;",
                "Result = new System.Uri(\"https://example.test\").AbsolutePath.Length + typeof(System.Net.Http.HttpClient).Name.Length; _ = System.Text.Json.JsonSerializer.Serialize(Value);");

        var package = await PublishActivity(store, source);
        var entries = await WorkflowPackageArchive.ReadAsync(new MemoryStream(await Bytes(store, package.Digest)), package.Digest, new());
        var dll = entries[$"lib/net10.0/{package.Id}.dll"].ToArray();
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(dll));
        var metadata = pe.GetMetadataReader();
        var names = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
        Assert.DoesNotContain(names, name => name.StartsWith("System.Private.", StringComparison.Ordinal));
        Assert.Contains("System.Runtime", names);
        Assert.Contains("System.Net.Http", names);
        Assert.Contains("System.Text.Json", names);

        // Compile the fluent consumer against framework reference metadata, as an
        // ordinary SDK project does. No implementation-only BCL reference can hide
        // an invalid public base type or method signature in the packed assembly.
        var references = ActivityCompilation.ReferencePaths().Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path))
            .Cast<Microsoft.CodeAnalysis.MetadataReference>().Append(Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(dll));

        var type = new ActivityPackageIdentity(package.Id, 2).CSharpTypeName;

        var consumer = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("ReferenceConsumerProof",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(ActivityConsumerSource.Create(type, true))], references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var content = new MemoryStream();
        var emitted = consumer.Emit(content);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
    }

    [Theory, InlineData("local-memory"), InlineData("local-sqlite")]
    public async Task FullFeedBackedScratchNeverWritesAndInspectionNeverConstructsOrRunsActivity(string profile)
    {
        await using var database = await ActivityStore.CreateAsync(profile);
        var store = database.NewRepository();
        using var folder = new TemporaryFolder();

        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "fixture.dependency.1.0.0.nupkg"),
            ActivityPackageFixture.Package("fixture.dependency", "1.0.0", new()
            {
                ["lib/net10.0/Dependency.dll"] = Emit("Dependency", "public static class Dependency { public static int Value => 1; }")
            }));

        var config = Path.Combine(folder.Path, "NuGet.Config");

        await File.WriteAllTextAsync(config,
            "<configuration><packageSources><clear/><add key=\"local\" value=\".\"/></packageSources></configuration>");

        var source = Source.Replace("input:", "packages: [fixture.dependency@1.0.0]\ninput:")
                .Replace("Result = Value * 2;", "Result = Dependency.Value; throw new Exception(\"body invoked\");") +
            "\ndeclarations: |\n  public Transform() => throw new Exception(\"constructor invoked\");\n";

        var draft = await store.SaveDefinitionAsync(new("acme.transform", 2, "activity", source), null);
        var revision = await store.GetCatalogRevisionAsync();
        var guarded = DispatchProxy.Create<IWorkflowCatalogStore, ReadOnlyCatalog>();
        ((ReadOnlyCatalog)guarded).Target = store;
        var artifacts = new ReadOnlyArtifacts(store);
        var publisher = new WorkflowPublisher(guarded, artifacts, new() { NuGetConfigPath = config });

        foreach (var item in new[] { draft, draft with { Generation = Guid.Empty, Revision = 0 } })
        {
            var report = await publisher.ValidateAsync(item);
            Assert.True(report.IsValid, string.Join("; ", report.Diagnostics));
        }

        Assert.Equal(0, artifacts.Puts);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Equal(draft, await store.GetDefinitionAsync("activity", draft.Name, 2));
        Assert.Empty((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "activity", draft.Name, 2))).Items);
    }

    [Theory, InlineData("local-memory"), InlineData("local-sqlite")]
    public async Task IdentityAndOccupiedChecksPrecedeCompilationAndDefaultFailuresUsePub021(string profile)
    {
        await using var database = await ActivityStore.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store);

        var mismatch =
            await publisher.ValidateAsync(new("other.name", 2, "activity", Source.Replace("Result = Value * 2;", "not valid csharp")));

        Assert.Contains(mismatch.Diagnostics,
            diagnostic => diagnostic.Code == "PUB005" && diagnostic.Stage == WorkflowPublishStage.Validate);

        var published = await PublishActivity(store);

        var occupied =
            await publisher.ValidateAsync(new("acme.transform", 2, "activity", Source.Replace("Result = Value * 2;", "not valid csharp")));

        Assert.Contains(occupied.Diagnostics,
            diagnostic => diagnostic.Code == "PUB022" && diagnostic.Class == WorkflowPublishDiagnosticClass.Conflict);

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.SaveDefinitionAsync(new("acme.transform", 2, "activity", Source), null));

        var replay = await publisher.PublishAsync(published.PublishedDraft!);
        Assert.Equal(published.Digest, replay.Package!.Digest);
        Assert.Null(await store.GetDefinitionAsync("activity", published.Id, 2));

        var badDefault = Source.Replace("acme.transform", "acme.bad-default")
            .Replace("{type: int, default: 3}", "{type: Guid, default: invalid-guid}")
            .Replace("Result = Value * 2;", "Result = 1;");

        var report = await publisher.ValidateAsync(new("acme.bad-default", 2, "activity", badDefault));
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "PUB021" && diagnostic.Stage == WorkflowPublishStage.Test);
        Assert.All(report.Diagnostics, diagnostic => Assert.Equal(ActivityCompilation.SourceResourceName, diagnostic.Path));
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "PUB007");
    }

    [Theory, InlineData("local-memory"), InlineData("local-sqlite")]
    public async Task ColdCatalogConsumerRunsDefaultsOutputsInOutAndRejectsWrongActivityVersion(string profile)
    {
        await using var database = await ActivityStore.CreateAsync(profile);
        var store = database.NewRepository();
        await PublishActivity(store);
        var version = await PublishWorkflow(store, ConsumerYaml());
        using var folder = new TemporaryFolder();
        await using var host = PublishedBundleScenario.Host(store, folder.Path, 0);
        var client = host.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(version.Name, 1, new { Attempts = 4 });
        var result = await client.AwaitAsync(id);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(6, result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
        Assert.Equal(5, result.WorkflowData.GetType().GetProperty("Attempts")!.GetValue(result.WorkflowData));
        var wrong = ConsumerYaml("WrongVersion").Replace("activityVersion: \"2\"", "activityVersion: \"3\"");
        var report = await new WorkflowPublisher(store, store).ValidateAsync(new("WrongVersion", 1, "workflow", wrong));
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "ACT003");
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == "acme.transform");
    }

    [Fact]
    public async Task SharedServiceHasDefaultContextIdentityInFreshHostAndMissingHostIsIncompatible()
    {
        var store = new InMemoryWorkflowStateRepository();
        var contracts = new[] { typeof(IPublishedGreetingService).Assembly };
        var options = new WorkflowPublisherOptions { SharedContractAssemblies = contracts };

        var source = """
                     activity: {key: acme.greeting, version: 1}
                     usings: [IxIFlow.BundleFixtures]
                     output:
                       Text: {type: string}
                     body: |
                       if (!ReferenceEquals(_service, context.Services.GetService(typeof(IPublishedGreetingService)))) throw new Exception("Service identity differs");
                       Text = _service.Greet("cold host");
                     declarations: |
                       private readonly IPublishedGreetingService _service;
                       public Greeting(IPublishedGreetingService service) => _service = service;
                     """;

        var package = await PublishActivity(store, source, options);

        var yaml = """
                   workflow: {name: GreetingConsumer, version: 1, dataType: IxIFlow.BundleFixtures.PublishedActivityData}
                   imports: {catalogs: [acme.greeting@1.0.0]}
                   definitions:
                     - kind: activity
                       activity: acme.greeting
                       activityVersion: "1"
                       output: [{source: Text, to: {kind: path, path: workflow.Text}}]
                   """;

        var version = await PublishWorkflow(store, yaml, options);
        using var folder = new TemporaryFolder();
        var services = new ServiceCollection();
        var builder = services.AddIxIFlow().ShareContracts(contracts).UseWorkflowBundles(folder.Path, 0);
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton<IPublishedGreetingService, GreetingService>();
        await using var host = services.BuildServiceProvider();
        var catalog = host.GetRequiredService<IWorkflowCatalog>();
        var capabilities = await host.GetRequiredService<HostSettings>().RefreshCapabilitiesAsync(catalog);
        Assert.Contains("workflow:GreetingConsumer@1", capabilities.Keys);

        using (var lease = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1))
        {
            Assert.Same(typeof(PublishedActivityData), lease.Definition.WorkflowDataType);
            var activity = lease.Definition.BundleContext!.LoadFromAssemblyName(new AssemblyName(package.Id));
            var constructor = activity.GetType("IxIFlow.Activities.Acme.Greeting")!.GetConstructors().Single();
            Assert.Same(typeof(IPublishedGreetingService), constructor.GetParameters().Single().ParameterType);
        }

        var client = host.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(version.Name, 1, new PublishedActivityData());
        var result = await client.AwaitAsync(id);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("hello cold host", ((PublishedActivityData)result.WorkflowData!).Text);
        Assert.Throws<InvalidOperationException>(() => builder.ShareContracts(contracts));
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!;
        Assert.False(WorkflowSharedAssemblies.IsCompatible(manifest));

        using (WorkflowSharedAssemblies.Enter(options.SharedProfile))
        {
            Assert.True(WorkflowSharedAssemblies.IsCompatible(manifest));
            var required = manifest.SharedAssemblies.ToDictionary();
            var key = required.Keys.Single(name => new AssemblyName(name).Name == contracts[0].GetName().Name);
            required.Remove(key);
            var changed = new AssemblyName(key) { Version = new Version(99, 0, 0, 0) };
            required.Add(changed.FullName, changed.Version.ToString());
            Assert.False(WorkflowSharedAssemblies.IsCompatible(manifest with { SharedAssemblies = required }));
        }

        using var missingFolder = new TemporaryFolder();
        await using var missing = PublishedBundleScenario.Host(store, missingFolder.Path);

        var missingCapabilities = await missing.GetRequiredService<HostSettings>()
            .RefreshCapabilitiesAsync(missing.GetRequiredService<IWorkflowCatalog>());

        Assert.DoesNotContain("workflow:GreetingConsumer@1", missingCapabilities.Keys);

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            missing.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync(version.Name, 1));

        var refused = await new WorkflowPublisher(store, store).ValidateAsync(new("GreetingConsumer", 1, "workflow", yaml));
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "PUB008");
    }

    [Fact]
    public void SharedContractsRejectNondefaultAndUndeclaredClosureAndFreezeAnInputSnapshot()
    {
        Assert.Throws<ArgumentException>(() => WorkflowSharedAssemblies.CreateProfile([typeof(ActivityPublishingTests).Assembly]));
        var context = new AssemblyLoadContext("contract-copy", true);

        try
        {
            var copy = context.LoadFromAssemblyPath(typeof(IPublishedGreetingService).Assembly.Location);
            Assert.Throws<ArgumentException>(() => WorkflowSharedAssemblies.CreateProfile([copy]));
        }
        finally
        {
            context.Unload();
        }

        var assemblies = new List<Assembly> { typeof(IPublishedGreetingService).Assembly };
        var options = new WorkflowPublisherOptions { SharedContractAssemblies = assemblies };
        var snapshot = options.SharedProfile;
        assemblies.Clear();
        Assert.Single(snapshot.Contracts);
        Assert.Same(snapshot, options.SharedProfile);
        var services = new ServiceCollection();
        var builder = services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<HostSettings>();
        Assert.Throws<InvalidOperationException>(() => builder.ShareContracts(typeof(IPublishedGreetingService).Assembly));
    }

    [Fact]
    public async Task SdkDependenciesFollowRealEmittedReferencesAndFluentConsumerUsesShippedRegistration()
    {
        var store = new InMemoryWorkflowStateRepository();
        var source = Source.Replace("acme.transform", "acme.fluent-proof");
        var package = await PublishActivity(store, source);
        var entries = await WorkflowPackageArchive.ReadAsync(new MemoryStream(await Bytes(store, package.Digest)), package.Digest, new());
        var dll = entries["lib/net10.0/" + package.Id + ".dll"].ToArray();
        var type = new ActivityPackageIdentity(package.Id, 2).CSharpTypeName;
        var code = ActivityConsumerSource.Create(type, true);

        var references = WorkflowCompilation.ReferencePaths().Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path))
            .Cast<Microsoft.CodeAnalysis.MetadataReference>().Append(Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(dll));

        var consumer = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("FluentConsumerProof",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code)], references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var content = new MemoryStream();
        var emitted = consumer.Emit(content);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        _ = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(dll));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(content.ToArray()));
        var result = (Task<int>)assembly.GetType("Consumer.Probe")!.GetMethod("Execute")!.Invoke(null, null)!;
        Assert.Equal(6, await result);

        var sdkSource = Source.Replace("acme.transform", "acme.sdk-references").Replace("Result = Value * 2;",
            "Result = typeof(IxIFlow.Core.WorkflowDefinition).Name.Length; _ = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection());");

        var sdkPackage = await PublishActivity(store, sdkSource);
        using var reader = new PackageArchiveReader(new MemoryStream(await Bytes(store, sdkPackage.Digest)));
        var dependencies = Assert.Single(reader.GetPackageDependencies()).Packages.ToArray();

        foreach (var assemblyName in new[]
                 {
                     "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions", "IxIFlow",
                     "IxIFlow.Abstractions"
                 })
            Assert.Contains(dependencies,
                dependency => dependency.Id == assemblyName && dependency.VersionRange.MinVersion == dependency.VersionRange.MaxVersion);

        var repeatedSdk = await PublishActivity(store, Source.Replace("acme.transform", "acme.sdk-declarations"),
            new() { SharedContractAssemblies = [typeof(IAsyncActivity).Assembly] });

        using var repeatedReader = new PackageArchiveReader(new MemoryStream(await Bytes(store, repeatedSdk.Digest)));

        Assert.Contains(Assert.Single(repeatedReader.GetPackageDependencies()).Packages,
            dependency => dependency.Id == "IxIFlow.Abstractions");
    }

    [Fact]
    public async Task ConsumerFeedFixtureListsAndServesAvailableWithdrawnBytes()
    {
        var store = new InMemoryWorkflowStateRepository();
        var package = await PublishActivity(store);
        await store.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(package.LifecycleRevision));
        await using var feed = new ActivityConsumerNuGetFeed(store);
        using var client = new HttpClient();
        using var index = JsonDocument.Parse(await client.GetByteArrayAsync(feed.Index));
        Assert.Equal("3.0.0", index.RootElement.GetProperty("version").GetString());
        using var versions = JsonDocument.Parse(await client.GetByteArrayAsync(feed.Base + "/flat/" + package.Id + "/index.json"));
        Assert.Equal(package.Version, Assert.Single(versions.RootElement.GetProperty("versions").EnumerateArray()).GetString());

        Assert.Equal(await Bytes(store, package.Digest),
            await client.GetByteArrayAsync(feed.Base + $"/flat/{package.Id}/{package.Version}/{package.Id}.{package.Version}.nupkg"));
    }

    [Fact]
    public async Task AColdCatalogCannotReplaceThePublishedActivitysDigestPinnedDependency()
    {
        var store = new InMemoryWorkflowStateRepository();

        byte[] Dependency(int value) => ActivityPackageFixture.Package("fixture.pin-dependency", "1.0.0", new()
        {
            ["lib/net10.0/Pinned.Dependency.dll"] = Emit("Pinned.Dependency",
                $"public static class PinnedDependency {{ public static int Value => {value}; }}")
        });

        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(Dependency(1)));

        var source = Source.Replace("acme.transform", "acme.pinned")
            .Replace("input:", "packages: [fixture.pin-dependency@1.0.0]\ninput:")
            .Replace("Result = Value * 2;", "Result = PinnedDependency.Value;");

        var package = await PublishActivity(store, source);

        var valid = await new WorkflowPublisher(store, store).ValidateAsync(new("PinnedConsumer", 1, "workflow",
            ConsumerYaml("PinnedConsumer", package.Id)));

        Assert.True(valid.IsValid, string.Join("; ", valid.Diagnostics));
        using var folder = new TemporaryFolder();
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, package.Id + ".2.0.0.nupkg"), await Bytes(store, package.Digest));
        await File.WriteAllBytesAsync(Path.Combine(folder.Path, "fixture.pin-dependency.1.0.0.nupkg"), Dependency(2));
        var config = Path.Combine(folder.Path, "NuGet.Config");

        await File.WriteAllTextAsync(config,
            "<configuration><packageSources><clear/><add key=\"local\" value=\".\"/></packageSources></configuration>");

        var cold = new InMemoryWorkflowStateRepository();

        var refused = await new WorkflowPublisher(cold, cold, new() { NuGetConfigPath = config })
            .ValidateAsync(new("PinnedConsumer", 1, "workflow", ConsumerYaml("PinnedConsumer", package.Id)));

        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "PUB020");
        Assert.Empty(await cold.GetPackagesAsync());
    }

    [Fact]
    public async Task NeutralAndRidClosureRoundTripsWithoutPromotingPlatformOnlyPackages()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);

        foreach (var id in new[] { "fixture.windows", "fixture.linux" })
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package(id, "1.0.0", new()
                { ["lib/net10.0/Platform.dll"] = Emit("Platform", "public sealed class PlatformValue { }") })));

        var graph = JsonSerializer.Serialize(new
        {
            runtimes = new Dictionary<string, object>
            {
                ["win-x64"] = new Dictionary<string, object>
                    { ["fixture.neutral"] = new Dictionary<string, string> { ["fixture.windows"] = "[1.0.0]" } },
                ["linux-x64"] = new Dictionary<string, object>
                    { ["fixture.neutral"] = new Dictionary<string, string> { ["fixture.linux"] = "[1.0.0]" } }
            }
        });

        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.neutral", "1.0.0", new()
            { ["runtime.json"] = Encoding.UTF8.GetBytes(graph) })));

        var source = Source.Replace("input:", "packages: [fixture.neutral@1.0.0]\ninput:");
        var package = await PublishActivity(store, source);
        using var reader = new PackageArchiveReader(new MemoryStream(await Bytes(store, package.Digest)));
        var neutral = Assert.Single(reader.GetPackageDependencies()).Packages.Select(item => item.Id).ToArray();
        Assert.Contains("fixture.neutral", neutral);
        Assert.DoesNotContain("fixture.windows", neutral);
        Assert.DoesNotContain("fixture.linux", neutral);

        using (var runtime = JsonDocument.Parse(reader.GetStream("runtime.json")))
        {
            Assert.Equal("[1.0.0]",
                runtime.RootElement.GetProperty("runtimes").GetProperty("win-x64").GetProperty(package.Id).GetProperty("fixture.windows")
                    .GetString());

            Assert.Equal("[1.0.0]",
                runtime.RootElement.GetProperty("runtimes").GetProperty("linux-x64").GetProperty(package.Id).GetProperty("fixture.linux")
                    .GetString());
        }

        using var resolved =
            await new WorkflowPackageResolver(store, store, new()).ResolveAsync(new List<string> { package.Id + "@" + package.Version },
                PackageAcquisition.CatalogOnly, [], default);

        Assert.Equal(4, resolved.Pins.Count);
        var windows = resolved.Pins.Single(pin => pin.Id == "fixture.windows");
        Assert.Contains("win-x64", windows.ApplicableRuntimeIdentifiers!);
        Assert.DoesNotContain("linux-x64", windows.ApplicableRuntimeIdentifiers!);
        var linux = resolved.Pins.Single(pin => pin.Id == "fixture.linux");
        Assert.Contains("linux-x64", linux.ApplicableRuntimeIdentifiers!);
        Assert.DoesNotContain("win-x64", linux.ApplicableRuntimeIdentifiers!);

        Assert.Equal(package.Closure!.OrderBy(pin => pin.Id),
            resolved.Pins.Where(pin => pin.Id != package.Id).OrderBy(pin => pin.Id)
                .Select(pin => new WorkflowPackageReference(pin.Id, pin.Version, pin.Digest)));

        var draft = new WorkflowDraft("RidActivityConsumer", 1, "workflow", ConsumerYaml("RidActivityConsumer"));
        var report = await publisher.ValidateAsync(draft);
        Assert.True(report.IsValid, string.Join("; ", report.Diagnostics));
    }

    [Fact]
    public async Task DefaultConverterTestTimeoutAndCancellationLeaveNoDurableWrites()
    {
        const string source = """
                              activity: {key: acme.slow-default, version: 1}
                              input:
                                Value: {type: SlowValue, default: wait}
                              body: |
                                return;
                              declarations: |
                                [System.ComponentModel.TypeConverter(typeof(SlowConverter))]
                                public sealed class SlowValue { public string Value { get; set; } = ""; }
                                public sealed class SlowConverter : System.ComponentModel.TypeConverter {
                                  public override object? ConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object value) {
                                    System.Threading.Thread.Sleep(500);
                                    return new SlowValue { Value = (string)value };
                                  }
                                }
                              """;

        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("acme.slow-default", 1, "activity", source), null);
        var revision = await store.GetCatalogRevisionAsync();
        var report = await new WorkflowPublisher(store, store, new() { BuildTimeout = TimeSpan.FromMilliseconds(50) }).ValidateAsync(draft);

        Assert.Contains(report.Diagnostics,
            diagnostic => diagnostic.Code == "PUB043" && diagnostic.Class == WorkflowPublishDiagnosticClass.Unavailable &&
                diagnostic.Stage == WorkflowPublishStage.Test);

        using var cancel = new CancellationTokenSource();
        var validating = new WorkflowPublisher(store, store).ValidateAsync(draft, cancel.Token);
        cancel.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validating);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Equal(draft, await store.GetDefinitionAsync("activity", draft.Name, 1));
        await Task.Delay(550); // The trusted converters finish, releasing abandoned contexts.
    }

    [Fact]
    public async Task ColdCatalogConsumerPropagatesDurableCancellationIntoTheGeneratedBody()
    {
        var store = new InMemoryWorkflowStateRepository();
        var source = Source.Replace("Result = Value * 2;", "await Task.Delay(Timeout.Infinite, cancellationToken);");
        await PublishActivity(store, source);
        var version = await PublishWorkflow(store, ConsumerYaml());
        using var folder = new TemporaryFolder();
        await using var host = PublishedBundleScenario.Host(store, folder.Path, 0);
        var client = host.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(version.Name, 1, new { Attempts = 4 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while ((await store.GetWorkflowInstanceSummaryAsync(id))?.Status != WorkflowStatus.Running)
            await Task.Delay(10, timeout.Token);

        var result = await client.CancelAsync(id, new CancellationReason { ReasonCode = "activity-test" }, timeout.Token);

        if (result.Status != WorkflowExecutionStatus.Cancelled)
            result = await client.AwaitAsync(id, cancellationToken: timeout.Token);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task HashVerifiedSiblingSymbolsGiveActivityYamlExceptionLinesInScratchAndBundleAndContextsCollect()
    {
        var source = Source.Replace("await Task.Yield();", ";")
            .Replace("Result = Value * 2;", "throw new InvalidOperationException(\"activity body\");");

        var store = new InMemoryWorkflowStateRepository();
        var package = await PublishActivity(store, source);
        var compilation = ActivityCompilation.Compile(source);
        var scratch = ThrowInScratch(compilation);
        var version = await PublishWorkflow(store, ConsumerYaml());
        using var folder = new TemporaryFolder();
        var bundle = ThrowInBundle(store, version, folder.Path);

        for (var index = 0; index < 100 && (scratch.IsAlive || bundle.IsAlive); index++)
        {
            // Advance the runtime's last-type metadata cache, as the existing bundle
            // collection tests do; it can otherwise retain the most recent private type.
            _ = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver().GetTypeInfo(typeof(PublishedActivityData),
                new JsonSerializerOptions());

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(25);
        }

        Assert.False(scratch.IsAlive, "Scratch context is still alive");
        Assert.False(bundle.IsAlive, "Bundle context is still alive");

        // Re-read through the hash verifier, then corrupt the sibling PDB bytes.
        var bytes = await Bytes(store, package.Digest);
        using var input = new ZipArchive(new MemoryStream(bytes));
        using var altered = new MemoryStream();

        using (var zip = new ZipArchive(altered, ZipArchiveMode.Create, true))
            foreach (var entry in input.Entries)
            {
                using var content = entry.Open();
                using var target = zip.CreateEntry(entry.FullName).Open();

                if (entry.FullName.EndsWith(".pdb"))
                    target.Write([1, 2, 3]);
                else
                    content.CopyTo(target);
            }

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            WorkflowPackageArchive.ReadAsync(new MemoryStream(altered.ToArray()), package.Digest, new()));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ThrowInScratch(WorkflowCompilationResult compilation)
    {
        var context = new PackageScratchContext(new Dictionary<string, byte[]> { ["acme.transform"] = compilation.Assembly },
            new Dictionary<string, byte[]> { ["acme.transform"] = compilation.Pdb });

        var weak = new WeakReference(context);

        try
        {
            AssertYamlFrame(context.LoadFromAssemblyName(new AssemblyName("acme.transform")));
        }
        finally
        {
            WorkflowJson.Release(context);
            context.Unload();
        }

        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ThrowInBundle(IWorkflowStateRepository store, WorkflowVersion version, string folder)
    {
        var host = PublishedBundleScenario.Host(store, folder, 0);

        try
        {
            using var lease = host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync(version.Name, 1).GetAwaiter()
                .GetResult();

            var context = lease.Definition.BundleContext!;
            AssertYamlFrame(context.LoadFromAssemblyName(new AssemblyName("acme.transform")));

            return new WeakReference(context);
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void AssertYamlFrame(Assembly assembly)
    {
        var activity = (IAsyncActivity)Activator.CreateInstance(assembly.GetType("IxIFlow.Activities.Acme.Transform")!)!;
        var error = Assert.Throws<InvalidOperationException>(() => activity.ExecuteAsync(null!).GetAwaiter().GetResult());

        Assert.Contains(new StackTrace(error, true).GetFrames(),
            frame => frame.GetFileName() == ActivityCompilation.SourceResourceName && frame.GetFileLineNumber() == 13);
    }

    internal static byte[] Emit(string name, string source)
    {
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(name,
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            WorkflowCompilation.ReferencePaths().Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path)),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var bytes = new MemoryStream();
        var result = compilation.Emit(bytes);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        return bytes.ToArray();
    }

    private static void AssertEmbeddedSource(byte[] pdb, string expected)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb));
        var reader = provider.GetMetadataReader();

        var document = reader.Documents.Single(handle =>
            reader.GetString(reader.GetDocument(handle).Name) == ActivityCompilation.SourceResourceName);

        var embedded = reader.GetCustomDebugInformation(document).Select(reader.GetCustomDebugInformation)
            .Single(info => reader.GetGuid(info.Kind) == new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE"));

        var blob = reader.GetBlobBytes(embedded.Value);

        if (BitConverter.ToInt32(blob) == 0)
            Assert.Equal(expected, Encoding.UTF8.GetString(blob, 4, blob.Length - 4));
        else
        {
            using var compressed = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
            using var content = new MemoryStream();
            compressed.CopyTo(content);
            Assert.Equal(expected, Encoding.UTF8.GetString(content.ToArray()));
        }
    }

    public class ReadOnlyCatalog : DispatchProxy
    {
        public IWorkflowCatalogStore Target = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (!(method!.Name.StartsWith("Get") || method.Name.StartsWith("List") || method.Name.StartsWith("get_")))
                throw new InvalidOperationException("Unexpected catalog mutation: " + method.Name);

            return method.Invoke(Target, arguments);
        }
    }

    private sealed class ReadOnlyArtifacts(IArtifactStore inner) : IArtifactStore
    {
        internal int Puts;

        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default)
        {
            Puts++;

            throw new InvalidOperationException("Unexpected put/age refresh");
        }

        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => inner.OpenAsync(digest, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) =>
            throw new InvalidOperationException("Unexpected delete");

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int limit,
            CancellationToken token = default) => throw new InvalidOperationException("Unexpected purge");
    }

    private sealed class GreetingService : IPublishedGreetingService
    {
        public string Greet(string value) => "hello " + value;
    }

    internal sealed class TemporaryFolder : IDisposable
    {
        internal string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IxIFlow.ActivityPublishing", Guid.NewGuid().ToString("N"));

        internal TemporaryFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }

    private sealed class ActivityStore(string profile) : IAsyncDisposable
    {
        private readonly TemporaryFolder _folder = new();
        private IWorkflowStateRepository? _repository;
        internal static Task<ActivityStore> CreateAsync(string profile) => Task.FromResult(new ActivityStore(profile));

        internal IWorkflowStateRepository NewRepository() => _repository ??= profile == "local-memory"
            ? new InMemoryWorkflowStateRepository()
            : new SqliteWorkflowStateRepository(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = System.IO.Path.Combine(_folder.Path, "catalog.db") }.ConnectionString);

        public async ValueTask DisposeAsync()
        {
            if (_repository is IAsyncDisposable disposable)
                await disposable.DisposeAsync();

            _folder.Dispose();
        }
    }
}