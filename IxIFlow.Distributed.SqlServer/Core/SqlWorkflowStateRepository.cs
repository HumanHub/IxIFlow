using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Core;

/// <summary>
/// Stores workflow instances in SQL Server so host processes share the same state.
/// </summary>
public sealed class SqlWorkflowStateRepository : IWorkflowStateRepository, IWorkflowCompletionOutbox
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public async Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var json = await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT StateJson FROM dbo.IxIFlowWorkflowInstances
            WHERE InstanceId = @InstanceId
              AND Status IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
              AND CompletionPublished = 0
            """, new { InstanceId = instanceId });
        return json == null ? null : Deserialize(json);
    }

    public async Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync()
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<string>("""
            SELECT TOP (100) StateJson FROM dbo.IxIFlowWorkflowInstances
            WHERE Status IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
              AND CompletionPublished = 0
            ORDER BY InstanceId
            """);
        return rows.Select(Deserialize).ToArray();
    }

    public async Task<IEnumerable<WorkflowInstance>> ClaimUnpublishedCompletionsAsync(
        string? instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<string>("""
            ;WITH pending AS (
                SELECT TOP (100) InstanceId
                FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE (@InstanceId IS NULL OR InstanceId = @InstanceId)
                  AND Status IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
                  AND CompletionPublished = 0
                  AND (CompletionClaimUntil IS NULL OR CompletionClaimUntil <= SYSUTCDATETIME())
                ORDER BY InstanceId
            )
            UPDATE instance
            SET CompletionClaimToken = @Token,
                CompletionClaimUntil = DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME())
            OUTPUT inserted.StateJson
            FROM dbo.IxIFlowWorkflowInstances AS instance
            INNER JOIN pending ON pending.InstanceId = instance.InstanceId;
            """, new { InstanceId = instanceId, Token = token,
                Milliseconds = (int)Math.Ceiling(duration.TotalMilliseconds) });
        return rows.Select(Deserialize).ToArray();
    }

    public async Task<bool> MarkCompletionPublishedAsync(string instanceId, long revision, string token)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.IxIFlowWorkflowInstances
            SET CompletionPublished = 1,
                CompletionClaimToken = NULL, CompletionClaimUntil = NULL
            WHERE InstanceId = @InstanceId AND Revision = @Revision
              AND Status IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
              AND CompletionPublished = 0
              AND CompletionClaimToken = @Token
              AND CompletionClaimUntil > SYSUTCDATETIME()
            """, new { InstanceId = instanceId, Revision = revision, Token = token });
        return changed == 1;
    }

    public async Task ReleaseCompletionClaimAsync(string instanceId, string token)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync("""
            UPDATE dbo.IxIFlowWorkflowInstances
            SET CompletionClaimToken = NULL, CompletionClaimUntil = NULL
            WHERE InstanceId = @InstanceId AND CompletionClaimToken = @Token
            """, new { InstanceId = instanceId, Token = token });
    }

    public SqlWorkflowStateRepository(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A SQL Server connection string is required", nameof(connectionString))
            : connectionString;
    }

    public async Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.IxIFlowWorkflowInstances
            SET CancellationJson = COALESCE(CancellationJson, @ReasonJson),
                CancellationAcknowledged = CASE WHEN CancellationJson IS NULL
                    THEN 0 ELSE CancellationAcknowledged END
            WHERE InstanceId = @InstanceId AND Status IN ('Running', 'Suspended', 'NeedsResolution')
            """, new { InstanceId = instanceId, ReasonJson = JsonSerializer.Serialize(reason) });
        return changed == 1;
    }

    public async Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var json = await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT CancellationJson FROM dbo.IxIFlowWorkflowInstances
            WHERE InstanceId = @InstanceId
            """, new { InstanceId = instanceId });
        return json == null ? null : JsonSerializer.Deserialize<CancellationReason>(json);
    }

    public async Task<IEnumerable<WorkflowInstance>> GetWorkflowsRequiringRecoveryAsync()
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<string>("""
            SELECT instance.StateJson FROM dbo.IxIFlowWorkflowInstances AS instance
            LEFT JOIN dbo.IxIFlowWorkflowLeases AS lease
                ON lease.InstanceId = instance.InstanceId
            WHERE (instance.Status = 'Running' AND
                   (lease.InstanceId IS NULL OR lease.ExpiresAtUtc <= SYSUTCDATETIME()))
               OR (instance.Status = 'Suspended' AND
                   instance.CancellationJson IS NOT NULL AND
                   instance.CancellationAcknowledged = 0)
               OR (instance.Status IN ('Suspended', 'NeedsResolution') AND
                   instance.NextDueAtUtc <= SYSUTCDATETIME())
            """);
        return rows.Select(Deserialize).ToArray();
    }

    public async Task<bool> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var changed = await connection.ExecuteAsync("""
            MERGE dbo.IxIFlowWorkflowLeases WITH (UPDLOCK, HOLDLOCK) AS target
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
                SuspensionId = @SuspensionId, NextDueAtUtc = @NextDueAtUtc,
                StateJson = @StateJson
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, SuspensionExpiresAt,
                 SuspensionId, NextDueAtUtc, StateJson)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId,
                        @SuspensionExpiresAt, @SuspensionId, @NextDueAtUtc, @StateJson);
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
            instance.NextDueAtUtc,
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
        var current = await connection.QuerySingleOrDefaultAsync<CommitState>("""
            SELECT Revision, CancellationJson FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId
            """, new { instance.InstanceId }, transaction);
        var currentRevision = current?.Revision ?? 0;
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
        if (currentRevision != expectedRevision ||
            (current?.CancellationJson != null && snapshot.CancellationReason == null &&
             snapshot.Status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut))
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
                SuspensionId = @SuspensionId, NextDueAtUtc = @NextDueAtUtc,
                StateJson = @StateJson, Revision = @Revision,
                CancellationAcknowledged = CASE WHEN @AcknowledgesCancellation = 1 AND
                    target.CancellationJson IS NOT NULL THEN 1 ELSE target.CancellationAcknowledged END
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, SuspensionExpiresAt,
                 SuspensionId, NextDueAtUtc, StateJson, Revision)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId,
                        @SuspensionExpiresAt, @SuspensionId, @NextDueAtUtc, @StateJson, @Revision);
            INSERT INTO dbo.IxIFlowWorkflowCommits (InstanceId, CommitId, ExpectedRevision, Revision, PayloadHash)
            VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
            """, new
        {
            instance.InstanceId, instance.WorkflowName, instance.CorrelationId,
            Status = instance.Status.ToString(),
            SuspensionExpiresAt = instance.SuspensionInfo?.ExpiresAt,
            SuspensionId = instance.SuspensionInfo?.SuspensionId,
            snapshot.NextDueAtUtc,
            StateJson = json, snapshot.Revision,
            ExpectedRevision = expectedRevision, CommitId = commitId, PayloadHash = payloadHash,
            AcknowledgesCancellation = snapshot.CancellationReason != null
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

    private sealed class CommitState
    {
        public long Revision { get; set; }
        public string? CancellationJson { get; set; }
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
                        NextDueAtUtc DATETIME2 NULL,
                        SuspensionId NVARCHAR(100) NULL,
                        StateJson NVARCHAR(MAX) NOT NULL,
                        CancellationJson NVARCHAR(MAX) NULL,
                        CancellationAcknowledged BIT NOT NULL DEFAULT 0,
                        CompletionPublished BIT NOT NULL DEFAULT 0,
                        CompletionClaimToken NVARCHAR(100) NULL,
                        CompletionClaimUntil DATETIME2 NULL,
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
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'NextDueAtUtc') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances ADD NextDueAtUtc DATETIME2 NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                    WHERE name = N'IX_IxIFlowWorkflowInstances_Due'
                      AND object_id = OBJECT_ID(N'dbo.IxIFlowWorkflowInstances'))
                    CREATE INDEX IX_IxIFlowWorkflowInstances_Due
                        ON dbo.IxIFlowWorkflowInstances (Status, NextDueAtUtc);
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'Revision') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances ADD Revision BIGINT NOT NULL DEFAULT 0;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'CancellationJson') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances ADD CancellationJson NVARCHAR(MAX) NULL;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'CancellationAcknowledged') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances
                        ADD CancellationAcknowledged BIT NOT NULL DEFAULT 0;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'CompletionPublished') IS NULL
                BEGIN
                    ALTER TABLE dbo.IxIFlowWorkflowInstances
                        ADD CompletionPublished BIT NOT NULL DEFAULT 0;
                    EXEC(N'UPDATE dbo.IxIFlowWorkflowInstances SET CompletionPublished = 1
                        WHERE Status IN (''Completed'', ''Failed'', ''Cancelled'', ''Terminated'', ''TimedOut'')');
                END
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'CompletionClaimToken') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances
                        ADD CompletionClaimToken NVARCHAR(100) NULL;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'CompletionClaimUntil') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances
                        ADD CompletionClaimUntil DATETIME2 NULL;
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.IxIFlowWorkflowInstances')
                      AND name = N'IX_IxIFlowWorkflowInstances_PendingCompletion')
                    EXEC(N'CREATE INDEX IX_IxIFlowWorkflowInstances_PendingCompletion
                        ON dbo.IxIFlowWorkflowInstances (InstanceId)
                        INCLUDE (CompletionClaimUntil)
                        WHERE CompletionPublished = 0');
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
