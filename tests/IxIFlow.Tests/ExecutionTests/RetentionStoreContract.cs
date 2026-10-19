using IxIFlow.Tests.Infrastructure;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

public enum RetentionScenario
{
    RemovalAfterRetention, PendingDeliveriesKeepInstance, DependentRowsGone, TombstoneRefusals,
    TombstoneExpiry, ConcurrentPurge, AwaitAfterFinish, AwaitAfterRemoval, RunningChildKeepsParent,
    ChildCompletionSurvivesRemoval, BoundedAndCancelled, StoreClockAndReplay, ChildStartRefusesTombstone, DeliveredOutboxRowsGone,
    ExplicitDeleteLeavesTombstone, DecoratedStoreKeepsRetention, UpdateKeepsParent, IncarnationFencesReuse,
    PurgeMeetsStart, PurgeMeetsChildStart, PurgeMeetsEvent
}

internal sealed class RetentionStoreContract(IWorkflowStateRepository store,
    Func<IReadOnlyCollection<string>, TimeSpan, Task> advance, Func<DbConnection>? newConnection = null)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan Tombstones = TimeSpan.FromDays(90);
    private readonly List<string> _ids = [];
    private readonly Dictionary<string, long> _epochs = [];
    private readonly List<string> _publishers = [];
    private Task RegisterPublisher(string name)
    {
        _publishers.Add(name);
        return Outbox.RegisterPublisherAsync(name, [OutboxEvent.WorkflowCompleted]);
    }
    private IWorkflowOutbox Outbox => Assert.IsAssignableFrom<IWorkflowOutbox>(store);
    private Task<RetentionResult> Purge(int size = 100, CancellationToken token = default) =>
        store.PurgeAsync(Retention, Tombstones, size, token);
    private Task Advance(TimeSpan by) => advance(_ids, by);
    private WorkflowInstance Instance(WorkflowStatus status = WorkflowStatus.Completed, string? parent = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _ids.Add(id);
        return new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = id, WorkflowName = "Retention", WorkflowVersion = 1,
            Status = status, ParentInstanceId = parent, WorkflowDataType = typeof(ResultData).AssemblyQualifiedName!,
            WorkflowDataJson = StoredJson.Inline("""{"Value":42}"""), CompletedAt = DateTime.MinValue };
    }
    private async Task<CheckpointCommit> Commit(WorkflowInstance instance, ParentNotice? notice = null)
    {
        var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, instance.Revision, parentNotice: notice);
        var result = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        instance.Revision = result.Revision;
        _epochs[instance.InstanceId] = commit.Epoch;
        await StoreTestSupport.ReleaseAsync(store, instance.InstanceId);
        return commit;
    }
    private async Task<WorkflowInstance> Complete()
    {
        var instance = Instance();
        await Commit(instance);
        return instance;
    }
    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton(store);
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    internal async Task VerifyAsync(RetentionScenario scenario)
    {
        try
        {
        switch (scenario)
        {
            case RetentionScenario.RemovalAfterRetention: await RemovalAfterRetention(); break;
            case RetentionScenario.PendingDeliveriesKeepInstance: await PendingDeliveriesKeepInstance(); break;
            case RetentionScenario.DependentRowsGone: await DependentRowsGone(); break;
            case RetentionScenario.TombstoneRefusals: await TombstoneRefusals(); break;
            case RetentionScenario.TombstoneExpiry: await TombstoneExpiry(); break;
            case RetentionScenario.ConcurrentPurge: await ConcurrentPurge(); break;
            case RetentionScenario.AwaitAfterFinish: await AwaitAfterFinish(); break;
            case RetentionScenario.AwaitAfterRemoval: await AwaitAfterRemoval(); break;
            case RetentionScenario.RunningChildKeepsParent: await RunningChildKeepsParent(); break;
            case RetentionScenario.ChildCompletionSurvivesRemoval: await ChildCompletionSurvivesRemoval(); break;
            case RetentionScenario.BoundedAndCancelled: await BoundedAndCancelled(); break;
            case RetentionScenario.StoreClockAndReplay: await StoreClockAndReplay(); break;
            case RetentionScenario.ChildStartRefusesTombstone: await ChildStartRefusesTombstone(); break;
            case RetentionScenario.DeliveredOutboxRowsGone: await DeliveredOutboxRowsGone(); break;
            case RetentionScenario.ExplicitDeleteLeavesTombstone: await ExplicitDeleteLeavesTombstone(); break;
            case RetentionScenario.DecoratedStoreKeepsRetention: await DecoratedStoreKeepsRetention(); break;
            case RetentionScenario.UpdateKeepsParent: await UpdateKeepsParent(); break;
            case RetentionScenario.IncarnationFencesReuse: await IncarnationFencesReuse(); break;
            case RetentionScenario.PurgeMeetsStart:
            case RetentionScenario.PurgeMeetsChildStart:
            case RetentionScenario.PurgeMeetsEvent: await PurgeMeetsWriter(scenario); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        }
        finally
        {
            foreach (var publisher in _publishers) await Outbox.UnregisterPublisherAsync(publisher);
            foreach (var id in _ids) await store.DeleteWorkflowInstanceAsync(id);
        }
    }

    private async Task RemovalAfterRetention()
    {
        var finished = await Complete();
        var running = Instance(WorkflowStatus.Running);
        await Commit(running);
        await Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, (await Purge()).InstancesRemoved);
        Assert.NotNull(await store.GetWorkflowInstanceAsync(finished.InstanceId));
        await Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await Purge()).InstancesRemoved);
        Assert.Null(await store.GetWorkflowInstanceAsync(finished.InstanceId));
        Assert.NotNull(await store.GetWorkflowInstanceAsync(running.InstanceId));
        var tombstone = (await store.GetTombstoneAsync(finished.InstanceId))!;
        Assert.Equal(Tombstones, tombstone.ExpiresAtUtc - tombstone.RemovedAtUtc);
    }

    private async Task PendingDeliveriesKeepInstance()
    {
        var publisher = Guid.NewGuid().ToString("N");
        await RegisterPublisher(publisher);
        var instance = await Complete();
        await Advance(TimeSpan.FromDays(31));
        var skipped = await Purge();
        Assert.Equal(0, skipped.InstancesRemoved);
        Assert.Equal(1, skipped.SkippedUnattemptedByPublisher[publisher]);
        var delivery = Assert.Single(await Outbox.ClaimDeliveriesAsync(publisher, "leased", TimeSpan.FromHours(1), 100));
        var attempted = await Purge();
        Assert.Equal(0, attempted.InstancesRemoved);
        Assert.DoesNotContain(publisher, attempted.SkippedUnattemptedByPublisher.Keys);
        Assert.NotNull(await store.GetWorkflowInstanceAsync(instance.InstanceId));
        Assert.Null(await store.GetTombstoneAsync(instance.InstanceId));
        Assert.NotNull(await Outbox.GetDeliveryAsync(delivery.Event.Id, publisher));
        Assert.True(await Outbox.SettleDeliveryAsync(delivery.Event.Id, publisher, "leased", OutboxPublishResult.GiveUp("gone", disablePublisher: true)));
        Assert.Equal(1, (await Purge()).InstancesRemoved);
        Assert.Null(await Outbox.GetDeliveryAsync(delivery.Event.Id, publisher));
    }

    private async Task DependentRowsGone()
    {
        var instance = Instance(WorkflowStatus.Suspended);
        instance.ExecutionStateJson = JsonSerializer.Serialize(new ExecutionCheckpoint
        {
            StructureHash = "retention", Continuations = [new ContinuationState
                { Id = "branch", Status = ContinuationStatus.Waiting, Stack = [new ScopePosition { ScopeId = "root" }] }]
        });
        var first = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0, openedWaits:
            [new OpenedWait("wait", WaitKind.Event, "reply", "reply-type", null)],
            traceEntries: [new ExecutionTraceEntry { ActivityName = "step" }]);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(first)).Status);
        var received = await store.AppendEventAsync(new WorkflowEvent(instance.InstanceId, "event", "reply-type", "{}", "reply"));
        Assert.Equal(EventAppendStatus.Stored, received.Status);
        Assert.NotEmpty(await store.GetTraceAsync(instance.InstanceId));
        var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.NotEmpty(saved.SavedParts!);
        saved.Status = WorkflowStatus.Completed;
        await Commit(saved);
        await Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await Purge()).InstancesRemoved);
        Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));
        Assert.Null(await store.GetWaitAsync(instance.InstanceId, "wait"));
        Assert.Null(await store.GetEventAsync(instance.InstanceId, "event"));
        Assert.Empty(await store.GetTraceAsync(instance.InstanceId));
        Assert.Empty((await store.GetPendingEventsAsync(instance.InstanceId, 0, 100)).Events);
        if (newConnection != null)
        {
            await using var connection = newConnection();
            var sqlServer = connection is Microsoft.Data.SqlClient.SqlConnection;
            foreach (var table in new[] { "Parts", "Commits", "Events", "Waits", "Traces", "Instances" })
            {
                var tableName = sqlServer ? "dbo.IxIFlowWorkflow" + table : "ixiflow_workflow_" + table.ToLowerInvariant();
                var column = sqlServer ? "InstanceId" : "instance_id";
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                    $"SELECT count(*) FROM {tableName} WHERE {column}=@Id", new { Id = instance.InstanceId }));
            }
        }
        await Advance(Tombstones);
        await Purge();
        // Reusing the old commit ID under a new granted epoch proves its receipt was removed too.
        first = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch,
            0, first.CommitId, openedWaits: first.OpenedWaits, traceEntries: first.ReadTraceEntries());
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(first)).Status);
        var recreated = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal(1, recreated.Revision);
        Assert.Equal(1, Assert.Single(await store.GetTraceAsync(instance.InstanceId)).Sequence);
    }

    private async Task TombstoneRefusals()
    {
        var instance = await Complete();
        await Advance(TimeSpan.FromDays(31));
        await Purge();
        var exception = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(InstanceWithId(instance.InstanceId), _epochs[instance.InstanceId], 0)));
        Assert.Equal(WorkflowErrorKind.InstanceRemoved, exception.Kind);
        var saved = InstanceWithId(instance.InstanceId);
        Assert.Equal(WorkflowErrorKind.InstanceRemoved, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, _epochs[instance.InstanceId], instance.Revision)))).Kind);
        foreach (var target in new string?[] { null, "old-wait" })
            Assert.Equal(EventAppendStatus.Closed, (await store.AppendEventAsync(
                new WorkflowEvent(instance.InstanceId, Guid.NewGuid().ToString("N"), "event", "{}", TargetWaitId: target))).Status);
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ResultData>("TombstoneStart").Step<Noop>().Build();
        var start = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(definition,
            new ResultData(), new WorkflowOptions { InstanceId = instance.InstanceId }));
        Assert.Equal(WorkflowErrorKind.InstanceRemoved, start.Kind);
        await Refused(() => client.StartAsync(definition, new ResultData(), new WorkflowOptions { InstanceId = instance.InstanceId }));
        await Refused(() => client.ResumeAsync(instance.InstanceId, new ResultData()));
        await Refused(() => client.ResumeAsync(instance.InstanceId, "key", new ResultData()));
        await Refused(() => client.ResumeAsync(instance.InstanceId, null, new ResultData(), "late"));
        await Refused(() => client.ReplyAsync(ReplyToken.Create(instance.InstanceId, "old-wait"), new ResultData()));
        await Refused(() => client.ReplyAsync(ReplyToken.Create(instance.InstanceId, "old-wait"), new ResultData(), "reply"));
        await Refused(() => client.CancelAsync(instance.InstanceId, new CancellationReason { ReasonCode = "operator" }));
        await Refused(() => client.RecoverAsync(instance.InstanceId));
        await Refused(() => client.ReleaseAsync(instance.InstanceId));
        await Refused(() => client.ResolveAsync(instance.InstanceId, "activity", ActivityResolution.Completed(new Dictionary<string, object?>())));

        static async Task Refused(Func<Task> action) => Assert.Equal(WorkflowErrorKind.InstanceRemoved,
            (await Assert.ThrowsAsync<WorkflowEngineException>(action)).Kind);
    }

    private static WorkflowInstance InstanceWithId(string id) => new()
        { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = id, WorkflowName = "Replacement", Status = WorkflowStatus.Running };

    private async Task TombstoneExpiry()
    {
        var instance = await Complete();
        await Advance(TimeSpan.FromDays(31));
        await Purge();
        await Advance(TimeSpan.FromDays(89));
        Assert.NotNull(await store.GetTombstoneAsync(instance.InstanceId));
        Assert.Equal(0, (await Purge()).TombstonesRemoved);
        await Advance(TimeSpan.FromDays(2));
        Assert.Null(await store.GetTombstoneAsync(instance.InstanceId));
        // Admission consults expiry without waiting for a sweep.
        await store.SeedAsync(InstanceWithId(instance.InstanceId));
        Assert.Equal(1, (await Purge()).TombstonesRemoved);
        Assert.NotNull(await store.GetWorkflowInstanceAsync(instance.InstanceId));
        Assert.Equal(EventAppendStatus.Rejected, (await store.AppendEventAsync(
            new WorkflowEvent(instance.InstanceId, "new-event", "type", "{}"))).Status);
    }

    private async Task ConcurrentPurge()
    {
        var instances = new List<WorkflowInstance>();
        for (var i = 0; i < 8; i++) instances.Add(await Complete());
        await Advance(TimeSpan.FromDays(31));
        var passes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => Purge(3))));
        Assert.All(passes, pass => Assert.InRange(pass.InstancesRemoved, 0, 3));
        var total = passes.Sum(pass => pass.InstancesRemoved);
        // Concurrent passes may select the same advisory candidates; subsequent passes drain the rest.
        while (total < 8)
        {
            var next = (await Purge(3)).InstancesRemoved;
            Assert.InRange(next, 1, 3);
            total += next;
        }
        Assert.Equal(8, total);
        foreach (var instance in instances)
        {
            Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));
            Assert.NotNull(await store.GetTombstoneAsync(instance.InstanceId));
        }
    }

    private async Task AwaitAfterFinish()
    {
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        foreach (var status in new[] { WorkflowStatus.Completed, WorkflowStatus.Failed, WorkflowStatus.Cancelled,
                     WorkflowStatus.Terminated, WorkflowStatus.TimedOut })
        {
            var instance = Instance(status);
            instance.LastError = status switch
            {
                WorkflowStatus.Failed => "failure retained",
                WorkflowStatus.Cancelled => "operator cancellation retained",
                WorkflowStatus.Terminated => "termination retained",
                WorkflowStatus.TimedOut => "timeout retained",
                _ => null
            };
            instance.LastErrorStackTrace = status == WorkflowStatus.Failed ? "failure stack retained" : null;
            if (status == WorkflowStatus.Cancelled)
                instance.CancellationReason = new CancellationReason
                    { ReasonCode = "operator", Description = instance.LastError! };
            await Commit(instance);
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await client.AwaitAsync(instance.InstanceId, WorkflowAwaitCondition.Finished, patience.Token)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(instance.InstanceId, result.InstanceId);
            Assert.Equal(42, Assert.IsType<ResultData>(result.WorkflowData).Value);
            Assert.Equal(instance.LastError, result.ErrorMessage);
            Assert.NotEqual(WorkflowExecutionStatus.Running, result.Status);
            Assert.Equal(status switch
            {
                WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
                WorkflowStatus.Failed => WorkflowExecutionStatus.Faulted,
                WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
                WorkflowStatus.Terminated => WorkflowExecutionStatus.Failed,
                WorkflowStatus.TimedOut => WorkflowExecutionStatus.TimedOut,
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            }, result.Status);
            Assert.Equal(instance.LastErrorStackTrace, result.ErrorStackTrace);
        }
    }

    private async Task AwaitAfterRemoval()
    {
        var instance = await Complete();
        await Advance(TimeSpan.FromDays(31));
        await Purge();
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Removed, await client.GetStatusAsync(instance.InstanceId));
        var trace = await client.GetTraceAsync(instance.InstanceId);
        Assert.Empty(trace);
        Assert.True(trace.IsRemoved);
        foreach (var kind in new WaitKind?[] { null, WaitKind.Event, WaitKind.Child })
        {
            var waits = await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: instance.InstanceId, Kind: kind));
            Assert.Empty(waits);
            Assert.True(waits.IsRemoved);
        }
        var activities = await client.GetPendingActivitiesAsync(instance.InstanceId);
        Assert.Empty(activities);
        Assert.True(activities.IsRemoved);
        Assert.True((await client.GetSnapshotAsync(instance.InstanceId))!.IsRemoved);
        foreach (var condition in new[] { WorkflowAwaitCondition.Idle, WorkflowAwaitCondition.Finished })
        {
            var result = await client.AwaitAsync(instance.InstanceId, condition);
            Assert.Equal(WorkflowExecutionStatus.Removed, result.Status);
            Assert.Null(result.WorkflowData);
            Assert.Contains("removed", result.ErrorMessage);
        }
    }

    private async Task RunningChildKeepsParent()
    {
        var parent = await Complete();
        var child = Instance(WorkflowStatus.Running, parent.InstanceId);
        child.ParentWaitId = "detached-wait";
        await Commit(child);
        await Advance(TimeSpan.FromDays(31));
        Assert.Equal(0, (await Purge()).InstancesRemoved);
        Assert.NotNull(await store.GetWorkflowInstanceAsync(parent.InstanceId));
        child.Status = WorkflowStatus.Completed;
        await Commit(child, new ParentNotice(parent.InstanceId, "detached-wait", "done", "child", """{"Value":42}"""));
        Assert.Equal(1, (await Purge()).InstancesRemoved);
        Assert.Null(await store.GetWorkflowInstanceAsync(parent.InstanceId));
        Assert.NotNull(await store.GetWorkflowInstanceAsync(child.InstanceId));
    }

    private async Task ChildCompletionSurvivesRemoval()
    {
        var parent = Instance(WorkflowStatus.Suspended);
        var child = Instance(WorkflowStatus.Running, parent.InstanceId);
        child.ParentWaitId = "child-wait";
        var start = CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, 0,
            openedWaits: [new OpenedWait("child-wait", WaitKind.Child, "child", "child-result", null)],
            childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child-wait")]);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(start)).Status);
        // The store gives a started child its own incarnation; its later commits must name it.
        child.Incarnation = (await store.GetWorkflowInstanceAsync(child.InstanceId))!.Incarnation;
        child.Revision = 1;
        child.Status = WorkflowStatus.Completed;
        await Commit(child, new ParentNotice(parent.InstanceId, "child-wait", "child-done", "child-result", """{"Value":42}"""));
        await Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await Purge()).InstancesRemoved);
        Assert.Null(await store.GetWorkflowInstanceAsync(child.InstanceId));
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(start)).Status);
        Assert.True((await store.GetWaitAsync(parent.InstanceId, "child-wait"))!.IsOpen);
        Assert.Equal("""{"Value":42}""", Assert.Single((await store.GetPendingEventsAsync(parent.InstanceId, 0, 100)).Events).PayloadJson.InlineText);
        Assert.Contains(parent.InstanceId, (await store.GetRunnableInstancesAsync(100)).Select(i => i.InstanceId));
    }

    private async Task BoundedAndCancelled()
    {
        for (var i = 0; i < 5; i++) await Complete();
        await Advance(TimeSpan.FromDays(31));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Purge(token: cancelled.Token));
        Assert.Equal(2, (await Purge(2)).InstancesRemoved);
        Assert.Equal(2, (await Purge(2)).InstancesRemoved);
        Assert.Equal(1, (await Purge(2)).InstancesRemoved);
        await Advance(TimeSpan.FromDays(91));
        Assert.Equal(2, (await Purge(2)).TombstonesRemoved);
        Assert.Equal(2, (await Purge(2)).TombstonesRemoved);
        Assert.Equal(1, (await Purge(2)).TombstonesRemoved);
    }

    private async Task StoreClockAndReplay()
    {
        var instance = Instance();
        var first = await Commit(instance);
        Assert.Equal(0, (await Purge()).InstancesRemoved); // CompletedAt is year 1: ignored.
        await Advance(TimeSpan.FromDays(29));
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(first)).Status);
        await Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await Purge()).InstancesRemoved); // Replay did not restart retention.
    }

    private async Task ChildStartRefusesTombstone()
    {
        var removed = await Complete();
        await Advance(TimeSpan.FromDays(31));
        await Purge();
        var parent = Instance(WorkflowStatus.Suspended);
        var child = InstanceWithId(removed.InstanceId);
        child.ParentInstanceId = parent.InstanceId;
        child.ParentWaitId = "child-wait";
        var commit = CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, 0,
            openedWaits: [new OpenedWait("child-wait", WaitKind.Child, "child", "child-result", null)],
            childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child-wait")]);
        var refused = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.CommitWorkflowInstanceAsync(commit));
        Assert.Equal(WorkflowErrorKind.InstanceRemoved, refused.Kind);
        Assert.Null(await store.GetWorkflowInstanceAsync(parent.InstanceId));
        Assert.Null(await store.GetWaitAsync(parent.InstanceId, "child-wait"));
    }

    private async Task DeliveredOutboxRowsGone()
    {
        var publisher = Guid.NewGuid().ToString("N");
        var pendingPublisher = Guid.NewGuid().ToString("N");
        await RegisterPublisher(publisher);
        await RegisterPublisher(pendingPublisher);
        var instance = await Complete();
        var delivery = Assert.Single(await Outbox.ClaimDeliveriesAsync(publisher, "owner", TimeSpan.FromHours(1), 100));
        Assert.True(await Outbox.SettleDeliveryAsync(delivery.Event.Id, publisher, "owner", OutboxPublishResult.Delivered()));
        await Advance(TimeSpan.FromDays(31));
        var skipped = await Purge(); // Another publisher still needs this event.
        Assert.Equal(0, skipped.InstancesRemoved);
        Assert.Equal(1, skipped.SkippedUnattemptedByPublisher[pendingPublisher]);
        var pending = Assert.Single(await Outbox.ClaimDeliveriesAsync(pendingPublisher, "other", TimeSpan.FromHours(1), 100));
        Assert.True(await Outbox.SettleDeliveryAsync(pending.Event.Id, pendingPublisher, "other", OutboxPublishResult.Delivered()));
        Assert.Equal(1, (await Purge()).InstancesRemoved);
        Assert.Null(await Outbox.GetDeliveryAsync(delivery.Event.Id, publisher));
        Assert.Null(await Outbox.GetDeliveryAsync(delivery.Event.Id, pendingPublisher));
        if (newConnection != null)
        {
            await using var connection = newConnection();
            var sqlServer = connection is Microsoft.Data.SqlClient.SqlConnection;
            var table = sqlServer ? "dbo.IxIFlowOutboxEvents" : "ixiflow_outbox_events";
            var column = sqlServer ? "InstanceId" : "instance_id";
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                $"SELECT count(*) FROM {table} WHERE {column}=@Id", new { Id = instance.InstanceId }));
        }
    }

    private async Task ExplicitDeleteLeavesTombstone()
    {
        foreach (var status in new[] { WorkflowStatus.Running, WorkflowStatus.Completed })
        {
            var instance = Instance(status);
            await Commit(instance);
            var period = TimeSpan.FromDays(7);
            await store.DeleteWorkflowInstanceAsync(instance.InstanceId, period);
            Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));
            var tombstone = Assert.IsType<WorkflowTombstone>(await store.GetTombstoneAsync(instance.InstanceId));
            Assert.Equal(period, tombstone.ExpiresAtUtc - tombstone.RemovedAtUtc);
            await Advance(TimeSpan.FromDays(1));
            var beforeRepeat = await store.GetTombstoneAsync(instance.InstanceId);
            await store.DeleteWorkflowInstanceAsync(instance.InstanceId, period);
            Assert.Equal(beforeRepeat, await store.GetTombstoneAsync(instance.InstanceId));
            Assert.Equal(WorkflowErrorKind.InstanceRemoved, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(InstanceWithId(instance.InstanceId),
                    _epochs[instance.InstanceId], 0)))).Kind);
            Assert.Equal(EventAppendStatus.Closed, (await store.AppendEventAsync(
                new WorkflowEvent(instance.InstanceId, "late", "type", "{}"))).Status);
            await Advance(period);
            Assert.Null(await store.GetTombstoneAsync(instance.InstanceId));
            await store.SeedAsync(InstanceWithId(instance.InstanceId));
        }
        var defaulted = await Complete();
        await store.DeleteWorkflowInstanceAsync(defaulted.InstanceId);
        var defaultTombstone = (await store.GetTombstoneAsync(defaulted.InstanceId))!;
        Assert.Equal(Tombstones, defaultTombstone.ExpiresAtUtc - defaultTombstone.RemovedAtUtc);
        var unknown = Guid.NewGuid().ToString("N");
        await store.DeleteWorkflowInstanceAsync(unknown);
        Assert.Null(await store.GetTombstoneAsync(unknown));
    }

    private async Task DecoratedStoreKeepsRetention()
    {
        var instance = await Complete();
        await Advance(TimeSpan.FromDays(31));
        var decorated = GuardedStateRepository.Wrap(store);
        Assert.Equal(1, (await decorated.PurgeAsync(Retention, Tombstones, 100)).InstancesRemoved);
        Assert.NotNull(await decorated.GetTombstoneAsync(instance.InstanceId));
        Assert.Null(await decorated.GetWorkflowInstanceAsync(instance.InstanceId));
    }

    private async Task UpdateKeepsParent()
    {
        var parent = await Complete();
        var child = Instance(WorkflowStatus.Running, parent.InstanceId);
        await Commit(child);
        child = (await store.GetWorkflowInstanceAsync(child.InstanceId))!;
        child.WorkflowDataJson = StoredJson.Inline("{\"Value\":43}");
        await Commit(child); // UpdateCommit rather than the initial insert.
        Assert.Equal(parent.InstanceId, (await store.GetWorkflowInstanceAsync(child.InstanceId))!.ParentInstanceId);
        Assert.Equal(child.InstanceId, Assert.Single(await store.GetChildInstancesAsync(parent.InstanceId)).InstanceId);
        if (newConnection != null)
        {
            await using var connection = newConnection();
            Assert.Equal(parent.InstanceId, await connection.ExecuteScalarAsync<string>(
                connection is Microsoft.Data.SqlClient.SqlConnection
                    ? "SELECT ParentInstanceId FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId=@Id"
                    : "SELECT parent_instance_id FROM ixiflow_workflow_instances WHERE instance_id=@Id",
                new { Id = child.InstanceId }));
        }
        await Advance(TimeSpan.FromDays(31));
        Assert.Equal(0, (await Purge()).InstancesRemoved);
        Assert.NotNull(await store.GetWorkflowInstanceAsync(parent.InstanceId));
    }

    private async Task IncarnationFencesReuse()
    {
        var instance = Instance(WorkflowStatus.Running);
        await Commit(instance);
        var previous = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "previous", TimeSpan.FromMinutes(1)))!;
        instance.Status = WorkflowStatus.Completed;
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, previous.Epoch, instance.Revision))).Status);
        await Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await Purge()).InstancesRemoved);
        await Advance(Tombstones);
        await Purge();
        var replacement = InstanceWithId(instance.InstanceId);
        await Commit(replacement);
        var current = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "current", TimeSpan.FromMinutes(1)))!;
        Assert.True(current.Epoch > previous.Epoch);
        Assert.NotEqual(instance.Incarnation, replacement.Incarnation);
        Assert.False(await store.ReleaseExecutionLeaseAsync(instance.InstanceId, "previous"));
        Assert.False(await store.RenewExecutionLeaseAsync(instance.InstanceId, "previous", TimeSpan.FromMinutes(1)));
        Assert.Equal(WorkflowCommitStatus.StaleEpoch, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, previous.Epoch, instance.Revision))).Status);
    }

    private async Task PurgeMeetsWriter(RetentionScenario scenario)
    {
        var removed = await Complete();
        var parent = Instance(WorkflowStatus.Suspended);
        await Commit(parent);
        await Advance(TimeSpan.FromDays(31));
        async Task Write()
        {
            if (scenario == RetentionScenario.PurgeMeetsEvent)
            {
                Assert.Equal(EventAppendStatus.Closed, (await store.AppendEventAsync(
                    new WorkflowEvent(removed.InstanceId, "arriving", "type", "{}"))).Status);
                return;
            }
            var replacement = InstanceWithId(removed.InstanceId);
            var commit = CheckpointCommit.Create(replacement, _epochs[removed.InstanceId], 0);
            if (scenario == RetentionScenario.PurgeMeetsChildStart)
            {
                replacement.ParentInstanceId = parent.InstanceId;
                replacement.ParentWaitId = "child";
                commit = CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, parent.Revision,
                    openedWaits: [new OpenedWait("child", WaitKind.Child, "child", "type", null)],
                    childStarts: [new ChildStart(replacement.InstanceId, JsonSerializer.Serialize(replacement), "child")]);
            }
            Assert.Equal(WorkflowErrorKind.InstanceRemoved, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.CommitWorkflowInstanceAsync(commit))).Kind);
        }
        await using var holder = newConnection?.Invoke();
        if (holder is Microsoft.Data.SqlClient.SqlConnection or Npgsql.NpgsqlConnection)
        {
            await holder.OpenAsync();
            var sqlServer = holder is Microsoft.Data.SqlClient.SqlConnection;
            var session = await holder.ExecuteScalarAsync<int>(sqlServer ? "SELECT @@SPID" : "SELECT pg_backend_pid()");
            await using var transaction = await holder.BeginTransactionAsync();
            // Purge takes its instance lock before the exclusive publisher lock. Holding that latter
            // lock keeps a real purge transaction open with the instance locked, without production hooks.
            await holder.ExecuteAsync(sqlServer ? """
                DECLARE @r INT;
                EXEC @r=sys.sp_getapplock @Resource=N'IxIFlow.OutboxPublishers', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
                IF @r<0 THROW 51000, 'Could not hold publisher lock', 1;
                """ : "SELECT pg_advisory_xact_lock(hashtext('IxIFlow.OutboxPublishers'))", transaction: transaction);
            var purging = Task.Run(() => Purge());
            Task? writing = null;
            try
            {
                await using var observer = newConnection!();
                await observer.OpenAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition(sqlServer ? """
                    SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.dm_exec_requests WHERE blocking_session_id=@Session)
                        THEN 1 ELSE 0 END AS bit)
                    """ : "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @Session=ANY(pg_blocking_pids(pid)))",
                    new { Session = session }, cancellationToken: timeout.Token)))
                    await Task.Delay(20, timeout.Token);
                Assert.False(purging.IsCompleted);
                writing = Task.Run(Write);
                await Task.Delay(200, timeout.Token);
                Assert.False(writing.IsCompleted);
            }
            finally
            {
                await transaction.CommitAsync();
                Assert.Equal(1, (await purging.WaitAsync(TimeSpan.FromSeconds(15))).InstancesRemoved);
                if (writing != null) await writing.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        else
        {
            // Memory and SQLite serialize the whole write. Exercise the same post-purge admission
            // through their shared contract; database lock waiting applies to the distributed stores.
            Assert.Equal(1, (await Purge()).InstancesRemoved);
            await Write();
        }
        Assert.NotNull(await store.GetTombstoneAsync(removed.InstanceId));
        Assert.Null(await store.GetWorkflowInstanceAsync(removed.InstanceId));
        Assert.Null(await store.GetEventAsync(removed.InstanceId, "arriving"));
        Assert.Null(await store.GetWaitAsync(parent.InstanceId, "child"));
        Assert.Equal(parent.Revision, (await store.GetWorkflowInstanceAsync(parent.InstanceId))!.Revision);
        // Unrelated IDs continue to be admitted.
        var unrelated = Instance(WorkflowStatus.Running);
        await Commit(unrelated);
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class ResultData { public int Value { get; set; } }
}
