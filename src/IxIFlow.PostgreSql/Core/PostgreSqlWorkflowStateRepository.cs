using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Relational;
using Npgsql;

namespace IxIFlow.Core;

/// <summary>
/// Stores workflow instances in PostgreSQL so host processes share the same state, and wakes hosts through
/// <c>LISTEN</c>/<c>NOTIFY</c> when an instance becomes claimable. Needs PostgreSQL 13 or later.
/// </summary>
public sealed partial class PostgreSqlWorkflowStateRepository : RelationalWorkflowStateRepository, IRunnableWakeSource
{
    private readonly string _connectionString;
    private readonly string _ownershipConnectionString;
    private readonly string _listenerConnectionString;

    public const int DefaultOwnershipPoolSize = 16;

    /// <summary>The channel a transaction notifies when it makes an instance claimable.</summary>
    public const string RunnableChannel = "ixiflow_runnable";

    /// <summary>
    /// Creates a store on <paramref name="connectionString"/>. Lease and cancellation traffic uses a
    /// second connection pool of at most <paramref name="ownershipPoolSize"/> connections, so a host
    /// whose runs and the application's own work exhaust the main pool still renews its leases and
    /// delivers cancellation. With pooling turned off the reservation has no effect.
    /// </summary>
    public PostgreSqlWorkflowStateRepository(string connectionString,
        int ownershipPoolSize = DefaultOwnershipPoolSize, OutboxSettings? outboxSettings = null) : base(outboxSettings)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A PostgreSQL connection string is required", nameof(connectionString))
            : connectionString;
        _ownershipConnectionString = ConfigureOwnershipPool(
            new NpgsqlConnectionStringBuilder(connectionString), ownershipPoolSize,
            "Maximum Pool Size", "Minimum Pool Size");
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
    internal override Task NotifyRunnableAsync(DbConnection connection, DbTransaction? transaction) =>
        connection.ExecuteAsync($"SELECT pg_notify('{RunnableChannel}', '')", transaction: transaction);

    private static string Qualified(string table, string columns) =>
        string.Join(", ", columns.Split(", ").Select(column => $"{table}.{column}"));
    // A child its parent still waits for ends that wait as terminated. The parent's row is locked before the
    // child's, as a child's terminal commit locks them, so the parent is read before any lock is taken, and read
    // again once the instance is locked; when it changed in between, nothing is done and the delete starts over.
    // Only the locked row is deleted: a later save or child insertion without the identity lock is left whole.
    internal override string DeleteRevalidationSql => """
            SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId FOR UPDATE
            """;

    internal override bool ReleaseReturnsDue => true;
    // In key order, so two commits that start the same children wait for each other rather than deadlock.
    internal override bool SortChildStarts => true;
    internal override bool IsRetryableCommit(Exception error) =>
        error is PostgresException
        {
            SqlState: PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable
        }
        or PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ixiflow_workflow_instances_pkey"
        };
    internal override object? At(DateTime? value) => Timestamp(value);
    internal override DbType? TimeDbType => DbType.DateTimeOffset;
    private static DateTimeOffset Timestamp(DateTime value) => new(Utc(value));

    private static DateTimeOffset? Timestamp(DateTime? value) => value is { } time ? Timestamp(time) : null;
    internal override async ValueTask<DbTransaction?> BeginTransactionAsync(DbConnection connection, Operation operation) =>
        operation is Operation.Registry or Operation.OutboxClaim or Operation.OutboxMark or Operation.Purge or Operation.LeaseClaim or Operation.Append or Operation.InboxPage or Operation.Commit or Operation.Delete
            or Operation.CancellationRequest or Operation.LeaseRelease
            ? await connection.BeginTransactionAsync() : null;

    internal override async Task<DbConnection> OpenConnectionAsync(bool ownership = false)
    {
        var connection = new NpgsqlConnection(ownership ? _ownershipConnectionString : _connectionString);
        await connection.OpenAsync();
        return connection;
    }

    internal override async Task<CancellationOutcome> RequestCancellationAsync(DbConnection connection,
        DbTransaction? transaction, string instanceId, string reasonJson, DateTime? time)
    {
        var now = time == null ? "clock_timestamp()" : "@Now";
        var nowValue = Timestamp(time);
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

    internal override async Task InsertWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<OpenedWait> waits, long inboxSequence, DateTime now)
    {
        await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_waits
                    (instance_id, wait_id, kind, wait_key, event_type, deadline_utc, opened_sequence, opened_at_utc, default_payload_json)
                SELECT @InstanceId, opened.wait_id, opened.kind, opened.wait_key, opened.event_type,
                    opened.deadline_utc, @Sequence, @Now, opened.default_payload_json
                FROM unnest(@WaitIds::text[], @Kinds::smallint[], @Keys::text[], @EventTypes::text[],
                    @Deadlines::timestamptz[], @DefaultPayloads::text[])
                    AS opened (wait_id, kind, wait_key, event_type, deadline_utc, default_payload_json)
                ON CONFLICT (instance_id, wait_id) DO NOTHING
                """, new
        {
            InstanceId = instanceId,
            Sequence = inboxSequence,
            Now = Timestamp(now),
            WaitIds = waits.Select(wait => wait.WaitId).ToArray(),
            Kinds = waits.Select(wait => (short)wait.Kind).ToArray(),
            Keys = waits.Select(wait => wait.Key).ToArray(),
            EventTypes = waits.Select(wait => wait.EventType).ToArray(),
            DefaultPayloads = waits.Select(wait => wait.DefaultPayloadJson).ToArray(),
            Deadlines = waits.Select(wait => Timestamp(now + wait.Timeout)).ToArray()
        }, transaction);
    }

    internal override async Task ArmWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ArmedWait> waits, DateTime now)
    {
        await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_waits AS stored SET deadline_utc = COALESCE(stored.deadline_utc, armed.deadline_utc),
                    default_payload_json = COALESCE(stored.default_payload_json, armed.default_payload_json)
                FROM unnest(@WaitIds::text[], @Deadlines::timestamptz[], @DefaultPayloads::text[])
                    AS armed (wait_id, deadline_utc, default_payload_json)
                WHERE stored.instance_id = @InstanceId AND stored.wait_id = armed.wait_id
                  AND stored.close_reason IS NULL
                """, new
        {
            InstanceId = instanceId,
            WaitIds = waits.Select(wait => wait.WaitId).ToArray(),
            Deadlines = waits.Select(wait => Timestamp(now + wait.Timeout)).ToArray(),
            DefaultPayloads = waits.Select(wait => wait.DefaultPayloadJson).ToArray()
        }, transaction);
    }

    internal override async Task CloseWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ClosedWait> waits, DateTime now)
    {
        await connection.ExecuteAsync("""
                UPDATE ixiflow_workflow_waits AS stored SET close_reason = closed.reason, closed_at_utc = @Now
                FROM unnest(@WaitIds::text[], @Reasons::smallint[]) AS closed (wait_id, reason)
                WHERE stored.instance_id = @InstanceId AND stored.wait_id = closed.wait_id
                  AND stored.close_reason IS NULL
                """, new
        {
            InstanceId = instanceId,
            Now = Timestamp(now),
            WaitIds = waits.Select(wait => wait.WaitId).ToArray(),
            Reasons = waits.Select(wait => (short)wait.Reason).ToArray()
        }, transaction);
    }

    internal override async Task InsertPartsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<CheckpointPart> parts)
    {
        await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_parts (instance_id, part_key, json)
                SELECT @InstanceId, part.part_key, part.json
                FROM unnest(@Keys::text[], @Jsons::text[]) AS part (part_key, json)
                """, new
        {
            InstanceId = instanceId,
            Keys = parts.Select(part => part.Key).ToArray(),
            Jsons = parts.Select(part => part.Json).ToArray()
        }, transaction);
    }

    internal override async Task AppendTraceAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<string> entries)
    {
        await connection.ExecuteAsync("""
            INSERT INTO ixiflow_workflow_traces (instance_id, sequence, entry_json)
            SELECT @InstanceId,
                (SELECT COALESCE(max(sequence), 0) FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId)
                    + entry.position,
                entry.entry_json
            FROM unnest(@Entries::text[]) WITH ORDINALITY AS entry (entry_json, position)
            """, new { InstanceId = instanceId, Entries = entries.ToArray() }, transaction);
    }

    internal override async Task<int> SettleAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<EventSettlement> settlements, DateTime now)
    {
        return await connection.ExecuteAsync("""
            UPDATE ixiflow_workflow_events AS stored
            SET outcome = settled.outcome, wait_id = settled.wait_id, rejection = settled.rejection,
                settled_at_utc = @Now
            FROM unnest(@EventIds::text[], @Outcomes::smallint[], @WaitIds::text[], @Rejections::smallint[])
                AS settled (event_id, outcome, wait_id, rejection)
            WHERE stored.instance_id = @InstanceId AND stored.event_id = settled.event_id AND stored.outcome = 0
            """, new
        {
            InstanceId = instanceId,
            Now = Timestamp(now),
            EventIds = settlements.Select(settlement => settlement.EventId).ToArray(),
            Outcomes = settlements.Select(settlement => (short)settlement.Outcome).ToArray(),
            WaitIds = settlements.Select(settlement => settlement.WaitId).ToArray(),
            Rejections = settlements.Select(settlement => (short?)settlement.Rejection).ToArray()
        }, transaction);
    }

    internal override async Task InsertChildrenAsync(DbConnection connection, DbTransaction transaction,
        string parentInstanceId, IReadOnlyList<ChildStart> children, DateTime now)
    {
        await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, incarnation, workflow_name, status, correlation_id, runnable_at_utc, state_json, revision,
                     parent_instance_id)
                SELECT child.instance_id, child.incarnation, child.workflow_name, child.status, child.correlation_id, @Now,
                    child.state_json, 1, @ParentInstanceId
                FROM unnest(@Ids::text[], @Incarnations::uuid[], @Names::text[], @Statuses::text[], @CorrelationIds::text[],
                    @Payloads::text[]) AS child (instance_id, incarnation, workflow_name, status, correlation_id, state_json)
                """, new
        {
            Now = Timestamp(now),
            ParentInstanceId = parentInstanceId,
            Ids = children.Select(child => child.Instance!.InstanceId).ToArray(),
            Incarnations = children.Select(_ => Guid.NewGuid()).ToArray(),
            Names = children.Select(child => child.Instance!.WorkflowName).ToArray(),
            Statuses = children.Select(child => child.Instance!.Status.ToString()).ToArray(),
            CorrelationIds = children.Select(child => child.Instance!.CorrelationId).ToArray(),
            Payloads = children.Select(child => StampStartPayload(child.Payload, now)).ToArray()
        }, transaction);
    }

    internal override string ReadInstanceSql(string filter, bool wholly) => $"""
                SELECT {StampedRowColumns} FROM ixiflow_workflow_instances
                WHERE instance_id = @InstanceId {filter};
                SELECT instance_id AS InstanceId, part_key AS PartKey, json AS Json FROM ixiflow_workflow_parts
                WHERE instance_id = @InstanceId {(wholly ? "" : "AND part_key = @DataKey")};
                SELECT instance_id AS InstanceId, revision AS Revision, incarnation AS Incarnation
                FROM ixiflow_workflow_instances
                WHERE instance_id = @InstanceId;
                """;

    internal override string QuerySql(string filter) => $"SELECT {StampedRowColumns} FROM ixiflow_workflow_instances WHERE {filter}";

    internal override string? Sql(Statement statement) => statement switch
    {
        Statement.OutboxClock => """
            SELECT clock_timestamp()
            """,
        Statement.PublisherLock => """
            SELECT pg_advisory_xact_lock(hashtext('IxIFlow.OutboxPublishers'))
            """,
        Statement.PublisherSharedLock => """
            SELECT pg_advisory_xact_lock_shared(hashtext('IxIFlow.OutboxPublishers'))
            """,
        Statement.PublisherUpsert => """
            INSERT INTO ixiflow_outbox_publishers (name, types_json)
            VALUES (@Name, @TypesJson)
            ON CONFLICT (name) DO UPDATE
            SET types_json=excluded.types_json
            """,
        Statement.Publishers => """
            SELECT name AS Name, types_json AS TypesJson, disabled_at_utc AS DisabledAtUtc, last_error AS LastError
            FROM ixiflow_outbox_publishers
            """,
        Statement.PublisherRead => """
            SELECT name AS Name, types_json AS TypesJson, disabled_at_utc AS DisabledAtUtc, last_error AS LastError
            FROM ixiflow_outbox_publishers
            WHERE name=@Name
            """,
        Statement.BackfillDeliveries => """
            INSERT INTO ixiflow_outbox_deliveries (event_id, publisher_name, attempts, next_attempt_at_utc, status, last_error)
            SELECT e.event_id, @Name, 0, @Now, CASE WHEN p.disabled_at_utc IS NULL THEN 0 ELSE 2 END,
                p.last_error
            FROM ixiflow_outbox_events e
            CROSS JOIN ixiflow_outbox_publishers p
            WHERE p.name=@Name
              AND e.event_type=@EventType
              AND NOT EXISTS (SELECT 1
            FROM ixiflow_outbox_deliveries d
            WHERE d.event_id=e.event_id
              AND d.publisher_name=@Name)
            """,
        Statement.InsertOutboxEvent => """
            INSERT INTO ixiflow_outbox_events (event_id, instance_id, incarnation, event_type, event_json, occurred_at_utc)
            SELECT @EventId, @InstanceId,
                instance.incarnation, @EventType, @EventJson, @Now
            FROM ixiflow_workflow_instances instance
            WHERE instance.instance_id=@InstanceId
              AND NOT EXISTS (SELECT 1
            FROM ixiflow_outbox_events event
            WHERE event.instance_id=@InstanceId
              AND event.incarnation=instance.incarnation
              AND event.event_type=@EventType)
            """,
        Statement.InsertDelivery => """
            INSERT INTO ixiflow_outbox_deliveries (event_id, publisher_name, attempts, next_attempt_at_utc, status, last_error)
            VALUES (@EventId, @Name, 0, @Now, @Status, @LastError)
            """,
        Statement.ClaimDeliveries => """
            SELECT d.event_id AS EventId,
                d.publisher_name AS PublisherName,
                d.attempts AS Attempts,
                d.first_attempt_at_utc AS FirstAttemptAtUtc,
                d.next_attempt_at_utc AS NextAttemptAtUtc,
                d.status AS Status,
                d.last_error AS LastError,
                d.lease_token AS LeaseToken,
                d.lease_until_utc AS LeaseUntilUtc,
                e.event_json AS EventJson
            FROM ixiflow_outbox_deliveries d
            JOIN ixiflow_outbox_events e ON e.event_id=d.event_id
            JOIN ixiflow_outbox_publishers p ON p.name=d.publisher_name
            WHERE d.publisher_name=@Name
              AND p.disabled_at_utc IS NULL
              AND d.status=0
              AND d.next_attempt_at_utc<=clock_timestamp()
              AND (d.lease_until_utc IS NULL OR d.lease_until_utc<=clock_timestamp())
            ORDER BY d.next_attempt_at_utc,
                d.event_id
            LIMIT @Limit
            FOR UPDATE OF d SKIP LOCKED
            """,
        Statement.DeliveryLock => """
            SELECT d.event_id AS EventId,
                d.publisher_name AS PublisherName,
                d.attempts AS Attempts,
                d.first_attempt_at_utc AS FirstAttemptAtUtc,
                d.next_attempt_at_utc AS NextAttemptAtUtc,
                d.status AS Status,
                d.last_error AS LastError,
                d.lease_token AS LeaseToken,
                d.lease_until_utc AS LeaseUntilUtc,
                e.event_json AS EventJson
            FROM ixiflow_outbox_deliveries d
            JOIN ixiflow_outbox_events e ON e.event_id=d.event_id
            WHERE d.event_id=@EventId
              AND d.publisher_name=@Name
            FOR UPDATE OF d
            """,
        Statement.DeliveryRead => """
            SELECT d.event_id AS EventId,
                d.publisher_name AS PublisherName,
                d.attempts AS Attempts,
                d.first_attempt_at_utc AS FirstAttemptAtUtc,
                d.next_attempt_at_utc AS NextAttemptAtUtc,
                d.status AS Status,
                d.last_error AS LastError,
                d.lease_token AS LeaseToken,
                d.lease_until_utc AS LeaseUntilUtc,
                e.event_json AS EventJson
            FROM ixiflow_outbox_deliveries d
            JOIN ixiflow_outbox_events e ON e.event_id=d.event_id
            WHERE d.event_id=@EventId
              AND d.publisher_name=@Name
            """,
        Statement.DeliveryUpdate => """
            UPDATE ixiflow_outbox_deliveries
            SET first_attempt_at_utc=@FirstAttemptAtUtc, attempts=@Attempts, next_attempt_at_utc=@NextAttemptAtUtc, status=@Status, last_error=@LastError, lease_token=@LeaseToken, lease_until_utc=@LeaseUntilUtc
            WHERE event_id=@EventId
              AND publisher_name=@Name
            """,
        Statement.UnregisterPublisher => """
            UPDATE ixiflow_outbox_deliveries
            SET status=2, lease_token=NULL, lease_until_utc=NULL
            WHERE publisher_name=@Name AND status=0;
            DELETE FROM ixiflow_outbox_publishers WHERE name=@Name;
            """,
        Statement.DisablePublisher => """
            UPDATE ixiflow_outbox_publishers
            SET disabled_at_utc=COALESCE(disabled_at_utc, @Now), last_error=@Error
            WHERE name=@Name;
            UPDATE ixiflow_outbox_deliveries
            SET status=2, last_error=@Error, lease_token=NULL, lease_until_utc=NULL
            WHERE publisher_name=@Name
              AND status=0;
            """,
        Statement.Cancellation => """
            SELECT cancellation_json FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            """,
        Statement.Cancellations => """
            SELECT instance_id, cancellation_json FROM ixiflow_workflow_instances
            WHERE instance_id = ANY(@InstanceIds) AND cancellation_json IS NOT NULL
            """,
        Statement.Runnable => """
            WITH store AS (SELECT clock_timestamp() AS now)
            SELECT instance.instance_id AS InstanceId, instance.runnable_at_utc AS RunnableAtUtc
            FROM store, ixiflow_workflow_instances AS instance
            LEFT JOIN ixiflow_workflow_leases AS lease
                ON lease.instance_id = instance.instance_id
            WHERE instance.runnable_at_utc <= store.now
              AND (lease.instance_id IS NULL OR lease.expires_at_utc <= store.now)
            ORDER BY instance.runnable_at_utc, instance.instance_id
            LIMIT @Limit
            """,
        // Lock the instance regardless of status before touching its lease; aggregate only after the lock.
        Statement.Running => """
            SELECT COUNT(CASE WHEN status = 'Running' THEN 1 END) FROM (
                SELECT status FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId FOR UPDATE
            ) AS locked
            """,
        Statement.ClaimLease => """
            INSERT INTO ixiflow_workflow_leases AS lease (instance_id, token, expires_at_utc, epoch, crash_count)
            VALUES (@InstanceId, @Token, clock_timestamp() + @Milliseconds * INTERVAL '1 millisecond',
                nextval('ixiflow_execution_epoch'), 0)
            ON CONFLICT (instance_id) DO UPDATE
            SET token = excluded.token,
                expires_at_utc = clock_timestamp() + @Milliseconds * INTERVAL '1 millisecond',
                epoch = excluded.epoch,
                crash_count = lease.crash_count + CASE
                    WHEN @Running AND lease.expires_at_utc > '-infinity' THEN 1 ELSE 0 END
            WHERE lease.expires_at_utc <= clock_timestamp()
            RETURNING epoch AS Epoch, crash_count AS CrashCount
            """,
        Statement.RenewLease => """
            UPDATE ixiflow_workflow_leases
            SET expires_at_utc = clock_timestamp() + @Milliseconds * INTERVAL '1 millisecond'
            WHERE instance_id = @InstanceId AND token = @Token
                AND expires_at_utc > clock_timestamp()
            """,
        Statement.ReleaseLease => """
            WITH released AS (
                UPDATE ixiflow_workflow_leases SET expires_at_utc = '-infinity'
                WHERE instance_id = @InstanceId AND token = @Token AND expires_at_utc > clock_timestamp()
                RETURNING instance_id
            )
            SELECT EXISTS (SELECT 1 FROM ixiflow_workflow_instances AS instance
                WHERE instance.instance_id = released.instance_id AND instance.runnable_at_utc <= clock_timestamp())
            FROM released
            """,
        // Locks an instance row as appends and commits lock it, then reads the store's time: a statement that
        // waits for a lock would otherwise carry a time from before it was granted (D-35).
        Statement.LockInbox or Statement.DeleteParentInbox => """
            SELECT status AS Status, inbox_sequence AS InboxSequence,
                COALESCE((state_json::jsonb ->> 'TracingEnabled')::boolean, true) AS TracingEnabled
            FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            FOR UPDATE;
            SELECT clock_timestamp();
            """,
        Statement.ParentInbox => """
            SELECT status AS Status, inbox_sequence AS InboxSequence FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            FOR UPDATE
            """,
        // Identity serialization protects a missing instance; the row lock also orders appends.
        // Read clock_timestamp() in the next statement so commit time follows the lock wait (D-35).
        Statement.CommitState => """
            SELECT revision AS Revision, incarnation AS Incarnation, cancellation_json AS CancellationJson,
                cancellation_acknowledged AS CancellationAcknowledged, inbox_sequence AS InboxSequence
            FROM ixiflow_workflow_instances
            WHERE instance_id = @InstanceId
            FOR UPDATE;
            SELECT clock_timestamp();
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
            FOR UPDATE
            """,
        Statement.TakenChild => """
                SELECT instance_id FROM ixiflow_workflow_instances WHERE instance_id = ANY(@Ids) LIMIT 1
                """,
        Statement.EventHash => """
            SELECT sequence AS Sequence, payload_hash AS PayloadHash FROM ixiflow_workflow_events
            WHERE instance_id = @InstanceId AND event_id = @EventId
            """,
        Statement.CandidateWait => $"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE instance_id = @InstanceId AND (@TargetWaitId::text IS NOT NULL AND wait_id = @TargetWaitId
                OR @TargetWaitId::text IS NULL AND close_reason IS NULL
                    AND kind = @Event AND (@Key::text IS NULL OR wait_key = @Key))
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
                @Hash, @Now, CASE WHEN @Rejection::smallint IS NULL THEN 0 ELSE 2 END, @Rejection,
                CASE WHEN @Rejection::smallint IS NULL THEN NULL ELSE @Now END)
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
                     WHERE instance_id = ANY(@InstanceIds)
                     """,
        Statement.Wait => $"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE instance_id = @InstanceId AND wait_id = @WaitId
            """,
        Statement.OpenWaits => $"""
            SELECT {WaitColumns} FROM ixiflow_workflow_waits
            WHERE close_reason IS NULL
              AND (@InstanceId::text IS NULL OR instance_id = @InstanceId)
              AND (@Key::text IS NULL OR wait_key = @Key)
              AND (@EventType::text IS NULL OR event_type = @EventType)
              AND (@Kind::smallint IS NULL AND kind NOT IN (@Delay, @Execution) OR kind = @Kind)
              AND (@AfterWaitId::text IS NULL OR wait_id > @AfterWaitId)
            ORDER BY wait_id
            LIMIT @Limit
            """,
        Statement.EarliestDeadline => """
                SELECT min(deadline_utc) FROM ixiflow_workflow_waits
                WHERE instance_id = @InstanceId AND close_reason IS NULL
                """,
        Statement.InsertCommit => """
              INSERT INTO ixiflow_workflow_instances
                  (instance_id, incarnation, workflow_name, status, correlation_id, parent_instance_id, runnable_at_utc, state_json, revision)
              VALUES (@InstanceId, @Incarnation, @WorkflowName, @Status, @CorrelationId, @ParentInstanceId, @RunnableAtUtc, @StateJson, @Revision);
              INSERT INTO ixiflow_workflow_commits (instance_id, commit_id, expected_revision, revision, payload_hash)
              VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
              """,
        Statement.UpdateCommit => """
              UPDATE ixiflow_workflow_instances SET
                  workflow_name = @WorkflowName, status = @Status,
                  correlation_id = @CorrelationId, parent_instance_id = @ParentInstanceId,
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
                SET expires_at_utc = CASE WHEN @Released THEN '-infinity' ELSE expires_at_utc END,
                    crash_count = CASE WHEN @ClearsCrashCount THEN 0 ELSE crash_count END
                WHERE instance_id = @InstanceId
                """,
        Statement.ReadParts => """
            SELECT instance_id AS InstanceId, part_key AS PartKey, json AS Json FROM ixiflow_workflow_parts
            WHERE instance_id = ANY(@Ids);
            SELECT instance_id AS InstanceId, revision AS Revision, incarnation AS Incarnation
            FROM ixiflow_workflow_instances
            WHERE instance_id = ANY(@Ids);
            """,
        Statement.DeleteState => """
            SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId
            """,
        Statement.DeleteLease => """
            DELETE FROM ixiflow_workflow_leases WHERE instance_id = @InstanceId
            """,
        Statement.DeleteRows => """
                DELETE FROM ixiflow_workflow_demands WHERE instance_id = @InstanceId;
                DELETE FROM ixiflow_outbox_deliveries WHERE event_id IN (SELECT e.event_id FROM ixiflow_outbox_events e WHERE e.instance_id=@InstanceId AND NOT EXISTS (SELECT 1 FROM ixiflow_outbox_deliveries pending WHERE pending.event_id=e.event_id AND pending.status=0));
                DELETE FROM ixiflow_outbox_events WHERE instance_id=@InstanceId AND NOT EXISTS (SELECT 1 FROM ixiflow_outbox_deliveries pending WHERE pending.event_id=ixiflow_outbox_events.event_id);
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
        // Takes the lock that commits, claims and deletes of one instance hold for their whole transaction, which
        // exists whether or not the instance's row does: a row that does not exist cannot be locked, and the first
        // commit, a claim and a delete must still not interleave. It is taken after the row lock of the instance's
        // parent, as a child's commit takes that one first, and before the instance's own row.
        Statement.LockIdentity => "SELECT pg_advisory_xact_lock(hashtextextended(@InstanceId, 0))",
        Statement.ByName => "workflow_name = @Value",
        Statement.ByStatus => "status = @Value",
        Statement.ByCorrelation => "correlation_id = @Value",
        Statement.ByParent => "parent_instance_id = @Value",
        Statement.DeleteAllParts => "DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId",
        Statement.DeleteParts => """
                DELETE FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId AND part_key = ANY(@Keys)
                """,
        _ => RegistrySql(statement) ?? AdmissionSql(statement)
    };

    internal override async Task CreateSchemaAsync(DbConnection connection)
    {
    await using var transaction = await connection.BeginTransactionAsync();
    // Separate hosts can initialize the same database at the same time.
    // Validate existing tables without running DDL that would lock other hosts' writes.
    await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('IxIFlow.WorkflowSchema'))",
        transaction: transaction);
    if (await connection.ExecuteScalarAsync<bool>(
            "SELECT to_regclass('ixiflow_workflow_instances') IS NOT NULL",
            transaction: transaction))
    {
        await ValidateCurrentSchemaAsync(connection, transaction);
        await transaction.CommitAsync();
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
            ended_at_utc timestamptz NULL,
            state_json text NOT NULL,
            cancellation_json text NULL,
            cancellation_acknowledged boolean NOT NULL DEFAULT false,
            revision bigint NOT NULL,
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
            default_payload_json text NULL,
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
        CREATE SEQUENCE ixiflow_execution_epoch AS bigint START WITH 1 INCREMENT BY 1 NO CYCLE;
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
    await connection.ExecuteAsync(OutboxSchema, transaction: transaction);
    await connection.ExecuteAsync(RetentionSchema, transaction: transaction);
    await connection.ExecuteAsync(AdmissionSchema, transaction: transaction);
    await connection.ExecuteAsync(RegistrySchema, transaction: transaction);
    await connection.ExecuteAsync(DemandsSchema, transaction: transaction);
    await transaction.CommitAsync();
    }
}
