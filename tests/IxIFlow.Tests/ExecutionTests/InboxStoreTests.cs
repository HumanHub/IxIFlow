using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using System.Text.Json;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The inbox and waits contract every store meets (D-02, D-03, D-04, D-32, D-33): an event is stored
/// and deduplicated by its ID before it is acknowledged, is refused on arrival when no wait could take
/// it, makes a running or suspended instance runnable in the same step, and is settled by the commit
/// that takes it, at most once. Commits open and close waits, and a wait opens at the inbox's sequence.
/// A commit that has not read every arrival keeps the instance runnable and does not park.
/// </summary>
[Trait("Feature", "waits")]
public sealed class InboxStoreTests : IClassFixture<SqliteTestDatabase<InboxStoreTests>>
{
    [Fact]
    [Trait("Spec", "D-02")]
    public Task MemoryStoreAppendsEvents() => VerifyAppend(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-02")]
    public Task SqlStoreAppendsEvents() =>
        VerifyAppend(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-02")]
    public Task PostgreSqlStoreAppendsEvents() =>
        VerifyAppend(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-02")]
    public async Task SqliteStoreAppendsEvents()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyAppend(repository);
    }

    private static async Task VerifyAppend(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var instance = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        var stopped = await IdleInstanceAsync(store, WorkflowStatus.NeedsResolution, operatorStop: true);
        var unresolved = await IdleInstanceAsync(store, WorkflowStatus.NeedsResolution);
        try
        {
            var first = await inbox.AppendEventAsync(Event(instance, "e1", "{\"n\":1}"));
            Assert.Equal(new EventAppendResult(EventAppendStatus.Stored, 1, true), first with { Notice = null });
            Assert.NotNull(first.Notice);
            Assert.Equal(instance, first.Notice.InstanceId);
            Assert.True(first.Notice.Claimable);
            Assert.Contains(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == instance);

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
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == stopped);
            Assert.Equal(EventOutcome.Pending, (await inbox.GetEventAsync(stopped, "s1"))!.Outcome);

            // An unresolved activity in one branch does not hold another branch's open wait.
            var resolution = (await store.GetWorkflowInstanceAsync(unresolved))!;
            var checkpoint = ExecutionCheckpoint.Read(resolution.ExecutionStateJson);
            var pending = checkpoint.Continuations[0].PendingActivity!.Id;
            Assert.Null(checkpoint.OperatorStop);
            var arrival = await inbox.AppendEventAsync(Event(unresolved, "r1", "{}"));
            Assert.Equal(EventAppendStatus.Stored, arrival.Status);
            Assert.True(arrival.MadeRunnable);
            Assert.Contains(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)),
                item => item.InstanceId == unresolved);
            var page = await inbox.GetPendingEventsAsync(unresolved, 0, 10);
            Assert.Equal("r1", Assert.Single(page.Events).EventId);
            checkpoint.Continuations[1].Status = ContinuationStatus.Completed;
            checkpoint.Waits.Clear();
            resolution.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            var consumed = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(resolution,
                (await StoreTestSupport.LeaseAsync(store, unresolved)).Epoch, resolution.Revision,
                settlements: [new EventSettlement("r1", EventOutcome.Consumed, ApprovalWait(unresolved))],
                closedWaits: [new ClosedWait(ApprovalWait(unresolved), WaitCloseReason.Answered)],
                inboxReadThrough: page.ReadThrough));
            Assert.Equal(WorkflowCommitStatus.Applied, consumed.Status);
            Assert.Equal(EventOutcome.Consumed, (await inbox.GetEventAsync(unresolved, "r1"))!.Outcome);
            Assert.Empty((await inbox.GetPendingEventsAsync(unresolved, 0, 10)).Events);
            var stillUnresolved = (await store.GetWorkflowInstanceAsync(unresolved))!;
            Assert.Equal(WorkflowStatus.NeedsResolution, stillUnresolved.Status);
            var retained = ExecutionCheckpoint.Read(stillUnresolved.ExecutionStateJson).Continuations[0];
            Assert.Equal(ContinuationStatus.WaitingResolution, retained.Status);
            Assert.Equal(pending, retained.PendingActivity!.Id);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instance);
            await store.DeleteWorkflowInstanceAsync(stopped);
            await store.DeleteWorkflowInstanceAsync(unresolved);
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
        VerifySettlement(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-04")]
    public Task PostgreSqlStoreSettlesEventsInTheCommit() =>
        VerifySettlement(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-04")]
    public async Task SqliteStoreSettlesEventsInTheCommit()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifySettlement(repository);
    }

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
            var commit = CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision, settlements:
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
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == instanceId);

            // Sending the same commit again is acknowledged; settling the event again is not.
            // A fresh arrival makes the next pass eligible before it attempts that settlement.
            await inbox.AppendEventAsync(Event(instanceId, "late", "{}"));
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
            saved.Revision = applied.Revision;
            Assert.Equal(WorkflowCommitStatus.EventsChanged, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision,
                    settlements: [new EventSettlement("taken", EventOutcome.Consumed, "wait-2")]))).Status);

            // An idle commit that has not read the latest arrival stays runnable now.
            var behindCommit = CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision, inboxReadThrough: 2);
            var behind = await store.CommitWorkflowInstanceAsync(behindCommit);
            Assert.Equal(WorkflowCommitStatus.Applied, behind.Status);
            Assert.True(behind.UnreadEvents);
            await StoreTestSupport.ReleaseAsync(store, saved.InstanceId);
            Assert.Contains(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == instanceId);
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
        VerifyNoParkPastUnread(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-04")]
    public Task PostgreSqlStoreDoesNotParkPastAnUnreadEvent() =>
        VerifyNoParkPastUnread(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-04")]
    public async Task SqliteStoreDoesNotParkPastAnUnreadEvent()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyNoParkPastUnread(repository);
    }

    private static async Task VerifyNoParkPastUnread(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            var grant = (await store.TryAcquireExecutionLeaseAsync(instanceId, "owner", TimeSpan.FromSeconds(30), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            var instance = new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId, WorkflowName = "InboxPark", Status = WorkflowStatus.Running,
                WorkflowDataJson = StoredJson.Inline("{}")
            };
            var started = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, 0,
                openedWaits: [OpenApproval(instanceId)]));
            instance.Revision = started.Revision;
            await inbox.AppendEventAsync(Event(instanceId, "arrived", "{}"));

            var park = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch,
                instance.Revision, parks: true, wakeAfter: TimeSpan.FromHours(1), inboxReadThrough: 0));

            // The run keeps its lease and is told to read the inbox first.
            Assert.Equal(WorkflowCommitStatus.Applied, park.Status);
            Assert.False(park.Parked);
            Assert.True(park.UnreadEvents);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instanceId, "other", TimeSpan.FromSeconds(5), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));

            // So does an idle commit that has not read the arrival.
            instance.Revision = park.Revision;
            instance.Status = WorkflowStatus.Suspended;
            instance.RunnableAtUtc = null;
            var idle = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch,
                instance.Revision, inboxReadThrough: 0));
            Assert.True(idle.UnreadEvents);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instanceId, "other", TimeSpan.FromSeconds(5), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
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
        VerifyClosed(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-02")]
    public Task PostgreSqlStoreCloses() =>
        VerifyClosed(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-02")]
    public async Task SqliteStoreCloses()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyClosed(repository);
    }

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
                (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision))).Status);
            Assert.Equal(EventAppendStatus.Closed,
                (await inbox.AppendEventAsync(Event(finished, "after", "{}"))).Status);
            Assert.Equal(EventAppendStatus.Duplicate,
                (await inbox.AppendEventAsync(Event(finished, "before", "{}"))).Status);
            // The event no wait took before the end is closed, and so is the wait.
            Assert.Equal(EventOutcome.Closed, (await inbox.GetEventAsync(finished, "before"))!.Outcome);
            Assert.Equal(WaitCloseReason.InstanceFinished,
                (await inbox.GetWaitAsync(finished, ApprovalWait(finished)))!.CloseReason);
            // An event sent to one of its waits is told why that wait closed.
            Assert.Equal(new EventAppendResult(EventAppendStatus.Rejected, Rejection: EventRejection.InstanceFinished),
                await inbox.AppendEventAsync(Event(finished, "to-wait", "{}") with
                {
                    Key = null, TargetWaitId = ApprovalWait(finished)
                }));
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
        VerifyRefusedOnArrival(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task PostgreSqlStoreRefusesEventsNoWaitCanTake() =>
        VerifyRefusedOnArrival(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-03")]
    public async Task SqliteStoreRefusesEventsNoWaitCanTake()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyRefusedOnArrival(repository);
    }

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
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == instanceId);

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
                CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision,
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
        VerifyWaitSequence(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task PostgreSqlStoreOpensWaitsAtTheInboxSequence() =>
        VerifyWaitSequence(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-03")]
    public async Task SqliteStoreOpensWaitsAtTheInboxSequence()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyWaitSequence(repository);
    }

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
            // so the owner can tell that the event came before it. Its deadline is the commit's time on
            // the store's clock plus its timeout (D-35).
            var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
            var timeout = TimeSpan.FromDays(30);
            var applied = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision,
                openedWaits:
                [
                    new OpenedWait(next, WaitKind.Event, "approval", "Approval", timeout),
                    new OpenedWait(child, WaitKind.Child, "shipment", "Shipment", null)
                ]));
            Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
            var page = await inbox.GetPendingEventsAsync(instanceId, 0, 10);
            Assert.Equal(1, Assert.Single(page.Events).Sequence);
            Assert.Equal(0, page.OpenWaits[first]);
            Assert.Equal(1, page.OpenWaits[next]);
            var stored = (await inbox.GetWaitAsync(instanceId, next))!;
            Assert.Equal((WaitKind.Event, "approval", "Approval", timeout, 1L),
                (stored.Kind, stored.Key, stored.EventType, stored.DeadlineUtc - stored.OpenedAtUtc,
                    stored.OpenedSequence));
            Assert.True(stored.IsOpen);

            // Opening a wait again keeps its first record.
            saved.Revision = applied.Revision;
            await inbox.AppendEventAsync(Event(instanceId, "later", "{}"));
            applied = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision,
                openedWaits: [new OpenedWait(next, WaitKind.Event, "approval", "Approval", timeout)]));
            Assert.Equal(1, (await inbox.GetWaitAsync(instanceId, next))!.OpenedSequence);

            // A child's wait takes only its child's completion, addressed to it (D-17): an event with
            // another key finds no wait.
            Assert.Equal(EventRejection.NoOpenWait, (await inbox.AppendEventAsync(
                Event(instanceId, "for-child", "{}") with { Key = "anything" })).Rejection);
            Assert.Equal(EventAppendStatus.Stored, (await inbox.AppendEventAsync(
                Event(instanceId, "child-done", "{}") with { Key = null, TargetWaitId = child })).Status);

            // Open waits can be listed by instance, key, type and kind, in wait reference order, and paged.
            Assert.Equal(new[] { first, next, child }.OrderBy(id => WaitReference.Of(instanceId, id), StringComparer.Ordinal),
                (await inbox.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId))).Select(wait => wait.WaitId));
            Assert.Equal([child], (await inbox.GetOpenWaitsAsync(
                new WaitQuery(InstanceId: instanceId, Kind: WaitKind.Child))).Select(wait => wait.WaitId));
            var byKey = await inbox.GetOpenWaitsAsync(
                new WaitQuery(InstanceId: instanceId, Key: "approval", EventType: "Approval", Limit: 1));
            var firstByKey = Assert.Single(byKey);
            var secondByKey = Assert.Single(await inbox.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId,
                Key: "approval", EventType: "Approval", AfterWaitRef: WaitReference.Of(instanceId, firstByKey.WaitId))));
            Assert.Equal(new[] { first, next }.OrderBy(id => WaitReference.Of(instanceId, id), StringComparer.Ordinal),
                new[] { firstByKey.WaitId, secondByKey.WaitId });

            // A closed wait leaves the open set.
            saved.Revision = applied.Revision;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision,
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
    public Task MemoryStoreSettlesALargeBacklogInOneCommit() => VerifyLargeCommit(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-04")]
    public Task SqlStoreSettlesALargeBacklogInOneCommit() =>
        VerifyLargeCommit(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-04")]
    public Task PostgreSqlStoreSettlesALargeBacklogInOneCommit() =>
        VerifyLargeCommit(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    // More events and waits than one SQL statement can carry parameters for.

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-04")]
    public async Task SqliteStoreSettlesALargeBacklogInOneCommit()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyLargeCommit(repository);
    }

    // More events and waits than one SQL statement can carry parameters for.
    private static async Task VerifyLargeCommit(IWorkflowStateRepository store)
    {
        const int count = 450;
        var inbox = (IWorkflowInbox)store;
        var instanceId = await IdleInstanceAsync(store, WorkflowStatus.Suspended);
        try
        {
            for (var index = 0; index < count; index++)
                await inbox.AppendEventAsync(Event(instanceId, $"e{index}", "{}"));
            var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
            var commit = CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision,
                settlements: Enumerable.Range(0, count).Select(index => new EventSettlement($"e{index}",
                    EventOutcome.Rejected, Rejection: EventRejection.NotMatched)).ToArray(),
                inboxReadThrough: count,
                openedWaits: Enumerable.Range(0, count).Select(index => new OpenedWait($"{instanceId}-w{index}",
                    WaitKind.Event, "bulk", "Approval", null)).ToArray());

            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
            Assert.Empty((await inbox.GetPendingEventsAsync(instanceId, 0, 10)).Events);
            Assert.Equal(EventRejection.NotMatched, (await inbox.GetEventAsync(instanceId, $"e{count - 1}"))!.Rejection);
            Assert.Equal(count, (await inbox.GetOpenWaitsAsync(
                new WaitQuery(InstanceId: instanceId, Key: "bulk", Limit: 1000))).Count);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    [Trait("Spec", "D-04")]
    public async Task ACommitSettlesEachEventOnceAndOnlyAsConsumedOrRejected()
    {
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = "settling", WorkflowName = "Inbox" };

        var store = new InMemoryWorkflowStateRepository();
        var epoch = (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch;
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, epoch, 0, settlements:
        [
            new EventSettlement("e1", EventOutcome.Consumed, "a"),
            new EventSettlement("e1", EventOutcome.Rejected, Rejection: EventRejection.NotMatched)
        ]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, epoch, 0,
            settlements: [new EventSettlement("e1", EventOutcome.Pending)]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, epoch, 0,
            settlements: [new EventSettlement("e1", EventOutcome.Consumed)]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, epoch, 0,
            settlements: [new EventSettlement("e1", EventOutcome.Rejected)]));

        // The commit keeps its own copy, so changing the list afterwards changes nothing.
        var settlements = new List<EventSettlement> { new("e1", EventOutcome.Consumed, "a") };
        var commit = CheckpointCommit.Create(instance, epoch, 0, settlements: settlements);
        settlements.Clear();
        Assert.Single(commit.Settlements);
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task ACommitOpensOrClosesEachWaitOnceAndHashesWhatItChanges()
    {
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = "waiting", WorkflowName = "Inbox" };
        var opened = new OpenedWait("w1", WaitKind.Event, "approval", "Approval", null);

        var store = new InMemoryWorkflowStateRepository();
        var epoch = (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch;
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, epoch, 0,
            openedWaits: [opened], closedWaits: [new ClosedWait("w1", WaitCloseReason.Answered)]));
        Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, epoch, 0,
            openedWaits: [opened with { EventType = "" }]));

        // Two commits that differ only in the waits they change are different commits.
        var plain = CheckpointCommit.Create(instance, epoch, 0, commitId: "c1");
        var opening = CheckpointCommit.Create(instance, epoch, 0, commitId: "c1", openedWaits: [opened]);
        var closing = CheckpointCommit.Create(instance, epoch, 0, commitId: "c1",
            closedWaits: [new ClosedWait("w1", WaitCloseReason.Answered)]);
        Assert.NotEqual(plain.ContentHash, opening.ContentHash);
        Assert.NotEqual(plain.ContentHash, closing.ContentHash);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public Task MemoryStoreStartsAndCompletesChildrenInTheirCommits() =>
        VerifyChildren(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-06")]
    public Task SqlStoreStartsAndCompletesChildrenInTheirCommits() =>
        VerifyChildren(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-06")]
    public Task PostgreSqlStoreStartsAndCompletesChildrenInTheirCommits() =>
        VerifyChildren(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    // SQL Server stores a child's start as the parent's commit carries it, without reading and writing it again; the
    // child's revision and runnable time are in their columns.

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-06")]
    public async Task SqliteStoreStartsAndCompletesChildrenInTheirCommits()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyChildren(repository);
    }

    // SQL Server stores a child's start as the parent's commit carries it, without reading and writing it again; the
    // child's revision and runnable time are in their columns.
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-06")]
    public Task SqlStoreKeepsAChildStartAsItsPayloadCarriesIt() => VerifyChildStartPayload(new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString), () => new Microsoft.Data.SqlClient.SqlConnection(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-06")]
    public Task PostgreSqlStoreKeepsAChildStartAsItsPayloadCarriesIt() => VerifyChildStartPayload(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString), () => new Npgsql.NpgsqlConnection(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    // A parent's commit starts a child as its own instance with the parent's wait for it; the child's
    // terminal commit appends its completion to that wait in the parent's inbox. Neither needs a relay.
    private static async Task VerifyChildren(IWorkflowStateRepository store)
    {
        var inbox = (IWorkflowInbox)store;
        var parentId = await IdleInstanceAsync(store, WorkflowStatus.Running);
        var childId = Guid.NewGuid().ToString("N");
        var otherId = Guid.NewGuid().ToString("N");
        var wait = $"{parentId}-child";
        try
        {
            var parent = (await store.GetWorkflowInstanceAsync(parentId))!;
            var start = CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                openedWaits: [new OpenedWait(wait, WaitKind.Child, "", "IxIFlow.ChildCompleted", null)],
                childStarts: [new ChildStart(childId, System.Text.Json.JsonSerializer.Serialize(
                    Child(childId, parentId, wait)), wait)]);
            var started = await store.CommitWorkflowInstanceAsync(start);

            Assert.Equal(WorkflowCommitStatus.Applied, started.Status);
            Assert.Equal([childId], started.MadeRunnable.Select(notice => notice.InstanceId));
            var child = (await store.GetWorkflowInstanceAsync(childId))!;
            Assert.Equal((1L, WorkflowStatus.Running, parentId, wait),
                (child.Revision, child.Status, child.ParentInstanceId, child.ParentWaitId));
            Assert.Contains(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == childId);
            // The same commit again starts nothing more; another start under the child's ID is refused.
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(start)).Status);
            parent.Revision = started.Revision;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                    openedWaits: [new OpenedWait($"{wait}-again", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)],
                    childStarts: [new ChildStart(childId, System.Text.Json.JsonSerializer.Serialize(
                        Child(childId, parentId, $"{wait}-again")), $"{wait}-again")])));

            // A parent's commit can ask its child to cancel; the first request stands, as the commit was made.
            var reason = new CancellationReason { ReasonCode = "parent" };
            parent.Status = WorkflowStatus.Suspended;
            var cancelling = CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                childCancellations: [new ChildCancellation(childId, reason, wait)]);
            reason.ReasonCode = "changed after the commit was made";
            var cancelled = await store.CommitWorkflowInstanceAsync(cancelling);
            Assert.Equal(WorkflowCommitStatus.Applied, cancelled.Status);
            Assert.Equal("parent", (await store.GetCancellationRequestAsync(childId))!.ReasonCode);
            Assert.Equal([childId], (await Task.WhenAll((await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: parentId, Limit: int.MaxValue))).Items.Select(async row => (await store.GetWorkflowInstanceAsync(row.InstanceId))!))).Select(item => item.InstanceId));

            // The child's terminal commit puts its completion in the parent's inbox, addressed to the wait.
            var grant = (await store.TryAcquireExecutionLeaseAsync(childId, "owner", TimeSpan.FromMinutes(1), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            child.Status = WorkflowStatus.Completed;
            var completed = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(child, grant.Epoch, 1,
                parentNotice: new ParentNotice(parentId, wait, $"completed-{childId}", "IxIFlow.ChildCompleted",
                    "{\"Status\":\"Completed\"}")));
            Assert.Equal(WorkflowCommitStatus.Applied, completed.Status);
            Assert.Equal([parentId], completed.MadeRunnable.Select(notice => notice.InstanceId));
            var notice = (await inbox.GetEventAsync(parentId, $"completed-{childId}"))!;
            Assert.Equal((EventOutcome.Pending, wait), (notice.Outcome, notice.TargetWaitId));
            Assert.Contains(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == parentId);

            // A child whose parent no longer waits still finishes: the completion is recorded as refused.
            parent.Status = WorkflowStatus.Running;
            parent.Revision = cancelled.Revision;
            var closing = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                openedWaits: [new OpenedWait($"{wait}-2", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)],
                childStarts: [new ChildStart(otherId, System.Text.Json.JsonSerializer.Serialize(
                    Child(otherId, parentId, $"{wait}-2")), $"{wait}-2")]));
            parent.Revision = closing.Revision;
            await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                closedWaits: [new ClosedWait($"{wait}-2", WaitCloseReason.Cancelled)]));
            var other = (await store.GetWorkflowInstanceAsync(otherId))!;
            var otherGrant = (await store.TryAcquireExecutionLeaseAsync(otherId, "owner", TimeSpan.FromMinutes(1), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            other.Status = WorkflowStatus.Failed;
            var late = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(other, otherGrant.Epoch, 1,
                parentNotice: new ParentNotice(parentId, $"{wait}-2", $"completed-{otherId}", "IxIFlow.ChildCompleted",
                    "{}")));
            Assert.Equal(WorkflowCommitStatus.Applied, late.Status);
            Assert.Empty(late.MadeRunnable);
            Assert.Equal(EventRejection.WaitCancelled,
                (await inbox.GetEventAsync(parentId, $"completed-{otherId}"))!.Rejection);

            // A child that is gone ends its parent's wait as terminated: one deleted while its parent waits,
            // and one missing when its parent asks it to cancel.
            parent = (await store.GetWorkflowInstanceAsync(parentId))!;
            var deletedChild = Guid.NewGuid().ToString("N");
            var missingChild = Guid.NewGuid().ToString("N");
            var starting = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                openedWaits:
                [
                    new OpenedWait($"{wait}-deleted", WaitKind.Child, "", "IxIFlow.ChildCompleted", null),
                    new OpenedWait($"{wait}-missing", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)
                ],
                childStarts: [new ChildStart(deletedChild, System.Text.Json.JsonSerializer.Serialize(
                    Child(deletedChild, parentId, $"{wait}-deleted")), $"{wait}-deleted")]));
            await store.DeleteWorkflowInstanceAsync(deletedChild);
            Assert.Equal((EventOutcome.Pending, $"{wait}-deleted"),
                ((await inbox.GetEventAsync(parentId, $"child-completed-{deletedChild}"))!.Outcome,
                    (await inbox.GetEventAsync(parentId, $"child-completed-{deletedChild}"))!.TargetWaitId));
            parent.Revision = starting.Revision;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision, childCancellations:
                [new ChildCancellation(missingChild, new CancellationReason { ReasonCode = "parent" }, $"{wait}-missing")]))).Status);
            Assert.Equal($"{wait}-missing",
                (await inbox.GetEventAsync(parentId, $"child-completed-{missingChild}"))!.TargetWaitId);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(parentId);
            await store.DeleteWorkflowInstanceAsync(childId);
            await store.DeleteWorkflowInstanceAsync(otherId);
        }
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public Task MemoryStoreGivesEachGoneChildItsOwnInboxPlace() =>
        VerifyGoneChildrenTakeTheirOwnPlaces(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-06")]
    public Task SqlStoreGivesEachGoneChildItsOwnInboxPlace() =>
        VerifyGoneChildrenTakeTheirOwnPlaces(
            new SqlWorkflowStateRepository(SqlTestDatabase<InboxStoreTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-06")]
    public Task PostgreSqlStoreGivesEachGoneChildItsOwnInboxPlace() =>
        VerifyGoneChildrenTakeTheirOwnPlaces(
            new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InboxStoreTests>.ConnectionString));

    // A commit that asks two missing children to cancel appends a completion for each to its own inbox. Each takes
    // its own sequence, so a reader that pages through the inbox one event at a time meets both.

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-06")]
    public async Task SqliteStoreGivesEachGoneChildItsOwnInboxPlace()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyGoneChildrenTakeTheirOwnPlaces(
            repository);
    }

    // A commit that asks two missing children to cancel appends a completion for each to its own inbox. Each takes
    // its own sequence, so a reader that pages through the inbox one event at a time meets both.
    private static async Task VerifyGoneChildrenTakeTheirOwnPlaces(IWorkflowStateRepository store)
    {
        var parentId = await IdleInstanceAsync(store, WorkflowStatus.Running);
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        try
        {
            var parent = (await store.GetWorkflowInstanceAsync(parentId))!;
            var opening = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                openedWaits:
                [
                    new OpenedWait($"{first}-wait", WaitKind.Child, "", "IxIFlow.ChildCompleted", null),
                    new OpenedWait($"{second}-wait", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)
                ]));
            parent.Revision = opening.Revision;
            var reason = new CancellationReason { ReasonCode = "parent" };
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision, childCancellations:
                [
                    new ChildCancellation(first, reason, $"{first}-wait"),
                    new ChildCancellation(second, reason, $"{second}-wait")
                ]))).Status);

            var firstPage = await store.GetPendingEventsAsync(parentId, 0, 1);
            var secondPage = await store.GetPendingEventsAsync(parentId, firstPage.Events.Single().Sequence, 1);

            Assert.Equal(new[] { $"child-completed-{first}", $"child-completed-{second}" }.Order(),
                firstPage.Events.Concat(secondPage.Events).Select(stored => stored.EventId).Order());
            Assert.Equal(firstPage.Events.Single().Sequence + 1, secondPage.Events.Single().Sequence);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(parentId);
        }
    }

    private static WorkflowInstance Child(string childId, string parentId, string waitId) => new()
    { ExecutionStateJson = StoreTestSupport.CheckpointJson,
        InstanceId = childId, WorkflowName = "Child", Status = WorkflowStatus.Running, WorkflowDataJson = StoredJson.Inline("{}"),
        ParentInstanceId = parentId, ParentWaitId = waitId
    };

    private static WorkflowEvent Event(string instanceId, string eventId, string payload) =>
        new(instanceId, eventId, "Approval", payload, Key: "approval");

    private static string ApprovalWait(string instanceId) => $"{instanceId}-approval";

    private static OpenedWait OpenApproval(string instanceId) =>
        new(ApprovalWait(instanceId), WaitKind.Event, "approval", "Approval", null);

    // An idle instance with an open "approval" wait.
    private static async Task<string> IdleInstanceAsync(IWorkflowStateRepository store, WorkflowStatus status,
        bool operatorStop = false)
    {
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Inbox", Status = status,
            WorkflowDataJson = StoredJson.Inline("{}")
        };
        if (status == WorkflowStatus.NeedsResolution)
        {
            var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            if (operatorStop)
                checkpoint.OperatorStop = new OperatorStopState
                    { Kind = OperatorStopKind.ResourceLimit, Reason = "test limit", StoppedAtUtc = DateTime.UtcNow };
            else
            {
                checkpoint.Continuations[0].Status = ContinuationStatus.WaitingResolution;
                checkpoint.Continuations[0].PendingActivity = new ActivityInvocationState
                    { StepId = "unconfirmed", ResolutionReason = "An ordinary activity has no confirmed end" };
                checkpoint.Continuations.Add(new ContinuationState { Id = "waiting", Status = ContinuationStatus.Waiting,
                    Stack = [new ScopePosition { ScopeId = "other-branch" }] });
                checkpoint.Waits.Add(new WaitState { Id = ApprovalWait(instance.InstanceId), ContinuationId = "waiting",
                    StepId = "approval", Key = "approval", EventType = "Approval" });
            }
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        }
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0, openedWaits: [OpenApproval(instance.InstanceId)]))).Status);
        return instance.InstanceId;
    }
    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-06")]
    public async Task SqliteStoreKeepsAChildStartAsItsPayloadCarriesIt()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<InboxStoreTests>.ConnectionString);
        await VerifyChildStartPayload(repository, () => new Microsoft.Data.Sqlite.SqliteConnection(SqliteTestDatabase<InboxStoreTests>.ConnectionString));
    }

    private static async Task VerifyChildStartPayload(IWorkflowStateRepository store, Func<System.Data.Common.DbConnection> newConnection)
    {
        var parentId = await IdleInstanceAsync(store, WorkflowStatus.Running);
        var childId = Guid.NewGuid().ToString("N");
        var wait = $"{parentId}-child";
        try
        {
            var parent = (await store.GetWorkflowInstanceAsync(parentId))!;
            var payload = System.Text.Json.JsonSerializer.Serialize(Child(childId, parentId, wait));
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                openedWaits: [new OpenedWait(wait, WaitKind.Child, "", "IxIFlow.ChildCompleted", null)],
                childStarts: [new ChildStart(childId, payload, wait)]))).Status);

            await using var connection = newConnection();
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = connection is Microsoft.Data.SqlClient.SqlConnection
                ? "SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId"
                : "SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId";
            var parameter = read.CreateParameter();
            parameter.ParameterName = connection is Npgsql.NpgsqlConnection ? "InstanceId" : "@InstanceId";
            parameter.Value = childId;
            read.Parameters.Add(parameter);
            // The child stores placement through its set reference, outside the trunk.
            var expected = System.Text.Json.Nodes.JsonNode.Parse(payload)!;
            expected["ExecutionStateJson"]!.AsObject().Remove("Demands");
            Assert.Equal(expected.ToJsonString(), (string)(await read.ExecuteScalarAsync())!);
            var child = (await store.GetWorkflowInstanceAsync(childId))!;
            Assert.Equal(1, child.Revision);
            Assert.NotNull(child.RunnableAtUtc);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(childId);
            await store.DeleteWorkflowInstanceAsync(parentId);
        }
    }


}
