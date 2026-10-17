using System.Text.Json;
using Dapper;
using Npgsql;

namespace IxIFlow.Core;

/// <summary>
/// Stores workflow instances in PostgreSQL so host processes share the same state, and wakes hosts through
/// <c>LISTEN</c>/<c>NOTIFY</c> when an instance becomes claimable (C-13). Needs PostgreSQL 13 or later.
/// </summary>
public sealed class PostgreSqlWorkflowStateRepository :
    IWorkflowStateRepository, IWorkflowCompletionOutbox, IWorkflowInbox, IRunnableWakeSource
{
    private readonly string _connectionString;
    private readonly string _ownershipConnectionString;
    private readonly string _listenerConnectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public const int DefaultOwnershipPoolSize = 16;

    /// <summary>The channel a transaction notifies when it makes an instance claimable (C-13).</summary>
    public const string RunnableChannel = "ixiflow_runnable";

    /// <summary>
    /// Creates a store on <paramref name="connectionString"/>. Lease and cancellation traffic uses a
    /// second connection pool of at most <paramref name="ownershipPoolSize"/> connections, so a host
    /// whose runs and the application's own work exhaust the main pool still renews its leases and
    /// delivers cancellation (B-09). With pooling turned off the reservation has no effect.
    /// </summary>
    public PostgreSqlWorkflowStateRepository(string connectionString,
        int ownershipPoolSize = DefaultOwnershipPoolSize)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A PostgreSQL connection string is required", nameof(connectionString))
            : connectionString;
        ArgumentOutOfRangeException.ThrowIfLessThan(ownershipPoolSize, 1);
        // A different application name keeps a separate pool in the client.
        var ownership = new NpgsqlConnectionStringBuilder(connectionString);
        ownership.ApplicationName = (ownership.ApplicationName ?? "") + " IxIFlow ownership";
        ownership.MaxPoolSize = ownershipPoolSize;
        ownership.MinPoolSize = Math.Min(ownership.MinPoolSize, ownershipPoolSize);
        _ownershipConnectionString = ownership.ConnectionString;
        // The listening connection is never pooled, so a broken one is not handed out again, nor multiplexed, which
        // cannot wait for notifications. It sends keepalives, so a connection that died silently fails its wait instead
        // of listening to nothing.
        var listener = new NpgsqlConnectionStringBuilder(connectionString);
        listener.ApplicationName = (listener.ApplicationName ?? "") + " IxIFlow wake-ups";
        listener.Pooling = false;
        listener.Multiplexing = false;
        if (listener.KeepAlive == 0)
            listener.KeepAlive = 30;
        _listenerConnectionString = listener.ConnectionString;
        // Claims and cancellation requests can wait on commit locks; together they use at most half
        // the reserved pool, so renewals and the cancellation watch always find a connection.
        _admissions = new SemaphoreSlim(Math.Max(1, ownershipPoolSize / 2));
    }

    private readonly SemaphoreSlim _admissions;

    private async Task<T> AdmittedAsync<T>(Func<Task<T>> call)
    {
        await _admissions.WaitAsync();
        try
        {
            return await call();
        }
        finally
        {
            _admissions.Release();
        }
    }

    public async Task ListenAsync(Action wake, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wake);
        await using var connection = new NpgsqlConnection(_listenerConnectionString);
        await connection.OpenAsync(cancellationToken);
        connection.Notification += (_, _) => wake();
        await using (var listen = new NpgsqlCommand($"LISTEN {RunnableChannel}", connection))
            await listen.ExecuteNonQueryAsync(cancellationToken);
        wake();
        while (true)
            await connection.WaitAsync(cancellationToken);
    }

    // Tells listening hosts, when the transaction commits, that an instance became claimable. Notifications
    // with the same payload in one transaction are delivered once.
    private static Task NotifyRunnableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        connection.ExecuteAsync($"SELECT pg_notify('{RunnableChannel}', '')", transaction: transaction);

    public async Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<StoredRow>($"""
            SELECT {RowColumns} FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId {UnpublishedCompletion}
            """, new { InstanceId = instanceId });
        return row == null ? null
            : (await AssembleAsync(connection, [row], UnpublishedCompletion, null)).SingleOrDefault();
    }

    public async Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync()
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<StoredRow>($"""
            SELECT {RowColumns} FROM ixiflow_workflow_instances
            WHERE true {UnpublishedCompletion}
            ORDER BY instance_id
            LIMIT 100
            """);
        return await AssembleAsync(connection, rows.ToArray(), UnpublishedCompletion, null);
    }

    public async Task<IEnumerable<WorkflowInstance>> ClaimUnpublishedCompletionsAsync(
        string? instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        // Rows another claim is taking are skipped, not waited for.
        var rows = await connection.QueryAsync<StoredRow>($"""
            WITH pending AS (
                SELECT instance_id
                FROM ixiflow_workflow_instances
                WHERE (@InstanceId::text IS NULL OR instance_id = @InstanceId)
                  {UnpublishedCompletion}
                  AND (completion_claim_until IS NULL OR completion_claim_until <= clock_timestamp())
                ORDER BY instance_id
                LIMIT 100
                FOR UPDATE SKIP LOCKED
            )
            UPDATE ixiflow_workflow_instances AS instance
            SET completion_claim_token = @Token,
                completion_claim_until = clock_timestamp() + @Milliseconds * INTERVAL '1 millisecond'
            FROM pending
            WHERE pending.instance_id = instance.instance_id
            RETURNING {Qualified("instance", RowColumns)}
            """, new { InstanceId = instanceId, Token = token,
                Milliseconds = (int)Math.Ceiling(duration.TotalMilliseconds) });
        // An instance read again is still one this claim holds.
        return await AssembleAsync(connection, rows.ToArray(),
            UnpublishedCompletion + " AND completion_claim_token = @Token", new { Token = token });
    }

    public async Task<bool> MarkCompletionPublishedAsync(string instanceId, long revision, string token)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var changed = await connection.ExecuteAsync($"""
            UPDATE ixiflow_workflow_instances
            SET completion_published = true,
                completion_claim_token = NULL, completion_claim_until = NULL
            WHERE instance_id = @InstanceId AND revision = @Revision
              {UnpublishedCompletion}
              AND completion_claim_token = @Token
              AND completion_claim_until > clock_timestamp()
            """, new { InstanceId = instanceId, Revision = revision, Token = token });
        return changed == 1;
    }

    public async Task ReleaseCompletionClaimAsync(string instanceId, string token)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync("""
            UPDATE ixiflow_workflow_instances
            SET completion_claim_token = NULL, completion_claim_until = NULL
            WHERE instance_id = @InstanceId AND completion_claim_token = @Token
            """, new { InstanceId = instanceId, Token = token });
    }

    public async Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        await EnsureSchemaAsync();
        return await AdmittedAsync(() => RequestCancellationCoreAsync(instanceId, reason));
    }

    private async Task<bool> RequestCancellationCoreAsync(string instanceId, CancellationReason reason)
    {
        await using var connection = await OpenOwnershipConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var outcome = await RequestCancellationAsync(connection, transaction, instanceId,
            JsonSerializer.Serialize(reason), "clock_timestamp()", null);
        if (outcome == CancellationOutcome.MadeRunnable)
            await NotifyRunnableAsync(connection, transaction);
        await transaction.CommitAsync();
        return outcome != CancellationOutcome.Gone && outcome != CancellationOutcome.Unchanged;
    }

    private enum CancellationOutcome
    {
        Unchanged,
        Recorded,
        MadeRunnable,
        Gone
    }

    // The first request wins. A request the owner has not acknowledged makes the instance runnable at
    // <paramref name="now"/>, an SQL expression for the store's time.
    private static async Task<CancellationOutcome> RequestCancellationAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string instanceId, string reasonJson, string now, object? nowValue)
    {
        var parameters = new DynamicParameters(new { InstanceId = instanceId, ReasonJson = reasonJson });
        if (nowValue != null)
            parameters.Add("Now", nowValue);
        // An updating statement's other parts read the row as it was before, so it still says whether it existed.
        return (CancellationOutcome)await connection.ExecuteScalarAsync<int>($"""
            WITH requested AS (
                UPDATE ixiflow_workflow_instances
                SET cancellation_json = COALESCE(cancellation_json, @ReasonJson),
                    cancellation_acknowledged = CASE WHEN cancellation_json IS NULL
                        THEN false ELSE cancellation_acknowledged END,
                    runnable_at_utc = CASE WHEN cancellation_json IS NULL OR NOT cancellation_acknowledged
                        THEN {now} ELSE runnable_at_utc END
                WHERE instance_id = @InstanceId AND status IN ('Running', 'Suspended', 'NeedsResolution')
                RETURNING cancellation_acknowledged
            )
            SELECT CASE
                WHEN EXISTS (SELECT 1 FROM requested WHERE NOT cancellation_acknowledged) THEN 2
                WHEN EXISTS (SELECT 1 FROM requested) THEN 1
                WHEN NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId) THEN 3
                ELSE 0 END
            """, parameters, transaction);
    }

    public async Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        await EnsureSchemaAsync();
        return await AdmittedAsync(() => GetCancellationRequestCoreAsync(instanceId));
    }

    private async Task<CancellationReason?> GetCancellationRequestCoreAsync(string instanceId)
    {
        await using var connection = await OpenOwnershipConnectionAsync();
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
        await using var connection = await OpenOwnershipConnectionAsync();
        var rows = await connection.QueryAsync<(string InstanceId, string CancellationJson)>("""
            SELECT instance_id, cancellation_json FROM ixiflow_workflow_instances
            WHERE instance_id = ANY(@InstanceIds) AND cancellation_json IS NOT NULL
            """, new { InstanceIds = instanceIds.Distinct(StringComparer.Ordinal).ToArray() });
        // One unreadable request does not hide the others.
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
        // The store's time is read once, so the index on the runnable time can bound the scan.
        var rows = await connection.QueryAsync<RunnableRow>("""
            WITH store AS (SELECT clock_timestamp() AS now)
            SELECT instance.instance_id AS InstanceId, instance.runnable_at_utc AS RunnableAtUtc
            FROM store, ixiflow_workflow_instances AS instance
            LEFT JOIN ixiflow_workflow_leases AS lease
                ON lease.instance_id = instance.instance_id
            WHERE instance.runnable_at_utc <= store.now
              AND (lease.instance_id IS NULL OR lease.expires_at_utc <= store.now)
            ORDER BY instance.runnable_at_utc, instance.instance_id
            LIMIT @Limit
            """, new { Limit = limit });
        return rows.Select(row => new RunnableInstance(row.InstanceId, row.RunnableAtUtc)).ToArray();
    }

    public async Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync();
        return await AdmittedAsync(() => ClaimAsync(instanceId, token, milliseconds));
    }

    private async Task<ExecutionLeaseGrant?> ClaimAsync(string instanceId, string token, int milliseconds)
    {
        await using var connection = await OpenOwnershipConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // The instance is locked before its lease row, in the same order as a commit, whether or not its row exists
        // yet: a commit made without a lease holds it while it checks that nobody holds the lease, so no lease is
        // granted until that commit is applied.
        await LockIdentityAsync(connection, transaction, instanceId);
        var running = await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT status FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId FOR UPDATE
            """, new { InstanceId = instanceId }, transaction) == "Running";
        // The lease row outlives its lease, so a later owner always gets a higher epoch. A lease that
        // expired without release while the instance was running lost work in flight: one more crash.
        // A claim racing this one waits for it and then finds the lease taken.
        var grant = await connection.QuerySingleOrDefaultAsync<LeaseGrantRow>("""
            INSERT INTO ixiflow_workflow_leases AS lease (instance_id, token, expires_at_utc, epoch, crash_count)
            VALUES (@InstanceId, @Token, clock_timestamp() + @Milliseconds * INTERVAL '1 millisecond', 1, 0)
            ON CONFLICT (instance_id) DO UPDATE
            SET token = excluded.token,
                expires_at_utc = clock_timestamp() + @Milliseconds * INTERVAL '1 millisecond',
                epoch = lease.epoch + 1,
                crash_count = lease.crash_count + CASE
                    WHEN @Running AND lease.expires_at_utc > '-infinity' THEN 1 ELSE 0 END
            WHERE lease.expires_at_utc <= clock_timestamp()
            RETURNING epoch AS Epoch, crash_count AS CrashCount
            """, new { InstanceId = instanceId, Token = token, Milliseconds = milliseconds, Running = running },
            transaction);
        await transaction.CommitAsync();
        return grant == null ? null : new ExecutionLeaseGrant(grant.Epoch, grant.CrashCount);
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
        await using var connection = await OpenOwnershipConnectionAsync();
        var changed = await connection.ExecuteAsync("""
            UPDATE ixiflow_workflow_leases
            SET expires_at_utc = clock_timestamp() + @Milliseconds * INTERVAL '1 millisecond'
            WHERE instance_id = @InstanceId AND token = @Token
                AND expires_at_utc > clock_timestamp()
            """, new { InstanceId = instanceId, Token = token, Milliseconds = milliseconds });
        return changed == 1;
    }

    public async Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        workflowEvent.Validate();
        var hash = workflowEvent.ContentHash();
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // The instance row is locked first, in the same order as a commit, so the waits read below are
        // exactly those committed before this event arrives. The event is received at the time read once
        // that lock is held, which orders it against every read of the inbox (D-35).
        var (instance, lockedAt) = await LockInboxAsync(connection, transaction, workflowEvent.InstanceId);
        var result = await AppendAsync(connection, transaction, workflowEvent, hash, instance, lockedAt);
        if (result.MadeRunnable)
            await NotifyRunnableAsync(connection, transaction);
        await transaction.CommitAsync();
        return result;
    }

    // Takes the lock that commits, claims and deletes of one instance hold for their whole transaction, which
    // exists whether or not the instance's row does: a row that does not exist cannot be locked, and the first
    // commit, a claim and a delete must still not interleave. It is taken after the row lock of the instance's
    // parent, as a child's commit takes that one first, and before the instance's own row.
    private static Task LockIdentityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string instanceId) =>
        connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@InstanceId, 0))",
            new { InstanceId = instanceId }, transaction);

    // Locks an instance row as appends and commits lock it, then reads the store's time: a statement that
    // waits for a lock would otherwise carry a time from before it was granted.
    private static async Task<(InboxRow? Instance, DateTime Now)> LockInboxAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string instanceId)
    {
        using var locked = await connection.QueryMultipleAsync("""
            SELECT status AS Status, inbox_sequence AS InboxSequence FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            FOR UPDATE;
            SELECT clock_timestamp();
            """, new { InstanceId = instanceId }, transaction);
        var instance = await locked.ReadSingleOrDefaultAsync<InboxRow>();
        return (instance, Utc(await locked.ReadSingleAsync<DateTime>()));
    }

    // Appends an event to an instance whose row this transaction has locked, read as instance; null when
    // it does not exist.
    private static async Task<EventAppendResult> AppendAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, WorkflowEvent workflowEvent, byte[] hash, InboxRow? instance, DateTime now)
    {
        // A known event ID answers the same way whatever happened to the instance since.
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
        // A finished instance takes no events; one sent to a wait is told why that wait closed.
        if (finished && workflowEvent.TargetWaitId == null)
            return new EventAppendResult(EventAppendStatus.Closed);
        // The named wait, or the open event wait that could take the event: one that is not past its
        // deadline comes first, as WorkflowStoreRules.Candidates orders them.
        var waits = (await connection.QueryAsync<WaitRow>($"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE instance_id = @InstanceId AND (@TargetWaitId::text IS NOT NULL AND wait_id = @TargetWaitId
                OR @TargetWaitId::text IS NULL AND close_reason IS NULL
                    AND kind = @Event AND (@Key::text IS NULL OR wait_key = @Key))
            ORDER BY CASE WHEN deadline_utc <= @Now THEN 1 ELSE 0 END
            LIMIT 1
            """, new
        {
            workflowEvent.InstanceId, workflowEvent.TargetWaitId, workflowEvent.Key, Event = (short)WaitKind.Event,
            Now = At(now)
        }, transaction)).Select(row => row.Read()).ToArray();
        var rejection = WorkflowStoreRules.RejectOnArrival(workflowEvent, waits.SingleOrDefault(), waits, now)
            ?? (finished ? EventRejection.InstanceFinished : null);
        // A refused event is recorded so a resend is recognized, but takes no place in the inbox.
        var sequence = rejection == null ? instance.InboxSequence + 1 : 0;
        // A stopped instance waits for its operator; the event is consumed after the release.
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
                @Hash, @Now, CASE WHEN @Rejection::smallint IS NULL THEN 0 ELSE 2 END, @Rejection,
                CASE WHEN @Rejection::smallint IS NULL THEN NULL ELSE @Now END)
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
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // The instance row is locked as appends and commits lock it, so the page is read between them:
        // the inbox sequence and the store's time bound the page, and every event received before that
        // time is on it (D-35). The open waits are read after the sequence: a wait opened in between
        // carries a sequence at least as high as every event on the page, so none of them can satisfy it.
        var (instance, now) = await LockInboxAsync(connection, transaction, instanceId);
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
        await using var connection = await OpenOwnershipConnectionAsync();
        foreach (var row in await connection.QueryAsync<(string InstanceId, long InboxSequence)>("""
                     SELECT instance_id, inbox_sequence FROM ixiflow_workflow_instances
                     WHERE instance_id = ANY(@InstanceIds)
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
              AND (@InstanceId::text IS NULL OR instance_id = @InstanceId)
              AND (@Key::text IS NULL OR wait_key = @Key)
              AND (@EventType::text IS NULL OR event_type = @EventType)
              AND (@Kind::smallint IS NULL AND kind NOT IN (@Delay, @Execution) OR kind = @Kind)
              AND (@AfterWaitId::text IS NULL OR wait_id > @AfterWaitId)
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
        await using var connection = await OpenOwnershipConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // An instance released while it is due can be claimed at once.
        var released = await connection.QuerySingleOrDefaultAsync<bool?>("""
            WITH released AS (
                UPDATE ixiflow_workflow_leases SET expires_at_utc = '-infinity'
                WHERE instance_id = @InstanceId AND token = @Token AND expires_at_utc > clock_timestamp()
                RETURNING instance_id
            )
            SELECT EXISTS (SELECT 1 FROM ixiflow_workflow_instances AS instance
                WHERE instance.instance_id = released.instance_id AND instance.runnable_at_utc <= clock_timestamp())
            FROM released
            """, new { InstanceId = instanceId, Token = token }, transaction);
        if (released == true)
            await NotifyRunnableAsync(connection, transaction);
        await transaction.CommitAsync();
        return released != null;
    }

    public async Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Revision != 0)
            throw new InvalidOperationException("Revisioned instances require an atomic commit");
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var runnableNow = WorkflowStoreRules.IsRunnableNow(instance, false);
        // The instance is saved whole, so any parts it was stored in go. A row a commit has revised is
        // left as it is, and the save fails.
        await connection.ExecuteAsync("""
            DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId
                AND EXISTS (SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId AND revision = 0)
            """, new { instance.InstanceId }, transaction);
        var due = await connection.QuerySingleOrDefaultAsync<bool?>("""
            INSERT INTO ixiflow_workflow_instances AS target
                (instance_id, workflow_name, status, correlation_id, runnable_at_utc, state_json)
            VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId,
                CASE WHEN @RunnableNow THEN clock_timestamp() ELSE @RunnableAtUtc END, @StateJson)
            ON CONFLICT (instance_id) DO UPDATE
            SET workflow_name = excluded.workflow_name, status = excluded.status,
                correlation_id = excluded.correlation_id, runnable_at_utc = excluded.runnable_at_utc,
                state_json = excluded.state_json
            WHERE target.revision = 0
            RETURNING COALESCE(runnable_at_utc <= clock_timestamp(), false)
            """, new
        {
            instance.InstanceId,
            instance.WorkflowName,
            Status = instance.Status.ToString(),
            instance.CorrelationId,
            RunnableAtUtc = At(instance.RunnableAtUtc),
            RunnableNow = runnableNow,
            StateJson = JsonSerializer.Serialize(instance)
        }, transaction);
        if (due == null)
            throw new InvalidOperationException("Revisioned instances require an atomic commit");
        // A save that leaves the instance due wakes listening hosts; their claims decide who runs it.
        if (due.Value)
            await NotifyRunnableAsync(connection, transaction);
        await transaction.CommitAsync();
    }

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsureSchemaAsync();
        try
        {
            return await CommitAsync(commit);
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.DeadlockDetected
                                                  or PostgresErrorCodes.SerializationFailure
                                                  or PostgresErrorCodes.LockNotAvailable ||
                                              error is { SqlState: PostgresErrorCodes.UniqueViolation,
                                                  ConstraintName: InstanceKey })
        {
            // The transaction was rolled back, so nothing was written. An instance's key is violated only when
            // another transaction created the same instance, or a child with the same ID, while this one ran;
            // a retry sees it.
            return new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0);
        }
    }

    private const string InstanceKey = "ixiflow_workflow_instances_pkey";

    // The payload is stored as the commit carries it; the revision and runnable time live in their columns
    // and are set on the instance when it is read.
    private async Task<WorkflowCommitResult> CommitAsync(CheckpointCommit commit)
    {
        var snapshot = commit.Instance;
        var revision = checked(commit.ExpectedRevision + 1);
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        // A child's terminal commit appends to its parent's inbox. Rows are locked parent first, as a
        // parent's commit that cancels its children does, so the two cannot deadlock.
        var parent = commit.ParentNotice is { } notice
            ? (await LockInboxAsync(connection, transaction, notice.ParentInstanceId)).Instance
            : null;
        await LockIdentityAsync(connection, transaction, commit.InstanceId);
        // One store time for the whole commit, read once the instance row's lock is held: its deadlines,
        // its runnable time and its records (D-35).
        CommitState? current;
        DateTime now;
        using (var locked = await connection.QueryMultipleAsync("""
            SELECT revision AS Revision, cancellation_json AS CancellationJson,
                cancellation_acknowledged AS CancellationAcknowledged, inbox_sequence AS InboxSequence
            FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            FOR UPDATE;
            SELECT clock_timestamp();
            """, new { commit.InstanceId }, transaction))
        {
            current = await locked.ReadSingleOrDefaultAsync<CommitState>();
            now = Utc(await locked.ReadSingleAsync<DateTime>());
        }
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
            FOR UPDATE
            """, new { commit.InstanceId, Now = At(now) }, transaction);
        // A leased commit needs the instance's current, active lease. An unleased commit (epoch zero)
        // is accepted only while nobody holds the lease; the revision check still orders it.
        var owns = commit.Epoch == 0
            ? lease?.IsActive != true
            : lease is { IsActive: true } && lease.Epoch == commit.Epoch;
        if (receipt != null)
        {
            if (receipt.ExpectedRevision != commit.ExpectedRevision ||
                !receipt.PayloadHash.SequenceEqual(commit.ContentHash))
                throw new InvalidOperationException("A commit ID cannot be reused for a different transition");
            await transaction.CommitAsync();
            // A terminal or parking commit released the lease; only a running owner must still hold it.
            // The same commit gets the same answer, including arrivals it has not read.
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
        // A child's ID is new; a commit that names an existing instance changes nothing.
        if (commit.ChildStarts.Count > 0 && await connection.ExecuteScalarAsync<string?>("""
                SELECT instance_id FROM ixiflow_workflow_instances WHERE instance_id = ANY(@Ids) LIMIT 1
                """, new { Ids = commit.ChildStarts.Select(child => child.ChildInstanceId).ToArray() }, transaction)
            is { } taken)
            throw new InvalidOperationException(
                $"Child workflow '{taken}' cannot start: an instance with its ID exists");

        // An event this commit settles must still be pending (D-04).
        var settled = commit.Settlements.Count == 0 ? 0 : await connection.ExecuteAsync("""
            UPDATE ixiflow_workflow_events AS stored
            SET outcome = settled.outcome, wait_id = settled.wait_id, rejection = settled.rejection,
                settled_at_utc = @Now
            FROM unnest(@EventIds::text[], @Outcomes::smallint[], @WaitIds::text[], @Rejections::smallint[])
                AS settled (event_id, outcome, wait_id, rejection)
            WHERE stored.instance_id = @InstanceId AND stored.event_id = settled.event_id AND stored.outcome = 0
            """, new
        {
            commit.InstanceId, Now = At(now),
            EventIds = commit.Settlements.Select(settlement => settlement.EventId).ToArray(),
            Outcomes = commit.Settlements.Select(settlement => (short)settlement.Outcome).ToArray(),
            WaitIds = commit.Settlements.Select(settlement => settlement.WaitId).ToArray(),
            Rejections = commit.Settlements.Select(settlement => (short?)settlement.Rejection).ToArray()
        }, transaction);
        if (settled != commit.Settlements.Count)
        {
            await transaction.RollbackAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.EventsChanged, currentRevision);
        }
        var unacknowledged = current is { CancellationJson: not null, CancellationAcknowledged: false };
        // A run that has not read every arrival neither parks nor goes idle out of reach.
        var unread = commit.InboxReadThrough is { } readThrough && current?.InboxSequence > readThrough;
        var parked = commit.Parks && !unacknowledged && !unread;
        await ApplyWaitChangesAsync(connection, transaction, commit, current?.InboxSequence ?? 0, now);
        // An idle instance is next due at the earliest deadline of its open waits (D-31, D-35).
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
        // A missing row is inserted, so a commit racing to create the same instance violates its key and
        // is retried, rather than overwriting it.
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
        // The lease row is written only when something changes, so most commits touch only their
        // instance row. An idle owner that has not read every arrival keeps the lease and reads them.
        var keepsLease = unread && snapshot.Status == WorkflowStatus.Suspended;
        var releases = (snapshot.Status != WorkflowStatus.Running || parked) && !keepsLease;
        // A finished instance takes no more events; those still pending are closed, and so are its waits.
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
                SET expires_at_utc = CASE WHEN @Released THEN '-infinity' ELSE expires_at_utc END,
                    crash_count = CASE WHEN @ClearsCrashCount THEN 0 ELSE crash_count END
                WHERE instance_id = @InstanceId
                """, new { snapshot.InstanceId, Released = releases, ClearsCrashCount = clears }, transaction);
        var madeRunnable = new List<string>();
        // Each child starts as its own instance, runnable now, with no owner yet (D-17).
        // The payload is stored as the start carries it; the child's revision and runnable time live in their columns.
        if (commit.ChildStarts.Count > 0)
        {
            // In key order, so two commits that start the same children wait for each other rather than deadlock.
            var children = commit.ChildStarts.Select(start => (start, Child: start.Instance!))
                .OrderBy(child => child.Child.InstanceId, StringComparer.Ordinal).ToArray();
            await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, workflow_name, status, correlation_id, runnable_at_utc, state_json, revision,
                     parent_instance_id)
                SELECT child.instance_id, child.workflow_name, child.status, child.correlation_id, @Now,
                    child.state_json, 1, @ParentInstanceId
                FROM unnest(@Ids::text[], @Names::text[], @Statuses::text[], @CorrelationIds::text[],
                    @Payloads::text[]) AS child (instance_id, workflow_name, status, correlation_id, state_json)
                """, new
            {
                Now = At(now), ParentInstanceId = commit.InstanceId,
                Ids = children.Select(child => child.Child.InstanceId).ToArray(),
                Names = children.Select(child => child.Child.WorkflowName).ToArray(),
                Statuses = children.Select(child => child.Child.Status.ToString()).ToArray(),
                CorrelationIds = children.Select(child => child.Child.CorrelationId).ToArray(),
                Payloads = children.Select(child => child.start.Payload).ToArray()
            }, transaction);
            madeRunnable.AddRange(children.Select(child => child.Child.InstanceId));
        }
        // Each event this commit appends to its own inbox takes the next sequence.
        var inbox = new InboxRow { Status = snapshot.Status.ToString(), InboxSequence = current?.InboxSequence ?? 0 };
        var woken = false;
        foreach (var cancellation in commit.ChildCancellations)
        {
            var outcome = await RequestCancellationAsync(connection, transaction, cancellation.ChildInstanceId,
                JsonSerializer.Serialize(cancellation.Reason), "@Now", At(now));
            if (outcome == CancellationOutcome.MadeRunnable)
                madeRunnable.Add(cancellation.ChildInstanceId);
            // A child that is gone will never answer; its parent's wait ends as its termination.
            else if (outcome == CancellationOutcome.Gone)
            {
                var gone = ToEvent(WorkflowStoreRules.ChildGone(cancellation.ChildInstanceId, commit.InstanceId,
                    cancellation.ParentWaitId));
                var appended = await AppendAsync(connection, transaction, gone, gone.ContentHash(), inbox, now);
                if (appended.Status == EventAppendStatus.Stored)
                    inbox.InboxSequence = appended.Sequence;
                woken |= appended.MadeRunnable;
            }
        }
        // The child's completion goes to its parent's inbox; whatever the parent's state, the child's
        // commit stands.
        if (commit.ParentNotice is { } completion)
        {
            var completed = ToEvent(completion);
            if (await AppendAsync(connection, transaction, completed, completed.ContentHash(), parent, now) is
                { Status: EventAppendStatus.Stored, MadeRunnable: true })
                madeRunnable.Add(completion.ParentInstanceId);
        }
        // Other hosts hear of what this commit made claimable: other instances, and this one when it is due
        // and nobody holds it, because the commit let it go, was made without a lease, or woke it with an event.
        if (madeRunnable.Count > 0 || woken || (runnableAt <= now && (releases || commit.Epoch == 0)))
            await NotifyRunnableAsync(connection, transaction);
        await transaction.CommitAsync();
        return new WorkflowCommitResult(WorkflowCommitStatus.Applied, revision,
            ReadReason(current?.CancellationJson), parked, unread, madeRunnable);
    }

    // A wait opens at the inbox sequence read under the instance row's lock, which appends also take,
    // so it is ordered exactly against every event. Its deadline is the commit's store time plus its
    // timeout (D-35). Opening or arming it again keeps its first record.
    private static async Task ApplyWaitChangesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CheckpointCommit commit, long inboxSequence, DateTime now)
    {
        if (commit.OpenedWaits.Count > 0)
            await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_waits
                    (instance_id, wait_id, kind, wait_key, event_type, deadline_utc, opened_sequence, opened_at_utc)
                SELECT @InstanceId, opened.wait_id, opened.kind, opened.wait_key, opened.event_type,
                    opened.deadline_utc, @Sequence, @Now
                FROM unnest(@WaitIds::text[], @Kinds::smallint[], @Keys::text[], @EventTypes::text[],
                    @Deadlines::timestamptz[]) AS opened (wait_id, kind, wait_key, event_type, deadline_utc)
                ON CONFLICT (instance_id, wait_id) DO NOTHING
                """, new
            {
                commit.InstanceId, Sequence = inboxSequence, Now = At(now),
                WaitIds = commit.OpenedWaits.Select(wait => wait.WaitId).ToArray(),
                Kinds = commit.OpenedWaits.Select(wait => (short)wait.Kind).ToArray(),
                Keys = commit.OpenedWaits.Select(wait => wait.Key).ToArray(),
                EventTypes = commit.OpenedWaits.Select(wait => wait.EventType).ToArray(),
                Deadlines = commit.OpenedWaits.Select(wait => At(now + wait.Timeout)).ToArray()
            }, transaction);
        if (commit.ArmedWaits.Count > 0)
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_waits AS stored SET deadline_utc = armed.deadline_utc
                FROM unnest(@WaitIds::text[], @Deadlines::timestamptz[]) AS armed (wait_id, deadline_utc)
                WHERE stored.instance_id = @InstanceId AND stored.wait_id = armed.wait_id
                  AND stored.close_reason IS NULL AND stored.deadline_utc IS NULL
                """, new
            {
                commit.InstanceId,
                WaitIds = commit.ArmedWaits.Select(wait => wait.WaitId).ToArray(),
                Deadlines = commit.ArmedWaits.Select(wait => At(now + wait.Timeout)).ToArray()
            }, transaction);
        if (commit.ClosedWaits.Count > 0)
            await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_waits AS stored SET close_reason = closed.reason, closed_at_utc = @Now
                FROM unnest(@WaitIds::text[], @Reasons::smallint[]) AS closed (wait_id, reason)
                WHERE stored.instance_id = @InstanceId AND stored.wait_id = closed.wait_id
                  AND stored.close_reason IS NULL
                """, new
            {
                commit.InstanceId, Now = At(now),
                WaitIds = commit.ClosedWaits.Select(wait => wait.WaitId).ToArray(),
                Reasons = commit.ClosedWaits.Select(wait => (short)wait.Reason).ToArray()
            }, transaction);
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
        return await ReadAtOneRevisionAsync(connection, instanceId, wholly: true, "", null);
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        return await ReadAtOneRevisionAsync(connection, instanceId, wholly: false, "", null);
    }

    private const int MaxReadAttempts = 20;

    // An instance row's columns as a read takes them. Its revision changes with every commit, and its incarnation with
    // every insert, so together they never repeat for an ID, not even when the instance is deleted and created again;
    // inbox appends, cancellation requests and claims change neither.
    private const string RowColumns =
        "instance_id AS InstanceId, state_json AS StateJson, runnable_at_utc AS RunnableAtUtc, revision AS Revision, " +
        "incarnation AS Incarnation";

    private static string Qualified(string table, string columns) =>
        string.Join(", ", columns.Split(", ").Select(column => $"{table}.{column}"));

    private const string UnpublishedCompletion =
        "AND status IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut') AND NOT completion_published";

    // The row, then its parts, then its revision and incarnation again: when they moved, a commit, or a delete and a
    // new instance with the same ID, may have changed the parts after the row was read, and the read starts again.
    // Each statement reads its own snapshot and takes no lock. A summary reads only the data part.
    // <paramref name="filter"/> is the condition the row was selected by, which a row read again must still meet.
    private static async Task<WorkflowInstance?> ReadAtOneRevisionAsync(NpgsqlConnection connection,
        string instanceId, bool wholly, string filter, object? filterValues)
    {
        var parameters = new DynamicParameters(new { InstanceId = instanceId, CheckpointParts.DataKey });
        if (filterValues != null)
            parameters.AddDynamicParams(filterValues);
        for (var attempt = 1; ; attempt++)
        {
            StoredRow? row;
            StoredPart[] parts;
            StoredStamp? stamp;
            using (var read = await connection.QueryMultipleAsync($"""
                SELECT {RowColumns} FROM ixiflow_workflow_instances
                WHERE instance_id = @InstanceId {filter};
                SELECT instance_id AS InstanceId, part_key AS PartKey, json AS Json FROM ixiflow_workflow_parts
                WHERE instance_id = @InstanceId {(wholly ? "" : "AND part_key = @DataKey")};
                SELECT instance_id AS InstanceId, revision AS Revision, incarnation AS Incarnation
                FROM ixiflow_workflow_instances
                WHERE instance_id = @InstanceId;
                """, parameters))
            {
                row = await read.ReadSingleOrDefaultAsync<StoredRow>();
                parts = (await read.ReadAsync<StoredPart>()).ToArray();
                stamp = await read.ReadSingleOrDefaultAsync<StoredStamp>();
            }
            if (row == null)
                return null;
            if (stamp != null && row.Same(stamp))
                return wholly ? row.Read(parts) : row.Summarize(parts.SingleOrDefault()?.Json);
            if (attempt == MaxReadAttempts)
                throw new InvalidOperationException(
                    $"Workflow instance '{instanceId}' changed in each of {MaxReadAttempts} reads");
            // A commit is in progress; reading again at once would most likely meet the next one.
            await Task.Delay(Random.Shared.Next(1, 5 * attempt));
        }
    }

    // Each row with the parts stored with it. The parts, revisions and incarnations are read after the rows; a row
    // that moved since is read again on its own, and kept only if it still meets <paramref name="filter"/>, the condition
    // it was selected by, so no instance mixes two commits or is listed for a state it has left.
    private static async Task<WorkflowInstance[]> AssembleAsync(NpgsqlConnection connection,
        IReadOnlyList<StoredRow> rows, string filter, object? filterValues)
    {
        if (rows.Count == 0)
            return [];
        ILookup<string, StoredPart> parts;
        Dictionary<string, StoredStamp> stamps;
        using (var read = await connection.QueryMultipleAsync("""
            SELECT instance_id AS InstanceId, part_key AS PartKey, json AS Json FROM ixiflow_workflow_parts
            WHERE instance_id = ANY(@Ids);
            SELECT instance_id AS InstanceId, revision AS Revision, incarnation AS Incarnation
            FROM ixiflow_workflow_instances
            WHERE instance_id = ANY(@Ids);
            """, new { Ids = rows.Select(row => row.InstanceId).ToArray() }))
        {
            parts = (await read.ReadAsync<StoredPart>()).ToLookup(part => part.InstanceId, StringComparer.Ordinal);
            stamps = (await read.ReadAsync<StoredStamp>())
                .ToDictionary(stored => stored.InstanceId, StringComparer.Ordinal);
        }
        var instances = new List<WorkflowInstance>();
        foreach (var row in rows)
            if (stamps.TryGetValue(row.InstanceId, out var stamp) && row.Same(stamp))
                instances.Add(row.Read(parts[row.InstanceId]));
            else if (await ReadAtOneRevisionAsync(connection, row.InstanceId, wholly: true, filter, filterValues)
                     is { } moved)
                instances.Add(moved);
        return instances.ToArray();
    }

    // A commit deletes the parts it removes and those it rewrites, then inserts what it writes (A-08); a commit that
    // replaces its parts deletes them all first.
    private static async Task ApplyPartsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CheckpointCommit commit)
    {
        if (commit.ReplacesParts)
            await connection.ExecuteAsync("DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId",
                new { commit.InstanceId }, transaction);
        else if (commit.DeletedParts.Count > 0 || commit.Parts.Count > 0)
            await connection.ExecuteAsync("""
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId AND part_key = ANY(@Keys)
                """, new
            {
                commit.InstanceId, Keys = commit.DeletedParts.Concat(commit.Parts.Select(part => part.Key)).ToArray()
            }, transaction);
        if (commit.Parts.Count > 0)
            await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_parts (instance_id, part_key, json)
                SELECT @InstanceId, part.part_key, part.json
                FROM unnest(@Keys::text[], @Jsons::text[]) AS part (part_key, json)
                """, new
            {
                commit.InstanceId,
                Keys = commit.Parts.Select(part => part.Key).ToArray(),
                Jsons = commit.Parts.Select(part => part.Json).ToArray()
            }, transaction);
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
        await using var connection = await OpenConnectionAsync();
        while (!await TryDeleteAsync(connection, instanceId))
        {
        }
    }

    // A child its parent still waits for ends that wait as terminated. The parent's row is locked before the
    // child's, as a child's terminal commit locks them, so the parent is read before any lock is taken, and read
    // again once the instance is locked; when it changed in between, nothing is done and the delete starts over.
    private static async Task<bool> TryDeleteAsync(NpgsqlConnection connection, string instanceId)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var parameters = new { InstanceId = instanceId };
        var seen = WaitingParent(await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId
            """, parameters, transaction));
        var parent = seen is { } waiting ? await LockInboxAsync(connection, transaction, waiting.ParentId) : default;
        // The instance is locked as commits and claims lock it, so none runs until the delete is over, then its row,
        // as appends lock it. Only the row found here is deleted: an instance created after this read, which a save
        // or a parent's commit can do without the lock on its ID, is left whole.
        await LockIdentityAsync(connection, transaction, instanceId);
        var locked = await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId FOR UPDATE
            """, parameters, transaction);
        if (WaitingParent(locked) != seen)
        {
            await transaction.RollbackAsync();
            return false;
        }
        // The lease row stays so a recreated instance never reuses an epoch; its crashes go.
        await connection.ExecuteAsync("""
            UPDATE ixiflow_workflow_leases SET expires_at_utc = '-infinity', crash_count = 0
            WHERE instance_id = @InstanceId
            """, parameters, transaction);
        if (locked != null)
        {
            if (seen is { } answering)
            {
                var gone = ToEvent(WorkflowStoreRules.ChildGone(instanceId, answering.ParentId, answering.WaitId));
                if ((await AppendAsync(connection, transaction, gone, gone.ContentHash(), parent.Instance,
                        parent.Now)).MadeRunnable)
                    await NotifyRunnableAsync(connection, transaction);
            }
            // Its rows in the other tables are deleted by key.
            await connection.ExecuteAsync("""
                DELETE FROM ixiflow_workflow_waits WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_events WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_commits WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId;
                """, parameters, transaction);
        }
        await transaction.CommitAsync();
        return true;
    }

    // The parent and its wait that an instance saved as this JSON would still answer, if any.
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
        var rows = await connection.QueryAsync<StoredRow>(
            $"SELECT {RowColumns} FROM ixiflow_workflow_instances WHERE {predicate}", new { Value = value });
        return await AssembleAsync(connection, rows.ToArray(), $"AND {predicate}", new { Value = value });
    }

    // A commit's trace entries follow those already stored, numbered in order (G-01). The instance row is locked,
    // so no other commit numbers entries at the same time.
    private static async Task AppendTraceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CheckpointCommit commit)
    {
        if (commit.TraceEntries.Count == 0)
            return;
        await connection.ExecuteAsync("""
            INSERT INTO ixiflow_workflow_traces (instance_id, sequence, entry_json)
            SELECT @InstanceId,
                (SELECT COALESCE(max(sequence), 0) FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId)
                    + entry.position,
                entry.entry_json
            FROM unnest(@Entries::text[]) WITH ORDINALITY AS entry (entry_json, position)
            """, new { commit.InstanceId, Entries = commit.TraceEntries.ToArray() }, transaction);
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

    // The runnable time and the revision are kept in their columns: the store changes the runnable time
    // without rewriting the state, and a commit stores its payload as it came, without its new revision.
    private sealed class StoredRow
    {
        public string InstanceId { get; set; } = "";
        public string StateJson { get; set; } = "";
        public DateTime? RunnableAtUtc { get; set; }
        public long Revision { get; set; }
        public Guid Incarnation { get; set; }

        public bool Same(StoredStamp stamp) => stamp.Revision == Revision && stamp.Incarnation == Incarnation;

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

    private sealed class StoredStamp
    {
        public string InstanceId { get; set; } = "";
        public long Revision { get; set; }
        public Guid Incarnation { get; set; }
    }

    // Times go to the server as timestamptz, which keeps microseconds; times read back are UTC.
    private static DateTimeOffset At(DateTime value) => new(Utc(value));

    private static DateTimeOffset? At(DateTime? value) => value is { } time ? At(time) : null;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // Leases and cancellation use the reserved pool.
    private async Task<NpgsqlConnection> OpenOwnershipConnectionAsync()
    {
        var connection = new NpgsqlConnection(_ownershipConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady)
            return;

        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady)
                return;

            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            // Separate hosts can initialize the same database at the same time.
            // The schema is created in one transaction, so its last object stands for all of it. A schema that
            // exists is left alone: creating an index that exists still locks its table against other hosts' writes.
            await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('IxIFlow.WorkflowSchema'))",
                transaction: transaction);
            if (await connection.ExecuteScalarAsync<bool>(
                    "SELECT to_regclass('ix_ixiflow_workflow_leases_expires_at') IS NOT NULL",
                    transaction: transaction))
            {
                await transaction.CommitAsync();
                _schemaReady = true;
                return;
            }
            // Identifiers compare as the engine compares them, ordinally.
            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS ixiflow_workflow_instances (
                    instance_id varchar(100) COLLATE "C" NOT NULL PRIMARY KEY,
                    workflow_name varchar(200) COLLATE "C" NOT NULL,
                    status varchar(50) COLLATE "C" NOT NULL,
                    correlation_id varchar(100) COLLATE "C" NULL,
                    runnable_at_utc timestamptz NULL,
                    state_json text NOT NULL,
                    cancellation_json text NULL,
                    cancellation_acknowledged boolean NOT NULL DEFAULT false,
                    completion_published boolean NOT NULL DEFAULT false,
                    completion_claim_token varchar(100) COLLATE "C" NULL,
                    completion_claim_until timestamptz NULL,
                    revision bigint NOT NULL DEFAULT 0,
                    inbox_sequence bigint NOT NULL DEFAULT 0,
                    inbox_read_through bigint NOT NULL DEFAULT 0,
                    incarnation uuid NOT NULL DEFAULT gen_random_uuid(),
                    parent_instance_id varchar(100) COLLATE "C" NULL
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
                    ON ixiflow_workflow_instances (instance_id) INCLUDE (completion_claim_until)
                    WHERE NOT completion_published;
                CREATE TABLE IF NOT EXISTS ixiflow_workflow_events (
                    instance_id varchar(100) COLLATE "C" NOT NULL,
                    event_id varchar(100) COLLATE "C" NOT NULL,
                    sequence bigint NOT NULL,
                    event_type varchar(400) COLLATE "C" NOT NULL,
                    event_key varchar(400) COLLATE "C" NULL,
                    target_wait_id varchar(100) COLLATE "C" NULL,
                    subject varchar(100) COLLATE "C" NULL,
                    payload_json text NOT NULL,
                    payload_hash bytea NOT NULL,
                    received_at_utc timestamptz NOT NULL,
                    outcome smallint NOT NULL,
                    wait_id varchar(100) COLLATE "C" NULL,
                    rejection smallint NULL,
                    settled_at_utc timestamptz NULL,
                    PRIMARY KEY (instance_id, event_id)
                );
                CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_events_pending
                    ON ixiflow_workflow_events (instance_id, sequence) WHERE outcome = 0;
                CREATE TABLE IF NOT EXISTS ixiflow_workflow_parts (
                    instance_id varchar(100) COLLATE "C" NOT NULL,
                    part_key varchar(200) COLLATE "C" NOT NULL,
                    json text NOT NULL,
                    PRIMARY KEY (instance_id, part_key)
                );
                CREATE TABLE IF NOT EXISTS ixiflow_workflow_traces (
                    instance_id varchar(100) COLLATE "C" NOT NULL,
                    sequence bigint NOT NULL,
                    entry_json text NOT NULL,
                    PRIMARY KEY (instance_id, sequence)
                );
                CREATE TABLE IF NOT EXISTS ixiflow_workflow_waits (
                    instance_id varchar(100) COLLATE "C" NOT NULL,
                    wait_id varchar(100) COLLATE "C" NOT NULL,
                    kind smallint NOT NULL,
                    wait_key varchar(400) COLLATE "C" NOT NULL,
                    event_type varchar(400) COLLATE "C" NOT NULL,
                    deadline_utc timestamptz NULL,
                    opened_sequence bigint NOT NULL,
                    opened_at_utc timestamptz NOT NULL,
                    close_reason smallint NULL,
                    closed_at_utc timestamptz NULL,
                    PRIMARY KEY (instance_id, wait_id)
                );
                CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_waits_open
                    ON ixiflow_workflow_waits (wait_key, event_type, wait_id) WHERE close_reason IS NULL;
                CREATE TABLE IF NOT EXISTS ixiflow_workflow_commits (
                    instance_id varchar(100) COLLATE "C" NOT NULL,
                    commit_id varchar(100) COLLATE "C" NOT NULL,
                    expected_revision bigint NOT NULL,
                    revision bigint NOT NULL,
                    payload_hash bytea NOT NULL,
                    PRIMARY KEY (instance_id, commit_id)
                );
                CREATE TABLE IF NOT EXISTS ixiflow_workflow_leases (
                    instance_id varchar(100) COLLATE "C" NOT NULL PRIMARY KEY,
                    token varchar(100) COLLATE "C" NOT NULL,
                    expires_at_utc timestamptz NOT NULL,
                    epoch bigint NOT NULL,
                    crash_count integer NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_ixiflow_workflow_leases_expires_at
                    ON ixiflow_workflow_leases (expires_at_utc);
                """, transaction: transaction);
            await transaction.CommitAsync();
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
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
