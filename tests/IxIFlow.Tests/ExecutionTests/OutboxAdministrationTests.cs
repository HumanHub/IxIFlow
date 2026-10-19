using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class OutboxAdministrationTests
{
    [Fact]
    public Task MemoryAdministrationContract()
    {
        var time = new FakeTimeProvider();
        IWorkflowStateRepository store = new InMemoryWorkflowStateRepository(time);
        return OutboxAdministrationContract.VerifyAsync(store,
            (_, delay) => { time.Advance(delay); return Task.CompletedTask; }, () => Task.FromResult(store));
    }

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteAdministrationContract()
    {
        var path = Path.Combine(Path.GetTempPath(), "outbox-admin-" + Guid.NewGuid().ToString("N") + ".db");
        var time = new FakeTimeProvider();
        var store = new SqliteWorkflowStateRepository("Data Source=" + path, time);
        try
        {
            await OutboxAdministrationContract.VerifyAsync(store,
                (_, delay) => { time.Advance(delay); return Task.CompletedTask; }, async () =>
                {
                    await store.DisposeAsync();
                    store = new SqliteWorkflowStateRepository("Data Source=" + path, time);
                    return store;
                });
        }
        finally { await store.DisposeAsync(); File.Delete(path); }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerAdministrationContract()
    {
        var cs = SqlTestDatabase<OutboxAdministrationTests>.ConnectionString;
        return OutboxAdministrationContract.VerifyAsync(new SqlWorkflowStateRepository(cs),
            (id, delay) => AgeAsync(new SqlConnection(cs), id, delay),
            () => Task.FromResult<IWorkflowStateRepository>(new SqlWorkflowStateRepository(cs)));
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlAdministrationContract()
    {
        var cs = PostgreSqlTestDatabase<OutboxAdministrationTests>.ConnectionString;
        return OutboxAdministrationContract.VerifyAsync(new PostgreSqlWorkflowStateRepository(cs),
            (id, delay) => AgeAsync(new NpgsqlConnection(cs), id, delay),
            () => Task.FromResult<IWorkflowStateRepository>(new PostgreSqlWorkflowStateRepository(cs)));
    }

    private static async Task AgeAsync(DbConnection connection, string id, TimeSpan delay)
    {
        await using (connection)
            await connection.ExecuteAsync(connection is SqlConnection ? """
                UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(SECOND,-@Seconds,EndedAtUtc) WHERE InstanceId=@Id;
                UPDATE dbo.IxIFlowOutboxEvents SET OccurredAtUtc=DATEADD(SECOND,-@Seconds,OccurredAtUtc) WHERE InstanceId=@Id;
                UPDATE d SET FirstAttemptAtUtc=DATEADD(SECOND,-@Seconds,FirstAttemptAtUtc),
                    RetriedAtUtc=DATEADD(SECOND,-@Seconds,RetriedAtUtc)
                FROM dbo.IxIFlowOutboxDeliveries d JOIN dbo.IxIFlowOutboxEvents e ON e.EventId=d.EventId WHERE e.InstanceId=@Id;
                """ : """
                UPDATE ixiflow_workflow_instances SET ended_at_utc=ended_at_utc-@Seconds*INTERVAL '1 second' WHERE instance_id=@Id;
                UPDATE ixiflow_outbox_events SET occurred_at_utc=occurred_at_utc-@Seconds*INTERVAL '1 second' WHERE instance_id=@Id;
                UPDATE ixiflow_outbox_deliveries d SET first_attempt_at_utc=first_attempt_at_utc-@Seconds*INTERVAL '1 second',
                    retried_at_utc=retried_at_utc-@Seconds*INTERVAL '1 second'
                FROM ixiflow_outbox_events e WHERE e.event_id=d.event_id AND e.instance_id=@Id;
                """, new { Id = id, Seconds = (int)delay.TotalSeconds });
    }

    [Fact]
    public async Task OperationsUseTheHostPipelineAndRequireAnActor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkflowStateRepository>();
        var operations = provider.GetRequiredService<IWorkflowOperations>();
        await store.RegisterPublisherAsync("operator", [OutboxEvent.WorkflowCompleted]);
        var instance = await OutboxStoreContract.CompleteAsync(store);
        var claim = Assert.Single(await store.ClaimDeliveriesAsync("operator", "owner", TimeSpan.FromMinutes(1), 1));
        await store.SettleDeliveryAsync(claim.Event.Id, "operator", "owner", OutboxPublishResult.GiveUp());
        Assert.Single((await operations.ListDeliveriesAsync(new(InstanceId: instance.InstanceId), "reader")).Items);
        Assert.True(await operations.RetryDeliveryAsync(claim.Event.Id, "operator", "alice"));
        claim = Assert.Single(await store.ClaimDeliveriesAsync("operator", "owner", TimeSpan.FromMinutes(1), 1));
        await store.SettleDeliveryAsync(claim.Event.Id, "operator", "owner", OutboxPublishResult.GiveUp(disablePublisher: true));
        Assert.True(await operations.EnablePublisherAsync("operator", "bob"));
        await operations.UnregisterPublisherAsync("operator", "carol");
        var publisher = Assert.Single(await operations.ListPublishersAsync("reader"));
        Assert.Equal("bob", publisher.EnabledBy);
        Assert.Equal("carol", publisher.UnregisteredBy);
        await Assert.ThrowsAsync<ArgumentException>(() => operations.ListDeliveriesAsync(new(), ""));
        await Assert.ThrowsAsync<ArgumentException>(() => operations.ListPublishersAsync(""));
        await Assert.ThrowsAsync<ArgumentException>(() => operations.RetryDeliveryAsync(claim.Event.Id, "operator", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => operations.EnablePublisherAsync("operator", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => operations.UnregisterPublisherAsync("operator", ""));
    }

    [Fact]
    public async Task HostRecordsHorizonWhenAClaimIsTooOldOrItsPublishOverruns()
    {
        foreach (var overrun in new[] { false, true })
        {
            var time = new FakeTimeProvider();
            var store = new InMemoryWorkflowStateRepository(time);
            var publisher = new SlowPublisher(time);
            await store.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
            await OutboxStoreContract.CompleteAsync(store);
            var delivery = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "owner", TimeSpan.FromHours(1), 1));
            if (!overrun) time.Advance(TimeSpan.FromSeconds(2));
            delivery = (await store.GetDeliveryAsync(delivery.Event.Id, publisher.Name))!;
            using var service = new WorkflowOutboxService(store, [publisher],
                new HostSettings(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), "horizon"),
                new OutboxSettings(leaseDuration: TimeSpan.FromHours(1), retryHorizon: TimeSpan.FromSeconds(1)),
                time, NullLogger<WorkflowOutboxService>.Instance);
            await service.DeliverAsync(publisher, delivery, default);
            var givenUp = (await store.GetDeliveryAsync(delivery.Event.Id, publisher.Name))!;
            Assert.Equal(OutboxDeliveryStatus.GivenUp, givenUp.Status);
            Assert.Equal(OutboxGivenUpReason.Horizon, givenUp.GivenUpReason);
        }
    }

    private sealed class SlowPublisher(FakeTimeProvider time) : IOutboxPublisher
    {
        public string Name => "slow";
        public IReadOnlyCollection<string> EventTypes => [OutboxEvent.WorkflowCompleted];
        public Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery, CancellationToken cancellationToken)
        { time.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult(OutboxPublishResult.RetryLater()); }
    }
}
