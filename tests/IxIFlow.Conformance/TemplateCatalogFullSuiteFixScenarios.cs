using System.Data;
using System.Data.SqlTypes;
using System.Text;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "templates")]
[Trait("Spec", "F-12")]
[Trait("Spec", "F-14")]
public sealed class TemplateCatalogFullSuiteFixScenarios
{
    [Fact]
    public void WithdrawalFixturePreservesSqlServerTimestampPrecisionWithoutAServer()
    {
        var stamp = new DateTime(2026, 10, 25, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560);
        Assert.NotEqual(stamp.Ticks, new SqlDateTime(stamp).Value.Ticks);
        var package = new WorkflowPackage("precision.model", "1.0.0", ArtifactDigest.Of([1]),
            new Dictionary<string, ArtifactDigest>(), [])
        {
            Withdrawn = true, WithdrawnAtUtc = stamp, WithdrawnBy = "owner"
        };
        // Apply the actual fixture's Dapper bag to SqlClient without opening a connection.
        using var connection = new SqlConnection("Server=unused;Database=unused;Integrated Security=true");
        using var command = new SqlCommand("SELECT @WithdrawnAtUtc", connection);
        TemplateCatalogScenarios.WithdrawalParameters(StoreProfiles.DistributedSqlServer, package).AddParameters(command);
        var parameter = command.Parameters["WithdrawnAtUtc"];
        Assert.Equal(SqlDbType.DateTime2, parameter.SqlDbType);
        Assert.Equal(stamp, Assert.IsType<DateTime>(parameter.SqlValue));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AcceptedUploadsAreRetainedWhileUnregisteredAndUnheldResolvedArtifactsAreCollected(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateRetention", Guid.NewGuid().ToString("N"));
        var files = new FileSystemArtifactStore(folder);
        try
        {
            var publisher = new WorkflowPublisher(store, files);
            var accepted = await publisher.UploadPackageAsync(new MemoryStream(
                ActivityPackageFixture.Package("retention.uploaded", "1.0.0", new())));
            Assert.Equal(WorkflowPackageOrigin.Uploaded, accepted.Origin);
            var aborted = await files.PutAsync(ArtifactKind.Package, new MemoryStream([1, 2, 3]));
            var resolved = await Register(store, files, "retention.resolved", [], WorkflowPackageOrigin.Resolved);
            Age(folder);

            Assert.Equal(2, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
            Assert.True(await files.ExistsAsync(accepted.Digest));
            Assert.False(await files.ExistsAsync(aborted));
            Assert.False(await files.ExistsAsync(resolved.Digest));
            // Collection preserves the immutable identity/digest guard.
            Assert.Equal(resolved.Digest, (await store.GetPackageAsync(resolved.Id, resolved.Version))!.Digest);
            Assert.Equal(0, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WithdrawnUploadedRootsRetainTheirResolvedClosureWithoutATemplateOrWorkflow(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateRetention", Guid.NewGuid().ToString("N"));
        var files = new FileSystemArtifactStore(folder);
        try
        {
            var dependency = await Register(store, files, "retention.dependency", [], WorkflowPackageOrigin.Resolved);
            var pin = new WorkflowPackageReference(dependency.Id, dependency.Version, dependency.Digest);
            var root = await Register(store, files, "retention.root", [pin], WorkflowPackageOrigin.Uploaded);
            await TemplateCatalogScenarios.WithdrawFixture(database, store, root);
            var withdrawn = (await store.GetPackageAsync(root.Id, root.Version))!;
            Assert.True(withdrawn.Withdrawn);
            Assert.Equal(root.Digest, withdrawn.Digest);
            Assert.Equal(pin, Assert.Single(withdrawn.Closure!));
            var holders = await store.GetPackageHoldersAsync(dependency.Id, dependency.Version, CatalogScope.Unrestricted);
            Assert.Equal(root.Id, Assert.Single(holders.Packages).Id);
            Assert.Empty(holders.Templates);
            Assert.Empty(holders.Workflows);
            Age(folder);

            Assert.Equal(0, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
            Assert.True(await files.ExistsAsync(root.Digest));
            Assert.True(await files.ExistsAsync(dependency.Digest));
            var refused = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterTemplateVersionAsync(
                TemplateCatalogScenarios.Content(packages: [new(root.Id, root.Version, root.Digest), pin]), files));
            Assert.Equal(WorkflowErrorKind.InvalidState, refused.Kind);
            Assert.Contains("withdrawn", refused.Message);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task<WorkflowPackage> Register(IWorkflowStateRepository store, IArtifactStore artifacts, string id,
        IReadOnlyList<WorkflowPackageReference> closure, WorkflowPackageOrigin origin)
    {
        var digest = await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream(Encoding.UTF8.GetBytes(id)));
        var package = new WorkflowPackage(id, "1.0.0", digest, new Dictionary<string, ArtifactDigest> { [id + ".nuspec"] = digest }, [])
        { Origin = origin };
        return (await store.RegisterPackageAsync(package, new([], PackageExtraction.Indexed, null, closure, []), artifacts)).Stored;
    }

    private static void Age(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.artifact"))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-100));
    }
}
