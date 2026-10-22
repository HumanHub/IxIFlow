using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "G-05")]
[Trait("Spec", "G-06")]
[Trait("Spec", "J-02")]
public sealed class DebugCommandWakeScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public Task ContinueRetriesARefusedImmediateClaimAfterLeaseRelease(string profile) =>
        VerifyAsync(profile, DebugCommandKind.Continue);

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public Task StepRetriesARefusedImmediateClaimAfterLeaseRelease(string profile) =>
        VerifyAsync(profile, DebugCommandKind.Step);

    private static async Task VerifyAsync(string profile, DebugCommandKind kind)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var requester = store.NewRepository();
        // SQLite permits one store owner. Independent host gates share that owner's repository.
        var owner = profile == StoreProfiles.LocalSqlite ? requester : store.NewRepository();
        await DebugCommandWakeScenario.VerifyAsync(requester, owner, kind,
            store.ConfigureHost, store.StartHostLinksAsync, store.StopHostLinksAsync);
    }
}
