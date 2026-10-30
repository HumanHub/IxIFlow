using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "G-05")]
[Trait("Spec", "G-06")]
[Trait("Spec", "J-02")]
public sealed class DebugCommandWakeRound3Scenarios
{
    public static IEnumerable<object[]> Cases => from profile in StoreProfiles.ProfileNames
        from scenario in DebugCommandWakeRound3Scenario.Cases
        select new object[] { profile, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AcceptedCommandRecoversWithoutScanning(string profile, string scenario)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var requester = store.NewRepository();
        var other = profile == StoreProfiles.LocalSqlite ? requester : store.NewRepository();

        await DebugCommandWakeRound3Scenario.VerifyAsync(requester, other, scenario,
            store.ConfigureHost, store.StartHostLinksAsync, store.StopHostLinksAsync);
    }
}