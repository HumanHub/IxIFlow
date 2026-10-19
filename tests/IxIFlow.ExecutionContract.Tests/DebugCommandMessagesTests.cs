using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
namespace IxIFlow.ContractTests;

[Trait("Feature", "host")]
public sealed class DebugCommandMessagesTests
{
    [Fact]
    [Trait("Spec", "G-08")]
    public async Task EditRequestsReceiptsAndAuditCrossAssignmentAndDelivery()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("EditMessages").Step<MessageHarness.Noop>().Build());
        var edit = new DataEditRequest(5, new(3, "branch"),
            [new(["Value"], JsonSerializer.SerializeToElement(7)), new(["Name"], JsonSerializer.SerializeToElement<string?>(null), true)]);
        var receipt = new DataEditResult(assign.Instance.InstanceId, "edit", 6, edit.Target);
        var command = new DebugCommand("edit", 7, DebugCommandKind.EditData, 3, "editor", 6, Edit: edit, EditResult: receipt);
        var state = new WorkflowDebugState(8, 3, [command]);
        var copy = harness.Cross(assign with { DebugState = state, DebuggingEnabled = true });
        var delivered = harness.Cross(new DebugCommands(assign.AssignmentId, state));
        foreach (var crossed in new[] { copy.DebugState!.Commands[0], delivered.State.Commands[0] })
        {
            Assert.Equal(command.Order, crossed.Order); Assert.Equal(command.AcknowledgedRevision, crossed.AcknowledgedRevision);
            Assert.Equal(receipt, crossed.EditResult); Assert.Equal(5, crossed.Edit!.ExpectedRevision);
            Assert.Equal(["Value"], crossed.Edit.Patches[0].Path); Assert.Equal(7, crossed.Edit.Patches[0].Value.GetInt32());
            Assert.True(crossed.Edit.Patches[1].Remove);
        }
        var audit = new DataEditedTrace("editor", "edit", edit.Target, 6, [["Value"], ["Name"]]);
        var json = JsonSerializer.Serialize(audit, ExecutionMessageContext.Default.DataEditedTrace);
        var read = JsonSerializer.Deserialize(json, ExecutionMessageContext.Default.DataEditedTrace)!;
        Assert.Equal(audit.EditId, read.EditId); Assert.Equal(audit.Target, read.Target);
        Assert.Equal(audit.Actor, read.Actor); Assert.Equal(audit.Revision, read.Revision);
        Assert.Collection(read.Paths, path => Assert.Equal(["Value"], path), path => Assert.Equal(["Name"], path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "G-08")]
    public async Task SerializedExecutorHoldsAnEditForTheHostWithoutConsumingItsFollowingContinue(bool enabled)
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("HeldEditMessages").Step<MessageHarness.Noop>().Build());
        assign.Instance.Status = WorkflowStatus.Paused;
        var request = new DataEditRequest(assign.Instance.Revision, new(0), [new(["Value"], JsonSerializer.SerializeToElement(7))]);
        var ready = await harness.ExecuteAsync(assign with { DebuggingEnabled = enabled,
            DebugState = new(2, 0, [new("edit", 1, DebugCommandKind.EditData, 0, "editor", Edit: request),
                new("continue", 2, DebugCommandKind.Continue, 0, "continuer")]) });
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        Assert.Empty(harness.Host.Requests); Assert.Equal(0, harness.Probe.Noops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "G-06")]
    public async Task SerializedExecutorCannotAdvanceAnInterruptedStepWithoutCapabilities(bool enabled)
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("HeldStepMessages" + enabled).Step<MessageHarness.Noop>().Build());
        var json = System.Text.Json.Nodes.JsonNode.Parse(assign.Instance.ExecutionStateJson)!;
        json["Debug"] = JsonSerializer.SerializeToNode(new DebugCheckpoint
        {
            Stepping = new("root", DebugStepKind.Over, new("activation", 0, 0, 0, false),
                ["activation"], ["root"], "step", "stepper", true)
        });
        assign.Instance.ExecutionStateJson = json.ToJsonString();
        var ready = await harness.ExecuteAsync(assign with { DebuggingEnabled = enabled, Parts = null,
            Breakpoints = enabled ? BreakpointResolver.Resolve(
                Workflow.Create<MessageHarness.Data>("HeldStepMessages" + enabled).Step<MessageHarness.Noop>().Build(),
                [new("condition", "root/0", Condition: "false")]) : [] });
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        Assert.Empty(harness.Host.Requests);
        Assert.Equal(0, harness.Probe.Noops);
    }

    [Fact]
    [Trait("Spec", "G-06")]
    public async Task StepCommandsAndRecoveryOriginsCrossAssignmentAndDelivery()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("StepMessages").Step<MessageHarness.Noop>().Build());
        foreach (var kind in Enum.GetValues<DebugStepKind>())
        {
            var command = new DebugCommand("step", 7, DebugCommandKind.Step, 3, "stepper",
                ContinuationId: "branch", StepKind: kind);
            var state = new WorkflowDebugState(7, 3, [command]);
            var copy = harness.Cross(assign with { DebugState = state, DebuggingEnabled = true });
            Assert.Equal(command, Assert.Single(copy.DebugState!.Commands));
            var delivery = harness.Cross(new DebugCommands(assign.AssignmentId, state));
            Assert.Equal(command, Assert.Single(delivery.State.Commands));
            var stepping = new DebugStepping("branch", kind, new("activation", 2, 4, 1, false),
                ["outer", "activation"], ["branch", "forked"], "step", "stepper", true);
            var resumed = JsonSerializer.Deserialize(JsonSerializer.Serialize(stepping,
                ExecutionMessageContext.Default.DebugStepping), ExecutionMessageContext.Default.DebugStepping)!;
            Assert.Equal(stepping.Origin, resumed.Origin);
            Assert.Equal(stepping.EligibleContinuations, resumed.EligibleContinuations);
            Assert.Equal(stepping.OriginScopes, resumed.OriginScopes);
            Assert.Equal(kind, resumed.Kind);
            Assert.True(resumed.Started);
        }
    }

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
