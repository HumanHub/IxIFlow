using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Core;

/// <summary>
/// Stores workflow instances in SQL Server so host processes share the same state.
/// </summary>
public sealed class SqlWorkflowStateRepository : IWorkflowStateRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public SqlWorkflowStateRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A SQL Server connection string is required", nameof(connectionString))
            : connectionString;
    }

    public async Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync("""
            MERGE dbo.IxIFlowWorkflowInstances WITH (HOLDLOCK) AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED THEN UPDATE SET
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId, SuspensionExpiresAt = @SuspensionExpiresAt,
                StateJson = @StateJson
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, SuspensionExpiresAt, StateJson)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @SuspensionExpiresAt, @StateJson);
            """, new
        {
            instance.InstanceId,
            instance.WorkflowName,
            Status = instance.Status.ToString(),
            instance.CorrelationId,
            SuspensionExpiresAt = instance.SuspensionInfo?.ExpiresAt,
            StateJson = JsonSerializer.Serialize(instance)
        });
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var json = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId",
            new { InstanceId = instanceId });
        return json == null ? null : Deserialize(json);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
        QueryAsync("WorkflowName = @Value", workflowName);

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
        QueryAsync("Status = @Value", status.ToString());

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
        QueryAsync("CorrelationId = @Value", correlationId);

    public async Task DeleteWorkflowInstanceAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId",
            new { InstanceId = instanceId });
    }

    public async Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsReadyForResumptionAsync()
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<string>("""
            SELECT StateJson FROM dbo.IxIFlowWorkflowInstances
            WHERE Status = @Status AND (SuspensionExpiresAt IS NULL OR SuspensionExpiresAt <= SYSUTCDATETIME())
            """, new { Status = WorkflowStatus.Suspended.ToString() });
        return rows.Select(Deserialize).ToArray();
    }

    private async Task<IEnumerable<WorkflowInstance>> QueryAsync(string predicate, string value)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<string>(
            $"SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE {predicate}", new { Value = value });
        return rows.Select(Deserialize).ToArray();
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync();
            await connection.ExecuteAsync("""
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowInstances', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.IxIFlowWorkflowInstances (
                        InstanceId NVARCHAR(100) NOT NULL PRIMARY KEY,
                        WorkflowName NVARCHAR(200) NOT NULL,
                        Status NVARCHAR(50) NOT NULL,
                        CorrelationId NVARCHAR(100) NULL,
                        SuspensionExpiresAt DATETIME2 NULL,
                        StateJson NVARCHAR(MAX) NOT NULL
                    );
                    CREATE INDEX IX_IxIFlowWorkflowInstances_Status
                        ON dbo.IxIFlowWorkflowInstances (Status, SuspensionExpiresAt);
                    CREATE INDEX IX_IxIFlowWorkflowInstances_WorkflowName
                        ON dbo.IxIFlowWorkflowInstances (WorkflowName);
                    CREATE INDEX IX_IxIFlowWorkflowInstances_CorrelationId
                        ON dbo.IxIFlowWorkflowInstances (CorrelationId);
                END
                """);
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static WorkflowInstance Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowInstance>(json)
        ?? throw new InvalidOperationException("A stored workflow instance could not be deserialized");
}
