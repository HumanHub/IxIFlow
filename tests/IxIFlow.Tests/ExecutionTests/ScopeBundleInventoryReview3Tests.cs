using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeBundleInventoryReview3Tests
{
    private static readonly Lazy<Task<(InMemoryWorkflowStateRepository Artifacts, WorkflowVersion Version)>> Bundle = new(PublishAsync);

    [Fact]
    public async Task PublishRecordsTheCompleteDefinitionInventoryInTheDigestProtectedManifest()
    {
        var (artifacts, version) = await Bundle.Value;
        var bundle = await ReadBundle(artifacts, version);
        Assert.Equal(4, bundle.Manifest.FormatVersion);
        Assert.Equal(version.Invocations, bundle.Manifest.Invocations);
        Assert.Equal("InventoryChild", Assert.Single(bundle.Manifest.Invocations).Name);
        Assert.Contains("invoke-child", Assert.Single(bundle.Manifest.Invocations).Step);
        var cache = CacheDirectory();
        try
        {
            using var catalog = new WorkflowCatalog(artifacts, artifacts: artifacts, bundles: new(cache));
            using var loaded = await catalog.ResolveWorkflowDefinitionAsync(version.Name, version.Version);
            Assert.Equal(WorkflowDefinitionValidator.GetInvocations(loaded.Definition), bundle.Manifest.Invocations);
            var bytes = WorkflowBundleArchive.Create(bundle.Manifest with { Invocations = [] }, Entries(bundle));
            Assert.NotEqual(version.ArtifactDigest, ArtifactDigest.Of(bytes));
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => WorkflowBundleArchive.ReadAsync(
                new MemoryStream(bytes), version.ArtifactDigest!.Value));
            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true); }
    }

    [Theory]
    [InlineData("missing"), InlineData("null"), InlineData("format-two")]
    public async Task ManifestRequiresTheNewFormatAndAnExplicitInventory(string defect)
    {
        var (artifacts, version) = await Bundle.Value;
        await using var original = (await artifacts.OpenAsync(version.ArtifactDigest!.Value))!;
        using var changed = new MemoryStream();
        await original.CopyToAsync(changed);
        using (var zip = new ZipArchive(changed, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(WorkflowBundleArchive.ManifestPath)!;
            JsonObject manifest;
            using (var stream = entry.Open()) manifest = JsonNode.Parse(stream)!.AsObject();
            if (defect == "missing") manifest.Remove("Invocations");
            else if (defect == "null") manifest["Invocations"] = null;
            else manifest["FormatVersion"] = 2;
            entry.Delete();
            using var output = zip.CreateEntry(WorkflowBundleArchive.ManifestPath).Open();
            JsonSerializer.Serialize(output, manifest);
        }
        var bytes = changed.ToArray();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => WorkflowBundleArchive.ReadAsync(
            new MemoryStream(bytes), ArtifactDigest.Of(bytes)));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
    }

    [Theory]
    [InlineData(false, false), InlineData(false, true)]
    [InlineData(true, false), InlineData(true, true)]
    public Task RegisteredBundleInventoryRefusesRequiringChildrenInEitherRegistrationOrderWithoutOpeningArtifacts(bool sqlite, bool childFirst) =>
        WithStore(sqlite, async (store, _) =>
        {
            var (artifacts, version) = await Bundle.Value;
            var metadataOnly = new MetadataOnlyArtifacts(artifacts);
            using var callerCatalog = new WorkflowCatalog(store);
            using var childCatalog = new WorkflowCatalog(store);
            if (childFirst) await childCatalog.RegisterWorkflowAsync(Child());
            else await callerCatalog.RegisterWorkflowVersionAsync(version, metadataOnly);
            var revision = await store.GetCatalogRevisionAsync();
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => childFirst
                ? callerCatalog.RegisterWorkflowVersionAsync(version, metadataOnly)
                : childCatalog.RegisterWorkflowAsync(Child()));
            Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
            Assert.Contains("invoke-child", error.Message);
            Assert.Contains("InventoryChild", error.Message);
            Assert.Equal(revision, await store.GetCatalogRevisionAsync());
            Assert.Null(await store.GetWorkflowVersionAsync(childFirst ? version.Name : "InventoryChild", 1));
            Assert.Equal(0, metadataOnly.Opens);
            Assert.Empty((await store.QueryInstancesAsync(new())).Items);
        });

    [Theory]
    [InlineData(false, false, false), InlineData(false, false, true)]
    [InlineData(false, true, false), InlineData(false, true, true)]
    [InlineData(true, false, false), InlineData(true, false, true)]
    [InlineData(true, true, false), InlineData(true, true, true)]
    public Task RegistrationAndPublicationUseMetadataAndExistenceChecksOnly(bool sqlite, bool publish, bool placeholder) =>
        WithStore(sqlite, async (store, _) =>
        {
            var (artifacts, version) = await Bundle.Value;
            using var catalog = new WorkflowCatalog(store);
            await catalog.RegisterWorkflowAsync(Workflow.Create<Data>("InventoryChild").Build());
            if (placeholder)
                version = version with { ArtifactDigest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([1, 2, 3])) };
            var metadataOnly = new MetadataOnlyArtifacts(artifacts);
            if (publish)
            {
                var draft = await store.SaveDefinitionAsync(new(version.Name, version.Version, "workflow", "source"), null);
                version = version with { PublishedDraft = draft.Id };
                await store.PublishDefinitionAsync(draft.Id, version, metadataOnly);
                Assert.Null(await store.GetDefinitionAsync("workflow", version.Name, version.Version));
            }
            else await store.RegisterWorkflowVersionAsync(version, metadataOnly);
            Assert.Equal(version.Invocations, (await store.GetWorkflowVersionAsync(version.Name, version.Version))!.Invocations);
            Assert.Equal(0, metadataOnly.Opens);
        });

    [Theory]
    [InlineData(false, "missing"), InlineData(true, "missing")]
    [InlineData(false, "null"), InlineData(true, "null")]
    [InlineData(false, "dependencies"), InlineData(true, "dependencies")]
    public Task RegistrationRefusesInventoryThatDisagreesWithDependenciesWithoutOpeningArtifacts(bool sqlite, string tamper) =>
        WithStore(sqlite, async (store, _) =>
        {
            var (artifacts, version) = await Bundle.Value;
            var changed = tamper switch
            {
                "missing" => version with { Dependencies = [] },
                "null" => version with { Invocations = null! },
                _ => version with { Dependencies = [new("ExtraChild", 1)] }
            };
            var metadataOnly = new MetadataOnlyArtifacts(artifacts);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(changed, metadataOnly));
            Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
            Assert.Equal(0, metadataOnly.Opens);
            Assert.Equal(0, await store.GetCatalogRevisionAsync());
            Assert.Null(await store.GetWorkflowVersionAsync(version.Name, version.Version));
        });

    [Theory]
    [InlineData(false, false, "row"), InlineData(false, true, "row")]
    [InlineData(true, false, "row"), InlineData(true, true, "row")]
    [InlineData(false, false, "manifest-and-row"), InlineData(false, true, "manifest-and-row")]
    [InlineData(true, false, "manifest-and-row"), InlineData(true, true, "manifest-and-row")]
    public Task EmptyForgedInventoryIsRefusedAtHostStartInEitherRegistrationOrder(bool sqlite, bool childFirst, string target) =>
        WithStore(sqlite, async (store, _) =>
        {
            var (artifacts, version) = await Bundle.Value;
            if (target == "manifest-and-row") version = await ChangeManifest(artifacts, version, []);
            version = version with { Invocations = [], Dependencies = [] };
            using var catalog = new WorkflowCatalog(store);
            if (childFirst) await catalog.RegisterWorkflowAsync(Child());
            await store.RegisterWorkflowVersionAsync(version, new MetadataOnlyArtifacts(artifacts));
            if (!childFirst) await catalog.RegisterWorkflowAsync(Child());
            await RefuseHostStart(store, artifacts, version);
        });

    [Theory]
    [InlineData(false, "manifest", "step"), InlineData(true, "manifest", "step")]
    [InlineData(false, "row", "step"), InlineData(true, "row", "step")]
    [InlineData(false, "manifest-and-row", "step"), InlineData(true, "manifest-and-row", "step")]
    [InlineData(false, "manifest-and-row", "name"), InlineData(true, "manifest-and-row", "name")]
    [InlineData(false, "manifest-and-row", "version"), InlineData(true, "manifest-and-row", "version")]
    public Task ForgedInvocationIdentitiesAreRefusedBeforeAnyParentStep(bool sqlite, string target, string identity) =>
        WithStore(sqlite, async (store, _) =>
        {
            var (artifacts, version) = await Bundle.Value;
            var invocation = Assert.Single(version.Invocations);
            invocation = identity switch
            {
                "step" => invocation with { Step = "forged-step" },
                "name" => invocation with { Name = "ForgedChild" },
                _ => invocation with { Version = 2 }
            };
            if (target != "row") version = await ChangeManifest(artifacts, version, [invocation]);
            if (target != "manifest") version = version with
            {
                Invocations = [invocation], Dependencies = [new(invocation.Name, invocation.Version)]
            };
            await store.RegisterWorkflowVersionAsync(version, new MetadataOnlyArtifacts(artifacts));
            await RefuseHostStart(store, artifacts, version);
        });

    [Theory, InlineData(false), InlineData(true)]
    public Task ExecutableAcquisitionChecksTheFullDependencyList(bool sqlite) => WithStore(sqlite, async (store, _) =>
    {
        var (artifacts, version) = await Bundle.Value;
        version = version with { Dependencies = [.. version.Dependencies!, new("ExtraChild", 1)] };
        await store.RegisterWorkflowVersionAsync(version, new MetadataOnlyArtifacts(artifacts));
        await RefuseHostStart(store, artifacts, version);
    });

    [Theory, InlineData(false), InlineData(true)]
    public Task ExecutableAcquisitionRejectsATamperedEmptyPersistedInventory(bool sqlite) => WithStore(sqlite, async (store, connectionString) =>
    {
        var (artifacts, version) = await Bundle.Value;
        await store.RegisterWorkflowVersionAsync(version, artifacts);
        var forged = version with { Invocations = [], Dependencies = [] };
        if (sqlite)
        {
            await using var connection = new SqliteConnection(connectionString + ";Pooling=False");
            await connection.ExecuteAsync("UPDATE ixiflow_workflow_versions SET metadata_json=@Json WHERE name=@Name AND version=@Version",
                new { Json = JsonSerializer.Serialize(forged), version.Name, version.Version });
        }
        else
        {
            var rows = (Dictionary<(string, int), WorkflowVersion>)typeof(InMemoryWorkflowStateRepository)
                .GetField("_versions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            rows[(version.Name, version.Version)] = forged;
        }
        await RefuseHostStart(store, artifacts, version);
    });

    private static async Task RefuseHostStart(IWorkflowStateRepository store, IArtifactStore artifacts, WorkflowVersion version)
    {
        var cache = CacheDirectory();
        var effect = Path.Combine(cache, "parent-effect");
        try
        {
            await using var host = PublishedBundleScenario.Host(store, cache, artifacts: artifacts);
            var catalog = host.GetRequiredService<IWorkflowCatalog>();
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.ResolveWorkflowDefinitionAsync(version.Name, version.Version));
            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
            Assert.Contains("invocation inventory", error.Message);
            error = await Assert.ThrowsAsync<WorkflowEngineException>(() => host.GetRequiredService<IWorkflowClient>()
                .StartAsync(version.Name, version.Version, new Data { EffectPath = effect }));
            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
            Assert.False(File.Exists(effect));
            Assert.Empty((await store.QueryInstancesAsync(new())).Items);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true); }
    }

    private static async Task<WorkflowVersion> ChangeManifest(IArtifactStore artifacts, WorkflowVersion version,
        IReadOnlyList<WorkflowVersionInvocation> invocations)
    {
        var bundle = await ReadBundle(artifacts, version);
        var manifest = bundle.Manifest with { Invocations = invocations };
        var bytes = WorkflowBundleArchive.Create(manifest, Entries(bundle));
        var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        var verified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes), digest);
        return version with { ArtifactDigest = digest, ManifestJson = JsonSerializer.Serialize(verified.Manifest) };
    }

    private static async Task<WorkflowBundle> ReadBundle(IArtifactStore artifacts, WorkflowVersion version)
    {
        await using var stream = (await artifacts.OpenAsync(version.ArtifactDigest!.Value))!;
        return await WorkflowBundleArchive.ReadAsync(stream, version.ArtifactDigest.Value);
    }
    private static Dictionary<string, byte[]> Entries(WorkflowBundle bundle) =>
        bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    private static string CacheDirectory() => Path.Combine(Path.GetTempPath(), "IxIFlow.InventoryAcquisition", Guid.NewGuid().ToString("N"));

    private static async Task<(InMemoryWorkflowStateRepository, WorkflowVersion)> PublishAsync()
    {
        var source = new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(source);
        await catalog.RegisterWorkflowAsync(Workflow.Create<Data>("InventoryChild").Build());
        var draft = await source.SaveDefinitionAsync(new("InventoryCaller", 1, "workflow", """
            workflow: {name: InventoryCaller, dataType: Data}
            definitions:
              - kind: activity
                activity: Effect
                input: [{target: Path, from: {kind: path, path: workflow.EffectPath}}]
              - kind: invokeWorkflow
                id: invoke-child
                workflow: {name: InventoryChild, version: 1}
            """), null);
        var result = await new WorkflowPublisher(catalog, source, new()
        { SourceOptions = YamlTestContracts.Create(("Data", typeof(Data)), ("Effect", typeof(Effect))) }).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Single(result.Workflow!.Invocations);
        return (source, result.Workflow);
    }
    private static WorkflowDefinition Child() => Workflow.Create<Data>("InventoryChild").RequiresScope<Value>().Build();
    private static async Task WithStore(bool sqlite, Func<IWorkflowStateRepository, string?, Task> test)
    {
        if (!sqlite) { await test(new InMemoryWorkflowStateRepository(), null); return; }
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.InventoryReview3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "catalog.db");
        try
        {
            await using var store = new SqliteWorkflowStateRepository(connectionString);
            await test(store, connectionString);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }
    private sealed class MetadataOnlyArtifacts(IArtifactStore inner) : IArtifactStore
    {
        public int Opens;
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Opens); throw new InvalidOperationException("Registration must not open artifacts"); }
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.ExistsAsync(digest, cancellationToken);
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) => inner.PutAsync(kind, content, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
            inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
    public sealed class Data { public string EffectPath { get; set; } = ""; }
    public sealed class Value;
    public sealed class Effect : IAsyncActivity
    {
        [ActivityInput] public string Path { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => File.WriteAllTextAsync(Path, "parent ran", cancellationToken);
    }
}
