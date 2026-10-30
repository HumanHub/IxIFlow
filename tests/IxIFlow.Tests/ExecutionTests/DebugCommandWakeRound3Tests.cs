using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-05")]
[Trait("Spec", "G-06")]
public sealed class DebugCommandWakeRound3Tests
{
    public static IEnumerable<object[]> Cases => DebugCommandWakeRound3Scenario.Cases.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Cases))]
    public Task AcceptedCommandRecoversWithoutScanning(string scenario)
    {
        var store = new InMemoryWorkflowStateRepository();

        return DebugCommandWakeRound3Scenario.VerifyAsync(store, store, scenario);
    }
}