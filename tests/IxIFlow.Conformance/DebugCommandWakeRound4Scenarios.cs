using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "F-17")]
public sealed class DebugCommandWakeRound4Scenarios
{
    public static IEnumerable<object[]> Cases => from profile in StoreProfiles.ProfileNames
        from scenario in DebugCommandWakeRound4Scenario.Cases
        select new object[] { profile, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryRecoveryExitReconcilesTheAcceptedCommand(string profile, string scenario)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var requester = store.NewRepository();
        var other = profile == StoreProfiles.LocalSqlite ? requester : store.NewRepository();

        await DebugCommandWakeRound4Scenario.VerifyAsync(requester, other, scenario,
            store.ConfigureHost, store.StartHostLinksAsync, store.StopHostLinksAsync);
    }
}