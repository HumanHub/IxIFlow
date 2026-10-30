using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration"), Trait("Spec", "F-19")]
public sealed class ActivityWithdrawalResolverSeedTests
{
    [Theory]
    [InlineData("CatalogOnly")]
    [InlineData("Scratch")]
    [InlineData("Persist")]
    public async Task TransitiveWithdrawalsAreNamedForCatalogAndSeededClosures(string mode)
    {
        var store = new InMemoryWorkflowStateRepository();
        var ingestion = new PackageIngestion(store, store);
        var leafBytes = ActivityPackageFixture.Package("withdrawal.leaf", "1.0.0", new());

        var rootBytes = ActivityPackageFixture.Package("withdrawal.root", "1.0.0", new(),
            "<dependency id=\"withdrawal.leaf\" version=\"[1.0.0]\"/>");

        var leaf = (await ingestion.UploadPackageAsync(new MemoryStream(leafBytes))).Stored;
        await ingestion.UploadPackageAsync(new MemoryStream(rootBytes));
        var rootSeed = await ingestion.ReadPackageAsync(new MemoryStream(rootBytes));
        var leafSeed = await ingestion.ReadPackageAsync(new MemoryStream(leafBytes));
        await store.SetPackageWithdrawnAsync(leaf.Id, leaf.Version, true, new(leaf.LifecycleRevision));
        var resolver = new WorkflowPackageResolver(store, store, new());
        var acquisition = Enum.Parse<PackageAcquisition>(mode);

        foreach (var candidates in new PackageUploadCandidate[][] { [], [rootSeed], [rootSeed, leafSeed] })
        {
            var error = await Assert.ThrowsAsync<WorkflowPublishException>(() => resolver.ResolveAsync(
                ["withdrawal.root@1.0.0"], acquisition, candidates, default));

            Assert.Equal("PUB025", error.Code);
            Assert.Contains("withdrawal.leaf@1.0.0", error.Message);
        }
    }
}