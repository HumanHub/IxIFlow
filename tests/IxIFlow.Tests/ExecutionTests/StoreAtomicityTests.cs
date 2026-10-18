using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class StoreAtomicityTests : IClassFixture<SqliteTestDatabase<StoreAtomicityTests>>
{
    [Fact, Trait("Spec", "D-04")]
    public Task MemoryMixedSettlementsRollBack() => InboxAtomicityContract.MixedSettlementsRollBack(new InMemoryWorkflowStateRepository());

    [Fact, Trait("Spec", "D-04"), Trait("Category", "SqliteIntegration")]
    public async Task SqliteMixedSettlementsRollBack()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<StoreAtomicityTests>.ConnectionString);
        await InboxAtomicityContract.MixedSettlementsRollBack(store);
    }

    [SqlServerFact, Trait("Spec", "D-04"), Trait("Category", "SqlIntegration")]
    public Task SqlServerMixedSettlementsRollBack() => InboxAtomicityContract.MixedSettlementsRollBack(
        new SqlWorkflowStateRepository(SqlTestDatabase<StoreAtomicityTests>.ConnectionString));

    [PostgreSqlFact, Trait("Spec", "D-04"), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlMixedSettlementsRollBack() => InboxAtomicityContract.MixedSettlementsRollBack(
        new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<StoreAtomicityTests>.ConnectionString));

    [Fact, Trait("Spec", "D-11")]
    public Task MemoryExpiredUnreclaimedOutboxOwner()
    {
        var time = new FakeTimeProvider();
        return OutboxStoreContract.ExpiredUnreclaimedOwnerCannotChangeDelivery(new InMemoryWorkflowStateRepository(time),
            _ => { time.Advance(TimeSpan.FromSeconds(30)); return Task.CompletedTask; });
    }

    [Fact, Trait("Spec", "D-11"), Trait("Category", "SqliteIntegration")]
    public async Task SqliteExpiredUnreclaimedOutboxOwner()
    {
        var time = new FakeTimeProvider();
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<StoreAtomicityTests>.ConnectionString, time);
        await OutboxStoreContract.ExpiredUnreclaimedOwnerCannotChangeDelivery(store,
            _ => { time.Advance(TimeSpan.FromSeconds(30)); return Task.CompletedTask; });
    }

    [SqlServerFact, Trait("Spec", "D-11"), Trait("Category", "SqlIntegration")]
    public Task SqlServerExpiredUnreclaimedOutboxOwner()
    {
        var cs = SqlTestDatabase<StoreAtomicityTests>.ConnectionString;
        return OutboxStoreContract.ExpiredUnreclaimedOwnerCannotChangeDelivery(new SqlWorkflowStateRepository(cs),
            d => Expire(new SqlConnection(cs), d));
    }

    [PostgreSqlFact, Trait("Spec", "D-11"), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlExpiredUnreclaimedOutboxOwner()
    {
        var cs = PostgreSqlTestDatabase<StoreAtomicityTests>.ConnectionString;
        return OutboxStoreContract.ExpiredUnreclaimedOwnerCannotChangeDelivery(new PostgreSqlWorkflowStateRepository(cs),
            d => Expire(new NpgsqlConnection(cs), d));
    }

    private static async Task Expire(DbConnection connection, OutboxDelivery delivery)
    {
        await using (connection)
            await connection.ExecuteAsync(connection is SqlConnection
                ? "UPDATE dbo.IxIFlowOutboxDeliveries SET LeaseUntilUtc=DATEADD(SECOND,-1,SYSUTCDATETIME()) WHERE EventId=@Id AND PublisherName=@PublisherName"
                : "UPDATE ixiflow_outbox_deliveries SET lease_until_utc=clock_timestamp()-INTERVAL '1 second' WHERE event_id=@Id AND publisher_name=@PublisherName",
                new { delivery.Event.Id, delivery.PublisherName });
    }
}
