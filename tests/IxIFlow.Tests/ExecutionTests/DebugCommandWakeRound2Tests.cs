using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-05")]
[Trait("Spec", "G-06")]
public sealed class DebugCommandWakeRound2Tests
{
    public static IEnumerable<object[]> Cases => DebugCommandWakeRound2Scenario.Cases.Select(scenario => new object[] { scenario });

    [Theory]
    [MemberData(nameof(Cases))]
    public Task AcceptedCommandRecoversWithoutScanning(string scenario)
    {
        var store = new InMemoryWorkflowStateRepository();

        return DebugCommandWakeRound2Scenario.VerifyAsync(store, store, scenario);
    }
}