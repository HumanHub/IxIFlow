using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "F-17")]
public sealed class DebugCommandWakeRound4Tests
{
    public static IEnumerable<object[]> Cases => DebugCommandWakeRound4Scenario.Cases.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Cases))]
    public Task EveryRecoveryExitReconcilesTheAcceptedCommand(string scenario)
    {
        var store = new InMemoryWorkflowStateRepository();

        return DebugCommandWakeRound4Scenario.VerifyAsync(store, store, scenario);
    }
}