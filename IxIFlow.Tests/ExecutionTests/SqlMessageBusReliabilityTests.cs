using Dapper;
using IxIFlow.Core;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Tests.ExecutionTests;

public class SqlMessageBusReliabilityTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task GenericConsumerContinuesAfterLosingAcknowledgementClaim()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = new SqlMessageBus(connectionString);
            await bus.PublishAsync(new GenericConsumerProbe { Token = token + "-first" });
            await bus.PublishAsync(new GenericConsumerProbe { Token = token + "-second" });
            await using var consumer = bus.ConsumeAsync<GenericConsumerProbe>().GetAsyncEnumerator();
            Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            var first = consumer.Current.Token;
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            Assert.Equal(1, await connection.ExecuteAsync("""
                UPDATE dbo.WorkflowMessages
                SET ProcessedAt = SYSUTCDATETIME(), ClaimToken = NULL, ClaimedUntil = NULL
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                  AND ProcessedAt IS NULL
                """, new { MessageType = typeof(GenericConsumerProbe).AssemblyQualifiedName,
                    Payload = $"%{first}%" }));

            Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(first, consumer.Current.Token);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                DELETE FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(GenericConsumerProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task GenericConsumerAcknowledgesEachMessageAfterProcessing()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = new SqlMessageBus(connectionString);
            await bus.PublishAsync(new GenericConsumerProbe { Token = token + "-first" });
            await bus.PublishAsync(new GenericConsumerProbe { Token = token + "-second" });
            await using var consumer = bus.ConsumeAsync<GenericConsumerProbe>().GetAsyncEnumerator();
            Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            var first = consumer.Current.Token;
            Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(first, consumer.Current.Token);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var acknowledged = await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                  AND ProcessedAt IS NOT NULL
                """, new { MessageType = typeof(GenericConsumerProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
            Assert.Equal(1, acknowledged);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                DELETE FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(GenericConsumerProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task RetriedCompletionPublishQueuesOneEventPerInstance()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            using var firstBus = new SqlMessageBus(connectionString);
            using var secondBus = new SqlMessageBus(connectionString);
            await Task.WhenAll(
                firstBus.PublishAsync(new WorkflowExecutionCompletedEvent
                    { InstanceId = instanceId, HostId = "host-a" }),
                secondBus.PublishAsync(new WorkflowExecutionCompletedEvent
                    { InstanceId = instanceId, HostId = "host-b" }));

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var count = await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(WorkflowExecutionCompletedEvent).AssemblyQualifiedName,
                    Payload = $"%{instanceId}%" });
            Assert.Equal(1, count);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                DELETE FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(WorkflowExecutionCompletedEvent).AssemblyQualifiedName,
                    Payload = $"%{instanceId}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task RetriedStartPublishQueuesOneEventPerInstance()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = new SqlMessageBus(connectionString);
            await bus.PublishAsync(new WorkflowExecutionStartedEvent
                { InstanceId = instanceId, HostId = "host-a" });
            await bus.PublishAsync(new WorkflowExecutionStartedEvent
                { InstanceId = instanceId, HostId = "host-b" });
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var count = await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(WorkflowExecutionStartedEvent).AssemblyQualifiedName,
                    Payload = $"%{instanceId}%" });
            Assert.Equal(1, count);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                DELETE FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(WorkflowExecutionStartedEvent).AssemblyQualifiedName,
                    Payload = $"%{instanceId}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task PendingMessagePollHasAFilteredIndex()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        using var bus = new SqlMessageBus(connectionString);
        try
        {
            await bus.PublishAsync(new BusDeliveryProbe { Token = token });
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var index = await connection.QuerySingleOrDefaultAsync<string>("""
                SELECT filter_definition FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.WorkflowMessages')
                  AND name = N'IX_WorkflowMessages_PendingPoll'
                """);
            Assert.NotNull(index);
            Assert.Contains("ProcessedAt", index);
            Assert.Contains("DeadLetteredAt", index);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                DELETE FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(BusDeliveryProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task PruningOldProcessedMessagesKeepsDeadLetters()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var processedToken = Guid.NewGuid().ToString("N");
        var deadToken = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = new SqlMessageBus(connectionString,
                TimeSpan.FromMilliseconds(20), maxDeliveryAttempts: 1);
            await bus.PublishAsync(new MaintenanceProbe { Token = processedToken });
            await using (var consumer = bus.ConsumeDeliveriesAsync<MaintenanceProbe>().GetAsyncEnumerator())
            {
                Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                await consumer.Current.AcknowledgeAsync();
            }
            await bus.PublishAsync(new MaintenanceProbe { Token = deadToken });
            await using (var consumer = bus.ConsumeDeliveriesAsync<MaintenanceProbe>().GetAsyncEnumerator())
            {
                Assert.True(await consumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                await consumer.Current.RejectAsync(new InvalidOperationException("test dead letter"));
            }
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                UPDATE dbo.WorkflowMessages
                SET ProcessedAt = DATEADD(DAY, -31, SYSUTCDATETIME())
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(MaintenanceProbe).AssemblyQualifiedName,
                    Payload = $"%{processedToken}%" });

            Assert.Equal(1, await bus.PruneExpiredAsync(TimeSpan.FromDays(30), 100));
            var remaining = await connection.QueryAsync<string>("""
                SELECT Payload FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType
                  AND (Payload LIKE @Processed OR Payload LIKE @Dead)
                """, new { MessageType = typeof(MaintenanceProbe).AssemblyQualifiedName,
                    Processed = $"%{processedToken}%", Dead = $"%{deadToken}%" });
            Assert.Single(remaining);
            Assert.Contains(deadToken, remaining.Single());
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                DELETE FROM dbo.WorkflowMessages WHERE MessageType = @MessageType
                  AND (Payload LIKE @Processed OR Payload LIKE @Dead)
                """, new { MessageType = typeof(MaintenanceProbe).AssemblyQualifiedName,
                    Processed = $"%{processedToken}%", Dead = $"%{deadToken}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task ConsumerRecoversAfterConnectionPoolExhaustion()
    {
        var builder = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!)
        {
            MaxPoolSize = 1,
            ConnectTimeout = 1,
            ApplicationName = "IxIFlowPoolRecovery_" + Guid.NewGuid().ToString("N")
        };
        var token = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = new SqlMessageBus(builder.ConnectionString,
                TimeSpan.FromMilliseconds(20));
            await bus.PublishAsync(new PoolProbe { Token = token });
            await using var blocker = new SqlConnection(builder.ConnectionString);
            await blocker.OpenAsync();
            await using var consumer = bus.ConsumeDeliveriesAsync<PoolProbe>()
                .GetAsyncEnumerator();
            var receiving = consumer.MoveNextAsync().AsTask();
            await Task.Delay(1500);
            await blocker.DisposeAsync();

            Assert.True(await receiving.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(token, consumer.Current.Message.Token);
            await consumer.Current.AcknowledgeAsync();
        }
        finally
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                DELETE FROM dbo.WorkflowMessages WHERE MessageType = @MessageType
                  AND Payload LIKE @Payload
                """, new { MessageType = typeof(PoolProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task OldNotificationIsPrunedWithoutAConsumer()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var hostId = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = new SqlMessageBus(connectionString);
            await bus.PublishAsync(new HostHeartbeatEvent { HostId = hostId });
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                UPDATE dbo.WorkflowMessages
                SET CreatedAt = DATEADD(DAY, -31, SYSUTCDATETIME())
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(HostHeartbeatEvent).AssemblyQualifiedName,
                    Payload = $"%{hostId}%" });

            Assert.Equal(1, await bus.PruneExpiredAsync(TimeSpan.FromDays(30), 100));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(HostHeartbeatEvent).AssemblyQualifiedName,
                    Payload = $"%{hostId}%" }));
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                DELETE FROM dbo.WorkflowMessages WHERE MessageType = @MessageType
                  AND Payload LIKE @Payload
                """, new { MessageType = typeof(HostHeartbeatEvent).AssemblyQualifiedName,
                    Payload = $"%{hostId}%" });
        }
    }

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
    public async Task OneClaimRenewalErrorDoesNotExposeAnActiveDelivery()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var token = Guid.NewGuid().ToString("N");
        try
        {
            using var firstBus = new FailingRenewalBus(connectionString);
            using var secondBus = new SqlMessageBus(connectionString, TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50));
            await firstBus.PublishAsync(new RenewingBusProbe { Token = token });
            await using var firstConsumer = firstBus.ConsumeDeliveriesAsync<RenewingBusProbe>()
                .GetAsyncEnumerator();
            Assert.True(await firstConsumer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await firstBus.RenewalFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var secondConsumer = secondBus.ConsumeDeliveriesAsync<RenewingBusProbe>()
                .GetAsyncEnumerator();
            var duplicate = secondConsumer.MoveNextAsync().AsTask();
            await Task.Delay(500);
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

    private sealed class FailingRenewalBus(string connectionString) : SqlMessageBus(connectionString,
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50))
    {
        private int _failed;
        public TaskCompletionSource RenewalFailed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<bool> RenewClaimAsync(string messageId, string claimToken)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                RenewalFailed.TrySetResult();
                throw new IOException("Transient renewal failure");
            }
            return base.RenewClaimAsync(messageId, claimToken);
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
    public async Task BusyMessageCanBeDeferredWithoutUsingItsFailureBudget()
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
                await consumer.Current.DeferAsync(TimeSpan.FromSeconds(1));
            }
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var row = await connection.QuerySingleAsync<(int AttemptCount, DateTime NextVisibleAt)>("""
                SELECT AttemptCount, NextVisibleAt FROM dbo.WorkflowMessages
                WHERE MessageType = @MessageType AND Payload LIKE @Payload
                """, new { MessageType = typeof(FailureBusProbe).AssemblyQualifiedName,
                    Payload = $"%{token}%" });
            Assert.Equal(0, row.AttemptCount);
            Assert.True(row.NextVisibleAt > DateTime.UtcNow);
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
            await bus.PublishAsync(new FailureBusProbe { Token = token, TargetHostId = "host-1" });
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
            await using var consumer = bus.ConsumeDeliveriesAsync<FailureBusProbe>("host-1").GetAsyncEnumerator();
            var receiving = consumer.MoveNextAsync().AsTask();
            (int AttemptCount, DateTime? DeadLetteredAt) row = default;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                row = await connection.QuerySingleAsync<(int AttemptCount, DateTime? DeadLetteredAt)>("""
                    SELECT AttemptCount, DeadLetteredAt FROM dbo.WorkflowMessages WHERE Id = @MessageId
                    """, new { MessageId = messageId });
                if (row.DeadLetteredAt != null)
                    break;
                await Task.Delay(50);
            }
            await bus.StopAsync();
            Assert.False(await receiving);

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
    public string TargetHostId { get; set; } = string.Empty;
}

public sealed class BusDeliveryProbe
{
    public string Token { get; set; } = string.Empty;
}

public sealed class RenewingBusProbe
{
    public string Token { get; set; } = string.Empty;
}

public sealed class MaintenanceProbe
{
    public string Token { get; set; } = string.Empty;
}

public sealed class PoolProbe
{
    public string Token { get; set; } = string.Empty;
}

public sealed class GenericConsumerProbe
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
