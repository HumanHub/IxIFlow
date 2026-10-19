using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
namespace IxIFlow.ContractTests;

[Trait("Feature", "host")]
public sealed class DebugCommandMessagesTests
{
    [Fact]
    [Trait("Spec", "G-04")]
    public async Task BreakpointsCrossAssignmentAndOrderedCommandDeliveryWithoutConditions()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("BreakpointMessages").Step<MessageHarness.Noop>().Build());
        var breakpoint = new Breakpoint("bp", "root/0", new("flow.yaml", 6), "% 2")
        {
            RequestedStepId = "author", VerifiedSource = new("flow.yaml", 8), StructureHash = assign.StructureHash, HitCount = 4
        };
        var state = new WorkflowDebugState(3, 1,
            [new("set", 1, DebugCommandKind.SetBreakpoints, 0, "setter", 5, [breakpoint with { HitCount = 0 }]),
             new("continue", 2, DebugCommandKind.Continue, 1, "continuer")]);
        assign = assign with { Breakpoints = [breakpoint], DebugState = state, DebuggingEnabled = true };
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(assign, ExecutionMessageContext.Default.Assign), ExecutionMessageContext.Default.Assign)!;
        Assert.Equal(breakpoint, Assert.Single(copy.Breakpoints));
        var delivered = new DebugCommands(assign.AssignmentId, state);
        var deliveredCopy = JsonSerializer.Deserialize(JsonSerializer.Serialize(delivered, ExecutionMessageContext.Default.DebugCommands), ExecutionMessageContext.Default.DebugCommands)!;
        Assert.Equal(assign.AssignmentId, deliveredCopy.AssignmentId);
        var command = deliveredCopy.State.Commands[0];
        Assert.Equal("setter", command.Actor);
        Assert.Equal(5, command.AcknowledgedRevision);
        Assert.Equal(breakpoint with { HitCount = 0 }, Assert.Single(command.Breakpoints!));
        Assert.Equal("continue", deliveredCopy.State.Commands[1].Id);
        Assert.Null(typeof(Breakpoint).GetProperty("Condition"));
    }

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
