using Dapper;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-01")]
public sealed class ActivityCatalogStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAvailabilityIsForwardedThroughTestAndGuardedDecorators(bool sqlite)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.DecoratedCatalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        IWorkflowStateRepository inner = sqlite ? new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "state.db") + ";Pooling=False") : new InMemoryWorkflowStateRepository();
        try
        {
            var decorated = GuardedStateRepository.Wrap(new FaultInjectingRepository(inner));
            var files = new FileSystemArtifactStore(Path.Combine(folder, "files"));
            using var catalog = new WorkflowCatalog(decorated, artifacts: files);
            Assert.Same(files, ((IWorkflowCatalogStore)inner).CatalogArtifacts);
            Assert.Same(files, catalog.CatalogArtifacts);
            var digest = await files.PutAsync(ArtifactKind.Package, new MemoryStream([1]));
            var descriptor = ActivityDescriptor.OfType(typeof(FileActivity), "decorated.activity", "1.0.0");
            await catalog.RegisterPackageAsync(new(descriptor.PackageId, descriptor.PackageVersion, digest,
                new Dictionary<string, ArtifactDigest> { ["fixture.nuspec"] = digest }, []), new([descriptor], PackageExtraction.Indexed, null, [], []), files);
            Assert.True(Assert.Single((await catalog.ListPackagesAsync(new(CatalogScope.Unrestricted, ArtifactPresent: true))).Items).ArtifactPresent);
            var activity = Assert.Single((await catalog.ListActivitiesAsync(new(CatalogScope.Unrestricted, ArtifactPresent: true))).Items);
            var provider = new ActivityProviderSelector(descriptor.PackageId, descriptor.PackageVersion, digest.Value);
            Assert.True((await catalog.GetActivityAsync(descriptor.Key, descriptor.Version, provider, CatalogScope.Unrestricted))!.Activity.ArtifactPresent);
            Assert.True((await catalog.GetPackageDetailAsync(descriptor.PackageId, descriptor.PackageVersion, CatalogScope.Unrestricted))!.Summary.ArtifactPresent);
            await files.DeleteAsync(digest);
            Assert.Empty((await catalog.ListActivitiesAsync(new(CatalogScope.Unrestricted, ArtifactPresent: true))).Items);
            Assert.Empty((await catalog.ListPackagesAsync(new(CatalogScope.Unrestricted, ArtifactPresent: true))).Items);
            Assert.False((await catalog.GetActivityAsync(activity.Descriptor.Key, descriptor.Version, provider, CatalogScope.Unrestricted))!.Activity.ArtifactPresent);
        }
        finally
        {
            if (inner is IAsyncDisposable disposable) await disposable.DisposeAsync();
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAvailabilityIsCurrentAndFilteringContinuesPastAbsentBatches(bool sqlite)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.CatalogFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var files = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        IWorkflowStateRepository store = sqlite ? new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "state.db") + ";Pooling=False") : new InMemoryWorkflowStateRepository();
        try
        {
            using var catalog = new WorkflowCatalog(store, artifacts: files);
            var first = await files.PutAsync(ArtifactKind.Package, new MemoryStream([1]));
            var last = await files.PutAsync(ArtifactKind.Package, new MemoryStream([2]));
            for (var index = 0; index < 140; index++)
            {
                var id = "files.p" + index.ToString("D3");
                var descriptor = ActivityDescriptor.OfType(typeof(FileActivity), id, "1.0.0") with { Key = "files." + index.ToString("D3") };
                await catalog.RegisterPackageAsync(new(id, "1.0.0", index < 139 ? first : last,
                    new Dictionary<string, ArtifactDigest> { ["file.nuspec"] = first }, []), new([descriptor], PackageExtraction.Indexed, null, [], []), files);
            }
            await files.DeleteAsync(first);
            var revision = await catalog.GetCatalogRevisionAsync();
            var packages = await catalog.ListPackagesAsync(new(CatalogScope.Unrestricted, ArtifactPresent: true, Limit: 1));
            Assert.Equal("files.p139", Assert.Single(packages.Items).Id);
            Assert.Null(packages.NextCursor);
            var activities = await catalog.ListActivitiesAsync(new(CatalogScope.Unrestricted, ArtifactPresent: true, Limit: 1));
            Assert.Equal("files.139", Assert.Single(activities.Items).Descriptor.Key);
            Assert.Null(activities.NextCursor);
            Assert.Equal(revision, packages.CatalogRevision);
            await files.DeleteAsync(last);
            Assert.Empty((await catalog.ListActivitiesAsync(new(CatalogScope.Unrestricted, ArtifactPresent: true))).Items);
            Assert.False((await catalog.GetPackageDetailAsync("files.p139", "1.0.0", CatalogScope.Unrestricted))!.Summary.ArtifactPresent);
            Assert.Equal(revision, await catalog.GetCatalogRevisionAsync());
            Assert.Single((await catalog.ListPackageVersionsAsync("files.p139", new(CatalogScope.Unrestricted))).Items);
        }
        finally
        {
            if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("extraction")]
    [InlineData("closure_json")]
    [InlineData("catalog_revision")]
    [InlineData("registered_by")]
    public async Task SqliteRejectsEarlierPackageSchemas(string column)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.CatalogSchema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "state.db") + ";Pooling=False";
        try
        {
            await using (var store = new SqliteWorkflowStateRepository(connectionString)) await store.GetCatalogRevisionAsync();
            await using (var connection = new SqliteConnection(connectionString))
            {
                if (column == "origin") await connection.ExecuteAsync("DROP INDEX ix_packages_origin");
                await connection.ExecuteAsync("ALTER TABLE ixiflow_packages DROP COLUMN " + column);
            }
            await using var reopened = new SqliteWorkflowStateRepository(connectionString);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetCatalogRevisionAsync());
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains("recreated", error.Message);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Theory]
    [InlineData("ix_packages_origin")]
    [InlineData("ix_package_activities_key")]
    [InlineData("ix_package_activities_category")]
    [InlineData("ix_installed_activities_key")]
    [InlineData("ix_installed_activities_category")]
    [InlineData("ix_package_pending_reverse")]
    public async Task SqliteRejectsMissingActivityAndPackageIndexes(string index)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.CatalogIndexes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "state.db") + ";Pooling=False";
        try
        {
            await using (var store = new SqliteWorkflowStateRepository(connectionString)) await store.GetCatalogRevisionAsync();
            await using (var connection = new SqliteConnection(connectionString)) await connection.ExecuteAsync("DROP INDEX " + index);
            await using var reopened = new SqliteWorkflowStateRepository(connectionString);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetCatalogRevisionAsync());
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Fact]
    public async Task UnsupportedRidIsUnloadableWhileEmptyDependencyPackageIsIndexed()
    {
        var (bytes, _) = ActivityPackageFixture.Create();
        var entries = await WorkflowPackageArchive.ReadAsync(new MemoryStream(bytes), ArtifactDigest.Of(bytes), new());
        var implementation = entries["lib/net10.0/Package.Activities.dll"].ToArray();
        var store = new InMemoryWorkflowStateRepository();
        var empty = await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("rid.empty", "1.0.0", [])));
        Assert.Equal(PackageExtraction.Indexed, empty.Activities.Extraction);
        Assert.Empty(empty.Activities.Activities);
        Assert.Empty(empty.Activities.Closure!);
        var shared = await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("rid.shared", "1.0.0", new()
        { ["lib/net10.0/" + Path.GetFileName(typeof(IAsyncActivity).Assembly.Location)] = await File.ReadAllBytesAsync(typeof(IAsyncActivity).Assembly.Location) })));
        Assert.Equal(PackageExtraction.Indexed, shared.Activities.Extraction);
        Assert.Empty(shared.Activities.Activities);
        var receipt = await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("rid.unsupported", "1.0.0", new()
        { ["ref/net10.0/Package.Activities.dll"] = implementation, ["runtimes/never-compatible-rid/lib/net10.0/Package.Activities.dll"] = implementation })));
        Assert.Equal(PackageExtraction.Unloadable, receipt.Activities.Extraction);
        Assert.Empty(receipt.Activities.Activities);
        Assert.NotNull(receipt.Activities.Closure);
    }

    [WorkflowActivity("files.activity")]
    public sealed class FileActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
