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

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task RejectedMessageIsDelayedAndRecordsTheFailure()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20));
            await bus.PublishAsync(new FailureBusProbe { Token = token });
            await using (var consumer = bus.ConsumeDeliveriesAsync<FailureBusProbe>().GetAsyncEnumerator())
            {
                Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                await consumer.Current.RejectAsync(new IOException("missing workflow definition"));
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var row = await connection.QuerySingleAsync<(int AttemptCount, DateTime NextVisibleAt,
                string LastError)>("""
                SELECT AttemptCount, NextVisibleAt, LastError
                FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(FailureBusProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
            Assert.Equal(1, row.AttemptCount);
            Assert.True(row.NextVisibleAt > DateTime.UtcNow);
            Assert.Contains("missing workflow definition", row.LastError);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "DELETE FROM dbo.WorkflowMessages WHERE MessageType = @MessageType AND Payload LIKE @Payload",
                new { MessageType = typeof(FailureBusProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task RepeatedFailureDeadLettersTheMessage()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20),
                failureBackoff: TimeSpan.FromMilliseconds(50), maxDeliveryAttempts: 2);
            await bus.PublishAsync(new FailureBusProbe { Token = token });
            await using var consumer = bus.ConsumeDeliveriesAsync<FailureBusProbe>().GetAsyncEnumerator();
            Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await consumer.Current.RejectAsync(new IOException("first failure"));
            Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await consumer.Current.RejectAsync(new IOException("second failure"));

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var row = await connection.QuerySingleAsync<(int AttemptCount, DateTime? DeadLetteredAt)>("""
                SELECT AttemptCount, DeadLetteredAt
                FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(FailureBusProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
            Assert.Equal(2, row.AttemptCount);
            Assert.NotNull(row.DeadLetteredAt);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "DELETE FROM dbo.WorkflowMessages WHERE MessageType = @MessageType AND Payload LIKE @Payload",
                new { MessageType = typeof(FailureBusProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task InvalidPayloadIsDeadLetteredWithoutStoppingTheConsumer()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        string? messageId = null;
        try
        {
            using var bus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20),
                failureBackoff: TimeSpan.FromMilliseconds(30), maxDeliveryAttempts: 2);
            await bus.PublishAsync(new FailureBusProbe { Token = token });
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            messageId = await connection.QuerySingleAsync<string>("""
                SELECT Id FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(FailureBusProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
            await connection.ExecuteAsync("""
                UPDATE dbo.WorkflowMessages SET Payload = '{invalid' WHERE Id = @MessageId
                """, new { MessageId = messageId });
            await using var consumer = bus.ConsumeDeliveriesAsync<FailureBusProbe>().GetAsyncEnumerator();
            var receiving = consumer.MoveNextAsync().AsTask();
            await Task.Delay(300);
            await bus.StopAsync();
            Assert.False(await receiving);

            var row = await connection.QuerySingleAsync<(int AttemptCount, DateTime? DeadLetteredAt)>("""
                SELECT AttemptCount, DeadLetteredAt FROM dbo.WorkflowMessages WHERE Id = @MessageId
                """, new { MessageId = messageId });
            Assert.Equal(2, row.AttemptCount);
            Assert.NotNull(row.DeadLetteredAt);
        }
        finally
        {
            if (messageId != null)
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync("DELETE FROM dbo.WorkflowMessages WHERE Id = @MessageId",
                    new { MessageId = messageId });
            }
        }
    }
}

public sealed class FailureBusProbe
{
    public string Token { get; set; } = string.Empty;
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
