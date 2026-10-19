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
    public async Task SerializedExecutorWithoutCompilerPersistsTheCountAndTimeThenPauses()
    {
        using var harness = new MessageHarness();
        var definition = Workflow.Create<MessageHarness.Data>("ConditionalMessages").Step<MessageHarness.Noop>().Build();
        var assign = await harness.AssignAsync(definition);
        var ready = await harness.ExecuteAsync(assign with
        {
            DebuggingEnabled = true,
            Breakpoints = BreakpointResolver.Resolve(definition, [new("bp", "root/0", Condition: "false")])
        });
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        Assert.Equal(2, harness.Host.Requests.Count);
        var prepared = WorkflowDebugRules.Checkpoint(harness.Host.Requests[0].Commit.ReadTrunk());
        Assert.NotNull(prepared.Evaluation);
        Assert.Equal(1, Assert.Single(prepared.Breakpoints).HitCount);
        var paused = WorkflowDebugRules.Checkpoint(harness.Host.Saved!);
        Assert.Equal("ConditionCompilerUnavailable", paused.Stop!.Reason);
        Assert.Equal(prepared.Evaluation.HitAtUtc, paused.Stop.HitAtUtc);
        Assert.Null(paused.Evaluation);
        Assert.Equal(0, harness.Probe.Noops);
        var trace = await harness.Host.Store.GetTraceAsync(assign.Instance.InstanceId);
        Assert.Equal("ConditionCompilerUnavailable", Assert.IsType<BreakpointHitTrace>(
            Assert.Single(trace, t => t.Entry.EntryType == TraceEntryType.BreakpointHit).Entry.Payload).Reason);
    }

    [Fact]
    [Trait("Spec", "G-04")]
    public async Task BreakpointsCrossAssignmentAndOrderedCommandDeliveryWithConditions()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("BreakpointMessages").Step<MessageHarness.Noop>().Build());
        var breakpoint = new Breakpoint("bp", "root/0", new("flow.yaml", 6), "% 2")
        {
            RequestedStepId = "author", VerifiedSource = new("flow.yaml", 8), StructureHash = assign.StructureHash, HitCount = 4,
            Condition = "context.WorkflowData.Value > 0"
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
        Assert.Equal(breakpoint.Condition, copy.Breakpoints[0].Condition);
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
