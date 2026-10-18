using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Core;

/// <summary>
/// Stores workflows in a SQLite file for one host. SQLite has no server clock, so deadlines,
/// receipt times and runnable times use the injected TimeProvider, read after the write lock is held.
/// Construction takes exclusive file ownership; dispose the repository to clear its pool and release it.
/// Unix requires working flock support and enabled .NET file locking. Network paths are unsupported.
/// </summary>
public sealed class SqliteWorkflowStateRepository :
    IWorkflowStateRepository, IWorkflowCompletionOutbox, IWorkflowInbox, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SqliteHostOwnership _ownership;
    private bool _schemaReady;
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

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        var row = await connection.QuerySingleOrDefaultAsync<StoredRow>($"""
            SELECT {RowColumns} FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId {UnpublishedCompletion}
            """, new { InstanceId = instanceId }, transaction);
        return row == null ? null
            : (await AssembleAsync(connection, [row], transaction)).SingleOrDefault();
    }

    public async Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync()
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        var rows = await connection.QueryAsync<StoredRow>($"""
            SELECT {RowColumns} FROM ixiflow_workflow_instances
            WHERE true {UnpublishedCompletion}
            ORDER BY instance_id
            LIMIT 100
            """, transaction: transaction);
        return await AssembleAsync(connection, rows.ToArray(), transaction);
    }

    public async Task<IEnumerable<WorkflowInstance>> ClaimUnpublishedCompletionsAsync(
        string? instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        await EnsureSchemaAsync();
        return await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var now = Now;
            var rows = await connection.QueryAsync<StoredRow>($"""
                UPDATE ixiflow_workflow_instances
                SET completion_claim_token = @Token, completion_claim_until = @Until
                WHERE instance_id IN (
                    SELECT instance_id FROM ixiflow_workflow_instances
                    WHERE (@InstanceId IS NULL OR instance_id = @InstanceId) {UnpublishedCompletion}
                      AND (completion_claim_until IS NULL OR completion_claim_until <= @Now)
                    ORDER BY instance_id LIMIT 100)
                RETURNING {RowColumns}
                """, new { InstanceId = instanceId, Token = token, Now = At(now), Until = At(now + duration) }, transaction);
            var instances = await AssembleAsync(connection, rows.ToArray(), transaction);
            await transaction.CommitAsync();
            return instances;
        });
    }

    public async Task<bool> MarkCompletionPublishedAsync(string instanceId, long revision, string token)
    {
        await EnsureSchemaAsync();
        return await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var now = Now;
            var changed = await connection.ExecuteAsync($"""
                UPDATE ixiflow_workflow_instances
                SET completion_published = true,
                    completion_claim_token = NULL, completion_claim_until = NULL
                WHERE instance_id = @InstanceId AND revision = @Revision
                  {UnpublishedCompletion}
                  AND completion_claim_token = @Token
                  AND completion_claim_until > @Now
                """, new { InstanceId = instanceId, Revision = revision, Token = token, Now = At(now) }, transaction);
            await transaction.CommitAsync();
            return changed == 1;
        });
    }

    public async Task ReleaseCompletionClaimAsync(string instanceId, string token)
    {
        await EnsureSchemaAsync();
        await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_instances
                SET completion_claim_token = NULL, completion_claim_until = NULL
                WHERE instance_id = @InstanceId AND completion_claim_token = @Token
                """, new { InstanceId = instanceId, Token = token }, transaction);
            await transaction.CommitAsync();
        });
    }

    public async Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        await EnsureSchemaAsync();
        return await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var outcome = await RequestCancellationAsync(connection, transaction, instanceId,
                JsonSerializer.Serialize(reason), At(Now));
            await transaction.CommitAsync();
            return outcome != CancellationOutcome.Gone && outcome != CancellationOutcome.Unchanged;
        });
    }

    private enum CancellationOutcome
    {
        Unchanged,
        Recorded,
        MadeRunnable,
        Gone
    }

    private static async Task<CancellationOutcome> RequestCancellationAsync(SqliteConnection connection,
        SqliteTransaction transaction, string instanceId, string reasonJson, string now)
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
            """, new { InstanceId = instanceId, ReasonJson = reasonJson, Now = now }, transaction);
        return row == null ? CancellationOutcome.Gone : changed == 0 ? CancellationOutcome.Unchanged
            : row.CancellationJson == null || !row.CancellationAcknowledged
                ? CancellationOutcome.MadeRunnable : CancellationOutcome.Recorded;
    }

    public async Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        await EnsureSchemaAsync();

        await using var connection = await OpenConnectionAsync();
        var json = await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT cancellation_json FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            """, new { InstanceId = instanceId });
        return json == null ? null : JsonSerializer.Deserialize<CancellationReason>(json);
    }

    public async Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        var requests = new Dictionary<string, CancellationReason>(StringComparer.Ordinal);
        if (instanceIds.Count == 0)
            return requests;
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<(string InstanceId, string CancellationJson)>("""
            SELECT instance_id, cancellation_json FROM ixiflow_workflow_instances
            WHERE instance_id IN @InstanceIds AND cancellation_json IS NOT NULL
            """, new { InstanceIds = instanceIds.Distinct(StringComparer.Ordinal).ToArray() });
        foreach (var row in rows)
            if (ReadReason(row.CancellationJson) is { } reason)
                requests[row.InstanceId] = reason;
        return requests;
    }

    public async Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<RunnableRow>("""
            WITH store AS (SELECT @Now AS now)
            SELECT instance.instance_id AS InstanceId, instance.runnable_at_utc AS RunnableAtUtc
            FROM store, ixiflow_workflow_instances AS instance
            LEFT JOIN ixiflow_workflow_leases AS lease
                ON lease.instance_id = instance.instance_id
            WHERE instance.runnable_at_utc <= store.now
              AND (lease.instance_id IS NULL OR lease.expires_at_utc <= store.now)
            ORDER BY instance.runnable_at_utc, instance.instance_id
            LIMIT @Limit
            """, new { Limit = limit, Now = At(Now) });
        return rows.Select(row => new RunnableInstance(row.InstanceId, row.RunnableAtUtc)).ToArray();
    }

    public async Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync();
        return await ClaimAsync(instanceId, token, milliseconds);
    }

    private async Task<ExecutionLeaseGrant?> ClaimAsync(string instanceId, string token, int milliseconds)
    {
        return await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var now = Now;
            var running = await connection.QuerySingleOrDefaultAsync<string>("""
                SELECT status FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId
                """, new { InstanceId = instanceId }, transaction) == "Running";
            var grant = await connection.QuerySingleOrDefaultAsync<LeaseGrantRow>("""
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
                """, new { InstanceId = instanceId, Token = token, Now = At(now), Until = At(now.AddMilliseconds(milliseconds)), Running = running },
                transaction);
            await transaction.CommitAsync();
            return grant == null ? null : new ExecutionLeaseGrant(grant.Epoch, grant.CrashCount);
        });
    }

    private sealed class LeaseGrantRow
    {
        public long Epoch { get; set; }
        public int CrashCount { get; set; }
    }

    public async Task<bool> RenewExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync();
        return await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var now = Now;
            var changed = await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_leases
                SET expires_at_utc = @Until
                WHERE instance_id = @InstanceId AND token = @Token
                    AND expires_at_utc > @Now
                """, new { InstanceId = instanceId, Token = token, Now = At(now), Until = At(now.AddMilliseconds(milliseconds)) }, transaction);
            await transaction.CommitAsync();
            return changed == 1;
        });
    }

    public async Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        workflowEvent.Validate();
        var hash = workflowEvent.ContentHash();
        await EnsureSchemaAsync();
        return await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var instance = await ReadInboxAsync(connection, transaction, workflowEvent.InstanceId);
            var lockedAt = Now;
            var result = await AppendAsync(connection, transaction, workflowEvent, hash, instance, lockedAt);
            await transaction.CommitAsync();
            return result;
        });
    }

    private static Task<InboxRow?> ReadInboxAsync(SqliteConnection connection,
        SqliteTransaction transaction, string instanceId) =>
        connection.QuerySingleOrDefaultAsync<InboxRow>("""
            SELECT status AS Status, inbox_sequence AS InboxSequence FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            """, new { InstanceId = instanceId }, transaction);

    private static async Task<EventAppendResult> AppendAsync(SqliteConnection connection,
        SqliteTransaction transaction, WorkflowEvent workflowEvent, byte[] hash, InboxRow? instance, DateTime now)
    {
        var existing = await connection.QuerySingleOrDefaultAsync<EventHashRow>("""
            SELECT sequence AS Sequence, payload_hash AS PayloadHash FROM ixiflow_workflow_events
            WHERE instance_id = @InstanceId AND event_id = @EventId
            """, new { workflowEvent.InstanceId, workflowEvent.EventId }, transaction);
        if (existing != null)
            return existing.PayloadHash.SequenceEqual(hash)
                ? new EventAppendResult(EventAppendStatus.Duplicate, existing.Sequence)
                : new EventAppendResult(EventAppendStatus.Conflict);
        if (instance == null)
            return new EventAppendResult(EventAppendStatus.NotFound);
        var finished = instance.Status is "Completed" or "Failed" or "Cancelled" or "Terminated" or "TimedOut";
        if (finished && workflowEvent.TargetWaitId == null)
            return new EventAppendResult(EventAppendStatus.Closed);
        var waits = (await connection.QueryAsync<WaitRow>($"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE instance_id = @InstanceId AND (@TargetWaitId IS NOT NULL AND wait_id = @TargetWaitId
                OR @TargetWaitId IS NULL AND close_reason IS NULL
                    AND kind = @Event AND (@Key IS NULL OR wait_key = @Key))
            ORDER BY CASE WHEN deadline_utc <= @Now THEN 1 ELSE 0 END
            LIMIT 1
            """, new
        {
            workflowEvent.InstanceId, workflowEvent.TargetWaitId, workflowEvent.Key, Event = (short)WaitKind.Event,
            Now = At(now)
        }, transaction)).Select(row => row.Read()).ToArray();
        var rejection = WorkflowStoreRules.RejectOnArrival(workflowEvent, waits.SingleOrDefault(), waits, now)
            ?? (finished ? EventRejection.InstanceFinished : null);
        var sequence = rejection == null ? instance.InboxSequence + 1 : 0;
        var madeRunnable = rejection == null && instance.Status is "Running" or "Suspended";
        if (rejection == null)
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_instances
                SET inbox_sequence = @Sequence,
                    runnable_at_utc = CASE WHEN @MadeRunnable THEN @Now ELSE runnable_at_utc END
                WHERE instance_id = @InstanceId
                """, new { workflowEvent.InstanceId, Sequence = sequence, MadeRunnable = madeRunnable, Now = At(now) },
                transaction);
        await connection.ExecuteAsync("""
            INSERT INTO ixiflow_workflow_events
                (instance_id, event_id, sequence, event_type, event_key, target_wait_id, subject, payload_json,
                 payload_hash, received_at_utc, outcome, rejection, settled_at_utc)
            VALUES (@InstanceId, @EventId, @Sequence, @EventType, @Key, @TargetWaitId, @Subject, @PayloadJson,
                @Hash, @Now, CASE WHEN @Rejection IS NULL THEN 0 ELSE 2 END, @Rejection,
                CASE WHEN @Rejection IS NULL THEN NULL ELSE @Now END)
            """, new
        {
            workflowEvent.InstanceId, workflowEvent.EventId, Sequence = sequence,
            workflowEvent.EventType, workflowEvent.Key, workflowEvent.TargetWaitId, workflowEvent.Subject,
            workflowEvent.PayloadJson, Hash = hash, Rejection = (short?)rejection, Now = At(now)
        }, transaction);
        return rejection == null
            ? new EventAppendResult(EventAppendStatus.Stored, sequence, madeRunnable)
            : new EventAppendResult(EventAppendStatus.Rejected, Rejection: rejection);
    }

    public async Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        await EnsureSchemaAsync();
        return await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var instance = await ReadInboxAsync(connection, transaction, instanceId);
            var now = Now;
            var through = instance?.InboxSequence ?? 0;
            StoredWorkflowEvent[] events;
            PageWait[] waits;
            using (var results = await connection.QueryMultipleAsync($"""
                SELECT {EventColumns}
                FROM ixiflow_workflow_events
                WHERE instance_id = @InstanceId AND outcome = 0 AND sequence > @AfterSequence AND sequence <= @Through
                ORDER BY sequence
                LIMIT @Limit;
                SELECT wait_id AS WaitId, opened_sequence AS OpenedSequence, deadline_utc AS DeadlineUtc
                FROM ixiflow_workflow_waits
                WHERE instance_id = @InstanceId AND close_reason IS NULL;
                """, new { InstanceId = instanceId, AfterSequence = afterSequence, Through = through, Limit = limit },
                transaction))
            {
                events = (await results.ReadAsync<EventRow>()).Select(row => row.Read()).ToArray();
                waits = (await results.ReadAsync<PageWait>()).ToArray();
            }
            await transaction.CommitAsync();
            return InboxPage.Of(events, limit, afterSequence, through,
                waits.ToDictionary(wait => wait.WaitId, wait => wait.OpenedSequence, StringComparer.Ordinal),
                waits.Where(wait => wait.DeadlineUtc != null).ToDictionary(wait => wait.WaitId,
                    wait => Utc(wait.DeadlineUtc!.Value), StringComparer.Ordinal),
                now);
        });
    }

    private sealed class PageWait
    {
        public string WaitId { get; set; } = "";
        public long OpenedSequence { get; set; }
        public DateTime? DeadlineUtc { get; set; }
    }

    public async Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<EventRow>($"""
            SELECT {EventColumns}
            FROM ixiflow_workflow_events WHERE instance_id = @InstanceId AND event_id = @EventId
            """, new { InstanceId = instanceId, EventId = eventId });
        return row?.Read();
    }

    public async Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
        IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);
        if (instanceIds.Count == 0)
            return sequences;
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        foreach (var row in await connection.QueryAsync<(string InstanceId, long InboxSequence)>("""
                     SELECT instance_id, inbox_sequence FROM ixiflow_workflow_instances
                     WHERE instance_id IN @InstanceIds
                     """, new { InstanceIds = instanceIds.Distinct(StringComparer.Ordinal).ToArray() }))
            sequences[row.InstanceId] = row.InboxSequence;
        return sequences;
    }

    public async Task<StoredWait?> GetWaitAsync(string instanceId, string waitId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<WaitRow>($"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE instance_id = @InstanceId AND wait_id = @WaitId
            """, new { InstanceId = instanceId, WaitId = waitId });
        return row?.Read();
    }

    public async Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<WaitRow>($"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE close_reason IS NULL
              AND (@InstanceId IS NULL OR instance_id = @InstanceId)
              AND (@Key IS NULL OR wait_key = @Key)
              AND (@EventType IS NULL OR event_type = @EventType)
              AND (@Kind IS NULL AND kind NOT IN (@Delay, @Execution) OR kind = @Kind)
              AND (@AfterWaitId IS NULL OR wait_id > @AfterWaitId)
            ORDER BY wait_id
            LIMIT @Limit
            """, new
        {
            query.Limit, query.InstanceId, query.Key, query.EventType, Kind = (short?)query.Kind, query.AfterWaitId,
            Delay = (short)WaitKind.Delay, Execution = (short)WaitKind.Execution
        });
        return rows.Select(row => row.Read()).ToArray();
    }

    private const string EventColumns =
        "instance_id AS InstanceId, event_id AS EventId, sequence AS Sequence, event_type AS EventType, " +
        "event_key AS EventKey, target_wait_id AS TargetWaitId, payload_json AS PayloadJson, " +
        "received_at_utc AS ReceivedAtUtc, outcome AS Outcome, wait_id AS WaitId, rejection AS Rejection, " +
        "subject AS Subject";

    private const string WaitColumns =
        "instance_id AS InstanceId, wait_id AS WaitId, kind AS Kind, wait_key AS WaitKey, event_type AS EventType, " +
        "deadline_utc AS DeadlineUtc, opened_sequence AS OpenedSequence, opened_at_utc AS OpenedAtUtc, " +
        "close_reason AS CloseReason, closed_at_utc AS ClosedAtUtc";

    private static WorkflowEvent ToEvent(ParentNotice notice) =>
        new(notice.ParentInstanceId, notice.EventId, notice.EventType, notice.PayloadJson,
            TargetWaitId: notice.ParentWaitId);

    private sealed class InboxRow
    {
        public string Status { get; set; } = "";
        public long InboxSequence { get; set; }
    }

    private sealed class EventHashRow
    {
        public long Sequence { get; set; }
        public byte[] PayloadHash { get; set; } = [];
    }

    private sealed class EventRow
    {
        public string InstanceId { get; set; } = "";
        public string EventId { get; set; } = "";
        public long Sequence { get; set; }
        public string EventType { get; set; } = "";
        public string? EventKey { get; set; }
        public string? TargetWaitId { get; set; }
        public string PayloadJson { get; set; } = "";
        public DateTime ReceivedAtUtc { get; set; }
        public short Outcome { get; set; }
        public string? WaitId { get; set; }
        public short? Rejection { get; set; }
        public string? Subject { get; set; }

        public StoredWorkflowEvent Read() => new(InstanceId, EventId, Sequence, EventType, EventKey,
            TargetWaitId, PayloadJson, Utc(ReceivedAtUtc), (EventOutcome)Outcome, WaitId,
            (EventRejection?)Rejection, Subject);
    }

    private sealed class WaitRow
    {
        public string InstanceId { get; set; } = "";
        public string WaitId { get; set; } = "";
        public short Kind { get; set; }
        public string WaitKey { get; set; } = "";
        public string EventType { get; set; } = "";
        public DateTime? DeadlineUtc { get; set; }
        public long OpenedSequence { get; set; }
        public DateTime OpenedAtUtc { get; set; }
        public short? CloseReason { get; set; }
        public DateTime? ClosedAtUtc { get; set; }

        public StoredWait Read() => new(InstanceId, WaitId, (WaitKind)Kind, WaitKey, EventType,
            DeadlineUtc is { } deadline ? Utc(deadline) : null, OpenedSequence, Utc(OpenedAtUtc),
            (WaitCloseReason?)CloseReason, ClosedAtUtc is { } closed ? Utc(closed) : null);
    }

    private sealed class RunnableRow
    {
        public string InstanceId { get; set; } = "";
        public DateTime RunnableAtUtc { get; set; }
    }

    public async Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        await EnsureSchemaAsync();
        return await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var changed = await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_leases SET expires_at_utc = '0001-01-01 00:00:00.0000000'
                WHERE instance_id = @InstanceId AND token = @Token AND expires_at_utc > @Now
                """, new { InstanceId = instanceId, Token = token, Now = At(Now) }, transaction);
            await transaction.CommitAsync();
            return changed == 1;
        });
    }

    public async Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Revision != 0)
            throw new InvalidOperationException("Revisioned instances require an atomic commit");
        await EnsureSchemaAsync();
        await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var runnableNow = WorkflowStoreRules.IsRunnableNow(instance, false);
            await connection.ExecuteAsync("""
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId
                    AND EXISTS (SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId AND revision = 0)
                """, new { instance.InstanceId }, transaction);
            var due = await connection.QuerySingleOrDefaultAsync<bool?>("""
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
                """, new
            {
                instance.InstanceId,
                instance.WorkflowName,
                Status = instance.Status.ToString(),
                instance.CorrelationId,
                RunnableAtUtc = At(instance.RunnableAtUtc),
                RunnableNow = runnableNow, Now = At(Now),
                StateJson = JsonSerializer.Serialize(instance)
            }, transaction);
            if (due == null)
                throw new InvalidOperationException("Revisioned instances require an atomic commit");
            await transaction.CommitAsync();
        });
    }

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsureSchemaAsync();
        return await WriteAsync(() => CommitAsync(commit));
    }

    private async Task<WorkflowCommitResult> CommitAsync(CheckpointCommit commit)
    {
        var snapshot = commit.Instance;
        var revision = checked(commit.ExpectedRevision + 1);
        await using var connection = await OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        var now = Now;
        var parent = commit.ParentNotice is { } notice
            ? await ReadInboxAsync(connection, transaction, notice.ParentInstanceId) : null;
        var current = await connection.QuerySingleOrDefaultAsync<CommitState>("""
            SELECT revision AS Revision, cancellation_json AS CancellationJson,
                cancellation_acknowledged AS CancellationAcknowledged, inbox_sequence AS InboxSequence
            FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId
            """, new { commit.InstanceId }, transaction);

        var currentRevision = current?.Revision ?? 0;
        var receipt = await connection.QuerySingleOrDefaultAsync<CommitReceipt>("""
            SELECT expected_revision AS ExpectedRevision, revision AS Revision, payload_hash AS PayloadHash
            FROM ixiflow_workflow_commits
            WHERE instance_id = @InstanceId AND commit_id = @CommitId
            """, new { commit.InstanceId, commit.CommitId }, transaction);
        var lease = await connection.QuerySingleOrDefaultAsync<LeaseRecord>("""
            SELECT epoch AS Epoch, crash_count AS CrashCount, expires_at_utc > @Now AS IsActive
            FROM ixiflow_workflow_leases
            WHERE instance_id = @InstanceId
            """, new { commit.InstanceId, Now = At(now) }, transaction);
        var owns = commit.Epoch == 0
            ? lease?.IsActive != true
            : lease is { IsActive: true } && lease.Epoch == commit.Epoch;
        if (receipt != null)
        {
            if (receipt.ExpectedRevision != commit.ExpectedRevision ||
                !receipt.PayloadHash.SequenceEqual(commit.ContentHash))
                throw new InvalidOperationException("A commit ID cannot be reused for a different transition");
            await transaction.CommitAsync();
            return snapshot.Status == WorkflowStatus.Running && !owns && !commit.Parks
                ? new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, currentRevision)
                : new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, receipt.Revision,
                    ReadReason(current?.CancellationJson), commit.Parks && !owns,
                    commit.InboxReadThrough is { } replayRead && current?.InboxSequence > replayRead);
        }
        if (!owns)
        {
            await transaction.CommitAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, currentRevision);
        }
        if (currentRevision != commit.ExpectedRevision ||
            (current?.CancellationJson != null && !snapshot.AcknowledgesCancellation &&
             snapshot.Status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut))
        {
            await transaction.CommitAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.Conflict, currentRevision);
        }
        if (commit.ChildStarts.Count > 0 && await connection.ExecuteScalarAsync<string?>("""
                SELECT instance_id FROM ixiflow_workflow_instances WHERE instance_id IN @Ids LIMIT 1
                """, new { Ids = commit.ChildStarts.Select(child => child.ChildInstanceId).ToArray() }, transaction)
            is { } taken)
            throw new InvalidOperationException(
                $"Child workflow '{taken}' cannot start: an instance with its ID exists");

        var settled = 0;
        foreach (var settlement in commit.Settlements)
            settled += await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_events
                SET outcome = @Outcome, wait_id = @WaitId, rejection = @Rejection, settled_at_utc = @Now
                WHERE instance_id = @InstanceId AND event_id = @EventId AND outcome = 0
                """, new { commit.InstanceId, settlement.EventId, Outcome = (short)settlement.Outcome,
                    settlement.WaitId, Rejection = (short?)settlement.Rejection, Now = At(now) }, transaction);

        if (settled != commit.Settlements.Count)
        {
            await transaction.RollbackAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.EventsChanged, currentRevision);
        }
        var unacknowledged = current is { CancellationJson: not null, CancellationAcknowledged: false };
        var unread = commit.InboxReadThrough is { } readThrough && current?.InboxSequence > readThrough;
        var parked = commit.Parks && !unacknowledged && !unread;
        await ApplyWaitChangesAsync(connection, transaction, commit, current?.InboxSequence ?? 0, now);
        var runnableNow = (commit.Parks && !parked) || (unread && snapshot.Status == WorkflowStatus.Suspended) ||
            WorkflowStoreRules.UnwindsNow(snapshot.Status, snapshot.AcknowledgesCancellation, unacknowledged);
        var earliestDeadline = !runnableNow && !commit.Stopped &&
                               snapshot.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution
            ? await connection.ExecuteScalarAsync<DateTime?>("""
                SELECT min(deadline_utc) FROM ixiflow_workflow_waits
                WHERE instance_id = @InstanceId AND close_reason IS NULL
                """, new { commit.InstanceId }, transaction)
            : null;
        var runnableAt = WorkflowStoreRules.CommittedRunnableAt(snapshot.Status, commit, runnableNow, now,
            earliestDeadline is { } deadline ? Utc(deadline) : null);
        var values = new
        {
            snapshot.InstanceId, snapshot.WorkflowName, snapshot.CorrelationId,
            Status = snapshot.Status.ToString(),
            commit.InboxReadThrough,
            StateJson = commit.Payload, Revision = revision,
            commit.ExpectedRevision, commit.CommitId, PayloadHash = commit.ContentHash,
            snapshot.AcknowledgesCancellation, RunnableAtUtc = At(runnableAt)
        };
        await connection.ExecuteAsync(current == null
            ? """
              INSERT INTO ixiflow_workflow_instances
                  (instance_id, workflow_name, status, correlation_id, runnable_at_utc, state_json, revision)
              VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @RunnableAtUtc, @StateJson, @Revision);
              INSERT INTO ixiflow_workflow_commits (instance_id, commit_id, expected_revision, revision, payload_hash)
              VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
              """
            : """
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
              """, values, transaction);
        await ApplyPartsAsync(connection, transaction, commit);
        await AppendTraceAsync(connection, transaction, commit);
        var keepsLease = unread && snapshot.Status == WorkflowStatus.Suspended;
        var releases = (snapshot.Status != WorkflowStatus.Running || parked) && !keepsLease;
        if (snapshot.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or
            WorkflowStatus.Terminated or WorkflowStatus.TimedOut)
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_events SET outcome = 4, settled_at_utc = @Now
                WHERE instance_id = @InstanceId AND outcome = 0;
                UPDATE ixiflow_workflow_waits SET close_reason = @Reason, closed_at_utc = @Now
                WHERE instance_id = @InstanceId AND close_reason IS NULL;
                """, new
            {
                snapshot.InstanceId, Reason = (short)WorkflowStoreRules.FinalWaitCloseReason(snapshot.Status),
                Now = At(now)
            }, transaction);
        var clears = commit.ClearsCrashCount && lease?.CrashCount > 0;
        if (lease != null && (releases || clears))
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_leases
                SET expires_at_utc = CASE WHEN @Released THEN '0001-01-01 00:00:00.0000000' ELSE expires_at_utc END,
                    crash_count = CASE WHEN @ClearsCrashCount THEN 0 ELSE crash_count END
                WHERE instance_id = @InstanceId
                """, new { snapshot.InstanceId, Released = releases, ClearsCrashCount = clears }, transaction);
        var madeRunnable = new List<string>();
        if (commit.ChildStarts.Count > 0)
        {
            var children = commit.ChildStarts.Select(start => (start, Child: start.Instance!)).ToArray();
            await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, workflow_name, status, correlation_id, runnable_at_utc, state_json, revision,
                     parent_instance_id)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @Now, @Payload, 1, @ParentInstanceId)
                """, children.Select(child => new { child.Child.InstanceId, child.Child.WorkflowName,
                    Status = child.Child.Status.ToString(), child.Child.CorrelationId, child.start.Payload,
                    Now = At(now), ParentInstanceId = commit.InstanceId }), transaction);

            madeRunnable.AddRange(children.Select(child => child.Child.InstanceId));
        }
        var inbox = new InboxRow { Status = snapshot.Status.ToString(), InboxSequence = current?.InboxSequence ?? 0 };
        foreach (var cancellation in commit.ChildCancellations)
        {
            var outcome = await RequestCancellationAsync(connection, transaction, cancellation.ChildInstanceId,
                JsonSerializer.Serialize(cancellation.Reason), At(now));
            if (outcome == CancellationOutcome.MadeRunnable)
                madeRunnable.Add(cancellation.ChildInstanceId);
            else if (outcome == CancellationOutcome.Gone)
            {
                var gone = ToEvent(WorkflowStoreRules.ChildGone(cancellation.ChildInstanceId, commit.InstanceId,
                    cancellation.ParentWaitId));
                var appended = await AppendAsync(connection, transaction, gone, gone.ContentHash(), inbox, now);
                if (appended.Status == EventAppendStatus.Stored)
                    inbox.InboxSequence = appended.Sequence;
            }
        }
        if (commit.ParentNotice is { } completion)
        {
            var completed = ToEvent(completion);
            if (await AppendAsync(connection, transaction, completed, completed.ContentHash(), parent, now) is
                { Status: EventAppendStatus.Stored, MadeRunnable: true })
                madeRunnable.Add(completion.ParentInstanceId);
        }
        await transaction.CommitAsync();
        return new WorkflowCommitResult(WorkflowCommitStatus.Applied, revision,
            ReadReason(current?.CancellationJson), parked, unread, madeRunnable);
    }

    private static async Task ApplyWaitChangesAsync(SqliteConnection connection, SqliteTransaction transaction,
        CheckpointCommit commit, long inboxSequence, DateTime now)
    {
        if (commit.OpenedWaits.Count > 0)
            await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_waits
                    (instance_id, wait_id, kind, wait_key, event_type, deadline_utc, opened_sequence, opened_at_utc)
                VALUES (@InstanceId, @WaitId, @Kind, @Key, @EventType, @Deadline, @Sequence, @Now)
                ON CONFLICT (instance_id, wait_id) DO NOTHING
                """, commit.OpenedWaits.Select(wait => new { commit.InstanceId, wait.WaitId,
                    Kind = (short)wait.Kind, wait.Key, wait.EventType, Deadline = At(now + wait.Timeout),
                    Sequence = inboxSequence, Now = At(now) }), transaction);
        if (commit.ArmedWaits.Count > 0)
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_waits SET deadline_utc = @Deadline
                WHERE instance_id = @InstanceId AND wait_id = @WaitId
                    AND close_reason IS NULL AND deadline_utc IS NULL
                """, commit.ArmedWaits.Select(wait => new { commit.InstanceId, wait.WaitId,
                    Deadline = At(now + wait.Timeout) }), transaction);
        if (commit.ClosedWaits.Count > 0)
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_waits SET close_reason = @Reason, closed_at_utc = @Now
                WHERE instance_id = @InstanceId AND wait_id = @WaitId AND close_reason IS NULL
                """, commit.ClosedWaits.Select(wait => new { commit.InstanceId, wait.WaitId,
                    Reason = (short)wait.Reason, Now = At(now) }), transaction);
    }

    private sealed class CommitReceipt
    {
        public long ExpectedRevision { get; set; }
        public long Revision { get; set; }
        public byte[] PayloadHash { get; set; } = [];
    }

    private static CancellationReason? ReadReason(string? json)
    {
        if (json == null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<CancellationReason>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class CommitState
    {
        public long Revision { get; set; }
        public string? CancellationJson { get; set; }
        public bool CancellationAcknowledged { get; set; }
        public long InboxSequence { get; set; }
    }

    private sealed class LeaseRecord
    {
        public long Epoch { get; set; }
        public int CrashCount { get; set; }
        public bool IsActive { get; set; }
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadInstanceAsync(connection, instanceId, true, transaction);
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadInstanceAsync(connection, instanceId, false, transaction);
    }

    private const string RowColumns =
        "instance_id AS InstanceId, state_json AS StateJson, runnable_at_utc AS RunnableAtUtc, revision AS Revision";

    private const string UnpublishedCompletion =
        "AND status IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut') AND NOT completion_published";

    // One WAL snapshot holds the trunk and parts at one revision, even across deletion and recreation.
    private static async Task<WorkflowInstance?> ReadInstanceAsync(SqliteConnection connection,
        string instanceId, bool wholly, SqliteTransaction transaction)
    {
        using var read = await connection.QueryMultipleAsync($"""
            SELECT {RowColumns} FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId;
            SELECT instance_id AS InstanceId, part_key AS PartKey, json AS Json FROM ixiflow_workflow_parts
            WHERE instance_id = @InstanceId {(wholly ? "" : "AND part_key = @DataKey")};
            """, new { InstanceId = instanceId, CheckpointParts.DataKey }, transaction);
        var row = await read.ReadSingleOrDefaultAsync<StoredRow>();
        var parts = (await read.ReadAsync<StoredPart>()).ToArray();
        return row == null ? null : wholly ? row.Read(parts) : row.Summarize(parts.SingleOrDefault()?.Json);
    }

    private static async Task<WorkflowInstance[]> AssembleAsync(SqliteConnection connection,
        IReadOnlyList<StoredRow> rows, SqliteTransaction transaction)
    {
        if (rows.Count == 0)
            return [];
        var parts = (await connection.QueryAsync<StoredPart>("""
            SELECT instance_id AS InstanceId, part_key AS PartKey, json AS Json FROM ixiflow_workflow_parts
            WHERE instance_id IN @Ids
            """, new { Ids = rows.Select(row => row.InstanceId).ToArray() }, transaction))
            .ToLookup(part => part.InstanceId, StringComparer.Ordinal);
        return rows.Select(row => row.Read(parts[row.InstanceId])).ToArray();
    }

    private static async Task ApplyPartsAsync(SqliteConnection connection, SqliteTransaction transaction,
        CheckpointCommit commit)
    {
        if (commit.ReplacesParts)
            await connection.ExecuteAsync("DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId",
                new { commit.InstanceId }, transaction);
        else if (commit.DeletedParts.Count > 0 || commit.Parts.Count > 0)
            await connection.ExecuteAsync("""
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId AND part_key IN @Keys
                """, new
            {
                commit.InstanceId, Keys = commit.DeletedParts.Concat(commit.Parts.Select(part => part.Key)).ToArray()
            }, transaction);
        if (commit.Parts.Count > 0)
            await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_parts (instance_id, part_key, json)
                VALUES (@InstanceId, @Key, @Json)
                """, commit.Parts.Select(part => new { commit.InstanceId, part.Key, part.Json }), transaction);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
        QueryAsync("workflow_name = @Value", workflowName);

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
        QueryAsync("status = @Value", status.ToString());

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
        QueryAsync("correlation_id = @Value", correlationId);

    public async Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentInstanceId);
        return await QueryAsync("parent_instance_id = @Value", parentInstanceId);
    }

    public async Task DeleteWorkflowInstanceAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await WriteAsync(async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = connection.BeginTransaction(deferred: false);
            var parameters = new { InstanceId = instanceId };
            var json = await connection.QuerySingleOrDefaultAsync<string>("""
                SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId
                """, parameters, transaction);
            // The lease row remains, so recreating an ID never repeats an execution epoch.
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_leases SET expires_at_utc = '0001-01-01 00:00:00.0000000', crash_count = 0
                WHERE instance_id = @InstanceId
                """, parameters, transaction);
            if (WaitingParent(json) is { } waiting)
            {
                var parent = await ReadInboxAsync(connection, transaction, waiting.ParentId);
                var gone = ToEvent(WorkflowStoreRules.ChildGone(instanceId, waiting.ParentId, waiting.WaitId));
                await AppendAsync(connection, transaction, gone, gone.ContentHash(), parent, Now);
            }
            await connection.ExecuteAsync("""
                DELETE FROM ixiflow_workflow_waits WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_events WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_commits WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId;
                """, parameters, transaction);
            await transaction.CommitAsync();
        });
    }

    private static (string ParentId, string WaitId)? WaitingParent(string? json) =>
        json != null && JsonSerializer.Deserialize<WorkflowInstance>(json) is
            { ParentInstanceId: { } parentId, ParentWaitId: { } waitId } instance &&
        instance.Status is not (WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or
            WorkflowStatus.Terminated or WorkflowStatus.TimedOut)
            ? (parentId, waitId)
            : null;

    private async Task<IEnumerable<WorkflowInstance>> QueryAsync(string predicate, string value)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        var rows = await connection.QueryAsync<StoredRow>(
            $"SELECT {RowColumns} FROM ixiflow_workflow_instances WHERE {predicate}", new { Value = value }, transaction);
        return await AssembleAsync(connection, rows.ToArray(), transaction);
    }

    private static async Task AppendTraceAsync(SqliteConnection connection, SqliteTransaction transaction,
        CheckpointCommit commit)
    {
        if (commit.TraceEntries.Count == 0)
            return;
        var sequence = await connection.ExecuteScalarAsync<long>("""
            SELECT COALESCE(max(sequence), 0) FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId
            """, new { commit.InstanceId }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO ixiflow_workflow_traces (instance_id, sequence, entry_json)
            VALUES (@InstanceId, @Sequence, @Entry)
            """, commit.TraceEntries.Select((entry, index) => new { commit.InstanceId,
                Sequence = sequence + index + 1, Entry = entry }), transaction);
    }

    public async Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<(long Sequence, string EntryJson)>("""
            SELECT sequence, entry_json FROM ixiflow_workflow_traces
            WHERE instance_id = @InstanceId AND sequence > @AfterSequence
            ORDER BY sequence
            LIMIT @Limit
            """, new { InstanceId = instanceId, AfterSequence = afterSequence, Limit = limit });
        return rows.Select(row => new StoredTraceEntry(row.Sequence,
            JsonSerializer.Deserialize<ExecutionTraceEntry>(row.EntryJson)!)).ToArray();
    }

    private sealed class StoredRow
    {
        public string InstanceId { get; set; } = "";
        public string StateJson { get; set; } = "";
        public DateTime? RunnableAtUtc { get; set; }
        public long Revision { get; set; }

        public WorkflowInstance Read(IEnumerable<StoredPart> parts) => Runnable(CheckpointParts.Assemble(StateJson,
            parts.Select(part => new CheckpointPart(part.PartKey, part.Json)), Revision));

        public WorkflowInstance Summarize(string? data) => Runnable(CheckpointParts.Summarize(StateJson, data, Revision));

        private WorkflowInstance Runnable(WorkflowInstance instance)
        {
            instance.RunnableAtUtc = RunnableAtUtc is { } runnableAt ? Utc(runnableAt) : null;
            return instance;
        }
    }

    private sealed class StoredPart
    {
        public string InstanceId { get; set; } = "";
        public string PartKey { get; set; } = "";
        public string Json { get; set; } = "";
    }

    // Fixed-width UTC text keeps ticks and sorts in time order.
    private static string At(DateTime value) => Utc(value).ToString("yyyy-MM-dd HH:mm:ss.fffffff",
        System.Globalization.CultureInfo.InvariantCulture);

    private static string? At(DateTime? value) => value is { } time ? At(time) : null;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    private async Task EnsureSchemaAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_schemaReady)
            return;

        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady)
                return;

            await WriteAsync(async () =>
            {
                await using var connection = await OpenConnectionAsync();
                await connection.ExecuteAsync("PRAGMA journal_mode=WAL");
                await using var transaction = connection.BeginTransaction(deferred: false);

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
                _schemaReady = true;
            });
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task<T> WriteAsync<T>(Func<Task<T>> write)
    {
        await _writer.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    private static int LeaseMilliseconds(string instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return checked((int)Math.Ceiling(duration.TotalMilliseconds));
    }
}
