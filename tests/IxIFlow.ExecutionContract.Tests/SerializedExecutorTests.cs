using IxIFlow.Builders;
using IxIFlow.Core;
using static IxIFlow.ContractTests.MessageHarness;

namespace IxIFlow.ContractTests;

[Trait("Feature", "host")]
[Trait("Spec", "A-02")]
public sealed class SerializedExecutorTests
{
    [Fact]
    public async Task AStaleEpochCheckpointIsRefusedWithoutCallingTheStore()
    {
        using var harness = new MessageHarness();
        await harness.AssignAsync(Workflow.Create<Data>("epoch").Step<Noop>().Build());
        harness.ChangeCheckpoint = request => request with { Commit = Rebuild(request.Commit, request.Commit.Epoch + 1) };
        var ready = await harness.ExecuteAsync();
        Assert.Equal(AssignmentOutcomeKind.OwnershipLost, ready.Outcome!.Kind);
        Assert.Equal(ConflictReason.StaleEpoch, harness.Host.LastReply!.Conflict);
        Assert.Equal(0, harness.Host.StoreCalls);
        Assert.Equal(0, harness.Probe.Noops);
    }

    [Fact]
    public async Task AnUnknownReplyResendsTheSameCommitAndGetsItsOriginalReceipt()
    {
        using var harness = new MessageHarness();
        await harness.AssignAsync(Workflow.Create<Data>("unknown").Step<Noop>().Build());
        harness.Host.LoseFirstReply = true;
        var ready = await harness.ExecuteAsync();
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        Assert.Equal(WorkflowCommitStatus.Applied, harness.Host.Receipts[0]);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, harness.Host.Receipts[1]);
        var first = harness.Host.Requests[0].Commit;
        var resend = harness.Host.Requests[1].Commit;
        Assert.NotSame(first, resend);
        Assert.Equal(first.CommitId, resend.CommitId);
        Assert.Equal(first.ExpectedRevision, resend.ExpectedRevision);
        Assert.Equal(first.ContentHash, resend.ContentHash);
        Assert.Equal(WorkflowStatus.Completed, harness.Host.Saved!.Status);
        Assert.Equal(harness.Host.Saved.Revision, ready.LastAcknowledgedCommit!.Revision);
        Assert.Equal(1, harness.Probe.Noops);
        Assert.False(string.IsNullOrWhiteSpace(ready.ExecutorName));
    }

    [Theory]
    [InlineData(CancelKind.Workflow)]
    [InlineData(CancelKind.Run)]
    [InlineData(CancelKind.Ownership)]
    public async Task EachCancelKindCrossesTheBoundaryAndKeepsItsSemantics(CancelKind kind)
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Cancellable());
        var running = harness.ExecuteAsync();
        await harness.Probe.Started.Task.WaitAsync(Patience);
        var calls = harness.Host.StoreCalls;
        harness.Deliver(new Cancel(assign.AssignmentId, kind,
            kind == CancelKind.Workflow ? new() { ReasonCode = "withdrawn" } : null));
        var ready = await running.WaitAsync(Patience);
        Assert.Equal(kind switch
        {
            CancelKind.Workflow => AssignmentOutcomeKind.Idle,
            CancelKind.Run => AssignmentOutcomeKind.RunCancelled,
            _ => AssignmentOutcomeKind.OwnershipLost
        }, ready.Outcome!.Kind);
        Assert.Equal(kind == CancelKind.Workflow ? 1 : 0, harness.Probe.Cleanups);
        Assert.Equal(kind == CancelKind.Workflow ? WorkflowStatus.Cancelled : WorkflowStatus.Running, harness.Host.Saved!.Status);
        if (kind == CancelKind.Workflow) Assert.Equal("withdrawn", harness.Host.Saved.CancellationReason!.ReasonCode);
        if (kind == CancelKind.Ownership) Assert.Equal(calls, harness.Host.StoreCalls);
    }

    [Fact]
    public async Task RecycleCommitsTheRunningActivityAndLeavesTheNextStepForRecovery()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Cancellable());
        var running = harness.ExecuteAsync();
        await harness.Probe.Started.Task.WaitAsync(Patience);
        var calls = harness.Host.StoreCalls;
        harness.Deliver(new Recycle(assign.AssignmentId));
        harness.Probe.Release.TrySetResult();
        var ready = await running.WaitAsync(Patience);
        Assert.Equal(AssignmentOutcomeKind.Drained, ready.Outcome!.Kind);
        Assert.True(harness.Host.StoreCalls > calls);
        Assert.Equal(WorkflowStatus.Running, harness.Host.Saved!.Status);
        Assert.Equal(1, harness.Probe.Noops);
        Assert.Equal(0, harness.Probe.Cleanups);
        Assert.Equal(harness.Host.Saved.Revision, ready.LastAcknowledgedCommit!.Revision);
    }

    [Fact]
    public async Task ARefusedChildNeverAppearsInACommit()
    {
        using var harness = new MessageHarness();
        var parent = Workflow.Create<Data>("parent").Step<Noop>().Invoke<Data>("child", 1).Build();
        await harness.RegisterAsync(Workflow.Create<Data>("child").Step<Noop>().Build());
        await harness.AssignAsync(parent);
        harness.Host.RefuseChildren = true;
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => harness.ExecuteAsync());
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        Assert.Single(harness.Host.Children);
        Assert.All(harness.Host.Requests, request => Assert.Empty(request.Commit.ChildStarts));
        Assert.Equal(WorkflowStatus.Running, harness.Host.Saved!.Status);
        Assert.Contains(typeof(StartChildReply), harness.Crossed);
    }

    [Fact]
    public async Task NonemptyBreakpointsAreRefusedBeforeAnyHostRequest()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<Data>("breakpoints").Step<Noop>().Build());
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            harness.ExecuteAsync(assign with { Breakpoints = [new("root/0", "true", 1)] }));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Equal([typeof(Assign)], harness.Crossed);
        Assert.Equal(0, harness.Host.StoreCalls);
    }

    [Fact]
    public async Task DelayedMessagesCannotReachTheNextAssignmentOnTheSameExecutor()
    {
        using var harness = new MessageHarness();
        var old = await harness.AssignAsync(Workflow.Create<Data>("old").Step<Noop>().Build());
        await harness.ExecuteAsync();
        var oldCommit = harness.Host.Requests[0];
        await harness.AssignAsync(Cancellable());
        var running = harness.ExecuteAsync();
        await harness.Probe.Started.Task.WaitAsync(Patience);
        var before = harness.Host.StoreCalls;
        harness.Deliver(new Cancel(old.AssignmentId, CancelKind.Ownership));
        harness.Deliver(new Cancel(old.AssignmentId, CancelKind.Run));
        harness.Deliver(new Cancel(old.AssignmentId, CancelKind.Workflow, new() { ReasonCode = "late" }));
        harness.Deliver(new Recycle(old.AssignmentId));
        harness.Deliver(new Arrivals(old.AssignmentId, long.MaxValue));
        Assert.Equal(ConflictReason.NotAssigned, (await harness.SendAsync(oldCommit)).Conflict);
        Assert.Equal(before, harness.Host.StoreCalls);
        harness.Probe.Release.TrySetResult();
        var ready = await running.WaitAsync(Patience);
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        Assert.Equal(WorkflowStatus.Completed, harness.Host.Saved!.Status);
        Assert.Null(harness.Host.Saved.CancellationReason);
        Assert.Equal(1, harness.Probe.Cleanups);
    }

    internal static CheckpointCommit Rebuild(CheckpointCommit commit, long? epoch = null,
        CommittedInstance? metadata = null) => CheckpointCommit.FromFields(metadata ?? commit.Instance,
        commit.Payload, commit.Parts, commit.DeletedParts, commit.ReplacesParts, commit.Incarnation,
        epoch ?? commit.Epoch, commit.CommitId, commit.ExpectedRevision, commit.ClearsCrashCount, commit.Parks,
        commit.Settlements, commit.InboxReadThrough, commit.OpenedWaits, commit.ArmedWaits, commit.ClosedWaits,
        commit.WakeAfter, commit.Stopped, commit.ChildStarts, commit.ChildCancellations, commit.ParentNotice, commit.TraceEntries, commit.Diagnostics, commit.RetainedDiagnosticIds);
}
