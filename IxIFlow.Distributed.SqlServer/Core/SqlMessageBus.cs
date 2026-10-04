using System.Data;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using Dapper;
using System.Runtime.CompilerServices;

namespace IxIFlow.Core;

/// <summary>
/// SQL Server-based message bus implementation for distributed workflow communication
/// Uses polling-based approach for reliable message delivery with persistence
/// </summary>
public class SqlMessageBus : IAcknowledgingMessageBus, IDisposable
{
    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;
    
    public SqlMessageBus(string connectionString, TimeSpan? pollInterval = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
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

        await connection.ExecuteAsync(@"
            INSERT INTO WorkflowMessages (Id, MessageType, Payload, CreatedAt, ProcessedAt, Priority)
            VALUES (@Id, @MessageType, @Payload, @CreatedAt, NULL, @Priority)",
            new
            {
                Id = Guid.NewGuid().ToString(),
                MessageType = typeof(T).AssemblyQualifiedName ?? typeof(T).Name,
                Payload = JsonSerializer.Serialize(message),
                CreatedAt = DateTime.UtcNow,
                Priority = GetMessagePriority(message)
            });
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

        while (!token.IsCancellationRequested)
        {
            SqlMessageDelivery<T>? delivery;
            try
            {
                delivery = await ClaimMessageAsync<T>(messageType, targetHostId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                yield break;
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
                if (!delivery.IsAcknowledged)
                {
                    await ReleaseClaimAsync(delivery.MessageId, delivery.ClaimToken);
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
                  AND (ClaimedUntil IS NULL OR ClaimedUntil <= SYSUTCDATETIME())
                  AND (@TargetHostId IS NULL OR JSON_VALUE(Payload, '$.TargetHostId') = @TargetHostId)
                ORDER BY Priority DESC, CreatedAt ASC
            )
            UPDATE message
            SET ClaimToken = @ClaimToken, ClaimedUntil = DATEADD(SECOND, 300, SYSUTCDATETIME())
            OUTPUT inserted.Id, inserted.Payload
            FROM dbo.WorkflowMessages AS message
            INNER JOIN next_message ON next_message.Id = message.Id;
            """, new { MessageType = messageType, TargetHostId = targetHostId, ClaimToken = claimToken });
        if (row == null)
        {
            return null;
        }

        var message = JsonSerializer.Deserialize<T>(row.Payload)
            ?? throw new InvalidOperationException($"Message {row.Id} could not be deserialized");
        return new SqlMessageDelivery<T>(this, row.Id, claimToken, message);
    }

    private async Task AcknowledgeAsync(string messageId, string claimToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.WorkflowMessages
            SET ProcessedAt = SYSUTCDATETIME(), ClaimToken = NULL, ClaimedUntil = NULL
            WHERE Id = @MessageId AND ClaimToken = @ClaimToken AND ProcessedAt IS NULL
            """, new { MessageId = messageId, ClaimToken = claimToken });
        if (changed != 1)
        {
            throw new InvalidOperationException($"Message claim {messageId} is no longer owned by this consumer");
        }
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

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady) return;
            await using var connection = new SqlConnection(_connectionString);
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
                END;
                IF COL_LENGTH(N'dbo.WorkflowMessages', N'ClaimToken') IS NULL
                    ALTER TABLE dbo.WorkflowMessages ADD ClaimToken NVARCHAR(50) NULL;
                IF COL_LENGTH(N'dbo.WorkflowMessages', N'ClaimedUntil') IS NULL
                    ALTER TABLE dbo.WorkflowMessages ADD ClaimedUntil DATETIME2 NULL;
                """);
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

    private sealed class SqlMessageDelivery<T>(SqlMessageBus bus, string messageId, string claimToken, T message)
        : IMessageDelivery<T> where T : class
    {
        public T Message => message;
        public string MessageId => messageId;
        public string ClaimToken => claimToken;
        public bool IsAcknowledged { get; private set; }

        public async Task AcknowledgeAsync()
        {
            if (IsAcknowledged) return;
            await bus.AcknowledgeAsync(messageId, claimToken);
            IsAcknowledged = true;
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
