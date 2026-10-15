using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The inbox contract every store meets (D-02, D-04, D-32): an event is stored and deduplicated by
/// its ID before it is acknowledged, makes a running or suspended instance runnable in the same
/// step, and is settled by the commit that takes it, at most once. A commit that has not read every
/// arrival keeps the instance runnable and does not park.
/// </summary>
[Trait("Feature", "waits")]
public sealed class InboxStoreTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

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
            var first = await inbox.AppendEventAsync(Event(instance, "e1", "{\"n\":1}"), Retention);
            Assert.Equal(new EventAppendResult(EventAppendStatus.Stored, 1, true), first);
            Assert.Contains(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == instance);

            // The same event again is the same delivery; another event under its ID is refused.
            Assert.Equal(EventAppendStatus.Duplicate,
                (await inbox.AppendEventAsync(Event(instance, "e1", "{\"n\":1}"), Retention)).Status);
            Assert.Equal(EventAppendStatus.Conflict,
                (await inbox.AppendEventAsync(Event(instance, "e1", "{\"n\":2}"), Retention)).Status);
            Assert.Equal(EventAppendStatus.Conflict, (await inbox.AppendEventAsync(
                Event(instance, "e1", "{\"n\":1}") with { TargetActivationId = "elsewhere" }, Retention)).Status);
            Assert.Equal(EventAppendStatus.NotFound,
                (await inbox.AppendEventAsync(Event(Guid.NewGuid().ToString("N"), "e1", "{}"), Retention)).Status);

            // Arrival order, paged by sequence.
            await inbox.AppendEventAsync(Event(instance, "e2", "{}"), Retention);
            await inbox.AppendEventAsync(Event(instance, "e3", "{}"), Retention);
            var full = await inbox.GetPendingEventsAsync(instance, 0, 2);
            Assert.Equal(["e1", "e2"], full.Events.Select(item => item.EventId));
            Assert.Equal(2, full.ReadThrough);
            var rest = await inbox.GetPendingEventsAsync(instance, 1, 10);
            Assert.Equal(["e2", "e3"], rest.Events.Select(item => item.EventId));
            Assert.Equal(3, rest.ReadThrough);

            // A stopped instance keeps the event for after its release, without becoming runnable.
            var held = await inbox.AppendEventAsync(Event(stopped, "s1", "{}"), Retention);
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
            await inbox.AppendEventAsync(Event(instanceId, "taken", "{}"), Retention);
            await inbox.AppendEventAsync(Event(instanceId, "refused", "{}"), Retention);
            var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
            saved.Status = WorkflowStatus.Suspended;
            saved.RunnableAtUtc = null;

            // The commit that takes an event settles it, and records how far it read.
            var commit = CheckpointCommit.Create(saved, 0, saved.Revision, settlements:
            [
                new EventSettlement("taken", EventOutcome.Consumed, "activation-1"),
                new EventSettlement("refused", EventOutcome.Rejected, Reason: "matches two waits")
            ], inboxReadThrough: 2);
            var applied = await store.CommitWorkflowInstanceAsync(commit);
            Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
            Assert.False(applied.UnreadEvents);
            var taken = (await inbox.GetEventAsync(instanceId, "taken"))!;
            Assert.Equal(EventOutcome.Consumed, taken.Outcome);
            Assert.Equal("activation-1", taken.ActivationId);
            Assert.Equal(EventOutcome.Rejected, (await inbox.GetEventAsync(instanceId, "refused"))!.Outcome);
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
                    settlements: [new EventSettlement("taken", EventOutcome.Consumed, "activation-2")]))).Status);

            // An idle commit that has not read the latest arrival stays runnable now.
            await inbox.AppendEventAsync(Event(instanceId, "late", "{}"), Retention);
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
            var started = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, 0));
            instance.Revision = started.Revision;
            await inbox.AppendEventAsync(Event(instanceId, "arrived", "{}"), Retention);

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
    public Task MemoryStoreClosesAndExpires() => VerifyClosedAndExpired(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-02")]
    public Task SqlStoreClosesAndExpires() =>
        VerifyClosedAndExpired(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyClosedAndExpired(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var expiring = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        var finished = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        try
        {
            // An event no wait takes expires; it is settled, not forgotten, so a redelivery is known.
            await inbox.AppendEventAsync(Event(expiring, "short", "{}"), TimeSpan.FromMilliseconds(500));
            await Task.Delay(1500);
            Assert.Empty((await inbox.GetPendingEventsAsync(expiring, 0, 10)).Events);

            // Consuming it now is refused without touching the instance.
            var current = (await store.GetWorkflowInstanceAsync(expiring))!;
            Assert.Equal(WorkflowCommitStatus.EventsChanged, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(current, 0, current.Revision,
                    settlements: [new EventSettlement("short", EventOutcome.Consumed, "late-activation")]))).Status);
            Assert.True(await inbox.SettleExpiredEventsAsync(1000) >= 1);
            Assert.Equal(EventOutcome.Expired, (await inbox.GetEventAsync(expiring, "short"))!.Outcome);
            Assert.Equal(EventAppendStatus.Duplicate,
                (await inbox.AppendEventAsync(Event(expiring, "short", "{}"), Retention)).Status);

            // A finished instance takes no new events, but still recognizes one it was sent.
            await inbox.AppendEventAsync(Event(finished, "before", "{}"), Retention);
            var saved = (await store.GetWorkflowInstanceAsync(finished))!;
            saved.Status = WorkflowStatus.Completed;
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, 0, saved.Revision))).Status);
            Assert.Equal(EventAppendStatus.Closed,
                (await inbox.AppendEventAsync(Event(finished, "after", "{}"), Retention)).Status);
            Assert.Equal(EventAppendStatus.Duplicate,
                (await inbox.AppendEventAsync(Event(finished, "before", "{}"), Retention)).Status);
            // The event no wait took before the end is closed.
            Assert.Equal(EventOutcome.Closed, (await inbox.GetEventAsync(finished, "before"))!.Outcome);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(expiring);
            await store.DeleteWorkflowInstanceAsync(finished);
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
            new EventSettlement("e1", EventOutcome.Rejected, Reason: "again")
        ]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 0, 0,
            settlements: [new EventSettlement("e1", EventOutcome.Expired)]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 0, 0,
            settlements: [new EventSettlement("e1", EventOutcome.Consumed)]));

        // The commit keeps its own copy, so changing the list afterwards changes nothing.
        var settlements = new List<EventSettlement> { new("e1", EventOutcome.Consumed, "a") };
        var commit = CheckpointCommit.Create(instance, 0, 0, settlements: settlements);
        settlements.Clear();
        Assert.Single(commit.Settlements);
    }

    private static WorkflowEvent Event(string instanceId, string eventId, string payload) =>
        new(instanceId, eventId, "Approval", payload, Key: "approval");

    private static async Task<string> IdleInstanceAsync(IWorkflowStateRepository store, WorkflowStatus status)
    {
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Inbox", Status = status,
            WorkflowDataJson = "{}"
        };
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0))).Status);
        return instance.InstanceId;
    }
}
