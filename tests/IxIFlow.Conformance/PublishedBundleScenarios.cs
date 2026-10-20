using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "F-01")]
[Trait("Spec", "F-02")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-13")]
public sealed class PublishedBundleScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublishedVersionRunsAndRecoversOnAFreshHost(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.BundleConformance", Guid.NewGuid().ToString("N"));
        await PublishedBundleScenario.CrossHostAsync(() => store.NewRepository(), root, store.ConfigureHost, store.StartHostLinksAsync);
    }
}
