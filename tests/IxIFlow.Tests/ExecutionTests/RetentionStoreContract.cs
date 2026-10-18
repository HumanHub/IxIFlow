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
    ChildCompletionSurvivesRemoval, BoundedAndCancelled, StoreClockAndReplay, ChildStartRefusesTombstone, DeliveredOutboxRowsGone
}

internal sealed class RetentionStoreContract(IWorkflowStateRepository store,
    Func<IReadOnlyCollection<string>, TimeSpan, Task> advance, Func<DbConnection>? newConnection = null)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan Tombstones = TimeSpan.FromDays(90);
    private readonly List<string> _ids = [];
    private IWorkflowOutbox Outbox => Assert.IsAssignableFrom<IWorkflowOutbox>(store);
    private Task<RetentionResult> Purge(int size = 100, CancellationToken token = default) =>
        store.PurgeAsync(Retention, Tombstones, size, token);
    private Task Advance(TimeSpan by) => advance(_ids, by);
    private WorkflowInstance Instance(WorkflowStatus status = WorkflowStatus.Completed, string? parent = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _ids.Add(id);
        return new WorkflowInstance { InstanceId = id, WorkflowName = "Retention", WorkflowVersion = 1,
            Status = status, ParentInstanceId = parent, WorkflowDataType = typeof(ResultData).AssemblyQualifiedName!,
            WorkflowDataJson = """{"Value":42}""", CompletedAt = DateTime.MinValue };
    }
    private async Task<CheckpointCommit> Commit(WorkflowInstance instance, ParentNotice? notice = null)
    {
        var commit = CheckpointCommit.Create(instance, 0, instance.Revision, parentNotice: notice);
        var result = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        instance.Revision = result.Revision;
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
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        }
        finally
        {
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
        await Outbox.RegisterPublisherAsync(publisher, [OutboxEvent.WorkflowCompleted]);
        var instance = await Complete();
        await Advance(TimeSpan.FromDays(31));
        Assert.Equal(0, (await Purge()).InstancesRemoved); // Unleased pending delivery.
        var delivery = Assert.Single(await Outbox.ClaimDeliveriesAsync(publisher, "leased", TimeSpan.FromHours(1), 100));
        Assert.Equal(0, (await Purge()).InstancesRemoved);
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
            DefinitionFingerprint = "retention", Continuations = [new ContinuationState
                { Id = "branch", Status = ContinuationStatus.Waiting, Stack = [new ScopePosition { ScopeId = "root" }] }]
        });
        var first = CheckpointCommit.Create(instance, 0, 0, openedWaits:
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
        // Reusing the old commit ID must apply from revision zero, proving its receipt was removed too.
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
            store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(InstanceWithId(instance.InstanceId), 0, 0)));
        Assert.Equal(WorkflowErrorKind.InstanceRemoved, exception.Kind);
        var saved = InstanceWithId(instance.InstanceId);
        Assert.Equal(WorkflowErrorKind.InstanceRemoved, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.SaveWorkflowInstanceAsync(saved))).Kind);
        foreach (var target in new string?[] { null, "old-wait" })
            Assert.Equal(EventAppendStatus.Closed, (await store.AppendEventAsync(
                new WorkflowEvent(instance.InstanceId, Guid.NewGuid().ToString("N"), "event", "{}", TargetWaitId: target))).Status);
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ResultData>("TombstoneStart").Step<Noop>().Build();
        var start = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(definition,
            new ResultData(), new WorkflowOptions { InstanceId = instance.InstanceId }));
        Assert.Equal(WorkflowErrorKind.InstanceRemoved, start.Kind);
        var refused = await client.ResumeAsync(instance.InstanceId, null, new ResultData(), "late");
        Assert.Equal(EventOutcome.Closed, refused.Event!.Outcome);
        Assert.Equal(EventRejection.InstanceFinished, refused.Event.Rejection);
        Assert.Equal(WorkflowExecutionStatus.Removed, refused.Status);
    }

    private static WorkflowInstance InstanceWithId(string id) => new()
        { InstanceId = id, WorkflowName = "Replacement", Status = WorkflowStatus.Running };

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
        await store.SaveWorkflowInstanceAsync(InstanceWithId(instance.InstanceId));
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
            instance.LastError = status == WorkflowStatus.Failed ? "failure retained" : null;
            await Commit(instance);
            var result = await client.AwaitAsync(instance.InstanceId, WorkflowAwaitCondition.Finished);
            Assert.Equal(instance.InstanceId, result.InstanceId);
            Assert.Equal(42, Assert.IsType<ResultData>(result.WorkflowData).Value);
            Assert.Equal(instance.LastError, result.ErrorMessage);
            Assert.NotEqual(WorkflowExecutionStatus.Running, result.Status);
        }
    }

    private async Task AwaitAfterRemoval()
    {
        var instance = await Complete();
        await Advance(TimeSpan.FromDays(31));
        await Purge();
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
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
        var start = CheckpointCommit.Create(parent, 0, 0,
            openedWaits: [new OpenedWait("child-wait", WaitKind.Child, "child", "child-result", null)],
            childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child-wait")]);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(start)).Status);
        child.Revision = 1;
        child.Status = WorkflowStatus.Completed;
        await Commit(child, new ParentNotice(parent.InstanceId, "child-wait", "child-done", "child-result", """{"Value":42}"""));
        await Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await Purge()).InstancesRemoved);
        Assert.Null(await store.GetWorkflowInstanceAsync(child.InstanceId));
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(start)).Status);
        Assert.True((await store.GetWaitAsync(parent.InstanceId, "child-wait"))!.IsOpen);
        Assert.Equal("""{"Value":42}""", Assert.Single((await store.GetPendingEventsAsync(parent.InstanceId, 0, 100)).Events).PayloadJson);
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
        var commit = CheckpointCommit.Create(parent, 0, 0,
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
        await Outbox.RegisterPublisherAsync(publisher, [OutboxEvent.WorkflowCompleted]);
        await Outbox.RegisterPublisherAsync(pendingPublisher, [OutboxEvent.WorkflowCompleted]);
        var instance = await Complete();
        var delivery = Assert.Single(await Outbox.ClaimDeliveriesAsync(publisher, "owner", TimeSpan.FromHours(1), 100));
        Assert.True(await Outbox.SettleDeliveryAsync(delivery.Event.Id, publisher, "owner", OutboxPublishResult.Delivered()));
        await Advance(TimeSpan.FromDays(31));
        Assert.Equal(0, (await Purge()).InstancesRemoved); // Another publisher still needs this event.
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

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class ResultData { public int Value { get; set; } }
}
