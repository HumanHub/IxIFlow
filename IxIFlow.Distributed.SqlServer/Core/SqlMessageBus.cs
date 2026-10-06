using System.Data;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using Dapper;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
/// SQL Server-based message bus implementation for distributed workflow communication
/// Uses polling-based approach for reliable message delivery with persistence
/// </summary>
public class SqlMessageBus : IAcknowledgingMessageBus, IMessageMaintenance, IDisposable
{
    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _claimDuration;
    private readonly TimeSpan _renewalInterval;
    private readonly TimeSpan _failureBackoff;
    private readonly int _maxDeliveryAttempts;
    private readonly ILogger<SqlMessageBus>? _logger;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;
    
    public SqlMessageBus(string connectionString, TimeSpan? pollInterval = null,
        TimeSpan? claimDuration = null, TimeSpan? renewalInterval = null,
        TimeSpan? failureBackoff = null, int maxDeliveryAttempts = 8,
        ILogger<SqlMessageBus>? logger = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _claimDuration = claimDuration ?? TimeSpan.FromMinutes(5);
        _renewalInterval = renewalInterval ?? TimeSpan.FromMinutes(1);
        _failureBackoff = failureBackoff ?? TimeSpan.FromSeconds(1);
        _maxDeliveryAttempts = maxDeliveryAttempts;
        _logger = logger;
        if (_claimDuration <= TimeSpan.Zero || _renewalInterval <= TimeSpan.Zero ||
            _renewalInterval >= _claimDuration || _claimDuration.TotalMilliseconds > int.MaxValue ||
            _failureBackoff < TimeSpan.FromMilliseconds(1) ||
            _failureBackoff > TimeSpan.FromMinutes(1) || _maxDeliveryAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(claimDuration));
        _cancellationTokenSource = new CancellationTokenSource();
    }

    /// <summary>
    /// Publish a message to the SQL message bus
    /// </summary>
    public async Task PublishAsync<T>(T message) where T : class
    {
        if (message == null) throw new ArgumentNullException(nameof(message));

        await EnsureSchemaAsync();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var eventIdentity = message switch
        {
            WorkflowExecutionCompletedEvent completed when
                !string.IsNullOrWhiteSpace(completed.InstanceId) =>
                (Prefix: "completion-", completed.InstanceId),
            WorkflowExecutionStartedEvent started when
                !string.IsNullOrWhiteSpace(started.InstanceId) =>
                (Prefix: "started-", started.InstanceId),
            _ => (Prefix: "", InstanceId: "")
        };
        var id = eventIdentity.Prefix.Length == 0
            ? Guid.NewGuid().ToString("N")
            : eventIdentity.Prefix + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(eventIdentity.InstanceId)).AsSpan(0, 16)).ToLowerInvariant();
        await connection.ExecuteAsync(@"
            INSERT INTO WorkflowMessages (Id, MessageType, Payload, CreatedAt, ProcessedAt, Priority)
            SELECT @Id, @MessageType, @Payload, @CreatedAt, NULL, @Priority
            WHERE NOT EXISTS (SELECT 1 FROM WorkflowMessages WITH (UPDLOCK, HOLDLOCK)
                WHERE Id = @Id)",
            new
            {
                Id = id,
                MessageType = typeof(T).AssemblyQualifiedName ?? typeof(T).Name,
                Payload = JsonSerializer.Serialize(message),
                CreatedAt = DateTime.UtcNow,
                Priority = GetMessagePriority(message)
            });
    }

    public async Task<int> PruneExpiredAsync(TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));
        if (batchSize < 1 || batchSize > 10_000)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        await EnsureSchemaAsync();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE TOP (@BatchSize) FROM dbo.WorkflowMessages
            WHERE ProcessedAt < @CutoffUtc
               OR (CreatedAt < @CutoffUtc AND (
                   MessageType LIKE @StartedType OR
                   MessageType LIKE @CompletedType OR
                   MessageType LIKE @HeartbeatType))
            """, new
            {
                BatchSize = batchSize,
                CutoffUtc = DateTime.UtcNow - retention,
                StartedType = typeof(WorkflowExecutionStartedEvent).FullName + ",%",
                CompletedType = typeof(WorkflowExecutionCompletedEvent).FullName + ",%",
                HeartbeatType = typeof(HostHeartbeatEvent).FullName + ",%"
            },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Consume messages of a specific type from the SQL message bus
    /// </summary>
    public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
    {
        await foreach (var delivery in ConsumeDeliveriesAsync<T>())
        {
            yield return delivery.Message;
        }
    }

    public async IAsyncEnumerable<IMessageDelivery<T>> ConsumeDeliveriesAsync<T>(
        string? targetHostId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token);
        var token = linked.Token;
        var messageType = typeof(T).AssemblyQualifiedName ?? typeof(T).Name;
        var consecutiveFailures = 0;

        while (!token.IsCancellationRequested)
        {
            SqlMessageDelivery<T>? delivery;
            try
            {
                delivery = await ClaimMessageAsync<T>(messageType, targetHostId);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                consecutiveFailures = Math.Min(consecutiveFailures + 1, 6);
                _logger?.LogWarning(error,
                    "Could not claim workflow message {MessageType}; retrying", messageType);
                await Task.Delay(TimeSpan.FromSeconds(1 << (consecutiveFailures - 1)), token);
                continue;
            }

            if (delivery == null)
            {
                try
                {
                    await Task.Delay(_pollInterval, token);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                continue;
            }

            try
            {
                yield return delivery;
            }
            finally
            {
                await delivery.StopRenewalAsync();
                if (!delivery.IsAcknowledged && !delivery.IsRejected)
                {
                    try
                    {
                        await ReleaseClaimAsync(delivery.MessageId, delivery.ClaimToken);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        _logger?.LogWarning(error,
                            "Could not release workflow message {MessageId}; claim will expire",
                            delivery.MessageId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Stop the message bus and cleanup resources
    /// </summary>
    public Task StopAsync()
    {
        _cancellationTokenSource.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Poll for unprocessed messages of a specific type
    /// </summary>
    private async Task<SqlMessageDelivery<T>?> ClaimMessageAsync<T>(string messageType, string? targetHostId)
        where T : class
    {
        await EnsureSchemaAsync();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var claimToken = Guid.NewGuid().ToString("N");
        var row = await connection.QuerySingleOrDefaultAsync<MessageRow>("""
            ;WITH next_message AS (
                SELECT TOP (1) Id
                FROM dbo.WorkflowMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE MessageType = @MessageType AND ProcessedAt IS NULL
                  AND DeadLetteredAt IS NULL
                  AND (NextVisibleAt IS NULL OR NextVisibleAt <= SYSUTCDATETIME())
                  AND (ClaimedUntil IS NULL OR ClaimedUntil <= SYSUTCDATETIME())
                  AND (@TargetHostId IS NULL OR
                       CASE WHEN ISJSON(Payload) = 1 THEN JSON_VALUE(Payload, '$.TargetHostId')
                            ELSE @TargetHostId END = @TargetHostId)
                ORDER BY Priority DESC, CreatedAt ASC
            )
            UPDATE message
            SET ClaimToken = @ClaimToken, ClaimedUntil = DATEADD(MILLISECOND, @ClaimMilliseconds, SYSUTCDATETIME())
            OUTPUT inserted.Id, inserted.Payload
            FROM dbo.WorkflowMessages AS message
            INNER JOIN next_message ON next_message.Id = message.Id;
            """, new { MessageType = messageType, TargetHostId = targetHostId,
                ClaimToken = claimToken, ClaimMilliseconds = (int)Math.Ceiling(_claimDuration.TotalMilliseconds) });
        if (row == null)
        {
            return null;
        }

        try
        {
            var message = JsonSerializer.Deserialize<T>(row.Payload)
                ?? throw new JsonException($"Message {row.Id} deserialized to null");
            return new SqlMessageDelivery<T>(this, row.Id, claimToken, message);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            await RejectClaimAsync(row.Id, claimToken, error);
            return null;
        }
    }

    private async Task AcknowledgeAsync(string messageId, string claimToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.WorkflowMessages
            SET ProcessedAt = SYSUTCDATETIME(), ClaimToken = NULL, ClaimedUntil = NULL
            WHERE Id = @MessageId AND ClaimToken = @ClaimToken AND ProcessedAt IS NULL
                AND ClaimedUntil > SYSUTCDATETIME()
            """, new { MessageId = messageId, ClaimToken = claimToken });
        if (changed != 1)
        {
            throw new MessageClaimLostException($"Message claim {messageId} is no longer owned by this consumer");
        }
    }

    protected virtual async Task<bool> RenewClaimAsync(string messageId, string claimToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.WorkflowMessages
            SET ClaimedUntil = DATEADD(MILLISECOND, @ClaimMilliseconds, SYSUTCDATETIME())
            WHERE Id = @MessageId AND ClaimToken = @ClaimToken AND ProcessedAt IS NULL
                AND ClaimedUntil > SYSUTCDATETIME()
            """, new { MessageId = messageId, ClaimToken = claimToken,
                ClaimMilliseconds = (int)Math.Ceiling(_claimDuration.TotalMilliseconds) });
        return changed == 1;
    }

    private async Task ReleaseClaimAsync(string messageId, string claimToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            UPDATE dbo.WorkflowMessages
            SET ClaimToken = NULL, ClaimedUntil = NULL
            WHERE Id = @MessageId AND ClaimToken = @ClaimToken AND ProcessedAt IS NULL
            """, new { MessageId = messageId, ClaimToken = claimToken });
    }

    private async Task RejectClaimAsync(string messageId, string claimToken, Exception error)
    {
        var description = $"{error.GetType().Name}: {error.Message}";
        if (description.Length > 2048)
            description = description[..2048];
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.WorkflowMessages
            SET AttemptCount = AttemptCount + 1,
                LastError = @Description,
                NextVisibleAt = CASE WHEN AttemptCount + 1 >= @MaxDeliveryAttempts
                    THEN NULL ELSE DATEADD(MILLISECOND,
                        @BaseBackoffMilliseconds *
                        CASE WHEN AttemptCount >= 5 THEN 32
                             ELSE CONVERT(INT, POWER(CONVERT(FLOAT, 2), AttemptCount)) END,
                        SYSUTCDATETIME()) END,
                DeadLetteredAt = CASE WHEN AttemptCount + 1 >= @MaxDeliveryAttempts
                    THEN SYSUTCDATETIME() ELSE NULL END,
                ClaimToken = NULL, ClaimedUntil = NULL
            WHERE Id = @MessageId AND ClaimToken = @ClaimToken AND ProcessedAt IS NULL
                AND ClaimedUntil > SYSUTCDATETIME()
            """, new { MessageId = messageId, ClaimToken = claimToken,
                Description = description, MaxDeliveryAttempts = _maxDeliveryAttempts,
                BaseBackoffMilliseconds = (int)Math.Ceiling(_failureBackoff.TotalMilliseconds) });
        if (changed != 1)
            throw new MessageClaimLostException($"Message claim {messageId} is no longer owned by this consumer");
    }

    private async Task DeferClaimAsync(string messageId, string claimToken, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero || delay.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(delay));
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.WorkflowMessages
            SET NextVisibleAt = DATEADD(MILLISECOND, @DelayMilliseconds, SYSUTCDATETIME()),
                ClaimToken = NULL, ClaimedUntil = NULL
            WHERE Id = @MessageId AND ClaimToken = @ClaimToken AND ProcessedAt IS NULL
                AND ClaimedUntil > SYSUTCDATETIME()
            """, new { MessageId = messageId, ClaimToken = claimToken,
                DelayMilliseconds = (int)Math.Ceiling(delay.TotalMilliseconds) });
        if (changed != 1)
            throw new MessageClaimLostException($"Message claim {messageId} is no longer owned by this consumer");
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady) return;
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            // Separate hosts can initialize the same database at the same time.
            var lockResult = await connection.ExecuteScalarAsync<int>("""
                DECLARE @result INT;
                EXEC @result = sys.sp_getapplock
                    @Resource = N'IxIFlow.MessageSchema',
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 30000;
                SELECT @result;
                """, transaction: transaction);
            if (lockResult < 0)
                throw new InvalidOperationException($"Could not lock the message schema ({lockResult})");
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
                END;
                IF COL_LENGTH(N'dbo.WorkflowMessages', N'ClaimToken') IS NULL
                    ALTER TABLE dbo.WorkflowMessages ADD ClaimToken NVARCHAR(50) NULL;
                IF COL_LENGTH(N'dbo.WorkflowMessages', N'ClaimedUntil') IS NULL
                    ALTER TABLE dbo.WorkflowMessages ADD ClaimedUntil DATETIME2 NULL;
                IF COL_LENGTH(N'dbo.WorkflowMessages', N'AttemptCount') IS NULL
                    ALTER TABLE dbo.WorkflowMessages ADD AttemptCount INT NOT NULL DEFAULT 0;
                IF COL_LENGTH(N'dbo.WorkflowMessages', N'NextVisibleAt') IS NULL
                    ALTER TABLE dbo.WorkflowMessages ADD NextVisibleAt DATETIME2 NULL;
                IF COL_LENGTH(N'dbo.WorkflowMessages', N'DeadLetteredAt') IS NULL
                    ALTER TABLE dbo.WorkflowMessages ADD DeadLetteredAt DATETIME2 NULL;
                IF COL_LENGTH(N'dbo.WorkflowMessages', N'LastError') IS NULL
                    ALTER TABLE dbo.WorkflowMessages ADD LastError NVARCHAR(2048) NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.WorkflowMessages')
                      AND name = N'IX_WorkflowMessages_PendingPoll')
                    EXEC(N'CREATE INDEX IX_WorkflowMessages_PendingPoll
                        ON dbo.WorkflowMessages (MessageType, Priority DESC, CreatedAt ASC)
                        INCLUDE (NextVisibleAt, ClaimedUntil)
                        WHERE ProcessedAt IS NULL AND DeadLetteredAt IS NULL');
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.WorkflowMessages')
                      AND name = N'IX_WorkflowMessages_ProcessedAt')
                    CREATE INDEX IX_WorkflowMessages_ProcessedAt
                        ON dbo.WorkflowMessages (ProcessedAt)
                        WHERE ProcessedAt IS NOT NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.WorkflowMessages')
                      AND name = N'IX_WorkflowMessages_CreatedAt')
                    CREATE INDEX IX_WorkflowMessages_CreatedAt
                        ON dbo.WorkflowMessages (CreatedAt)
                        INCLUDE (MessageType)
                        WHERE ProcessedAt IS NULL;
                """, transaction: transaction);
            await transaction.CommitAsync();
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    /// <summary>
    /// Get message priority for ordering
    /// </summary>
    private static int GetMessagePriority<T>(T message)
    {
        return message switch
        {
            ExecuteWorkflowCommand cmd => cmd.Priority,
            CancelWorkflowCommand => 1000, // High priority for cancellations
            ResumeWorkflowCommand => 500,   // Medium priority for resumes
            _ => 0 // Default priority
        };
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }

    /// <summary>
    /// Helper class for database row mapping
    /// </summary>
    private class MessageRow
    {
        public string Id { get; set; } = "";
        public string MessageType { get; set; } = "";
        public string Payload { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int Priority { get; set; }
    }

    private sealed class SqlMessageDelivery<T> : IMessageDelivery<T> where T : class
    {
        private readonly SqlMessageBus _bus;
        private readonly string _messageId;
        private readonly string _claimToken;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _renewal;
        private int _stopped;
        public T Message { get; }
        public string MessageId => _messageId;
        public string ClaimToken => _claimToken;
        public bool IsAcknowledged { get; private set; }
        public bool IsRejected { get; private set; }

        public SqlMessageDelivery(SqlMessageBus bus, string messageId, string claimToken, T message)
        {
            _bus = bus;
            _messageId = messageId;
            _claimToken = claimToken;
            Message = message;
            _renewal = RenewAsync();
        }

        public async Task AcknowledgeAsync()
        {
            if (IsAcknowledged) return;
            await _bus.AcknowledgeAsync(_messageId, _claimToken);
            IsAcknowledged = true;
            await StopRenewalAsync();
        }

        public async Task RejectAsync(Exception error)
        {
            ArgumentNullException.ThrowIfNull(error);
            if (IsRejected) return;
            await _bus.RejectClaimAsync(_messageId, _claimToken, error);
            IsRejected = true;
            await StopRenewalAsync();
        }

        public async Task DeferAsync(TimeSpan delay)
        {
            if (IsRejected) return;
            await _bus.DeferClaimAsync(_messageId, _claimToken, delay);
            IsRejected = true;
            await StopRenewalAsync();
        }

        public async Task StopRenewalAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            _stop.Cancel();
            await _renewal;
            _stop.Dispose();
        }

        private async Task RenewAsync()
        {
            var expiresAtUtc = DateTime.UtcNow.Add(_bus._claimDuration);
            try
            {
                while (true)
                {
                    await Task.Delay(_bus._renewalInterval, _stop.Token);
                    while (true)
                    {
                        try
                        {
                            if (!await _bus.RenewClaimAsync(_messageId, _claimToken))
                                return;
                            expiresAtUtc = DateTime.UtcNow.Add(_bus._claimDuration);
                            break;
                        }
                        catch (Exception) when (!_stop.IsCancellationRequested &&
                                                DateTime.UtcNow < expiresAtUtc)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(50), _stop.Token);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch
            {
                // The claim expires naturally if SQL cannot renew it.
            }
        }
    }
}

/*
SQL TABLE DEFINITIONS:

-- WorkflowMessages table for message bus
CREATE TABLE WorkflowMessages (
    Id NVARCHAR(50) PRIMARY KEY,
    MessageType NVARCHAR(500) NOT NULL,
    Payload NVARCHAR(MAX) NOT NULL,
    CreatedAt DATETIME2 NOT NULL,
    ProcessedAt DATETIME2 NULL,
    Priority INT NOT NULL DEFAULT 0,
    
    INDEX IX_WorkflowMessages_MessageType_ProcessedAt_Priority 
        (MessageType, ProcessedAt, Priority DESC, CreatedAt ASC)
);

-- HostRegistry table for host registration and discovery
CREATE TABLE HostRegistry (
    HostId NVARCHAR(100) PRIMARY KEY,
    EndpointUrl NVARCHAR(500) NOT NULL,
    Tags NVARCHAR(1000) NOT NULL DEFAULT '',
    Weight INT NOT NULL DEFAULT 1,
    MaxConcurrentWorkflows INT NOT NULL DEFAULT 100,
    LastHeartbeat DATETIME2 NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    
    INDEX IX_HostRegistry_IsActive_LastHeartbeat (IsActive, LastHeartbeat),
    INDEX IX_HostRegistry_Tags (Tags)
);

-- WorkflowInstances table for distributed state (extends existing if needed)
CREATE TABLE WorkflowInstances (
    InstanceId NVARCHAR(50) PRIMARY KEY,
    WorkflowName NVARCHAR(200) NOT NULL,
    WorkflowVersion NVARCHAR(50) NOT NULL,
    Status NVARCHAR(50) NOT NULL,
    CorrelationId NVARCHAR(50) NULL,
    CurrentHostId NVARCHAR(100) NULL,
    CreatedAt DATETIME2 NOT NULL,
    StartedAt DATETIME2 NULL,
    CompletedAt DATETIME2 NULL,
    TotalSteps INT NOT NULL DEFAULT 0,
    CurrentStepNumber INT NOT NULL DEFAULT 0,
    WorkflowDataJson NVARCHAR(MAX) NOT NULL,
    WorkflowDataType NVARCHAR(500) NOT NULL,
    ExecutionStateJson NVARCHAR(MAX) NULL,
    SuspensionInfoJson NVARCHAR(MAX) NULL,
    LastError NVARCHAR(MAX) NULL,
    LastErrorStackTrace NVARCHAR(MAX) NULL,
    PropertiesJson NVARCHAR(MAX) NULL,
    
    INDEX IX_WorkflowInstances_Status (Status),
    INDEX IX_WorkflowInstances_CurrentHostId (CurrentHostId),
    INDEX IX_WorkflowInstances_CorrelationId (CorrelationId)
);

-- WorkflowEvents table for event storage (extends existing if needed)
CREATE TABLE WorkflowEvents (
    Id NVARCHAR(50) PRIMARY KEY,
    WorkflowInstanceId NVARCHAR(50) NOT NULL,
    EventType NVARCHAR(500) NOT NULL,
    EventDataJson NVARCHAR(MAX) NOT NULL,
    OccurredAt DATETIME2 NOT NULL,
    ProcessedAt DATETIME2 NULL,
    
    FOREIGN KEY (WorkflowInstanceId) REFERENCES WorkflowInstances(InstanceId),
    INDEX IX_WorkflowEvents_WorkflowInstanceId (WorkflowInstanceId),
    INDEX IX_WorkflowEvents_EventType_ProcessedAt (EventType, ProcessedAt)
);

-- HostMetrics table for monitoring and health tracking
CREATE TABLE HostMetrics (
    Id NVARCHAR(50) PRIMARY KEY,
    HostId NVARCHAR(100) NOT NULL,
    CurrentWorkflowCount INT NOT NULL,
    MaxWorkflowCount INT NOT NULL,
    CpuUsage DECIMAL(5,2) NOT NULL DEFAULT 0,
    MemoryUsage DECIMAL(5,2) NOT NULL DEFAULT 0,
    Status NVARCHAR(50) NOT NULL,
    RecordedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    
    FOREIGN KEY (HostId) REFERENCES HostRegistry(HostId),
    INDEX IX_HostMetrics_HostId_RecordedAt (HostId, RecordedAt DESC)
);
*/
