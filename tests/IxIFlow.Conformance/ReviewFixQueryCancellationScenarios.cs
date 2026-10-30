using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "durability")]
public sealed class ReviewFixQueryCancellationScenarios
{
    public static TheoryData<string, string> Queries
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var profile in StoreProfiles.ProfileNames.Where(profile =>
                         profile is StoreProfiles.DistributedSqlServer or StoreProfiles.DistributedPostgreSql))
            foreach (var operation in new[] { "publishers", "renew", "settle" })
                data.Add(profile, operation);

            return data;
        }
    }

    [Theory, MemberData(nameof(Queries))]
    public async Task CancellationReachesTheCommandWhileItsDatabaseLockIsStillHeld(string profile, string operation)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.RegisterPublisherAsync("query-cancellation", [OutboxEvent.WorkflowCompleted]);

        var instance = new WorkflowInstance
        {
            InstanceId = "completed", WorkflowName = "Review", Status = WorkflowStatus.Completed,
            ExecutionStateJson = StoreTestSupport.CheckpointJson, WorkflowDataJson = StoredJson.Inline("{}")
        };

        await store.SeedAsync(instance);
        var delivery = Assert.Single(await store.ClaimDeliveriesAsync("query-cancellation", "owner", TimeSpan.FromMinutes(5), 1));
        var sqlServer = profile == StoreProfiles.DistributedSqlServer;
        await using var holder = database.NewConnection();
        await holder.OpenAsync();
        var session = await holder.ExecuteScalarAsync<int>(sqlServer ? "SELECT @@SPID" : "SELECT pg_backend_pid()");
        await using var held = await holder.BeginTransactionAsync();
        var publisherRead = operation == "publishers";

        var table = sqlServer
            ? publisherRead ? "dbo.IxIFlowOutboxPublishers" : "dbo.IxIFlowOutboxDeliveries"
            : publisherRead
                ? "ixiflow_outbox_publishers"
                : "ixiflow_outbox_deliveries";

        // A schema lock blocks even SQL Server snapshot reads. The column is always rolled back.
        await holder.ExecuteAsync(
            sqlServer
                ? $"ALTER TABLE {table} ADD ReviewCancellation int NULL"
                : $"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE", transaction: held);

        using var cancel = new CancellationTokenSource();

        var pending = operation switch
        {
            "publishers" => (Task)store.ListPublishersAsync(cancel.Token),
            "renew" => store.RenewDeliveryLeaseAsync(
                delivery.Event.Id, "query-cancellation", "owner", TimeSpan.FromMinutes(10), cancel.Token),
            _ => store.SettleDeliveryAsync(delivery.Event.Id, "query-cancellation", "owner", OutboxPublishResult.Delivered(), cancel.Token)
        };

        try
        {
            await using var observer = database.NewConnection();
            using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            while (await observer.ExecuteScalarAsync<int>(
                       new CommandDefinition(
                           sqlServer
                               ? "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@Session AND database_id=DB_ID()"
                               : "SELECT COUNT(*)::int FROM pg_stat_activity WHERE @Session=ANY(pg_blocking_pids(pid))",
                           new { Session = session }, cancellationToken: observation.Token)) == 0)
                await Task.Yield();

            Assert.False(pending.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await held.RollbackAsync();

            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }

        var unchanged = (await store.GetDeliveryAsync(delivery.Event.Id, "query-cancellation"))!;
        Assert.Equal(delivery.Status, unchanged.Status);
        Assert.Equal(delivery.LeaseUntilUtc, unchanged.LeaseUntilUtc);
    }
}