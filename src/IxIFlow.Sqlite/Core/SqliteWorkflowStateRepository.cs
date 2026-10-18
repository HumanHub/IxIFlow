using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Relational;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Core;

/// <summary>
/// Stores workflows in a SQLite file for one host. SQLite has no server clock, so deadlines,
/// receipt times and runnable times use the injected TimeProvider, read after the write lock is held.
/// Construction takes exclusive file ownership; dispose the repository to clear its pool and release it.
/// Unix requires working flock support and enabled .NET file locking. Network paths are unsupported.
/// </summary>
public sealed class SqliteWorkflowStateRepository : RelationalWorkflowStateRepository, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SqliteHostOwnership _ownership;
    private volatile bool _disposed;

    public SqliteWorkflowStateRepository(string connectionString, TimeProvider? timeProvider = null)
    {
        _connectionString = FileConnectionString(connectionString);
        _time = timeProvider ?? TimeProvider.System;
        _ownership = new SqliteHostOwnership(_connectionString);
    }

    /// <summary>
    /// Writers queue through the repository gate. A one-second driver timeout bounds unexpected
    /// external contention, which single-host ownership (C-18) forbids; zero would wait forever.
    /// </summary>
    internal static string FileConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var source = new SqliteConnectionStringBuilder(connectionString);
        if (source.Mode == SqliteOpenMode.Memory || source.DataSource is "" or ":memory:" ||
            source.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SQLite requires a database file path for single-host ownership",
                nameof(connectionString));
        if (source.DataSource.StartsWith(@"\\", StringComparison.Ordinal) ||
            source.DataSource.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("SQLite requires a local database file; UNC paths are unsupported",
                nameof(connectionString));
        source.DataSource = Path.GetFullPath(source.DataSource);
        source.DefaultTimeout = 1;
        return source.ConnectionString;
    }

    // BEGIN IMMEDIATE already excludes other writers; reset the lease before taking the parent inbox.
    internal override DeletePlan Deletion => new(LeaseResetPoint.BeforeParentLock);
    internal override void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(_disposed, typeof(SqliteWorkflowStateRepository));
    internal override bool UsesLocalClock => true;
    internal override DateTime LocalNow() => _time.GetUtcNow().UtcDateTime;
    internal override bool ReadsUseStamps => false;
    internal override object? At(DateTime? value) => Timestamp(value);
    internal override DbType? TimeDbType => DbType.String;
    private static string Timestamp(DateTime value) => Utc(value).ToString("yyyy-MM-dd HH:mm:ss.fffffff",
        System.Globalization.CultureInfo.InvariantCulture);

    private static string? Timestamp(DateTime? value) => value is { } time ? Timestamp(time) : null;
    internal override Task<T> ExecuteOperationAsync<T>(Operation operation, Func<Task<T>> call) =>
        operation == Operation.CancellationRead ? call() : WriteAsync(call);

    internal override ValueTask<DbTransaction?> BeginTransactionAsync(DbConnection connection, Operation operation) =>
        ValueTask.FromResult<DbTransaction?>(((SqliteConnection)connection).BeginTransaction(
            deferred: operation is Operation.Read or Operation.Query or Operation.OutboxRead));

    internal override async Task<DbConnection> OpenConnectionAsync(bool ownership = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(SqliteWorkflowStateRepository));
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal override async Task<CancellationOutcome> RequestCancellationAsync(DbConnection connection,
        DbTransaction? transaction, string instanceId, string reasonJson, DateTime? now)
    {
        var row = await connection.QuerySingleOrDefaultAsync<CommitState>("""
            SELECT revision AS Revision, cancellation_json AS CancellationJson,
                cancellation_acknowledged AS CancellationAcknowledged
            FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId
            """, new { InstanceId = instanceId }, transaction);
        var changed = await connection.ExecuteAsync("""
            UPDATE ixiflow_workflow_instances
            SET cancellation_json = COALESCE(cancellation_json, @ReasonJson),
                cancellation_acknowledged = CASE WHEN cancellation_json IS NULL
                    THEN false ELSE cancellation_acknowledged END,
                runnable_at_utc = CASE WHEN cancellation_json IS NULL OR NOT cancellation_acknowledged
                    THEN @Now ELSE runnable_at_utc END
            WHERE instance_id = @InstanceId AND status IN ('Running', 'Suspended', 'NeedsResolution')
            """, new { InstanceId = instanceId, ReasonJson = reasonJson, Now = Timestamp(now) }, transaction);
        return row == null ? CancellationOutcome.Gone : changed == 0 ? CancellationOutcome.Unchanged
            : row.CancellationJson == null || !row.CancellationAcknowledged
                ? CancellationOutcome.MadeRunnable : CancellationOutcome.Recorded;
    }

    internal override async Task InsertWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<OpenedWait> waits, long inboxSequence, DateTime now)
    {
        await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_waits
                    (instance_id, wait_id, kind, wait_key, event_type, deadline_utc, opened_sequence, opened_at_utc)
                VALUES (@InstanceId, @WaitId, @Kind, @Key, @EventType, @Deadline, @Sequence, @Now)
                ON CONFLICT (instance_id, wait_id) DO NOTHING
                """, waits.Select(wait => new
        {
            InstanceId = instanceId,
            wait.WaitId,
            Kind = (short)wait.Kind,
            wait.Key,
            wait.EventType,
            Deadline = Timestamp(now + wait.Timeout),
            Sequence = inboxSequence,
            Now = Timestamp(now)
        }), transaction);
    }

    internal override async Task ArmWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ArmedWait> waits, DateTime now)
    {
        await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_waits SET deadline_utc = @Deadline
                WHERE instance_id = @InstanceId AND wait_id = @WaitId
                    AND close_reason IS NULL AND deadline_utc IS NULL
                """, waits.Select(wait => new
        {
            InstanceId = instanceId,
            wait.WaitId,
            Deadline = Timestamp(now + wait.Timeout)
        }), transaction);
    }

    internal override async Task CloseWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ClosedWait> waits, DateTime now)
    {
        await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_waits SET close_reason = @Reason, closed_at_utc = @Now
                WHERE instance_id = @InstanceId AND wait_id = @WaitId AND close_reason IS NULL
                """, waits.Select(wait => new
        {
            InstanceId = instanceId,
            wait.WaitId,
            Reason = (short)wait.Reason,
            Now = Timestamp(now)
        }), transaction);
    }

    internal override async Task InsertPartsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<CheckpointPart> parts)
    {
        await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_parts (instance_id, part_key, json)
                VALUES (@InstanceId, @Key, @Json)
                """, parts.Select(part => new { InstanceId = instanceId, part.Key, part.Json }), transaction);
    }

    internal override async Task AppendTraceAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<string> entries)
    {
        var sequence = await connection.ExecuteScalarAsync<long>("""
            SELECT COALESCE(max(sequence), 0) FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId
            """, new { InstanceId = instanceId }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO ixiflow_workflow_traces (instance_id, sequence, entry_json)
            VALUES (@InstanceId, @Sequence, @Entry)
            """, entries.Select((entry, index) => new
        {
            InstanceId = instanceId,
            Sequence = sequence + index + 1,
            Entry = entry
        }), transaction);
    }

    internal override async Task<int> SettleAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<EventSettlement> settlements, DateTime now)
    {
        var settled = 0;
        foreach (var settlement in settlements)
            settled += await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_events
                SET outcome = @Outcome, wait_id = @WaitId, rejection = @Rejection, settled_at_utc = @Now
                WHERE instance_id = @InstanceId AND event_id = @EventId AND outcome = 0
                """, new
            {
                InstanceId = instanceId,
                settlement.EventId,
                Outcome = (short)settlement.Outcome,
                settlement.WaitId,
                Rejection = (short?)settlement.Rejection,
                Now = Timestamp(now)
            }, transaction);

        return settled;
    }

    internal override async Task InsertChildrenAsync(DbConnection connection, DbTransaction transaction,
        string parentInstanceId, IReadOnlyList<ChildStart> children, DateTime now)
    {
        await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, workflow_name, status, correlation_id, runnable_at_utc, state_json, revision,
                     parent_instance_id)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @Now, @Payload, 1, @ParentInstanceId)
                """, children.Select(child => new
        {
            child.Instance!.InstanceId,
            child.Instance!.WorkflowName,
            Status = child.Instance!.Status.ToString(),
            child.Instance!.CorrelationId,
            child.Payload,
            Now = Timestamp(now),
            ParentInstanceId = parentInstanceId
        }), transaction);
    }

    internal override string ReadInstanceSql(string filter, bool wholly) => $"""
            SELECT {RowColumns} FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId;
            SELECT instance_id AS InstanceId, part_key AS PartKey, json AS Json FROM ixiflow_workflow_parts
            WHERE instance_id = @InstanceId {(wholly ? "" : "AND part_key = @DataKey")};
            """;

    internal override string QuerySql(string filter) => $"SELECT {RowColumns} FROM ixiflow_workflow_instances WHERE {filter}";

    internal override string? Sql(Statement statement) => statement switch
    {
        Statement.Unpublished => $"""
            SELECT {RowColumns} FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId {UnpublishedCompletion}
            """,
        Statement.UnpublishedBatch => $"""
            SELECT {RowColumns} FROM ixiflow_workflow_instances
            WHERE true {UnpublishedCompletion}
            ORDER BY instance_id
            LIMIT 100
            """,
        Statement.ClaimCompletions => $"""
                UPDATE ixiflow_workflow_instances
                SET completion_claim_token = @Token, completion_claim_until = @Until
                WHERE instance_id IN (
                    SELECT instance_id FROM ixiflow_workflow_instances
                    WHERE (@InstanceId IS NULL OR instance_id = @InstanceId) {UnpublishedCompletion}
                      AND (completion_claim_until IS NULL OR completion_claim_until <= @Now)
                    ORDER BY instance_id LIMIT 100)
                RETURNING {RowColumns}
                """,
        Statement.MarkCompletion => $"""
                UPDATE ixiflow_workflow_instances
                SET completion_published = true,
                    completion_claim_token = NULL, completion_claim_until = NULL
                WHERE instance_id = @InstanceId AND revision = @Revision
                  {UnpublishedCompletion}
                  AND completion_claim_token = @Token
                  AND completion_claim_until > @Now
                """,
        Statement.ReleaseCompletion => """
                UPDATE ixiflow_workflow_instances
                SET completion_claim_token = NULL, completion_claim_until = NULL
                WHERE instance_id = @InstanceId AND completion_claim_token = @Token
                """,
        Statement.Cancellation => """
            SELECT cancellation_json FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            """,
        Statement.Cancellations => """
            SELECT instance_id, cancellation_json FROM ixiflow_workflow_instances
            WHERE instance_id IN @InstanceIds AND cancellation_json IS NOT NULL
            """,
        Statement.Runnable => """
            WITH store AS (SELECT @Now AS now)
            SELECT instance.instance_id AS InstanceId, instance.runnable_at_utc AS RunnableAtUtc
            FROM store, ixiflow_workflow_instances AS instance
            LEFT JOIN ixiflow_workflow_leases AS lease
                ON lease.instance_id = instance.instance_id
            WHERE instance.runnable_at_utc <= store.now
              AND (lease.instance_id IS NULL OR lease.expires_at_utc <= store.now)
            ORDER BY instance.runnable_at_utc, instance.instance_id
            LIMIT @Limit
            """,
        Statement.Running => """
                SELECT COUNT(*) FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId AND status = 'Running'
                """,
        Statement.ClaimLease => """
                INSERT INTO ixiflow_workflow_leases (instance_id, token, expires_at_utc, epoch, crash_count)
                VALUES (@InstanceId, @Token, @Until, 1, 0)
                ON CONFLICT (instance_id) DO UPDATE
                SET token = excluded.token,
                    expires_at_utc = @Until,
                    epoch = ixiflow_workflow_leases.epoch + 1,
                    crash_count = ixiflow_workflow_leases.crash_count + CASE
                        WHEN @Running AND ixiflow_workflow_leases.expires_at_utc > '0001-01-01 00:00:00.0000000' THEN 1 ELSE 0 END
                WHERE ixiflow_workflow_leases.expires_at_utc <= @Now
                RETURNING epoch AS Epoch, crash_count AS CrashCount
                """,
        Statement.RenewLease => """
                UPDATE ixiflow_workflow_leases
                SET expires_at_utc = @Until
                WHERE instance_id = @InstanceId AND token = @Token
                    AND expires_at_utc > @Now
                """,
        Statement.ReleaseLease => """
                UPDATE ixiflow_workflow_leases SET expires_at_utc = '0001-01-01 00:00:00.0000000'
                WHERE instance_id = @InstanceId AND token = @Token AND expires_at_utc > @Now
                """,
        Statement.LockInbox or Statement.ParentInbox or Statement.DeleteParentInbox => """
            SELECT status AS Status, inbox_sequence AS InboxSequence FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            """,
        Statement.CommitState => """
            SELECT revision AS Revision, cancellation_json AS CancellationJson,
                cancellation_acknowledged AS CancellationAcknowledged, inbox_sequence AS InboxSequence
            FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId
            """,
        Statement.Receipt => """
            SELECT expected_revision AS ExpectedRevision, revision AS Revision, payload_hash AS PayloadHash
            FROM ixiflow_workflow_commits
            WHERE instance_id = @InstanceId AND commit_id = @CommitId
            """,
        Statement.Lease => """
            SELECT epoch AS Epoch, crash_count AS CrashCount, expires_at_utc > @Now AS IsActive
            FROM ixiflow_workflow_leases
            WHERE instance_id = @InstanceId
            """,
        Statement.TakenChild => """
                SELECT instance_id FROM ixiflow_workflow_instances WHERE instance_id IN @Ids LIMIT 1
                """,
        Statement.EventHash => """
            SELECT sequence AS Sequence, payload_hash AS PayloadHash FROM ixiflow_workflow_events
            WHERE instance_id = @InstanceId AND event_id = @EventId
            """,
        Statement.CandidateWait => $"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE instance_id = @InstanceId AND (@TargetWaitId IS NOT NULL AND wait_id = @TargetWaitId
                OR @TargetWaitId IS NULL AND close_reason IS NULL
                    AND kind = @Event AND (@Key IS NULL OR wait_key = @Key))
            ORDER BY CASE WHEN deadline_utc <= @Now THEN 1 ELSE 0 END
            LIMIT 1
            """,
        Statement.AdvanceInbox => """
                UPDATE ixiflow_workflow_instances
                SET inbox_sequence = @Sequence,
                    runnable_at_utc = CASE WHEN @MadeRunnable THEN @Now ELSE runnable_at_utc END
                WHERE instance_id = @InstanceId
                """,
        Statement.InsertEvent => """
            INSERT INTO ixiflow_workflow_events
                (instance_id, event_id, sequence, event_type, event_key, target_wait_id, subject, payload_json,
                 payload_hash, received_at_utc, outcome, rejection, settled_at_utc)
            VALUES (@InstanceId, @EventId, @Sequence, @EventType, @Key, @TargetWaitId, @Subject, @PayloadJson,
                @Hash, @Now, CASE WHEN @Rejection IS NULL THEN 0 ELSE 2 END, @Rejection,
                CASE WHEN @Rejection IS NULL THEN NULL ELSE @Now END)
            """,
        Statement.PendingEvents => $"""
                SELECT {EventColumns}
                FROM ixiflow_workflow_events
                WHERE instance_id = @InstanceId AND outcome = 0 AND sequence > @AfterSequence AND sequence <= @Through
                ORDER BY sequence
                LIMIT @Limit;
                SELECT wait_id AS WaitId, opened_sequence AS OpenedSequence, deadline_utc AS DeadlineUtc
                FROM ixiflow_workflow_waits
                WHERE instance_id = @InstanceId AND close_reason IS NULL;
                """,
        Statement.Event => $"""
            SELECT {EventColumns}
            FROM ixiflow_workflow_events WHERE instance_id = @InstanceId AND event_id = @EventId
            """,
        Statement.InboxSequences => """
                     SELECT instance_id, inbox_sequence FROM ixiflow_workflow_instances
                     WHERE instance_id IN @InstanceIds
                     """,
        Statement.Wait => $"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE instance_id = @InstanceId AND wait_id = @WaitId
            """,
        Statement.OpenWaits => $"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE close_reason IS NULL
              AND (@InstanceId IS NULL OR instance_id = @InstanceId)
              AND (@Key IS NULL OR wait_key = @Key)
              AND (@EventType IS NULL OR event_type = @EventType)
              AND (@Kind IS NULL AND kind NOT IN (@Delay, @Execution) OR kind = @Kind)
              AND (@AfterWaitId IS NULL OR wait_id > @AfterWaitId)
            ORDER BY wait_id
            LIMIT @Limit
            """,
        Statement.EarliestDeadline => """
                SELECT min(deadline_utc) FROM ixiflow_workflow_waits
                WHERE instance_id = @InstanceId AND close_reason IS NULL
                """,
        Statement.InsertCommit => """
              INSERT INTO ixiflow_workflow_instances
                  (instance_id, workflow_name, status, correlation_id, runnable_at_utc, state_json, revision)
              VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @RunnableAtUtc, @StateJson, @Revision);
              INSERT INTO ixiflow_workflow_commits (instance_id, commit_id, expected_revision, revision, payload_hash)
              VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
              """,
        Statement.UpdateCommit => """
              UPDATE ixiflow_workflow_instances SET
                  workflow_name = @WorkflowName, status = @Status,
                  correlation_id = @CorrelationId,
                  runnable_at_utc = @RunnableAtUtc,
                  inbox_read_through = COALESCE(@InboxReadThrough, inbox_read_through),
                  state_json = @StateJson, revision = @Revision,
                  cancellation_acknowledged = CASE WHEN @AcknowledgesCancellation AND
                      cancellation_json IS NOT NULL THEN true ELSE cancellation_acknowledged END
              WHERE instance_id = @InstanceId;
              INSERT INTO ixiflow_workflow_commits (instance_id, commit_id, expected_revision, revision, payload_hash)
              VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
              """,
        Statement.FinishInbox => """
                UPDATE ixiflow_workflow_events SET outcome = 4, settled_at_utc = @Now
                WHERE instance_id = @InstanceId AND outcome = 0;
                UPDATE ixiflow_workflow_waits SET close_reason = @Reason, closed_at_utc = @Now
                WHERE instance_id = @InstanceId AND close_reason IS NULL;
                """,
        Statement.UpdateLease => """
                UPDATE ixiflow_workflow_leases
                SET expires_at_utc = CASE WHEN @Released THEN '0001-01-01 00:00:00.0000000' ELSE expires_at_utc END,
                    crash_count = CASE WHEN @ClearsCrashCount THEN 0 ELSE crash_count END
                WHERE instance_id = @InstanceId
                """,
        Statement.SaveParts => """
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId
                    AND EXISTS (SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId AND revision = 0)
                """,
        Statement.Save => """
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, workflow_name, status, correlation_id, runnable_at_utc, state_json)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId,
                    CASE WHEN @RunnableNow THEN @Now ELSE @RunnableAtUtc END, @StateJson)
                ON CONFLICT (instance_id) DO UPDATE
                SET workflow_name = excluded.workflow_name, status = excluded.status,
                    correlation_id = excluded.correlation_id, runnable_at_utc = excluded.runnable_at_utc,
                    state_json = excluded.state_json
                WHERE ixiflow_workflow_instances.revision = 0
                RETURNING COALESCE(runnable_at_utc <= @Now, false)
                """,
        Statement.ReadParts => """
            SELECT instance_id AS InstanceId, part_key AS PartKey, json AS Json FROM ixiflow_workflow_parts
            WHERE instance_id IN @Ids
            """,
        Statement.DeleteState => """
                SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId
                """,
        Statement.DeleteLease => """
                UPDATE ixiflow_workflow_leases SET expires_at_utc = '0001-01-01 00:00:00.0000000', crash_count = 0
                WHERE instance_id = @InstanceId
                """,
        Statement.DeleteRows => """
                DELETE FROM ixiflow_workflow_waits WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_events WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_commits WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId;
                """,
        Statement.Trace => """
            SELECT sequence, entry_json FROM ixiflow_workflow_traces
            WHERE instance_id = @InstanceId AND sequence > @AfterSequence
            ORDER BY sequence
            LIMIT @Limit
            """,
        Statement.ByName => "workflow_name = @Value",
        Statement.ByStatus => "status = @Value",
        Statement.ByCorrelation => "correlation_id = @Value",
        Statement.ByParent => "parent_instance_id = @Value",
        Statement.UnpublishedFilter => UnpublishedCompletion,
        Statement.ClaimedFilter => UnpublishedCompletion + " AND completion_claim_token = @Token",
        Statement.DeleteAllParts => "DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId",
        Statement.DeleteParts => """
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId AND part_key IN @Keys
                """,
        _ => null
    };

    internal override async Task CreateSchemaAsync(DbConnection connection)
    {
    await connection.ExecuteAsync("PRAGMA journal_mode=WAL");
    await using var transaction = ((SqliteConnection)connection).BeginTransaction(deferred: false);

    await connection.ExecuteAsync("""
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_instances (
            instance_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
            workflow_name TEXT COLLATE BINARY NOT NULL,
            status TEXT COLLATE BINARY NOT NULL,
            correlation_id TEXT COLLATE BINARY NULL,
            runnable_at_utc TEXT NULL,
            state_json TEXT NOT NULL,
            cancellation_json TEXT NULL,
            cancellation_acknowledged INTEGER NOT NULL DEFAULT false,
            completion_published INTEGER NOT NULL DEFAULT false,
            completion_claim_token TEXT COLLATE BINARY NULL,
            completion_claim_until TEXT NULL,
            revision INTEGER NOT NULL DEFAULT 0,
            inbox_sequence INTEGER NOT NULL DEFAULT 0,
            inbox_read_through INTEGER NOT NULL DEFAULT 0,
            parent_instance_id TEXT COLLATE BINARY NULL
        );
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_instances_status
            ON ixiflow_workflow_instances (status);
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_instances_workflow_name
            ON ixiflow_workflow_instances (workflow_name);
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_instances_correlation_id
            ON ixiflow_workflow_instances (correlation_id);
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_instances_parent
            ON ixiflow_workflow_instances (parent_instance_id) WHERE parent_instance_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_instances_runnable
            ON ixiflow_workflow_instances (runnable_at_utc, instance_id) WHERE runnable_at_utc IS NOT NULL;
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_instances_pending_completion
            ON ixiflow_workflow_instances (instance_id)
            WHERE NOT completion_published;
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_events (
            instance_id TEXT COLLATE BINARY NOT NULL,
            event_id TEXT COLLATE BINARY NOT NULL,
            sequence INTEGER NOT NULL,
            event_type TEXT COLLATE BINARY NOT NULL,
            event_key TEXT COLLATE BINARY NULL,
            target_wait_id TEXT COLLATE BINARY NULL,
            subject TEXT COLLATE BINARY NULL,
            payload_json TEXT NOT NULL,
            payload_hash BLOB NOT NULL,
            received_at_utc TEXT NOT NULL,
            outcome INTEGER NOT NULL,
            wait_id TEXT COLLATE BINARY NULL,
            rejection INTEGER NULL,
            settled_at_utc TEXT NULL,
            PRIMARY KEY (instance_id, event_id)
        );
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_events_pending
            ON ixiflow_workflow_events (instance_id, sequence) WHERE outcome = 0;
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_parts (
            instance_id TEXT COLLATE BINARY NOT NULL,
            part_key TEXT COLLATE BINARY NOT NULL,
            json TEXT NOT NULL,
            PRIMARY KEY (instance_id, part_key)
        );
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_traces (
            instance_id TEXT COLLATE BINARY NOT NULL,
            sequence INTEGER NOT NULL,
            entry_json TEXT NOT NULL,
            PRIMARY KEY (instance_id, sequence)
        );
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_waits (
            instance_id TEXT COLLATE BINARY NOT NULL,
            wait_id TEXT COLLATE BINARY NOT NULL,
            kind INTEGER NOT NULL,
            wait_key TEXT COLLATE BINARY NOT NULL,
            event_type TEXT COLLATE BINARY NOT NULL,
            deadline_utc TEXT NULL,
            opened_sequence INTEGER NOT NULL,
            opened_at_utc TEXT NOT NULL,
            close_reason INTEGER NULL,
            closed_at_utc TEXT NULL,
            PRIMARY KEY (instance_id, wait_id)
        );
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_waits_open
            ON ixiflow_workflow_waits (wait_key, event_type, wait_id) WHERE close_reason IS NULL;
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_commits (
            instance_id TEXT COLLATE BINARY NOT NULL,
            commit_id TEXT COLLATE BINARY NOT NULL,
            expected_revision INTEGER NOT NULL,
            revision INTEGER NOT NULL,
            payload_hash BLOB NOT NULL,
            PRIMARY KEY (instance_id, commit_id)
        );
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_leases (
            instance_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
            token TEXT COLLATE BINARY NOT NULL,
            expires_at_utc TEXT NOT NULL,
            epoch INTEGER NOT NULL,
            crash_count INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_leases_expires_at
            ON ixiflow_workflow_leases (expires_at_utc);
        """, transaction: transaction);
    await transaction.CommitAsync();
    }

    private async Task<T> WriteAsync<T>(Func<Task<T>> write)
    {
        await _writer.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(SqliteWorkflowStateRepository));
            return await write();
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
            throw new InvalidOperationException(
                "SQLite is busy despite single-host ownership. External writers are unsupported (C-18).", error);
        }
        finally
        {
            _writer.Release();
        }
    }

    private Task WriteAsync(Func<Task> write) => WriteAsync(async () =>
    {
        await write();
        return true;
    });

    public async ValueTask DisposeAsync()
    {
        await _writer.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            using var connection = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(connection);
            _ownership.Dispose();
        }
        finally
        {
            _writer.Release();
        }
    }
}
