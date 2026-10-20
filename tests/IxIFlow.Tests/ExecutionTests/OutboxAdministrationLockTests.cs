using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class OutboxAdministrationLockTests
{
    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerRetryAndPurgeWaitForTheSamePublisherLock() => VerifyAsync(true);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRetryAndPurgeWaitForTheSamePublisherLock() => VerifyAsync(false);

    private static async Task VerifyAsync(bool sqlServer)
    {
        var cs = sqlServer ? SqlTestDatabase<OutboxAdministrationLockTests>.ConnectionString
            : PostgreSqlTestDatabase<OutboxAdministrationLockTests>.ConnectionString;
        DbConnection Connect() => sqlServer ? new SqlConnection(cs) : new NpgsqlConnection(cs);
        IWorkflowStateRepository store = sqlServer ? new SqlWorkflowStateRepository(cs) : new PostgreSqlWorkflowStateRepository(cs);
        var publisher = "locked-" + Guid.NewGuid().ToString("N");
        await store.RegisterPublisherAsync(publisher, [OutboxEvent.WorkflowCompleted]);
        var instance = await OutboxStoreContract.CompleteAsync(store);
        var delivery = Assert.Single(await store.ClaimDeliveriesAsync(publisher, "owner", TimeSpan.FromHours(1), 1));
        Assert.True(await store.SettleDeliveryAsync(delivery.Event.Id, publisher, "owner", OutboxPublishResult.GiveUp()));
        await using var holder = Connect();
        await holder.OpenAsync();
        await holder.ExecuteAsync(sqlServer
            ? "UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(DAY,-31,SYSUTCDATETIME()) WHERE InstanceId=@Id"
            : "UPDATE ixiflow_workflow_instances SET ended_at_utc=clock_timestamp()-INTERVAL '31 days' WHERE instance_id=@Id",
            new { Id = instance.InstanceId });
        var session = await holder.ExecuteScalarAsync<int>(sqlServer ? "SELECT @@SPID" : "SELECT pg_backend_pid()");
        await using var held = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(sqlServer ? """
            DECLARE @r INT;
            EXEC @r=sys.sp_getapplock @Resource=N'IxIFlow.OutboxPublishers', @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=1000;
            IF @r<0 THROW 51000, 'Could not hold publisher lock', 1;
            """ : "SELECT pg_advisory_xact_lock(hashtext('IxIFlow.OutboxPublishers'))", transaction: held);
        async Task WaitForBlocked(int count)
        {
            await using var observer = Connect();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await observer.ExecuteScalarAsync<int>(new CommandDefinition(sqlServer
                // SQL Server reports a queued waiter as blocked by the waiter ahead of it, so count the lock's waiters.
                ? "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type='APPLICATION' AND request_status='WAIT' AND resource_database_id=DB_ID() AND request_session_id<>@Session"
                : "SELECT COUNT(*)::int FROM pg_stat_activity WHERE @Session=ANY(pg_blocking_pids(pid))",
                new { Session = session }, cancellationToken: timeout.Token)) < count)
                await Task.Yield();
        }
        var retry = Task.Run(() => store.RetryDeliveryAsync(delivery.Event.Id, publisher, "alice"));
        Task<RetentionResult>? purge = null;
        try
        {
            // This fails if retry only takes a delivery lock, even if a casual race happens to pass.
            await WaitForBlocked(1);
            Assert.False(retry.IsCompleted);
            purge = Task.Run(() => store.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 100));
            await WaitForBlocked(2);
            Assert.False(purge.IsCompleted);
        }
        finally { await held.RollbackAsync(); }
        var retried = await retry.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(purge);
        await purge.WaitAsync(TimeSpan.FromSeconds(15));
        var surviving = await store.GetDeliveryAsync(delivery.Event.Id, publisher);
        if (retried)
        {
            Assert.NotNull(surviving);
            Assert.Equal(OutboxDeliveryStatus.Pending, surviving.Status);
            Assert.NotNull(await store.GetWorkflowInstanceAsync(instance.InstanceId));
        }
        else
        {
            Assert.Null(surviving);
            Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));
        }
        await store.UnregisterPublisherAsync(publisher, "cleanup");
        await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
    }
}
