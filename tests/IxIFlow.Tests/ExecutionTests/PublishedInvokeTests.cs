using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-18")]
[Trait("Spec", "F-10")]
public sealed class PublishedInvokeTests
{
    private static WorkflowPublisherOptions Options() => new()
    {
        SourceOptions = new() { Types = { ["Data"] = "global::" + typeof(FirstData).FullName } }
    };

    private static async Task<WorkflowVersion> Child(IWorkflowStateRepository store, int version = 1, string? lists = "input: [Value]\n  output: [Value]")
    {
        var yaml = $"workflow:\n  name: Callee\n  version: {version}\n  dataType: Data\n  {lists}\ndefinitions: []";
        return await Publish(store, "Callee", version, yaml, Options());
    }

    private static string Caller(string? version = null, string imports = "", string data = "Data", string name = "Caller") => $$$"""
        workflow: {name: {{{name}}}, version: 1, dataType: {{{data}}} }
        {{{imports}}}
        definitions:
          - kind: invokeWorkflow
            id: child
            workflow: {name: Callee{{{(version == null ? "" : ", version: " + version)}}} }
            input: [{target: Value, from: {kind: path, path: workflow.Value}}]
            output: [{source: Value, to: {path: workflow.Result}}]
        """;

    private static async Task<WorkflowVersion> Publish(IWorkflowStateRepository store, string name, int version, string yaml, WorkflowPublisherOptions? options = null)
    {
        var draft = await store.SaveDefinitionAsync(new(name, version, "workflow", yaml), 0);
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return result.Version!;
    }

    private static async Task<WorkflowBundle> Bundle(IWorkflowStateRepository store, WorkflowVersion version)
    {
        await using var stream = (await store.OpenAsync(version.ArtifactDigest!.Value))!;
        return await WorkflowBundleArchive.ReadAsync(stream, version.ArtifactDigest.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishPinsLatestPublishedAndExplicitDeprecatedVersions(bool deprecated)
    {
        var store = new InMemoryWorkflowStateRepository();
        await Child(store);
        await Child(store, 2);
        await store.SetWorkflowVersionStateAsync("Callee", 2, WorkflowVersionState.Deprecated);
        var version = await Publish(store, "Caller", 1, Caller(deprecated ? "2" : null, "imports: {workflows: [Callee]}"), Options());
        var dependency = Assert.Single(version.Dependencies!);
        Assert.Equal(deprecated ? 2 : 1, dependency.Version);
        Assert.Equal(WorkflowDependencyKind.Child, dependency.Kind);
        var bundle = await Bundle(store, version);
        var yaml = Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span);
        Assert.Contains("Callee@1", yaml);
        if (deprecated) Assert.Contains("Callee@2", yaml);
        var source = Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.SourcePath].Span);
        Assert.Contains("WorkflowContract.Parse(", source);
        Assert.DoesNotContain(bundle.Entries.Keys, path => path.StartsWith("inline/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("input: [Missing]", "WF005")]
    [InlineData("input: [Value], output: [Missing]", "WF005")]
    public async Task PublishReportsListErrorsBeforeCompilation(string lists, string code)
    {
        var store = new InMemoryWorkflowStateRepository();
        var yaml = "workflow: {name: Bad, dataType: Data, " + lists + "}\ndefinitions: []";
        var draft = await store.SaveDefinitionAsync(new("Bad", 1, "workflow", yaml), 0);
        var result = await new WorkflowPublisher(store, store, Options()).PublishAsync(draft.Id);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, error => error.Code == code && error.Line == 1);
        Assert.Equal(draft, await store.GetDefinitionAsync("Bad", 1));
    }

    [Theory]
    [InlineData("input: []\n  output: [Value]", "MAP004", 7)]
    [InlineData("input: [Value]\n  output: []", "MAP005", 8)]
    public async Task PublishUsesTheCatalogListsForMappingErrors(string lists, string code, int line)
    {
        var store = new InMemoryWorkflowStateRepository();
        await Child(store, lists: lists);
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller("1")), 0);
        var result = await new WorkflowPublisher(store, store, Options()).PublishAsync(draft.Id);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(code, error.Code);
        Assert.Equal(line, error.Line);
        Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAndColdLoadExecuteWithTheCatalogContract(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.PublishedInvoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IWorkflowStateRepository store = sqlite ? new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(root, "state.db")) : new InMemoryWorkflowStateRepository();
        try
        {
            var child = await Child(store);
            var caller = await Publish(store, "Caller", 1, Caller(), Options());
            await using (var host = PublishedBundleScenario.Host(store, Path.Combine(root, "cache")))
            {
                using var definition = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync("Caller", 1);
                Assert.Equal(child.ContractJson, Assert.Single(definition.Definition.Steps).InvokedContract!.ToJson());
                var client = host.GetRequiredService<IWorkflowClient>();
                var result = await client.AwaitAsync(await client.StartAsync("Caller", 1, new FirstData { Value = "pinned" }), WorkflowAwaitCondition.Finished);
                Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
                Assert.Equal("pinned", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
                Assert.Equal(caller.ArtifactDigest, (await store.GetWorkflowInstanceAsync(result.InstanceId))!.BundleDigest);
            }
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SetWorkflowVersionStateAsync("Callee", 1, WorkflowVersionState.Retired));
            Assert.Contains("required by caller", error.Message);
            await store.SetWorkflowVersionStateAsync("Caller", 1, WorkflowVersionState.Retired);
            await store.SetWorkflowVersionStateAsync("Callee", 1, WorkflowVersionState.Retired);
        }
        finally
        {
            if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishUnionsPackageReferencesBeforeResolvingTheCallerDataType()
    {
        var store = new InMemoryWorkflowStateRepository();
        var packaged = await ActivityPackageFixture.PublishAsync(store);
        var yaml = Caller("1", data: "Packaged.Data").Replace("name: Callee", "name: PackageLifecycle");
        var caller = await Publish(store, "Caller", 1, yaml);
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(caller.ManifestJson!)!;
        var expected = JsonSerializer.Deserialize<WorkflowBundleManifest>(packaged.ManifestJson!)!;
        Assert.Equal(JsonSerializer.Serialize(expected.ActivityPackages), JsonSerializer.Serialize(manifest.ActivityPackages));
        Assert.Equal(expected.ActivityPackages.Select(pin => pin.Digest), caller.ArtifactReferences!);
        var bundle = await Bundle(store, caller);
        Assert.DoesNotContain(bundle.Entries.Keys, path => path.Contains("Package.Activities", StringComparison.Ordinal));
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.PublishedInvoke", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = PublishedBundleScenario.Host(store, root);
            using var loaded = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync("Caller", 1);
            Assert.Equal("Packaged.Data", loaded.Definition.WorkflowDataType!.FullName);
            Assert.Same(loaded.Definition.WorkflowDataType, Assert.Single(loaded.Definition.Steps).InvokedDataType);
            Assert.DoesNotContain(System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == "Package.Activities");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PublishPreservesCalleeRidSelectionsAndPromotesCompileReferences()
    {
        var store = new InMemoryWorkflowStateRepository();
        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.NativeActivityPackage()));
        var resolver = new WorkflowPackageResolver(store, store, new());
        using var callee = await resolver.ResolveAsync(new List<string> { "fixture.native-call@1.0.0" }, CancellationToken.None);
        var original = Assert.Single(callee.Pins);
        Assert.True(original.RequiresRuntimeIdentifier);
        using var caller = new ResolvedWorkflowPackages();
        caller.Add(original, callee.Contents[original.Id], compile: false);
        Assert.Empty(caller.References);
        await resolver.UnionAsync(caller, callee.Pins, new DocumentLocation(1, 1), CancellationToken.None);
        var merged = Assert.Single(caller.Pins);
        Assert.Equal(JsonSerializer.Serialize(original.RuntimeAssets), JsonSerializer.Serialize(merged.RuntimeAssets));
        Assert.Equal(original.RequiresRuntimeIdentifier, merged.RequiresRuntimeIdentifier);
        Assert.Equal(original.ApplicableRuntimeIdentifiers, merged.ApplicableRuntimeIdentifiers);
        Assert.Equal(original.ReferenceAssemblies.Count, caller.References.Count);
        Assert.All(caller.References, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task PublishRejectsConflictingCalleeAndCallerPackagePins()
    {
        var store = new InMemoryWorkflowStateRepository();
        await ActivityPackageFixture.PublishAsync(store);
        var fixtures = ActivityPackageFixture.Create(2);
        var publisher = new WorkflowPublisher(store, store);
        await publisher.UploadPackageAsync(new MemoryStream(fixtures.Activity));
        await publisher.UploadPackageAsync(new MemoryStream(fixtures.Dependency));
        var yaml = Caller("1", "imports: {catalogs: [fixture.activities@2.0.0]}", "Packaged.Data").Replace("name: Callee", "name: PackageLifecycle");
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", yaml), 0);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.Contains(result.Diagnostics, error => error.Code == "PUB010" && error.Message.Contains("fixture.activities"));
        Assert.Null(result.Version);
        Assert.Equal(draft, await store.GetDefinitionAsync("Caller", 1));
    }

    [Fact]
    public async Task PublishResolvesCodeRegisteredCalleeContracts()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(store);
        var child = Workflow.Create<FirstData>("Callee").Input(data => data.Value).Output(data => data.Value).Build();
        await catalog.RegisterWorkflowAsync(child);
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller()), 0);
        var result = await new WorkflowPublisher(catalog, store, Options()).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(new WorkflowVersionDependency("Callee", 1, WorkflowDependencyKind.Child), Assert.Single(result.Version!.Dependencies!));
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("framework")]
    [InlineData("shared")]
    public async Task PublishRejectsIncompatibleCalleeProfiles(string profile)
    {
        var store = new InMemoryWorkflowStateRepository();
        var original = await Child(store);
        var bundle = await Bundle(store, original);
        var manifest = bundle.Manifest with { Name = "Incompatible" };
        manifest = profile switch
        {
            "engine" => manifest with { EngineVersion = "99.0.0.0" },
            "framework" => manifest with { TargetFramework = "net99.0" },
            _ => manifest with { SharedAssemblies = new Dictionary<string, string> { ["System.Runtime, Version=99.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"] = "99.0.0.0" } }
        };
        var bytes = WorkflowBundleArchive.Create(manifest, bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        var stored = await WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes), digest);
        await store.RegisterWorkflowVersionAsync(original with
        {
            Name = "Incompatible", ArtifactDigest = digest, PublishedDraftRevision = null, ManifestJson = JsonSerializer.Serialize(stored.Manifest)
        });
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller("1").Replace("name: Callee", "name: Incompatible")), 0);
        var result = await new WorkflowPublisher(store, store, Options()).PublishAsync(draft.Id);
        Assert.Contains(result.Diagnostics, error => error.Code == "PUB009" && error.Message.Contains("republish the callee"));
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
    }

    [Theory]
    [InlineData(WorkflowVersionState.Draft)]
    [InlineData(WorkflowVersionState.Retired)]
    public async Task PublishRefusesUnstartableCatalogCallees(WorkflowVersionState state)
    {
        var store = new InMemoryWorkflowStateRepository();
        var contract = WorkflowContract.OfType(typeof(FirstData));
        await store.RegisterWorkflowVersionAsync(new("Callee", 1, "hash", contract.ToJson(), contract.Hash, state));
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller("1")), 0);
        var result = await new WorkflowPublisher(store, store, Options()).PublishAsync(draft.Id);
        Assert.Contains(result.Diagnostics, error => error.Code == "STEP011" && error.Line == 6);
        Assert.Equal(draft, await store.GetDefinitionAsync("Caller", 1));
    }

    [Fact]
    public async Task DependencyKindsAreDurableImmutableAndValidated()
    {
        var store = new InMemoryWorkflowStateRepository();
        var child = await Child(store);
        var caller = await Publish(store, "Caller", 1, Caller(), Options());
        var saved = JsonSerializer.Deserialize<WorkflowVersion>(JsonSerializer.Serialize(caller))!;
        Assert.Equal(WorkflowDependencyKind.Child, Assert.Single(saved.Dependencies!).Kind);
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(caller with { Dependencies = [] }));
        Assert.Contains("immutable", error.Message);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RegisterWorkflowVersionAsync(child with
        {
            Name = "InvalidKind", Dependencies = [new("Callee", 1, (WorkflowDependencyKind)42)]
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildDependencyStillProtectsUnfinishedInstancesOfRetiredCallers(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.PublishedInvoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IWorkflowStateRepository store = sqlite ? new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(root, "state.db")) : new InMemoryWorkflowStateRepository();
        try
        {
            await Child(store);
            var caller = await Publish(store, "Caller", 1, Caller(), Options());
            var instance = new WorkflowInstance
            {
                InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Caller", WorkflowVersion = 1,
                Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
                WorkflowDataJson = StoredJson.Inline("{}"), WorkflowDataType = WorkflowTypeIdentity.SavedName(typeof(FirstData)), BundleDigest = caller.ArtifactDigest
            };
            await store.SeedAsync(instance, wakeAfter: TimeSpan.Zero);
            await store.SetWorkflowVersionStateAsync("Caller", 1, WorkflowVersionState.Retired);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SetWorkflowVersionStateAsync("Callee", 1, WorkflowVersionState.Retired));
            Assert.Contains("unfinished parent instances of Caller v1", error.Message);
            instance.Status = WorkflowStatus.Completed;
            await store.SeedAsync(instance);
            await store.SetWorkflowVersionStateAsync("Callee", 1, WorkflowVersionState.Retired);
        }
        finally
        {
            if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "child default")]
    [InlineData(true, "mapped")]
    public async Task PublishedInvokeUsesPinnedDefaultsBeforeStartingTheChild(bool mapped, string expected)
    {
        var store = new InMemoryWorkflowStateRepository();
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = "global::" + typeof(WorkflowContractData).FullName;
        options.SourceOptions.Types["Echo"] = "global::" + typeof(Echo).FullName;
        await Publish(store, "Callee", 1, """
            workflow: {name: Callee, dataType: Data}
            definitions:
              - kind: activity
                activity: Echo
                input: [{target: Value, from: {path: workflow.Value}}]
                output: [{source: Value, to: {path: workflow.Result}}]
            """, options);
        var yaml = Caller("1").Replace("output: [{source: Value", "output: [{source: Result");
        if (!mapped) yaml = yaml.Replace("input: [{target: Value, from: {kind: path, path: workflow.Value}}]", "input: []");
        await Publish(store, "Caller", 1, yaml, Options());
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.PublishedInvoke", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = PublishedBundleScenario.Host(store, root);
            var client = host.GetRequiredService<IWorkflowClient>();
            var result = await client.AwaitAsync(await client.StartAsync("Caller", 1, new FirstData { Value = "mapped" }), WorkflowAwaitCondition.Finished);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(expected, result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(typeof(Dictionary<string, List<int[]>>))]
    [InlineData(typeof(GenericData<string>.Nested<int>))]
    public void PublishedCalleeTypeIdentitiesResolveThroughMetadata(Type type)
    {
        var references = WorkflowCompilation.CreateReferences(new());
        var name = references.SavedTypeName(WorkflowTypeIdentity.SavedName(type));
        var compiled = WorkflowCompilation.Compile("public class Holder { public " + name + " Value { get; set; } }", references.Paths);
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
    }

    public sealed class GenericData<T>
    {
        public sealed class Nested<U>;
    }
}
