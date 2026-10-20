using System.Reflection;
using System.Text.Json;
using Dapper;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "F-12")]
[Trait("Spec", "F-20")]
[Trait("Spec", "F-11")]
public sealed class PublishedActivityPackageScenarios
{
    public static TheoryData<string, string> MissingSchema
    {
        get
        {
            var result = new TheoryData<string, string>();
            foreach (var profile in StoreProfiles.ProfileNames.Where(profile => profile != StoreProfiles.LocalMemory))
                foreach (var part in new[] { "table", "id", "version", "digest", "metadata_json", "dependency_groups_json" }) result.Add(profile, part);
            return result;
        }
    }

    [Theory]
    [MemberData(nameof(MissingSchema))]
    public async Task ExistingDatabaseRequiresThePackageTableAndEveryColumn(string profile, string part)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var first = database.NewRepository();
        Assert.Empty(await first.GetPackagesAsync());
        if (first is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await using var connection = database.NewConnection();
        var ms = profile.StartsWith(StoreProfiles.DistributedSqlServer, StringComparison.Ordinal);
        var table = ms ? "dbo.IxIFlowPackages" : "ixiflow_packages";
        var column = ms ? part switch { "metadata_json" => "MetadataJson", "dependency_groups_json" => "DependencyGroupsJson", "id" => "Id", "version" => "Version", _ => "Digest" } : part;
        await connection.ExecuteAsync(part == "table" ? $"DROP TABLE {table}"
            : ms ? $"EXEC sp_rename '{table}.{column}', 'OldPackageColumn', 'COLUMN'"
            : $"ALTER TABLE {table} RENAME COLUMN {column} TO old_package_column");
        var reopened = database.NewRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetPackagesAsync());
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("must be recreated", error.Message);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DependencyMetadataAndBoundedIdentityQueriesHaveStoreParity(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store);
        foreach (var version in new[] { "1.0.0", "2.0.0", "3.0.0" })
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.metadata", version, new(),
                "<dependency id=\"fixture.child\" version=\"[1.0.0,2.0.0)\"/>")));
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var reopened = database.NewRepository();
        var exact = (await reopened.GetPackageAsync("FIXTURE.METADATA", "2.0.0"))!;
        Assert.Equal("2.0.0", exact.Version);
        var dependency = Assert.Single(Assert.Single(exact.DependencyGroups).Dependencies);
        Assert.Equal("fixture.child", dependency.Id);
        Assert.Equal("[1.0.0, 2.0.0)", dependency.VersionRange);
        Assert.Null(await reopened.GetPackageAsync("fixture.metadata", "4.0.0"));
        var first = Assert.Single(await reopened.GetPackagePageAsync("FIXTURE.METADATA", null, 1));
        Assert.Equal("1.0.0", first.Version);
        Assert.Equal(new[] { "2.0.0", "3.0.0" }, (await reopened.GetPackagePageAsync("fixture.metadata", first.Version, 2)).Select(row => row.Version));
        Assert.Empty(await reopened.GetPackagePageAsync("fixture.metadata", "3.0.0", 1));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.RegisterPackageAsync(exact with { DependencyGroups = [] }, reopened));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        foreach (var version in new[] { "1.0.0-01", "1.0.0-a.00", "1.0.0-BETA" })
        {
            error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.RegisterPackageAsync(exact with { Version = version }, reopened));
            Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        }
        error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.RegisterPackageAsync(exact with { DependencyGroups = null! }, reopened));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PackageActivityRunsAndRecoversOnAFreshHost(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await ActivityPackageFixture.CrossHostAsync(() => store.NewRepository(), Folder(), store.ConfigureHost, store.StartHostLinksAsync);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PackageCatalogSurvivesReopenAndRefusesDifferentContentForAnIdentity(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store);
        var fixture = ActivityPackageFixture.Create();
        var package = await publisher.UploadPackageAsync(new MemoryStream(fixture.Activity));
        Assert.Equal(package.Digest, (await publisher.UploadPackageAsync(new MemoryStream(fixture.Activity))).Digest);
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var reopened = database.NewRepository();
        publisher = new WorkflowPublisher(reopened, reopened);
        var saved = Assert.Single(await reopened.GetPackagesAsync("FIXTURE.ACTIVITIES"));
        Assert.Equal(package.Id, saved.Id);
        Assert.Equal(package.Version, saved.Version);
        Assert.Equal(package.Digest, saved.Digest);
        Assert.Equal(package.Entries.OrderBy(pair => pair.Key), saved.Entries.OrderBy(pair => pair.Key));
        var different = ActivityPackageFixture.Package("fixture.activities", "1.0.0", new() { ["extra.txt"] = [1] });
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => publisher.UploadPackageAsync(new MemoryStream(different)));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(package.Digest, Assert.Single(await reopened.GetPackagesAsync()).Digest);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RetentionKeepsPublishedAndInstancePinnedPackagesAndCollectsAbandonedUploads(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var root = Folder();
        var files = new FileSystemArtifactStore(root);
        var publisher = new WorkflowPublisher(store, files);
        var fixture = ActivityPackageFixture.Create();
        await publisher.UploadPackageAsync(new MemoryStream(fixture.Activity));
        await publisher.UploadPackageAsync(new MemoryStream(fixture.Dependency));
        var abandoned = await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.orphan", "1.0.0", new())));
        var draft = await store.SaveDefinitionAsync(new("PackageLifecycle", 1, "workflow", ActivityPackageFixture.Yaml()), 0);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var version = result.Version!;
        var packages = JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!.ActivityPackages;
        foreach (var file in Directory.EnumerateFiles(root, "*.artifact")) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-100));
        Assert.Equal(1, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
        Assert.False(await files.ExistsAsync(abandoned.Digest));
        foreach (var pin in packages) Assert.True(await files.ExistsAsync(pin.Digest));
        await store.SeedAsync(new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = version.Name, WorkflowVersion = 1,
            BundleDigest = version.ArtifactDigest, Status = WorkflowStatus.Completed,
            WorkflowDataJson = StoredJson.Inline("{}"), ExecutionStateJson = StoreTestSupport.CheckpointJson
        });
        // Disaster fixture: retained instances must protect the closure even if catalog metadata is lost.
        if (store is InMemoryWorkflowStateRepository memory)
        {
            var versions = (Dictionary<(string, int), WorkflowVersion>)typeof(InMemoryWorkflowStateRepository)
                .GetField("_versions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
            versions.Clear();
        }
        else
        {
            await using var connection = database.NewConnection();
            await connection.ExecuteAsync(profile.StartsWith("distributed-sqlserver", StringComparison.Ordinal)
                ? "DELETE FROM dbo.IxIFlowWorkflowVersions" : "DELETE FROM ixiflow_workflow_versions");
        }
        Assert.Equal(0, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
        foreach (var pin in packages) Assert.True(await files.ExistsAsync(pin.Digest));
        Assert.True(await files.ExistsAsync(version.ArtifactDigest!.Value));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ConcurrentDifferentUploadsHaveExactlyOneImmutableWinner(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        Assert.Empty(await store.GetPackagesAsync());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packages = new[] { ActivityPackageFixture.Package("fixture.concurrent", "1.0.0-Beta", new() { ["first.txt"] = [1] }),
            ActivityPackageFixture.Package("fixture.concurrent", "1.0.0-beta", new() { ["second.txt"] = [2] }) };
        var tasks = packages.Select(bytes => Task.Run(async () =>
        {
            await gate.Task;
            try { return (Package: await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(bytes)), Error: (WorkflowEngineException?)null); }
            catch (WorkflowEngineException error) { return (Package: (WorkflowPackage?)null, Error: error); }
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        var winner = Assert.Single(results, result => result.Package != null).Package!;
        Assert.Equal(WorkflowErrorKind.InvalidRequest, Assert.Single(results, result => result.Error != null).Error!.Kind);
        var saved = Assert.Single(await store.GetPackagesAsync("FIXTURE.CONCURRENT"));
        Assert.Equal("1.0.0-beta", saved.Version);
        Assert.Equal(winner.Digest, saved.Digest);
        Assert.Equal(winner.Entries.OrderBy(pair => pair.Key), saved.Entries.OrderBy(pair => pair.Key));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task UnreadableOrphanPinsReportAndPreservePackagesWhileUnrelatedBundlesAreCollected(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var root = Folder();
        var files = new FileSystemArtifactStore(root);
        var publisher = new WorkflowPublisher(store, files);
        var package = await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.conservative", "1.0.0", new())));
        var missing = ArtifactDigest.Of([91]);
        await store.SeedAsync(new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "LostCatalog", WorkflowVersion = 1,
            BundleDigest = null, Status = WorkflowStatus.Completed,
            WorkflowDataJson = StoredJson.Inline("{}"), ExecutionStateJson = StoreTestSupport.CheckpointJson
        });
        // Seed's normal catalog guard refuses an unregistered bundle. Model catalog loss
        // with the same persisted pin fixture used by the existing retention scenarios.
        if (store is InMemoryWorkflowStateRepository memory)
        {
            var instances = typeof(InMemoryWorkflowStateRepository).GetField("_instances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
            var values = (IEnumerable<WorkflowInstance>)instances.GetType().GetProperty("Values")!.GetValue(instances)!;
            Assert.Single(values).BundleDigest = missing;
        }
        else
        {
            await using var connection = database.NewConnection();
            await connection.ExecuteAsync(profile.StartsWith("distributed-sqlserver", StringComparison.Ordinal)
                ? "UPDATE dbo.IxIFlowWorkflowInstances SET BundleDigest=@Digest" : "UPDATE ixiflow_workflow_instances SET bundle_digest=@Digest", new { Digest = missing.Value });
        }
        var abandoned = await files.PutAsync(ArtifactKind.Bundle, new MemoryStream([92]));
        foreach (var file in Directory.EnumerateFiles(root, "*.artifact")) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-100));
        long unreadable = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        { if (instrument.Name == "ixiflow.artifact.retention.unreadable_pin") current.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => Interlocked.Add(ref unreadable, value));
        listener.Start();
        Assert.Equal(1, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
        Assert.True(await files.ExistsAsync(package.Digest));
        Assert.False(await files.ExistsAsync(abandoned));
        Assert.True(Interlocked.Read(ref unreadable) > 0);
    }

    private static string Folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "IxIFlow.PackageConformance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
