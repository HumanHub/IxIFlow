using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
namespace IxIFlow.ContractTests;

[Trait("Feature", "host")]
public sealed class DebugCommandMessagesTests
{
    [Fact]
    public async Task AssignmentAndDeliveryRoundTripVersionedCommandsWithTheirAssignmentIdentity()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("DebugMessages").Step<MessageHarness.Noop>().Build());
        var state = new WorkflowDebugState(3, 1,
            [new("pause", 1, DebugCommandKind.Pause, 0, "pauser", 5), new("continue", 2, DebugCommandKind.Continue, 1, "resumer")]);
        assign = assign with { DebugState = state, DebuggingEnabled = true };
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(assign, ExecutionMessageContext.Default.Assign), ExecutionMessageContext.Default.Assign)!;
        Assert.True(copy.DebuggingEnabled);
        Assert.Equal(3, copy.DebugState!.Version);
        Assert.Equal(state.Commands, copy.DebugState.Commands);
        var delivered = new DebugCommands(assign.AssignmentId, state);
        var deliveredCopy = JsonSerializer.Deserialize(JsonSerializer.Serialize(delivered, ExecutionMessageContext.Default.DebugCommands), ExecutionMessageContext.Default.DebugCommands)!;
        Assert.Equal(assign.AssignmentId, deliveredCopy.AssignmentId);
        Assert.Equal(state.Commands, deliveredCopy.State.Commands);
    }
}
