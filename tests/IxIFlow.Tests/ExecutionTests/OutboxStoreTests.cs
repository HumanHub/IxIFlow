using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-11")]
[Trait("Feature", "durability")]
public sealed class OutboxStoreTests
{
    [Fact]
    public Task MemoryDeliveryContract()
    {
        var time = new FakeTimeProvider();
        return OutboxStoreContract.VerifyAsync(new InMemoryWorkflowStateRepository(time), (_, delay) =>
        { time.Advance(delay); return Task.CompletedTask; });
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteDeliveryContract()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var time = new FakeTimeProvider();
        await using (var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "outbox.db"), time))
            await OutboxStoreContract.VerifyAsync(store, (_, delay) => { time.Advance(delay); return Task.CompletedTask; });
        Directory.Delete(folder, recursive: true);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerDeliveryContract()
    {
        var connectionString = SqlTestDatabase<OutboxStoreTests>.ConnectionString;
        return OutboxStoreContract.VerifyAsync(new SqlWorkflowStateRepository(connectionString), async (delivery, delay) =>
        {
            // Server clocks are authoritative and cannot be faked. Move only this test's stored due
            // times into the past, rather than sleeping or changing the machine/database clock.
            await using var connection = new SqlConnection(connectionString);
            await connection.ExecuteAsync("""
                UPDATE dbo.IxIFlowOutboxDeliveries
                SET NextAttemptAtUtc=DATEADD(SECOND, -@Seconds, NextAttemptAtUtc),
                    LeaseUntilUtc=DATEADD(SECOND, -@Seconds, LeaseUntilUtc)
                WHERE EventId=@Id AND PublisherName=@PublisherName
                """, new { Seconds = (int)delay.TotalSeconds, delivery.Event.Id, delivery.PublisherName });
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlDeliveryContract()
    {
        var connectionString = PostgreSqlTestDatabase<OutboxStoreTests>.ConnectionString;
        return OutboxStoreContract.VerifyAsync(new PostgreSqlWorkflowStateRepository(connectionString), async (delivery, delay) =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.ExecuteAsync("""
                UPDATE ixiflow_outbox_deliveries
                SET next_attempt_at_utc=next_attempt_at_utc - @Seconds * INTERVAL '1 second',
                    lease_until_utc=lease_until_utc - @Seconds * INTERVAL '1 second'
                WHERE event_id=@Id AND publisher_name=@PublisherName
                """, new { Seconds = (int)delay.TotalSeconds, delivery.Event.Id, delivery.PublisherName });
        });
    }

    [Theory]
    [InlineData(1, 5)] [InlineData(2, 10)] [InlineData(3, 20)] [InlineData(20, 3600)]
    public void DefaultBackoffHasACap(int attempts, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), OutboxRetry.Delay(attempts));
}
