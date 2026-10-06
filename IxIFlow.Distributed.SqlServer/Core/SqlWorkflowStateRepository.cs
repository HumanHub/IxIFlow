using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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

    public async Task<bool> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var changed = await connection.ExecuteAsync("""
            MERGE dbo.IxIFlowWorkflowLeases WITH (HOLDLOCK) AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED AND target.ExpiresAtUtc <= SYSUTCDATETIME() THEN
                UPDATE SET Token = @Token,
                    ExpiresAtUtc = DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME())
            WHEN NOT MATCHED THEN
                INSERT (InstanceId, Token, ExpiresAtUtc)
                VALUES (@InstanceId, @Token,
                    DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME()));
            """, new { InstanceId = instanceId, Token = token, Milliseconds = milliseconds });
        return changed == 1;
    }

    public async Task<bool> RenewExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.IxIFlowWorkflowLeases
            SET ExpiresAtUtc = DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME())
            WHERE InstanceId = @InstanceId AND Token = @Token
                AND ExpiresAtUtc > SYSUTCDATETIME()
            """, new { InstanceId = instanceId, Token = token, Milliseconds = milliseconds });
        return changed == 1;
    }

    public async Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var changed = await connection.ExecuteAsync("""
            DELETE FROM dbo.IxIFlowWorkflowLeases
            WHERE InstanceId = @InstanceId AND Token = @Token
            """, new { InstanceId = instanceId, Token = token });
        return changed == 1;
    }

    public async Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Revision != 0)
            throw new InvalidOperationException("Revisioned instances require an atomic commit");
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync("""
            MERGE dbo.IxIFlowWorkflowInstances WITH (HOLDLOCK) AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED AND target.Revision = 0 THEN UPDATE SET
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId, SuspensionExpiresAt = @SuspensionExpiresAt,
                SuspensionId = @SuspensionId, StateJson = @StateJson
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, SuspensionExpiresAt, SuspensionId, StateJson)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @SuspensionExpiresAt, @SuspensionId, @StateJson);
            IF @@ROWCOUNT = 0
                THROW 51000, 'Revisioned instances require an atomic commit', 1;
            """, new
        {
            instance.InstanceId,
            instance.WorkflowName,
            Status = instance.Status.ToString(),
            instance.CorrelationId,
            SuspensionExpiresAt = instance.SuspensionInfo?.ExpiresAt,
            SuspensionId = instance.SuspensionInfo?.SuspensionId,
            StateJson = JsonSerializer.Serialize(instance)
        });
    }

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(
        WorkflowInstance instance, long expectedRevision, string commitId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (commitId.Length > 100)
            throw new ArgumentException("Commit IDs must be at most 100 characters", nameof(commitId));
        var snapshot = Deserialize(JsonSerializer.Serialize(instance));
        snapshot.Revision = checked(expectedRevision + 1);
        var json = JsonSerializer.Serialize(snapshot);
        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(json));

        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var currentRevision = await connection.QuerySingleOrDefaultAsync<long?>("""
            SELECT Revision FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId
            """, new { instance.InstanceId }, transaction) ?? 0;
        var receipt = await connection.QuerySingleOrDefaultAsync<CommitReceipt>("""
            SELECT ExpectedRevision, Revision, PayloadHash FROM dbo.IxIFlowWorkflowCommits
            WHERE InstanceId = @InstanceId AND CommitId = @CommitId
            """, new { instance.InstanceId, CommitId = commitId }, transaction);
        var lease = await connection.QuerySingleOrDefaultAsync<LeaseRecord>("""
            SELECT Token,
                CAST(CASE WHEN ExpiresAtUtc > SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT) AS IsActive
            FROM dbo.IxIFlowWorkflowLeases WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId
            """, new { instance.InstanceId }, transaction);
        if (receipt != null)
        {
            if (receipt.ExpectedRevision != expectedRevision || !receipt.PayloadHash.SequenceEqual(payloadHash))
                throw new InvalidOperationException("A commit ID cannot be reused for a different transition");
            if (snapshot.Status == WorkflowStatus.Running && instance.ExecutionLeaseToken != null &&
                (lease == null || lease.Token != instance.ExecutionLeaseToken || !lease.IsActive))
            {
                await transaction.CommitAsync();
                return new WorkflowCommitResult(WorkflowCommitStatus.Conflict, currentRevision);
            }
            await transaction.CommitAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, receipt.Revision);
        }
        if (currentRevision != expectedRevision)
        {
            await transaction.CommitAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.Conflict, currentRevision);
        }

        if (lease == null ? instance.ExecutionLeaseToken != null :
            lease.Token != instance.ExecutionLeaseToken || !lease.IsActive)
        {
            await transaction.CommitAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.Conflict, currentRevision);
        }

        await connection.ExecuteAsync("""
            MERGE dbo.IxIFlowWorkflowInstances AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED THEN UPDATE SET
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId, SuspensionExpiresAt = @SuspensionExpiresAt,
                SuspensionId = @SuspensionId, StateJson = @StateJson, Revision = @Revision
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, SuspensionExpiresAt, SuspensionId, StateJson, Revision)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @SuspensionExpiresAt, @SuspensionId, @StateJson, @Revision);
            INSERT INTO dbo.IxIFlowWorkflowCommits (InstanceId, CommitId, ExpectedRevision, Revision, PayloadHash)
            VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
            """, new
        {
            instance.InstanceId, instance.WorkflowName, instance.CorrelationId,
            Status = instance.Status.ToString(),
            SuspensionExpiresAt = instance.SuspensionInfo?.ExpiresAt,
            SuspensionId = instance.SuspensionInfo?.SuspensionId,
            StateJson = json, snapshot.Revision,
            ExpectedRevision = expectedRevision, CommitId = commitId, PayloadHash = payloadHash
        }, transaction);
        if (snapshot.Status != WorkflowStatus.Running)
            await connection.ExecuteAsync("""
                DELETE FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId = @InstanceId
                """, new { instance.InstanceId }, transaction);
        await transaction.CommitAsync();
        return new WorkflowCommitResult(WorkflowCommitStatus.Applied, snapshot.Revision);
    }

    private sealed class CommitReceipt
    {
        public long ExpectedRevision { get; set; }
        public long Revision { get; set; }
        public byte[] PayloadHash { get; set; } = [];
    }

    private sealed class LeaseRecord
    {
        public string Token { get; set; } = "";
        public bool IsActive { get; set; }
    }

    public async Task<bool> TryClaimSuspendedWorkflowAsync(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Status != WorkflowStatus.Running ||
            string.IsNullOrWhiteSpace(instance.SuspensionInfo?.SuspensionId))
        {
            throw new ArgumentException("A running instance with a suspension ID is required", nameof(instance));
        }
        if (instance.Revision != 0)
            return false;

        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var updated = await connection.ExecuteAsync("""
            UPDATE dbo.IxIFlowWorkflowInstances
            SET Status = @RunningStatus, StateJson = @StateJson
            WHERE InstanceId = @InstanceId AND Status = @SuspendedStatus AND Revision = 0
                AND SuspensionId = @SuspensionId
            """, new
        {
            instance.InstanceId,
            SuspensionId = instance.SuspensionInfo.SuspensionId,
            RunningStatus = WorkflowStatus.Running.ToString(),
            SuspendedStatus = WorkflowStatus.Suspended.ToString(),
            StateJson = JsonSerializer.Serialize(instance)
        });
        return updated == 1;
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
            "DELETE FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId = @InstanceId",
            new { InstanceId = instanceId });
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
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            // Separate hosts can initialize the same database at the same time.
            var lockResult = await connection.ExecuteScalarAsync<int>("""
                DECLARE @result INT;
                EXEC @result = sys.sp_getapplock
                    @Resource = N'IxIFlow.WorkflowSchema',
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 30000;
                SELECT @result;
                """, transaction: transaction);
            if (lockResult < 0)
                throw new InvalidOperationException($"Could not lock the workflow schema ({lockResult})");
            await connection.ExecuteAsync("""
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowInstances', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.IxIFlowWorkflowInstances (
                        InstanceId NVARCHAR(100) NOT NULL PRIMARY KEY,
                        WorkflowName NVARCHAR(200) NOT NULL,
                        Status NVARCHAR(50) NOT NULL,
                        CorrelationId NVARCHAR(100) NULL,
                        SuspensionExpiresAt DATETIME2 NULL,
                        SuspensionId NVARCHAR(100) NULL,
                        StateJson NVARCHAR(MAX) NOT NULL,
                        Revision BIGINT NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IX_IxIFlowWorkflowInstances_Status
                        ON dbo.IxIFlowWorkflowInstances (Status, SuspensionExpiresAt);
                    CREATE INDEX IX_IxIFlowWorkflowInstances_WorkflowName
                        ON dbo.IxIFlowWorkflowInstances (WorkflowName);
                    CREATE INDEX IX_IxIFlowWorkflowInstances_CorrelationId
                        ON dbo.IxIFlowWorkflowInstances (CorrelationId);
                END
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'SuspensionId') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances ADD SuspensionId NVARCHAR(100) NULL;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'Revision') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances ADD Revision BIGINT NOT NULL DEFAULT 0;
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowCommits', N'U') IS NULL
                    CREATE TABLE dbo.IxIFlowWorkflowCommits (
                        InstanceId NVARCHAR(100) NOT NULL,
                        CommitId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        ExpectedRevision BIGINT NOT NULL,
                        Revision BIGINT NOT NULL,
                        PayloadHash BINARY(32) NOT NULL,
                        CONSTRAINT PK_IxIFlowWorkflowCommits PRIMARY KEY (InstanceId, CommitId),
                        CONSTRAINT FK_IxIFlowWorkflowCommits_Instance FOREIGN KEY (InstanceId)
                            REFERENCES dbo.IxIFlowWorkflowInstances (InstanceId) ON DELETE CASCADE
                    );
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowLeases', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.IxIFlowWorkflowLeases (
                        InstanceId NVARCHAR(100) NOT NULL PRIMARY KEY,
                        Token NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        ExpiresAtUtc DATETIME2 NOT NULL
                    );
                    CREATE INDEX IX_IxIFlowWorkflowLeases_ExpiresAt
                        ON dbo.IxIFlowWorkflowLeases (ExpiresAtUtc);
                END
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                UPDATE dbo.IxIFlowWorkflowInstances
                SET SuspensionId = COALESCE(
                    NULLIF(JSON_VALUE(StateJson, '$.SuspensionInfo.SuspensionId'), ''),
                    CONVERT(NVARCHAR(36), NEWID()))
                WHERE Revision = 0 AND Status = 'Suspended' AND SuspensionId IS NULL;
                UPDATE dbo.IxIFlowWorkflowInstances
                SET StateJson = JSON_MODIFY(StateJson, '$.SuspensionInfo.SuspensionId', SuspensionId)
                WHERE Revision = 0 AND Status = 'Suspended' AND SuspensionId IS NOT NULL
                    AND JSON_VALUE(StateJson, '$.SuspensionInfo.SuspensionId') IS NULL;
                """, transaction: transaction);
            await transaction.CommitAsync();
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

    private static int LeaseMilliseconds(string instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return checked((int)Math.Ceiling(duration.TotalMilliseconds));
    }
}
