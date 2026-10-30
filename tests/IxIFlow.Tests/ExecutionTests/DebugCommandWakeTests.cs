using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-05")]
[Trait("Spec", "G-06")]
public sealed class DebugCommandWakeTests
{
    [Fact]
    public async Task ContinueRetriesARefusedImmediateClaimAfterLeaseRelease()
    {
        var repository = new InMemoryWorkflowStateRepository();
        await DebugCommandWakeScenario.VerifyAsync(repository, repository, DebugCommandKind.Continue);
    }

    [Fact]
    public async Task StepRetriesARefusedImmediateClaimAfterLeaseRelease()
    {
        var repository = new InMemoryWorkflowStateRepository();
        await DebugCommandWakeScenario.VerifyAsync(repository, repository, DebugCommandKind.Step);
    }
}