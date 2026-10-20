namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "J-02")]
public sealed class CoordinatorProfileTests
{
    [Fact]
    public void EveryDistributedProviderHasZeroOneAndTwoCoordinatorsAndLocalProfilesHaveZero()
    {
        var profiles = StoreProfiles.ProfileNames.ToArray();
        Assert.Equal(8, profiles.Distinct().Count());
        foreach (var provider in new[] { StoreProfiles.DistributedSqlServer, StoreProfiles.DistributedPostgreSql })
            Assert.Equal(new[] { 0, 1, 2 }, profiles.Where(profile => profile.Split('/')[0] == provider)
                .Select(StoreProfiles.CoordinatorCount));
        Assert.Equal(0, StoreProfiles.CoordinatorCount(StoreProfiles.LocalMemory));
        Assert.Equal(0, StoreProfiles.CoordinatorCount(StoreProfiles.LocalSqlite));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoreProfiles.CoordinatorCount("local-sqlite/coordinators-1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => StoreProfiles.CoordinatorCount("local-memory/coordinators-1"));
    }
}
