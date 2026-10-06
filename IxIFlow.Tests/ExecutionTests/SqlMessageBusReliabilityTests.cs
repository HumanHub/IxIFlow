using Dapper;
using IxIFlow.Core;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Tests.ExecutionTests;

public class SqlMessageBusReliabilityTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task ActiveDeliveryKeepsItsClaimPastTheOriginalExpiry()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            using var firstBus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50));
            using var secondBus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50));
            await firstBus.PublishAsync(new RenewingBusProbe { Token = token });
            await using var firstConsumer = firstBus.ConsumeDeliveriesAsync<RenewingBusProbe>()
                .GetAsyncEnumerator();
            Assert.True(await firstConsumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(token, firstConsumer.Current.Message.Token);

            await using var secondConsumer = secondBus.ConsumeDeliveriesAsync<RenewingBusProbe>()
                .GetAsyncEnumerator();
            var duplicate = secondConsumer.MoveNextAsync().AsTask();
            await Task.Delay(600);
            await secondBus.StopAsync();
            Assert.False(await duplicate);
            await firstConsumer.Current.AcknowledgeAsync();
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "DELETE FROM dbo.WorkflowMessages WHERE MessageType = @MessageType AND Payload LIKE @Payload",
                new { MessageType = typeof(RenewingBusProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task MessageWithoutHandlerAcknowledgement_IsRedelivered()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            IF OBJECT_ID(N'dbo.WorkflowMessages', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.WorkflowMessages (
                    Id NVARCHAR(50) NOT NULL PRIMARY KEY,
                    MessageType NVARCHAR(500) NOT NULL,
                    Payload NVARCHAR(MAX) NOT NULL,
                    CreatedAt DATETIME2 NOT NULL,
                    ProcessedAt DATETIME2 NULL,
                    Priority INT NOT NULL
                );
            END
            """);

        var token = Guid.NewGuid().ToString("N");
        try
        {
            using (var firstBus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20)))
            {
                await firstBus.PublishAsync(new BusDeliveryProbe { Token = token });
                await using var firstConsumer = firstBus.ConsumeAsync<BusDeliveryProbe>().GetAsyncEnumerator();
                Assert.True(await firstConsumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(token, firstConsumer.Current.Token);
                // The consumer exits without acknowledging or handling the message.
            }

            using var secondBus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20));
            await using var secondConsumer = secondBus.ConsumeAsync<BusDeliveryProbe>().GetAsyncEnumerator();
            var receiveTask = secondConsumer.MoveNextAsync().AsTask();
            await Task.Delay(300);
            await secondBus.StopAsync();
            Assert.True(await receiveTask, "An unhandled message must remain available for delivery.");
            Assert.Equal(token, secondConsumer.Current.Token);
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM dbo.WorkflowMessages WHERE MessageType = @MessageType AND Payload LIKE @Payload",
                new
                {
                    MessageType = typeof(BusDeliveryProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%"
                });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task AcknowledgedMessage_IsNotRedelivered()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            using (var firstBus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20)))
            {
                await firstBus.PublishAsync(new BusDeliveryProbe { Token = token });
                await using var consumer = firstBus.ConsumeDeliveriesAsync<BusDeliveryProbe>().GetAsyncEnumerator();
                Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(token, consumer.Current.Message.Token);
                await consumer.Current.AcknowledgeAsync();
            }

            using var secondBus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20));
            await using var secondConsumer = secondBus.ConsumeDeliveriesAsync<BusDeliveryProbe>().GetAsyncEnumerator();
            var receiveTask = secondConsumer.MoveNextAsync().AsTask();
            await Task.Delay(200);
            await secondBus.StopAsync();
            Assert.False(await receiveTask);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "DELETE FROM dbo.WorkflowMessages WHERE MessageType = @MessageType AND Payload LIKE @Payload",
                new { MessageType = typeof(BusDeliveryProbe).AssemblyQualifiedName, Payload = $"%{token}%" });
        }
    }
}

public sealed class BusDeliveryProbe
{
    public string Token { get; set; } = string.Empty;
}

public sealed class RenewingBusProbe
{
    public string Token { get; set; } = string.Empty;
}

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")))
        {
            Skip = "Set IXIFLOW_TEST_SQL_CONNECTION_STRING to an isolated SQL Server test database.";
        }
    }
}
