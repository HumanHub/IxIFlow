using System.Data;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using Dapper;

namespace IxIFlow.Core;

/// <summary>
/// SQL Server-based message bus implementation for distributed workflow communication
/// Uses polling-based approach for reliable message delivery with persistence
/// </summary>
public class SqlMessageBus : IMessageBus, IDisposable
{
    private readonly string _connectionString;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Dictionary<string, Task> _consumerTasks;
    private readonly object _lock = new();
    
    public SqlMessageBus(string connectionString, TimeSpan? pollInterval = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _cancellationTokenSource = new CancellationTokenSource();
        _consumerTasks = new Dictionary<string, Task>();
    }

    /// <summary>
    /// Publish a message to the SQL message bus
    /// </summary>
    public async Task PublishAsync<T>(T message) where T : class
    {
        if (message == null) throw new ArgumentNullException(nameof(message));

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
        var messageType = typeof(T).AssemblyQualifiedName ?? typeof(T).Name;
        
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            IEnumerable<T> messages;
            
            try
            {
                messages = await PollMessagesAsync<T>(messageType);
            }
            catch (Exception ex)
            {
                // Log error and continue polling
                Console.WriteLine($"Error polling messages: {ex.Message}");
                await Task.Delay(_pollInterval, _cancellationTokenSource.Token);
                continue;
            }

            foreach (var message in messages)
            {
                yield return message;
            }

            // Wait before next poll
            try
            {
                await Task.Delay(_pollInterval, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Stop the message bus and cleanup resources
    /// </summary>
    public async Task StopAsync()
    {
        _cancellationTokenSource.Cancel();
        
        lock (_lock)
        {
            var tasks = _consumerTasks.Values.ToArray();
            _consumerTasks.Clear();
            
            Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// Poll for unprocessed messages of a specific type
    /// </summary>
    private async Task<IEnumerable<T>> PollMessagesAsync<T>(string messageType) where T : class
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Use a transaction to atomically fetch and mark messages as processed
        await using var transaction = await connection.BeginTransactionAsync();
        
        try
        {
            // Fetch unprocessed messages ordered by priority and creation time
            var messageRows = await connection.QueryAsync<MessageRow>(@"
                SELECT TOP 100 Id, MessageType, Payload, CreatedAt, Priority
                FROM WorkflowMessages 
                WHERE MessageType = @MessageType AND ProcessedAt IS NULL
                ORDER BY Priority DESC, CreatedAt ASC",
                new { MessageType = messageType },
                transaction);

            var messages = new List<T>();
            var messageIds = new List<string>();

            foreach (var row in messageRows)
            {
                try
                {
                    var message = JsonSerializer.Deserialize<T>(row.Payload);
                    if (message != null)
                    {
                        messages.Add(message);
                        messageIds.Add(row.Id);
                    }
                }
                catch
                {
                    // Skip malformed messages but mark them as processed
                    messageIds.Add(row.Id);
                }
            }

            // Mark messages as processed
            if (messageIds.Count > 0)
            {
                await connection.ExecuteAsync(@"
                    UPDATE WorkflowMessages 
                    SET ProcessedAt = @ProcessedAt 
                    WHERE Id IN @MessageIds",
                    new 
                    { 
                        ProcessedAt = DateTime.UtcNow,
                        MessageIds = messageIds 
                    },
                    transaction);
            }

            await transaction.CommitAsync();
            return messages;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
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
