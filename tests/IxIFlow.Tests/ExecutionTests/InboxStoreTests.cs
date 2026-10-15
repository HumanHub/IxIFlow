using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The inbox and waits contract every store meets (D-02, D-03, D-04, D-32, D-33): an event is stored
/// and deduplicated by its ID before it is acknowledged, is refused on arrival when no wait could take
/// it, makes a running or suspended instance runnable in the same step, and is settled by the commit
/// that takes it, at most once. Commits open and close waits, and a wait opens at the inbox's sequence.
/// A commit that has not read every arrival keeps the instance runnable and does not park.
/// </summary>
[Trait("Feature", "waits")]
public sealed class InboxStoreTests
{
    [Fact]
    [Trait("Spec", "D-02")]
    public Task MemoryStoreAppendsEvents() => VerifyAppend(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-02")]
    public Task SqlStoreAppendsEvents() =>
        VerifyAppend(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyAppend(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var instance = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        var stopped = await IdleInstanceAsync(store, WorkflowStatus.NeedsResolution);
        try
        {
            var first = await inbox.AppendEventAsync(Event(instance, "e1", "{\"n\":1}"));
            Assert.Equal(new EventAppendResult(EventAppendStatus.Stored, 1, true), first);
            Assert.Contains(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == instance);

            // The same event again is the same delivery; another event under its ID is refused.
            Assert.Equal(EventAppendStatus.Duplicate,
                (await inbox.AppendEventAsync(Event(instance, "e1", "{\"n\":1}"))).Status);
            Assert.Equal(EventAppendStatus.Conflict,
                (await inbox.AppendEventAsync(Event(instance, "e1", "{\"n\":2}"))).Status);
            Assert.Equal(EventAppendStatus.Conflict, (await inbox.AppendEventAsync(
                Event(instance, "e1", "{\"n\":1}") with { TargetWaitId = "elsewhere" })).Status);
            Assert.Equal(EventAppendStatus.NotFound,
                (await inbox.AppendEventAsync(Event(Guid.NewGuid().ToString("N"), "e1", "{}"))).Status);

            // Arrival order, paged by sequence.
            await inbox.AppendEventAsync(Event(instance, "e2", "{}"));
            await inbox.AppendEventAsync(Event(instance, "e3", "{}"));
            var full = await inbox.GetPendingEventsAsync(instance, 0, 2);
            Assert.Equal(["e1", "e2"], full.Events.Select(item => item.EventId));
            Assert.Equal(2, full.ReadThrough);
            var rest = await inbox.GetPendingEventsAsync(instance, 1, 10);
            Assert.Equal(["e2", "e3"], rest.Events.Select(item => item.EventId));
            Assert.Equal(3, rest.ReadThrough);

            // A stopped instance keeps the event for after its release, without becoming runnable.
            var held = await inbox.AppendEventAsync(Event(stopped, "s1", "{}"));
            Assert.Equal(EventAppendStatus.Stored, held.Status);
            Assert.False(held.MadeRunnable);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == stopped);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instance);
            await store.DeleteWorkflowInstanceAsync(stopped);
        }
        Assert.Null(await inbox.GetEventAsync(instance, "e1"));
        Assert.Null(await inbox.GetWaitAsync(instance, ApprovalWait(instance)));
    }

    [Fact]
    [Trait("Spec", "D-04")]
    public Task MemoryStoreSettlesEventsInTheCommit() => VerifySettlement(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-04")]
    public Task SqlStoreSettlesEventsInTheCommit() =>
        VerifySettlement(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifySettlement(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var instanceId = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        try
        {
            await inbox.AppendEventAsync(Event(instanceId, "taken", "{}"));
            await inbox.AppendEventAsync(Event(instanceId, "refused", "{}"));
            var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
            saved.Status = WorkflowStatus.Suspended;
            saved.RunnableAtUtc = null;

            // The commit that takes an event settles it, and records how far it read.
            var commit = CheckpointCommit.Create(saved, 0, saved.Revision, settlements:
            [
                new EventSettlement("taken", EventOutcome.Consumed, "wait-1"),
                new EventSettlement("refused", EventOutcome.Rejected, Rejection: EventRejection.Ambiguous)
            ], inboxReadThrough: 2);
            var applied = await store.CommitWorkflowInstanceAsync(commit);
            Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
            Assert.False(applied.UnreadEvents);
            var taken = (await inbox.GetEventAsync(instanceId, "taken"))!;
            Assert.Equal(EventOutcome.Consumed, taken.Outcome);
            Assert.Equal("wait-1", taken.WaitId);
            var refused = (await inbox.GetEventAsync(instanceId, "refused"))!;
            Assert.Equal(EventOutcome.Rejected, refused.Outcome);
            Assert.Equal(EventRejection.Ambiguous, refused.Rejection);
            // Nothing is pending, but the page still reads through every arrival.
            var empty = await inbox.GetPendingEventsAsync(instanceId, 0, 10);
            Assert.Empty(empty.Events);
            Assert.Equal(2, empty.ReadThrough);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == instanceId);

            // Sending the same commit again is acknowledged; settling the event again is not.
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
            saved.Revision = applied.Revision;
            Assert.Equal(WorkflowCommitStatus.EventsChanged, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(saved, 0, saved.Revision,
                    settlements: [new EventSettlement("taken", EventOutcome.Consumed, "wait-2")]))).Status);

            // An idle commit that has not read the latest arrival stays runnable now.
            await inbox.AppendEventAsync(Event(instanceId, "late", "{}"));
            var behindCommit = CheckpointCommit.Create(saved, 0, saved.Revision, inboxReadThrough: 2);
            var behind = await store.CommitWorkflowInstanceAsync(behindCommit);
            Assert.Equal(WorkflowCommitStatus.Applied, behind.Status);
            Assert.True(behind.UnreadEvents);
            Assert.Contains(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == instanceId);
            // The same commit sent again gets the same answer.
            Assert.True((await store.CommitWorkflowInstanceAsync(behindCommit)).UnreadEvents);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    [Trait("Spec", "D-04")]
    public Task MemoryStoreDoesNotParkPastAnUnreadEvent() => VerifyNoParkPastUnread(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-04")]
    public Task SqlStoreDoesNotParkPastAnUnreadEvent() =>
        VerifyNoParkPastUnread(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyNoParkPastUnread(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            var grant = (await store.TryAcquireExecutionLeaseAsync(instanceId, "owner", TimeSpan.FromSeconds(30)))!;
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId, WorkflowName = "InboxPark", Status = WorkflowStatus.Running,
                WorkflowDataJson = "{}"
            };
            var started = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, 0,
                openedWaits: [OpenApproval(instanceId)]));
            instance.Revision = started.Revision;
            await inbox.AppendEventAsync(Event(instanceId, "arrived", "{}"));

            instance.RunnableAtUtc = DateTime.UtcNow.AddHours(1);
            var park = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch,
                instance.Revision, parks: true, inboxReadThrough: 0));

            // The run keeps its lease and is told to read the inbox first.
            Assert.Equal(WorkflowCommitStatus.Applied, park.Status);
            Assert.False(park.Parked);
            Assert.True(park.UnreadEvents);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instanceId, "other", TimeSpan.FromSeconds(5)));

            // So does an idle commit that has not read the arrival.
            instance.Revision = park.Revision;
            instance.Status = WorkflowStatus.Suspended;
            instance.RunnableAtUtc = null;
            var idle = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch,
                instance.Revision, inboxReadThrough: 0));
            Assert.True(idle.UnreadEvents);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instanceId, "other", TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    [Trait("Spec", "D-02")]
    public Task MemoryStoreCloses() => VerifyClosed(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-02")]
    public Task SqlStoreCloses() =>
        VerifyClosed(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyClosed(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var finished = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        try
        {
            // A finished instance takes no new events, but still recognizes one it was sent.
            await inbox.AppendEventAsync(Event(finished, "before", "{}"));
            var saved = (await store.GetWorkflowInstanceAsync(finished))!;
            saved.Status = WorkflowStatus.Completed;
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, 0, saved.Revision))).Status);
            Assert.Equal(EventAppendStatus.Closed,
                (await inbox.AppendEventAsync(Event(finished, "after", "{}"))).Status);
            Assert.Equal(EventAppendStatus.Duplicate,
                (await inbox.AppendEventAsync(Event(finished, "before", "{}"))).Status);
            // The event no wait took before the end is closed, and so is the wait.
            Assert.Equal(EventOutcome.Closed, (await inbox.GetEventAsync(finished, "before"))!.Outcome);
            Assert.Equal(WaitCloseReason.InstanceFinished,
                (await inbox.GetWaitAsync(finished, ApprovalWait(finished)))!.CloseReason);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(finished);
        }
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public Task MemoryStoreRefusesEventsNoWaitCanTake() => VerifyRefusedOnArrival(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task SqlStoreRefusesEventsNoWaitCanTake() =>
        VerifyRefusedOnArrival(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyRefusedOnArrival(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var instanceId = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        var approval = ApprovalWait(instanceId);
        try
        {
            // A key no open wait has, and a wait the instance never opened, are refused on arrival.
            var noWait = await inbox.AppendEventAsync(Event(instanceId, "k1", "{}") with { Key = "payment" });
            Assert.Equal(new EventAppendResult(EventAppendStatus.Rejected, Rejection: EventRejection.NoOpenWait),
                noWait);
            Assert.Equal(EventAppendStatus.Rejected, (await inbox.AppendEventAsync(
                Event(instanceId, "t1", "{}") with { Key = null, TargetWaitId = "never-opened" })).Status);
            Assert.Equal(EventRejection.UnknownWait, (await inbox.GetEventAsync(instanceId, "t1"))!.Rejection);
            // A refused event is recorded, so the same event again is a duplicate, and takes no place
            // in the inbox: nothing is pending and the instance is not woken for it.
            Assert.Equal(EventAppendStatus.Duplicate, (await inbox.AppendEventAsync(
                Event(instanceId, "k1", "{}") with { Key = "payment" })).Status);
            Assert.Equal(EventOutcome.Rejected, (await inbox.GetEventAsync(instanceId, "k1"))!.Outcome);
            var page = await inbox.GetPendingEventsAsync(instanceId, 0, 10);
            Assert.Empty(page.Events);
            Assert.Equal(0, page.ReadThrough);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == instanceId);

            // An event addressed to an open wait is kept for the owner, and so is one without a key
            // while any event wait is open.
            Assert.Equal(EventAppendStatus.Stored, (await inbox.AppendEventAsync(
                Event(instanceId, "t2", "{}") with { Key = null, TargetWaitId = approval })).Status);
            Assert.Equal(EventAppendStatus.Stored,
                (await inbox.AppendEventAsync(Event(instanceId, "any", "{}") with { Key = null })).Status);

            // Once the wait is answered, an event addressed to it is refused with that reason, and one
            // addressed by its key finds no open wait.
            var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(saved, 0, saved.Revision,
                    settlements: [new EventSettlement("t2", EventOutcome.Consumed, approval)], inboxReadThrough: 1,
                    closedWaits: [new ClosedWait(approval, WaitCloseReason.Answered)]))).Status);
            var answered = await inbox.AppendEventAsync(
                Event(instanceId, "t3", "{}") with { Key = null, TargetWaitId = approval });
            Assert.Equal(EventRejection.WaitAnswered, answered.Rejection);
            Assert.Equal(EventRejection.NoOpenWait,
                (await inbox.AppendEventAsync(Event(instanceId, "k2", "{}"))).Rejection);
            Assert.Equal(EventRejection.NoOpenWait,
                (await inbox.AppendEventAsync(Event(instanceId, "any2", "{}") with { Key = null })).Rejection);
            var closed = (await inbox.GetWaitAsync(instanceId, approval))!;
            Assert.False(closed.IsOpen);
            Assert.Equal(WaitCloseReason.Answered, closed.CloseReason);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public Task MemoryStoreOpensWaitsAtTheInboxSequence() => VerifyWaitSequence(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task SqlStoreOpensWaitsAtTheInboxSequence() =>
        VerifyWaitSequence(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyWaitSequence(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var instanceId = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        var first = ApprovalWait(instanceId);
        var next = $"{instanceId}-next";
        var child = $"{instanceId}-child";
        try
        {
            await inbox.AppendEventAsync(Event(instanceId, "early", "{}"));

            // A wait opened after an event arrived is stamped with the inbox's sequence at its commit,
            // so the owner can tell that the event came before it.
            var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
            var deadline = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var applied = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, 0, saved.Revision,
                openedWaits:
                [
                    new OpenedWait(next, WaitKind.Event, "approval", "Approval", deadline),
                    new OpenedWait(child, WaitKind.Child, "shipment", "Shipment", null)
                ]));
            Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
            var page = await inbox.GetPendingEventsAsync(instanceId, 0, 10);
            Assert.Equal(1, Assert.Single(page.Events).Sequence);
            Assert.Equal(0, page.OpenWaits[first]);
            Assert.Equal(1, page.OpenWaits[next]);
            var stored = (await inbox.GetWaitAsync(instanceId, next))!;
            Assert.Equal((WaitKind.Event, "approval", "Approval", deadline, 1L),
                (stored.Kind, stored.Key, stored.EventType, stored.DeadlineUtc, stored.OpenedSequence));
            Assert.True(stored.IsOpen);

            // Opening a wait again keeps its first record.
            saved.Revision = applied.Revision;
            await inbox.AppendEventAsync(Event(instanceId, "later", "{}"));
            applied = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, 0, saved.Revision,
                openedWaits: [new OpenedWait(next, WaitKind.Event, "approval", "Approval", deadline)]));
            Assert.Equal(1, (await inbox.GetWaitAsync(instanceId, next))!.OpenedSequence);

            // While a child's wait is shown here, an event with any key is kept for the child to decide.
            Assert.Equal(EventAppendStatus.Stored, (await inbox.AppendEventAsync(
                Event(instanceId, "for-child", "{}") with { Key = "anything" })).Status);

            // Open waits can be listed by instance, key, type and kind, in wait ID order, and paged.
            Assert.Equal(new[] { first, next, child }.Order(StringComparer.Ordinal),
                (await inbox.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId))).Select(wait => wait.WaitId));
            Assert.Equal([child], (await inbox.GetOpenWaitsAsync(
                new WaitQuery(InstanceId: instanceId, Kind: WaitKind.Child))).Select(wait => wait.WaitId));
            var byKey = await inbox.GetOpenWaitsAsync(
                new WaitQuery(InstanceId: instanceId, Key: "approval", EventType: "Approval", Limit: 1));
            var firstByKey = Assert.Single(byKey);
            var secondByKey = Assert.Single(await inbox.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId,
                Key: "approval", EventType: "Approval", AfterWaitId: firstByKey.WaitId)));
            Assert.Equal(new[] { first, next }.Order(StringComparer.Ordinal),
                new[] { firstByKey.WaitId, secondByKey.WaitId });

            // A closed wait leaves the open set.
            saved.Revision = applied.Revision;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(saved, 0, saved.Revision,
                    closedWaits: [new ClosedWait(first, WaitCloseReason.TimedOut)]))).Status);
            Assert.DoesNotContain(first, (await inbox.GetPendingEventsAsync(instanceId, 0, 10)).OpenWaits.Keys);
            Assert.Equal(WaitCloseReason.TimedOut, (await inbox.GetWaitAsync(instanceId, first))!.CloseReason);
            Assert.Equal(EventRejection.WaitTimedOut, (await inbox.AppendEventAsync(
                Event(instanceId, "too-late", "{}") with { Key = null, TargetWaitId = first })).Rejection);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    [Trait("Spec", "D-04")]
    public void ACommitSettlesEachEventOnceAndOnlyAsConsumedOrRejected()
    {
        var instance = new WorkflowInstance { InstanceId = "settling", WorkflowName = "Inbox" };

        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 0, 0, settlements:
        [
            new EventSettlement("e1", EventOutcome.Consumed, "a"),
            new EventSettlement("e1", EventOutcome.Rejected, Rejection: EventRejection.NotMatched)
        ]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 0, 0,
            settlements: [new EventSettlement("e1", EventOutcome.Pending)]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 0, 0,
            settlements: [new EventSettlement("e1", EventOutcome.Consumed)]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 0, 0,
            settlements: [new EventSettlement("e1", EventOutcome.Rejected)]));

        // The commit keeps its own copy, so changing the list afterwards changes nothing.
        var settlements = new List<EventSettlement> { new("e1", EventOutcome.Consumed, "a") };
        var commit = CheckpointCommit.Create(instance, 0, 0, settlements: settlements);
        settlements.Clear();
        Assert.Single(commit.Settlements);
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public void ACommitOpensOrClosesEachWaitOnceAndHashesWhatItChanges()
    {
        var instance = new WorkflowInstance { InstanceId = "waiting", WorkflowName = "Inbox" };
        var opened = new OpenedWait("w1", WaitKind.Event, "approval", "Approval", null);

        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 0, 0,
            openedWaits: [opened], closedWaits: [new ClosedWait("w1", WaitCloseReason.Answered)]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 0, 0,
            openedWaits: [opened with { EventType = "" }]));

        // Two commits that differ only in the waits they change are different commits.
        var plain = CheckpointCommit.Create(instance, 0, 0, commitId: "c1");
        var opening = CheckpointCommit.Create(instance, 0, 0, commitId: "c1", openedWaits: [opened]);
        var closing = CheckpointCommit.Create(instance, 0, 0, commitId: "c1",
            closedWaits: [new ClosedWait("w1", WaitCloseReason.Answered)]);
        Assert.NotEqual(plain.ContentHash, opening.ContentHash);
        Assert.NotEqual(plain.ContentHash, closing.ContentHash);
    }

    private static WorkflowEvent Event(string instanceId, string eventId, string payload) =>
        new(instanceId, eventId, "Approval", payload, Key: "approval");

    private static string ApprovalWait(string instanceId) => $"{instanceId}-approval";

    private static OpenedWait OpenApproval(string instanceId) =>
        new(ApprovalWait(instanceId), WaitKind.Event, "approval", "Approval", null);

    // An idle instance with an open "approval" wait.
    private static async Task<string> IdleInstanceAsync(IWorkflowStateRepository store, WorkflowStatus status)
    {
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Inbox", Status = status,
            WorkflowDataJson = "{}"
        };
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, 0, 0, openedWaits: [OpenApproval(instance.InstanceId)]))).Status);
        return instance.InstanceId;
    }
}
