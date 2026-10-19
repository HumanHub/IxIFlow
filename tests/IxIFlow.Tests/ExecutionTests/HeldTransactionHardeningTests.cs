using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class HeldTransactionHardeningTests
{
    [SqlServerFact, Trait("Spec", "C-02"), Trait("Category", "SqlIntegration")]
    public Task SqlServerParentLeaseExpiresWhileWaitingForChild() => ExpireWhileBlocked(true);
    [PostgreSqlFact, Trait("Spec", "C-02"), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlParentLeaseExpiresWhileWaitingForChild() => ExpireWhileBlocked(false);
    [SqlServerFact, Trait("Spec", "D-05"), Trait("Spec", "H-05"), Trait("Category", "SqlIntegration")]
    public Task SqlServerRetainedReplayMakesSelectedPurgeCandidateIneligible() => ReplayWhilePurgeBlocked(true);
    [PostgreSqlFact, Trait("Spec", "D-05"), Trait("Spec", "H-05"), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRetainedReplayMakesSelectedPurgeCandidateIneligible() => ReplayWhilePurgeBlocked(false);
    [SqlServerFact, Trait("Spec", "D-11"), Trait("Category", "SqlIntegration")]
    public Task SqlServerClaimSkipsLockedOldestDelivery() => ClaimWhileOldestLocked(true);
    [PostgreSqlFact, Trait("Spec", "D-11"), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlClaimSkipsLockedOldestDelivery() => ClaimWhileOldestLocked(false);

    private static (IWorkflowStateRepository Store, Func<DbConnection> Connect) Database(bool sqlServer)
    {
        if (sqlServer)
        {
            var cs = SqlTestDatabase<HeldTransactionHardeningTests>.ConnectionString;
            return (new SqlWorkflowStateRepository(cs), () => new SqlConnection(cs));
        }
        var pg = PostgreSqlTestDatabase<HeldTransactionHardeningTests>.ConnectionString;
        return (new PostgreSqlWorkflowStateRepository(pg), () => new NpgsqlConnection(pg));
    }

    private static async Task WaitForBlocked(Func<DbConnection> connect, int session)
    {
        await using var observer = connect();
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition(observer is SqlConnection
            ? "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.dm_exec_requests WHERE blocking_session_id=@Session) THEN 1 ELSE 0 END AS bit)"
            : "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @Session=ANY(pg_blocking_pids(pid)))",
            new { Session = session }, cancellationToken: timeout.Token)))
            await Task.Yield();
    }

    private static async Task ExpireWhileBlocked(bool sqlServer)
    {
        var (store, connect) = Database(sqlServer);
        var parent = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "BlockedParent",
            Status = WorkflowStatus.Running, WorkflowDataJson = "{\"before\":true}" };
        var child = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "LockedChild",
            Status = WorkflowStatus.Running, ParentInstanceId = parent.InstanceId, ParentWaitId = "child" };
        var grant = (await store.TryAcquireExecutionLeaseAsync(parent.InstanceId, "owner", TimeSpan.FromHours(1)))!;
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, grant.Epoch, 0))).Status);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(child, (await StoreTestSupport.LeaseAsync(store, child.InstanceId)).Epoch, 0))).Status);
        var before = (await store.GetWorkflowInstanceAsync(parent.InstanceId))!;
        var childBefore = (await store.GetWorkflowInstanceAsync(child.InstanceId))!;
        parent = (await store.GetWorkflowInstanceAsync(parent.InstanceId))!;
        parent.WorkflowDataJson = "{\"after\":true}";
        var commit = CheckpointCommit.Create(parent, grant.Epoch, before.Revision, openedWaits:
            [new OpenedWait("child", WaitKind.Child, "child", "child-result", null)],
            childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child")]);
        await using var holder = connect();
        await holder.OpenAsync();
        var session = await holder.ExecuteScalarAsync<int>(sqlServer ? "SELECT @@SPID" : "SELECT pg_backend_pid()");
        await using var transaction = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(sqlServer
            ? "UPDATE dbo.IxIFlowWorkflowInstances WITH (ROWLOCK) SET StateJson=StateJson WHERE InstanceId=@Id"
            : "UPDATE ixiflow_workflow_instances SET state_json=state_json WHERE instance_id=@Id",
            new { Id = child.InstanceId }, transaction);
        var committing = Task.Run(() => store.CommitWorkflowInstanceAsync(commit));
        try
        {
            await WaitForBlocked(connect, session);
            Assert.False(committing.IsCompleted);
            await using var expire = connect();
            // The captured pre-wait time is earlier than this timestamp. No takeover changes the epoch.
            await expire.ExecuteAsync(sqlServer
                ? "UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc=SYSUTCDATETIME() WHERE InstanceId=@Id"
                : "UPDATE ixiflow_workflow_leases SET expires_at_utc=clock_timestamp() WHERE instance_id=@Id",
                new { Id = parent.InstanceId });
        }
        finally { await transaction.RollbackAsync(); }
        Assert.Equal(WorkflowCommitStatus.StaleEpoch, (await committing.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await store.GetWorkflowInstanceAsync(parent.InstanceId)));
        Assert.Equal(JsonSerializer.Serialize(childBefore), JsonSerializer.Serialize(await store.GetWorkflowInstanceAsync(child.InstanceId)));
        Assert.Null(await store.GetWaitAsync(parent.InstanceId, "child"));
        // A rejected fenced commit cannot leave a receipt.
        await using var reader = connect();
        Assert.Equal(0, await reader.ExecuteScalarAsync<int>(sqlServer
            ? "SELECT COUNT(*) FROM dbo.IxIFlowWorkflowCommits WHERE InstanceId=@InstanceId AND CommitId=@CommitId"
            : "SELECT COUNT(*) FROM ixiflow_workflow_commits WHERE instance_id=@InstanceId AND commit_id=@CommitId",
            new { commit.InstanceId, commit.CommitId }));
        await store.DeleteWorkflowInstanceAsync(child.InstanceId);
        await store.DeleteWorkflowInstanceAsync(parent.InstanceId);
    }

    private static async Task ReplayWhilePurgeBlocked(bool sqlServer)
    {
        var (store, connect) = Database(sqlServer);
        var instance = await OutboxStoreContract.CompleteAsync(store);
        // Create a lease row without keeping ownership of the terminal instance.
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "seed", TimeSpan.FromHours(1)));
        Assert.True(await store.ReleaseExecutionLeaseAsync(instance.InstanceId, "seed"));
        await using var holder = connect();
        await holder.OpenAsync();
        await holder.ExecuteAsync(sqlServer
            ? "UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(DAY,-31,SYSUTCDATETIME()) WHERE InstanceId=@Id"
            : "UPDATE ixiflow_workflow_instances SET ended_at_utc=clock_timestamp()-INTERVAL '31 days' WHERE instance_id=@Id",
            new { Id = instance.InstanceId });
        var session = await holder.ExecuteScalarAsync<int>(sqlServer ? "SELECT @@SPID" : "SELECT pg_backend_pid()");
        await using var transaction = await holder.BeginTransactionAsync();
        // Candidate selection reads instances/outbox, not leases. Purge waits here after selection
        // and before acquiring the publisher lock and rechecking eligibility.
        await holder.ExecuteAsync(sqlServer
            ? "UPDATE dbo.IxIFlowWorkflowLeases WITH (ROWLOCK) SET CrashCount=CrashCount WHERE InstanceId=@Id"
            : "UPDATE ixiflow_workflow_leases SET crash_count=crash_count WHERE instance_id=@Id",
            new { Id = instance.InstanceId }, transaction);
        var purging = Task.Run(() => store.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 100));
        var publisher = "replay-" + instance.InstanceId;
        OutboxDelivery? pending = null;
        try
        {
            await WaitForBlocked(connect, session);
            Assert.False(purging.IsCompleted);
            await store.RegisterPublisherAsync(publisher, [OutboxEvent.WorkflowCompleted], replayRetained: true)
                .WaitAsync(TimeSpan.FromSeconds(10));
            pending = Assert.Single(await store.ClaimDeliveriesAsync(publisher, "replay", TimeSpan.FromHours(1), 1));
            Assert.Equal(instance.InstanceId, pending.Event.InstanceId);
        }
        finally { await transaction.RollbackAsync(); }
        Assert.Equal(0, (await purging.WaitAsync(TimeSpan.FromSeconds(15))).InstancesRemoved);
        Assert.NotNull(await store.GetWorkflowInstanceAsync(instance.InstanceId));
        Assert.Null(await store.GetTombstoneAsync(instance.InstanceId));
        var surviving = (await store.GetDeliveryAsync(pending!.Event.Id, publisher))!;
        Assert.Equal(pending, surviving with { StoreNowUtc = pending.StoreNowUtc });
        Assert.Equal(OutboxDeliveryStatus.Pending, surviving.Status);
        await store.UnregisterPublisherAsync(publisher);
        await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
    }

    private static async Task ClaimWhileOldestLocked(bool sqlServer)
    {
        var (store, connect) = Database(sqlServer);
        var publisher = "skip-" + Guid.NewGuid().ToString("N");
        await store.RegisterPublisherAsync(publisher, [OutboxEvent.WorkflowCompleted]);
        var first = await OutboxStoreContract.CompleteAsync(store);
        var second = await OutboxStoreContract.CompleteAsync(store);
        await using var holder = connect();
        await holder.OpenAsync();
        // Distinguish due order independently of event IDs and server clock precision.
        await holder.ExecuteAsync(sqlServer ? """
            UPDATE d SET NextAttemptAtUtc=DATEADD(MINUTE, CASE WHEN e.InstanceId=@First THEN -2 ELSE -1 END, SYSUTCDATETIME())
            FROM dbo.IxIFlowOutboxDeliveries d JOIN dbo.IxIFlowOutboxEvents e ON e.EventId=d.EventId WHERE d.PublisherName=@Publisher
            """ : """
            UPDATE ixiflow_outbox_deliveries d SET next_attempt_at_utc=clock_timestamp()-
                CASE WHEN e.instance_id=@First THEN INTERVAL '2 minutes' ELSE INTERVAL '1 minute' END
            FROM ixiflow_outbox_events e WHERE d.event_id=e.event_id AND d.publisher_name=@Publisher
            """, new { First = first.InstanceId, Publisher = publisher });
        await using var transaction = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(sqlServer ? """
            UPDATE d WITH (ROWLOCK) SET NextAttemptAtUtc=DATEADD(SECOND,-1,NextAttemptAtUtc) FROM dbo.IxIFlowOutboxDeliveries d
            JOIN dbo.IxIFlowOutboxEvents e ON e.EventId=d.EventId WHERE e.InstanceId=@Id AND d.PublisherName=@Publisher
            """ : """
            UPDATE ixiflow_outbox_deliveries d SET next_attempt_at_utc=next_attempt_at_utc-INTERVAL '1 second'
            FROM ixiflow_outbox_events e WHERE d.event_id=e.event_id AND e.instance_id=@Id AND d.publisher_name=@Publisher
            """, new { Id = first.InstanceId, Publisher = publisher }, transaction);
        var claiming = Task.Run(() => store.ClaimDeliveriesAsync(publisher, "other", TimeSpan.FromMinutes(1), 1));
        try
        {
            // Must complete while the oldest row is still locked. Timeout is a failure, never a pause.
            var next = Assert.Single(await claiming.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(second.InstanceId, next.Event.InstanceId);
            Assert.Equal(1, next.Attempts);
        }
        finally
        {
            await transaction.RollbackAsync();
            await claiming.WaitAsync(TimeSpan.FromSeconds(15));
        }
        var oldest = Assert.Single(await store.ClaimDeliveriesAsync(publisher, "after-release", TimeSpan.FromMinutes(1), 1));
        Assert.Equal(first.InstanceId, oldest.Event.InstanceId);
        Assert.Equal(1, oldest.Attempts);
        await store.UnregisterPublisherAsync(publisher);
        await store.DeleteWorkflowInstanceAsync(first.InstanceId);
        await store.DeleteWorkflowInstanceAsync(second.InstanceId);
    }
}
