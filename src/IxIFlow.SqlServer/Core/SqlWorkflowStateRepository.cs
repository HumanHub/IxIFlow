using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Relational;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Core;

/// <summary>
/// Stores workflow instances in SQL Server so host processes share the same state.
/// </summary>
public sealed partial class SqlWorkflowStateRepository : RelationalWorkflowStateRepository
{
    private readonly string _connectionString;

    public const int DefaultOwnershipPoolSize = 16;

    /// <summary>
    /// Creates a store on <paramref name="connectionString"/>. Lease and cancellation traffic uses a
    /// second connection pool of at most <paramref name="ownershipPoolSize"/> connections, so a host
    /// whose runs and the application's own work exhaust the main pool still renews its leases and
    /// delivers cancellation. With pooling turned off the reservation has no effect.
    /// </summary>
    public SqlWorkflowStateRepository(string connectionString, int ownershipPoolSize = DefaultOwnershipPoolSize,
        OutboxSettings? outboxSettings = null) : base(outboxSettings)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A SQL Server connection string is required", nameof(connectionString))
            : connectionString;
        _ownershipConnectionString = ConfigureOwnershipPool(
            new SqlConnectionStringBuilder(connectionString), ownershipPoolSize,
            "Max Pool Size", "Min Pool Size", maxApplicationNameLength: 128);
    }

    private readonly string _ownershipConnectionString;

    private const string ServerEventColumns =
        "InstanceId, EventId, Sequence, EventType, EventKey, TargetWaitId, PayloadJson, ReceivedAtUtc, " +
        "Outcome, WaitId, Rejection, Subject";

    private const string ServerWaitColumns =
        "InstanceId, WaitId, Kind, WaitKey, EventType, DeadlineUtc, OpenedSequence, OpenedAtUtc, CloseReason, " +
        "ClosedAtUtc, DefaultPayloadJson";
    private const string ServerRowColumns = "InstanceId, StateJson, RunnableAtUtc, Revision, Incarnation";

    // Lock and revalidate the instance before its lease and tombstone, including absent-key admission.
    internal override string DeleteRevalidationSql => """
        SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, HOLDLOCK)
        WHERE InstanceId = @InstanceId
        """;
    internal override object LeaseParameters(string instanceId, DateTime now) => new { InstanceId = instanceId };
    internal override object? At(DateTime? value) => value;
    internal override DbType? TimeDbType => DbType.DateTime2;
    internal override DbType SmallIntDbType => DbType.Byte;
    internal override object? SmallInt(short? value) => value is { } number ? (byte)number : null;
    internal override bool NormalizeRunnableTime => false;
    internal override DateTime ReadTime(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    internal override int StatementBatchSize => StatementBatch;
    internal override int OwnershipReadBatchSize => 1000;
    internal override bool IsRetryableCommit(Exception error) => error is SqlException { Number: 1205 or 1222 };
    internal override async ValueTask<DbTransaction?> BeginTransactionAsync(DbConnection connection, Operation operation) =>
        operation is Operation.OutboxClaim or Operation.OutboxMark or Operation.Purge or Operation.Save or Operation.LeaseClaim or Operation.Append or Operation.InboxPage or Operation.Commit or Operation.Delete
            ? await connection.BeginTransactionAsync() : null;

    internal override async Task<DbConnection> OpenConnectionAsync(bool ownership = false)
    {
        var connection = new SqlConnection(ownership ? _ownershipConnectionString : _connectionString);
        await connection.OpenAsync();
        return connection;
    }

    internal override async Task<CancellationOutcome> RequestCancellationAsync(DbConnection connection,
        DbTransaction? transaction, string instanceId, string reasonJson, DateTime? now)
    {
        var clock = now == null ? "SYSUTCDATETIME()" : "@Now";
        var update = $"""
            UPDATE dbo.IxIFlowWorkflowInstances
            SET CancellationJson = COALESCE(CancellationJson, @ReasonJson),
                CancellationAcknowledged = CASE WHEN CancellationJson IS NULL
                    THEN 0 ELSE CancellationAcknowledged END,
                RunnableAtUtc = CASE WHEN CancellationJson IS NULL OR CancellationAcknowledged = 0
                    THEN {clock} ELSE RunnableAtUtc END
            WHERE InstanceId = @InstanceId AND Status IN ('Running', 'Suspended', 'NeedsResolution')
            """;
        var parameters = new DynamicParameters(new { InstanceId = instanceId, ReasonJson = reasonJson });
        if (now == null)
        {
            var changed = await connection.ExecuteAsync(update, parameters, transaction);
            return changed == 1 ? CancellationOutcome.Recorded : CancellationOutcome.Unchanged;
        }
        parameters.Add("Now", At(now), TimeDbType);
        var outcome = await connection.ExecuteScalarAsync<byte>(update + ";" + """

            DECLARE @Changed INT = @@ROWCOUNT;
            SELECT CAST(CASE
                WHEN @Changed = 1 AND EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances
                    WHERE InstanceId = @InstanceId AND CancellationAcknowledged = 0) THEN 1
                WHEN NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId) THEN 2
                ELSE 0 END AS TINYINT);
            """, parameters, transaction);
        return outcome switch
        {
            1 => CancellationOutcome.MadeRunnable,
            2 => CancellationOutcome.Gone,
            _ => CancellationOutcome.Unchanged
        };
    }

    // Six parameters per opened wait, plus shared values, stay below SQL Server's 2,100 limit.
    private const int StatementBatch = 300;

    internal override async Task InsertWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<OpenedWait> waits, long inboxSequence, DateTime now)
    {
        var parameters = WithTimes(new { InstanceId = instanceId, Sequence = inboxSequence }, ("Now", now));
        var rows = new List<string>();
        for (var index = 0; index < waits.Count; index++)
        {
            var wait = waits[index];
            rows.Add($"(@OW{index}, @OK{index}, @OY{index}, @OT{index}, @OD{index}, @OP{index})");
            parameters.Add($"OW{index}", wait.WaitId);
            parameters.Add($"OK{index}", (byte)wait.Kind);
            parameters.Add($"OY{index}", wait.Key);
            parameters.Add($"OT{index}", wait.EventType);
            parameters.Add($"OP{index}", wait.DefaultPayloadJson, DbType.String, size: -1);
            parameters.Add($"OD{index}", now + wait.Timeout, DbType.DateTime2);
        }
        await connection.ExecuteAsync($"""
                INSERT INTO dbo.IxIFlowWorkflowWaits
                    (InstanceId, WaitId, Kind, WaitKey, EventType, DeadlineUtc, OpenedSequence, OpenedAtUtc, DefaultPayloadJson)
                SELECT @InstanceId, opened.WaitId, opened.Kind, opened.WaitKey, opened.EventType, opened.DeadlineUtc,
                    @Sequence, @Now, opened.DefaultPayloadJson
                FROM (VALUES {string.Join(", ", rows)}) AS opened (WaitId, Kind, WaitKey, EventType, DeadlineUtc, DefaultPayloadJson)
                WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowWaits AS stored
                    WHERE stored.InstanceId = @InstanceId AND stored.WaitId = opened.WaitId);

                """, parameters, transaction);
    }

    internal override async Task ArmWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ArmedWait> waits, DateTime now)
    {
        var parameters = WithTimes(new { InstanceId = instanceId }, ("Now", now));
        var rows = new List<string>();
        for (var index = 0; index < waits.Count; index++)
        {
            rows.Add($"(@AW{index}, @AD{index}, @AP{index})");
            parameters.Add($"AW{index}", waits[index].WaitId);
            parameters.Add($"AD{index}", now + waits[index].Timeout, DbType.DateTime2);
            parameters.Add($"AP{index}", waits[index].DefaultPayloadJson, DbType.String, size: -1);
        }
        await connection.ExecuteAsync($"""
                UPDATE stored SET DeadlineUtc = COALESCE(stored.DeadlineUtc, armed.DeadlineUtc),
                    DefaultPayloadJson = COALESCE(stored.DefaultPayloadJson, armed.DefaultPayloadJson)
                FROM dbo.IxIFlowWorkflowWaits AS stored
                JOIN (VALUES {string.Join(", ", rows)}) AS armed (WaitId, DeadlineUtc, DefaultPayloadJson) ON stored.WaitId = armed.WaitId
                WHERE stored.InstanceId = @InstanceId AND stored.CloseReason IS NULL;

                """, parameters, transaction);
    }

    internal override async Task CloseWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ClosedWait> waits, DateTime now)
    {
        var parameters = WithTimes(new { InstanceId = instanceId }, ("Now", now));
        var rows = new List<string>();
        for (var index = 0; index < waits.Count; index++)
        {
            rows.Add($"(@CW{index}, @CR{index})");
            parameters.Add($"CW{index}", waits[index].WaitId);
            parameters.Add($"CR{index}", (byte)waits[index].Reason);
        }
        await connection.ExecuteAsync($"""
                UPDATE stored SET CloseReason = closed.Reason, ClosedAtUtc = @Now
                FROM dbo.IxIFlowWorkflowWaits AS stored
                JOIN (VALUES {string.Join(", ", rows)}) AS closed (WaitId, Reason) ON stored.WaitId = closed.WaitId
                WHERE stored.InstanceId = @InstanceId AND stored.CloseReason IS NULL;
                """, parameters, transaction);
    }

    internal override async Task InsertPartsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<CheckpointPart> parts)
    {
        var parameters = new DynamicParameters(new { InstanceId = instanceId });
        var rows = new List<string>();
        for (var index = 0; index < parts.Count; index++)
        {
            rows.Add($"(@InstanceId, @K{index}, @J{index})");
            parameters.Add($"K{index}", parts[index].Key);
            parameters.Add($"J{index}", parts[index].Json, DbType.String, size: -1);
        }
        await connection.ExecuteAsync($"""
                INSERT INTO dbo.IxIFlowWorkflowParts (InstanceId, PartKey, Json) VALUES {string.Join(", ", rows)}
                """, parameters, transaction);
    }

    internal override async Task AppendTraceAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<string> entries)
    {
        var last = await connection.ExecuteScalarAsync<long>("""
            SELECT ISNULL(MAX(Sequence), 0) FROM dbo.IxIFlowWorkflowTraces WHERE InstanceId = @InstanceId
            """, new { InstanceId = instanceId }, transaction);
        var offset = 0;
        foreach (var batch in entries.Chunk(StatementBatch))
        {
            var parameters = new DynamicParameters(new { InstanceId = instanceId });
            var rows = new List<string>();
            for (var index = 0; index < batch.Length; index++)
            {
                rows.Add($"(@InstanceId, @S{index}, @J{index})");
                parameters.Add($"S{index}", last + offset + index + 1);
                parameters.Add($"J{index}", batch[index]);
            }
            await connection.ExecuteAsync(
                $"INSERT INTO dbo.IxIFlowWorkflowTraces (InstanceId, Sequence, EntryJson) VALUES {string.Join(", ", rows)}",
                parameters, transaction);
            offset += batch.Length;
        }
    }

    internal override async Task<int> SettleAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<EventSettlement> settlements, DateTime now)
    {
        var parameters = new DynamicParameters(new { InstanceId = instanceId });
        var rows = new List<string>();
        for (var index = 0; index < settlements.Count; index++)
        {
            var settlement = settlements[index];
            rows.Add($"(@E{index}, @O{index}, @W{index}, @R{index})");
            parameters.Add($"E{index}", settlement.EventId);
            parameters.Add($"O{index}", (byte)settlement.Outcome);
            parameters.Add($"W{index}", settlement.WaitId);
            parameters.Add($"R{index}", (byte?)settlement.Rejection);
        }
        return await connection.ExecuteAsync($"""
                UPDATE stored
                SET Outcome = settled.Outcome, WaitId = settled.WaitId, Rejection = settled.Rejection,
                    SettledAtUtc = SYSUTCDATETIME()
                FROM dbo.IxIFlowWorkflowEvents AS stored
                JOIN (VALUES {string.Join(", ", rows)}) AS settled (EventId, Outcome, WaitId, Rejection)
                    ON stored.EventId = settled.EventId
                WHERE stored.InstanceId = @InstanceId AND stored.Outcome = 0
                """, parameters, transaction);

    }

    internal override async Task InsertChildrenAsync(DbConnection connection, DbTransaction transaction,
        string parentInstanceId, IReadOnlyList<ChildStart> children, DateTime now)
    {
        foreach (var start in children)
        {
            var child = start.Instance!;
            await connection.ExecuteAsync("""
                INSERT INTO dbo.IxIFlowWorkflowInstances
                    (InstanceId, Incarnation, WorkflowName, Status, CorrelationId, RunnableAtUtc, StateJson, Revision, ParentInstanceId)
                VALUES (@InstanceId, @Incarnation, @WorkflowName, @Status, @CorrelationId, @Now, @StateJson, 1, @ParentInstanceId)
                """, WithTimes(new
            {
                child.InstanceId,
                Incarnation = Guid.NewGuid(),
                child.WorkflowName,
                Status = child.Status.ToString(),
                child.CorrelationId,
                StateJson = start.Payload,
                ParentInstanceId = parentInstanceId
            }, ("Now", now)), transaction);
        }
    }

    internal override string ReadInstanceSql(string filter, bool wholly) => $"""
                SELECT {ServerRowColumns} FROM dbo.IxIFlowWorkflowInstances
                WHERE InstanceId = @InstanceId {filter};
                SELECT InstanceId, PartKey, Json FROM dbo.IxIFlowWorkflowParts
                WHERE InstanceId = @InstanceId {(wholly ? "" : "AND PartKey = @DataKey")};
                SELECT InstanceId, Revision, Incarnation FROM dbo.IxIFlowWorkflowInstances
                WHERE InstanceId = @InstanceId;
                """;

    internal override string QuerySql(string filter) => $"SELECT {ServerRowColumns} FROM dbo.IxIFlowWorkflowInstances WHERE {filter}";

    internal override string? Sql(Statement statement) => statement switch
    {
        Statement.OutboxClock => """
            SELECT SYSUTCDATETIME()
            """,
        Statement.PublisherLock => """
            DECLARE @r INT;
            EXEC @r = sys.sp_getapplock @Resource=N'IxIFlow.OutboxPublishers', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
            IF @r < 0 THROW 51000, 'Could not lock outbox publishers', 1;
            """,
        Statement.PublisherSharedLock => """
            DECLARE @r INT;
            EXEC @r = sys.sp_getapplock @Resource=N'IxIFlow.OutboxPublishers', @LockMode=N'Shared', @LockOwner=N'Transaction', @LockTimeout=30000;
            IF @r < 0 THROW 51000, 'Could not lock outbox publishers', 1;
            """,
        Statement.PublisherUpsert => """
            UPDATE dbo.IxIFlowOutboxPublishers
            SET TypesJson=@TypesJson
            WHERE Name=@Name;
            IF @@ROWCOUNT=0 INSERT INTO dbo.IxIFlowOutboxPublishers (Name, TypesJson)
            VALUES (@Name, @TypesJson);
            """,
        Statement.Publishers => """
            SELECT Name AS Name, TypesJson AS TypesJson, DisabledAtUtc AS DisabledAtUtc, LastError AS LastError
            FROM dbo.IxIFlowOutboxPublishers
            """,
        Statement.PublisherRead => """
            SELECT Name AS Name, TypesJson AS TypesJson, DisabledAtUtc AS DisabledAtUtc, LastError AS LastError
            FROM dbo.IxIFlowOutboxPublishers
            WHERE Name=@Name
            """,
        Statement.BackfillDeliveries => """
            INSERT INTO dbo.IxIFlowOutboxDeliveries (EventId, PublisherName, Attempts, NextAttemptAtUtc, Status, LastError)
            SELECT e.EventId, @Name, 0, @Now, CASE WHEN p.DisabledAtUtc IS NULL THEN 0 ELSE 2 END,
                p.LastError
            FROM dbo.IxIFlowOutboxEvents e
            CROSS JOIN dbo.IxIFlowOutboxPublishers p
            WHERE p.Name=@Name
              AND e.EventType=@EventType
              AND NOT EXISTS (SELECT 1
            FROM dbo.IxIFlowOutboxDeliveries d
            WHERE d.EventId=e.EventId
              AND d.PublisherName=@Name)
            """,
        Statement.InsertOutboxEvent => """
            INSERT INTO dbo.IxIFlowOutboxEvents (EventId, InstanceId, Incarnation, EventType, EventJson, OccurredAtUtc)
            SELECT @EventId, @InstanceId,
                instance.Incarnation, @EventType, @EventJson, @Now
            FROM dbo.IxIFlowWorkflowInstances instance
            WHERE instance.InstanceId=@InstanceId
              AND NOT EXISTS (SELECT 1
            FROM dbo.IxIFlowOutboxEvents event
            WHERE event.InstanceId=@InstanceId
              AND event.Incarnation=instance.Incarnation
              AND event.EventType=@EventType)
            """,
        Statement.InsertDelivery => """
            INSERT INTO dbo.IxIFlowOutboxDeliveries (EventId, PublisherName, Attempts, NextAttemptAtUtc, Status, LastError)
            VALUES (@EventId, @Name, 0, @Now, @Status, @LastError)
            """,
        Statement.ClaimDeliveries => """
            SELECT TOP (@Limit) d.EventId AS EventId,
                d.PublisherName AS PublisherName,
                d.Attempts AS Attempts,
                d.FirstAttemptAtUtc AS FirstAttemptAtUtc,
                d.NextAttemptAtUtc AS NextAttemptAtUtc,
                d.Status AS Status,
                d.LastError AS LastError,
                d.LeaseToken AS LeaseToken,
                d.LeaseUntilUtc AS LeaseUntilUtc,
                e.EventJson AS EventJson
            FROM dbo.IxIFlowOutboxDeliveries d WITH (UPDLOCK, READPAST, ROWLOCK)
            JOIN dbo.IxIFlowOutboxEvents e ON e.EventId=d.EventId
            JOIN dbo.IxIFlowOutboxPublishers p ON p.Name=d.PublisherName
            WHERE d.PublisherName=@Name
              AND p.DisabledAtUtc IS NULL
              AND d.Status=0
              AND d.NextAttemptAtUtc<=SYSUTCDATETIME()
              AND (d.LeaseUntilUtc IS NULL OR d.LeaseUntilUtc<=SYSUTCDATETIME())
            ORDER BY d.NextAttemptAtUtc,
                d.EventId
            """,
        Statement.DeliveryLock => """
            SELECT d.EventId AS EventId,
                d.PublisherName AS PublisherName,
                d.Attempts AS Attempts,
                d.FirstAttemptAtUtc AS FirstAttemptAtUtc,
                d.NextAttemptAtUtc AS NextAttemptAtUtc,
                d.Status AS Status,
                d.LastError AS LastError,
                d.LeaseToken AS LeaseToken,
                d.LeaseUntilUtc AS LeaseUntilUtc,
                e.EventJson AS EventJson
            FROM dbo.IxIFlowOutboxDeliveries d WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
            JOIN dbo.IxIFlowOutboxEvents e ON e.EventId=d.EventId
            WHERE d.EventId=@EventId
              AND d.PublisherName=@Name
            """,
        Statement.DeliveryRead => """
            SELECT d.EventId AS EventId,
                d.PublisherName AS PublisherName,
                d.Attempts AS Attempts,
                d.FirstAttemptAtUtc AS FirstAttemptAtUtc,
                d.NextAttemptAtUtc AS NextAttemptAtUtc,
                d.Status AS Status,
                d.LastError AS LastError,
                d.LeaseToken AS LeaseToken,
                d.LeaseUntilUtc AS LeaseUntilUtc,
                e.EventJson AS EventJson
            FROM dbo.IxIFlowOutboxDeliveries d
            JOIN dbo.IxIFlowOutboxEvents e ON e.EventId=d.EventId
            WHERE d.EventId=@EventId
              AND d.PublisherName=@Name
            """,
        Statement.DeliveryUpdate => """
            UPDATE dbo.IxIFlowOutboxDeliveries
            SET FirstAttemptAtUtc=@FirstAttemptAtUtc, Attempts=@Attempts, NextAttemptAtUtc=@NextAttemptAtUtc, Status=@Status, LastError=@LastError, LeaseToken=@LeaseToken, LeaseUntilUtc=@LeaseUntilUtc
            WHERE EventId=@EventId
              AND PublisherName=@Name
            """,
        Statement.UnregisterPublisher => """
            UPDATE dbo.IxIFlowOutboxDeliveries
            SET Status=2, LeaseToken=NULL, LeaseUntilUtc=NULL
            WHERE PublisherName=@Name AND Status=0;
            DELETE FROM dbo.IxIFlowOutboxPublishers WHERE Name=@Name;
            """,
        Statement.DisablePublisher => """
            UPDATE dbo.IxIFlowOutboxPublishers
            SET DisabledAtUtc=COALESCE(DisabledAtUtc, @Now), LastError=@Error
            WHERE Name=@Name;
            UPDATE dbo.IxIFlowOutboxDeliveries
            SET Status=2, LastError=@Error, LeaseToken=NULL, LeaseUntilUtc=NULL
            WHERE PublisherName=@Name
              AND Status=0;
            """,
        Statement.Cancellation => """
            SELECT CancellationJson FROM dbo.IxIFlowWorkflowInstances
            WHERE InstanceId = @InstanceId
            """,
        Statement.Cancellations => """
                SELECT InstanceId, CancellationJson FROM dbo.IxIFlowWorkflowInstances
                WHERE InstanceId IN @InstanceIds AND CancellationJson IS NOT NULL
                """,
        Statement.Runnable => """
            SELECT TOP (@Limit) instance.InstanceId, instance.RunnableAtUtc
            FROM dbo.IxIFlowWorkflowInstances AS instance
            LEFT JOIN dbo.IxIFlowWorkflowLeases AS lease
                ON lease.InstanceId = instance.InstanceId
            WHERE instance.RunnableAtUtc <= SYSUTCDATETIME()
              AND (lease.InstanceId IS NULL OR lease.ExpiresAtUtc <= SYSUTCDATETIME())
            ORDER BY instance.RunnableAtUtc, instance.InstanceId
            """,
        // The instance row is locked before the lease row, in the same order as a commit. No range
        // lock: a missing row is not a running instance, and a range lock would block other claims.
        // Lock terminal rows too, so a claim reads any tombstone after concurrent deletion finishes.
        Statement.Running => """
            SELECT COUNT(CASE WHEN Status = 'Running' THEN 1 END)
            FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE InstanceId = @InstanceId
            """,
        Statement.ClaimLease => """
            -- Lock the lease before allocating so a waiting claim cannot install an older epoch.
            DECLARE @Epoch BIGINT;
            SELECT @Epoch = Epoch FROM dbo.IxIFlowWorkflowLeases WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId;
            SET @Epoch = NEXT VALUE FOR dbo.IxIFlowExecutionEpoch;
            MERGE dbo.IxIFlowWorkflowLeases WITH (UPDLOCK, HOLDLOCK) AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED AND target.ExpiresAtUtc <= SYSUTCDATETIME() THEN
                UPDATE SET Token = @Token,
                    ExpiresAtUtc = DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME()),
                    Epoch = @Epoch,
                    CrashCount = target.CrashCount + CASE
                        WHEN @Running = 1 AND target.ExpiresAtUtc > CAST('0001-01-01' AS DATETIME2) THEN 1
                        ELSE 0 END
            WHEN NOT MATCHED THEN
                INSERT (InstanceId, Token, ExpiresAtUtc, Epoch, CrashCount)
                VALUES (@InstanceId, @Token,
                    DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME()), @Epoch, 0)
            OUTPUT inserted.Epoch, inserted.CrashCount;
            """,
        Statement.RenewLease => """
            UPDATE dbo.IxIFlowWorkflowLeases
            SET ExpiresAtUtc = DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME())
            WHERE InstanceId = @InstanceId AND Token = @Token
                AND ExpiresAtUtc > SYSUTCDATETIME()
            """,
        Statement.ReleaseLease => """
            UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc = CAST('0001-01-01' AS DATETIME2)
            WHERE InstanceId = @InstanceId AND Token = @Token AND ExpiresAtUtc > SYSUTCDATETIME()
            """,
        // The instance row is locked first, in the same order as a commit, so the waits read below are
        // exactly those committed before this event arrives. The event is received at the time read once
        // that lock is held, which orders it against every read of the inbox (D-35); a statement reads
        // its time when it starts, so the time is read by the statement after the lock.
        Statement.LockInbox => """
            SELECT Status, InboxSequence,
                CAST(COALESCE(JSON_VALUE(StateJson, '$.TracingEnabled'), 'true') AS bit) AS TracingEnabled
            FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE InstanceId = @InstanceId;
            SELECT SYSUTCDATETIME();
            """,
        // Only row locks here: a range lock on a missing parent can block unrelated child commits.
        Statement.ParentInbox => """
                SELECT Status, InboxSequence FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
                WHERE InstanceId = @InstanceId
                """,
        // A first commit must exclude another insertion of this ID. Read the clock in the next
        // statement, after any lock wait, so the captured commit time cannot predate the lock (D-35).
        Statement.CommitState => """
            SELECT Revision, Incarnation, CancellationJson, CancellationAcknowledged, InboxSequence
            FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId;
            SELECT SYSUTCDATETIME();
            """,
        Statement.Receipt => """
            SELECT ExpectedRevision, Revision, PayloadHash FROM dbo.IxIFlowWorkflowCommits
            WHERE InstanceId = @InstanceId AND CommitId = @CommitId
            """,
        Statement.Lease => """
            SELECT Epoch, CrashCount,
                CAST(CASE WHEN ExpiresAtUtc > SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT) AS IsActive
            FROM dbo.IxIFlowWorkflowLeases WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId
            """,
        Statement.TakenChild => """
                SELECT TOP (1) InstanceId FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId IN @Ids
                """,
        Statement.EventHash => """
            SELECT Sequence, PayloadHash FROM dbo.IxIFlowWorkflowEvents
            WHERE InstanceId = @InstanceId AND EventId = @EventId
            """,
        Statement.CandidateWait => $"""
            SELECT TOP (1) {ServerWaitColumns} FROM dbo.IxIFlowWorkflowWaits
            WHERE InstanceId = @InstanceId AND (@TargetWaitId IS NOT NULL AND WaitId = @TargetWaitId
                OR @TargetWaitId IS NULL AND CloseReason IS NULL
                    AND Kind = @Event AND (@Key IS NULL OR WaitKey = @Key))
            ORDER BY CASE WHEN DeadlineUtc <= @Now THEN 1 ELSE 0 END
            """,
        Statement.InsertEvent => """
            IF @Rejection IS NULL
                UPDATE dbo.IxIFlowWorkflowInstances
                SET InboxSequence = @Sequence,
                    RunnableAtUtc = CASE WHEN @MadeRunnable = 1 THEN @Now ELSE RunnableAtUtc END
                WHERE InstanceId = @InstanceId;
            INSERT INTO dbo.IxIFlowWorkflowEvents
                (InstanceId, EventId, Sequence, EventType, EventKey, TargetWaitId, Subject, PayloadJson,
                 PayloadHash, ReceivedAtUtc, Outcome, Rejection, SettledAtUtc)
            VALUES (@InstanceId, @EventId, @Sequence, @EventType, @Key, @TargetWaitId, @Subject, @PayloadJson,
                @Hash, @Now, CASE WHEN @Rejection IS NULL THEN 0 ELSE 2 END, @Rejection,
                CASE WHEN @Rejection IS NULL THEN NULL ELSE @Now END);
            """,
        // The instance row is locked as appends and commits lock it, so the page is read between them:
        // the inbox sequence and the store's time bound the page, and every event received before that
        // time is on it (D-35). The open waits are read after the sequence: a wait opened in between
        // carries a sequence at least as high as every event on the page, so none of them can satisfy it.
        Statement.LockAndReadPage => $"""
            DECLARE @Through BIGINT = 0, @Now DATETIME2 = SYSUTCDATETIME();
            SELECT @Through = InboxSequence
            FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK) WHERE InstanceId = @InstanceId;
            SET @Now = SYSUTCDATETIME();
            SELECT @Through AS Through, @Now AS Now;
            SELECT TOP (@Limit) {ServerEventColumns}
            FROM dbo.IxIFlowWorkflowEvents
            WHERE InstanceId = @InstanceId AND Outcome = 0 AND Sequence > @AfterSequence AND Sequence <= @Through
            ORDER BY Sequence;
            SELECT WaitId, OpenedSequence, DeadlineUtc FROM dbo.IxIFlowWorkflowWaits
            WHERE InstanceId = @InstanceId AND CloseReason IS NULL;
            """,
        Statement.Event => $"""
            SELECT {ServerEventColumns}
            FROM dbo.IxIFlowWorkflowEvents WHERE InstanceId = @InstanceId AND EventId = @EventId
            """,
        Statement.InboxSequences => """
                         SELECT InstanceId, InboxSequence FROM dbo.IxIFlowWorkflowInstances
                         WHERE InstanceId IN @InstanceIds
                         """,
        Statement.Wait => $"""
            SELECT {ServerWaitColumns} FROM dbo.IxIFlowWorkflowWaits
            WHERE InstanceId = @InstanceId AND WaitId = @WaitId
            """,
        Statement.OpenWaits => $"""
            SELECT TOP (@Limit) {ServerWaitColumns} FROM dbo.IxIFlowWorkflowWaits
            WHERE CloseReason IS NULL
              AND (@InstanceId IS NULL OR InstanceId = @InstanceId)
              AND (@Key IS NULL OR WaitKey = @Key)
              AND (@EventType IS NULL OR EventType = @EventType)
              AND (@Kind IS NULL AND Kind NOT IN (@Delay, @Execution) OR Kind = @Kind)
              AND (@AfterWaitId IS NULL OR WaitId > @AfterWaitId)
            ORDER BY WaitId
            """,
        Statement.EarliestDeadline => """
                SELECT MIN(DeadlineUtc) FROM dbo.IxIFlowWorkflowWaits
                WHERE InstanceId = @InstanceId AND CloseReason IS NULL
                """,
        Statement.InsertCommit or Statement.UpdateCommit => """
            MERGE dbo.IxIFlowWorkflowInstances AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED THEN UPDATE SET
                Incarnation = CASE WHEN @ExpectedRevision = 0 THEN @Incarnation ELSE target.Incarnation END,
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId, ParentInstanceId = @ParentInstanceId,
                RunnableAtUtc = @RunnableAtUtc,
                InboxReadThrough = COALESCE(@InboxReadThrough, target.InboxReadThrough),
                StateJson = @StateJson, Revision = @Revision,
                CancellationAcknowledged = CASE WHEN @AcknowledgesCancellation = 1 AND
                    target.CancellationJson IS NOT NULL THEN 1 ELSE target.CancellationAcknowledged END
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, Incarnation, WorkflowName, Status, CorrelationId, ParentInstanceId, RunnableAtUtc, StateJson, Revision)
                VALUES (@InstanceId, @Incarnation, @WorkflowName, @Status, @CorrelationId, @ParentInstanceId, @RunnableAtUtc, @StateJson,
                        @Revision);
            INSERT INTO dbo.IxIFlowWorkflowCommits (InstanceId, CommitId, ExpectedRevision, Revision, PayloadHash)
            VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
            """,
        Statement.FinishInbox => """
                UPDATE dbo.IxIFlowWorkflowEvents SET Outcome = 4, SettledAtUtc = SYSUTCDATETIME()
                WHERE InstanceId = @InstanceId AND Outcome = 0;
                UPDATE dbo.IxIFlowWorkflowWaits SET CloseReason = @Reason, ClosedAtUtc = @Now
                WHERE InstanceId = @InstanceId AND CloseReason IS NULL;
                """,
        Statement.UpdateLease => """
                UPDATE dbo.IxIFlowWorkflowLeases
                SET ExpiresAtUtc = CASE WHEN @Released = 1 THEN CAST('0001-01-01' AS DATETIME2)
                        ELSE ExpiresAtUtc END,
                    CrashCount = CASE WHEN @ClearsCrashCount = 1 THEN 0 ELSE CrashCount END
                WHERE InstanceId = @InstanceId
                """,
        Statement.Save => """
            DELETE FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId
                AND EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId AND Revision = 0);
            MERGE dbo.IxIFlowWorkflowInstances WITH (HOLDLOCK) AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED AND target.Revision = 0 THEN UPDATE SET
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId, ParentInstanceId = @ParentInstanceId,
                RunnableAtUtc = CASE WHEN @RunnableNow = 1 THEN SYSUTCDATETIME() ELSE @RunnableAtUtc END,
                StateJson = @StateJson
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, Incarnation, WorkflowName, Status, CorrelationId, ParentInstanceId, RunnableAtUtc, StateJson)
                VALUES (@InstanceId, @Incarnation, @WorkflowName, @Status, @CorrelationId, @ParentInstanceId,
                        CASE WHEN @RunnableNow = 1 THEN SYSUTCDATETIME() ELSE @RunnableAtUtc END, @StateJson)
            OUTPUT CAST(CASE WHEN inserted.RunnableAtUtc <= SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT);
            """,
        Statement.ReadParts => """
                SELECT InstanceId, PartKey, Json FROM dbo.IxIFlowWorkflowParts WHERE InstanceId IN @Ids;
                SELECT InstanceId, Revision, Incarnation FROM dbo.IxIFlowWorkflowInstances
                WHERE InstanceId IN @Ids;
                """,
        Statement.DeleteState => """
            SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId
            """,
        Statement.DeleteParentInbox => """
                SELECT Status, InboxSequence FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
                WHERE InstanceId = @InstanceId;
                SELECT SYSUTCDATETIME();
                """,
        // The instance row is locked first, as commits and appends lock it, and its rows in the other
        // tables are deleted by key. A cascading delete would take range locks in those tables, which
        // deadlock with commits of other instances inserting into the same ranges.
        Statement.DeleteRows => """
            SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowOutboxDeliveries WHERE EventId IN (SELECT e.EventId FROM dbo.IxIFlowOutboxEvents e WHERE e.InstanceId=@InstanceId AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowOutboxDeliveries pending WHERE pending.EventId=e.EventId AND pending.Status=0));
            DELETE FROM dbo.IxIFlowOutboxEvents WHERE InstanceId=@InstanceId AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowOutboxDeliveries pending WHERE pending.EventId=dbo.IxIFlowOutboxEvents.EventId);
            DELETE FROM dbo.IxIFlowWorkflowWaits WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowTraces WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowEvents WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowCommits WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId;
            """,
        Statement.Trace => """
            SELECT TOP (@Limit) Sequence, EntryJson FROM dbo.IxIFlowWorkflowTraces
            WHERE InstanceId = @InstanceId AND Sequence > @AfterSequence
            ORDER BY Sequence
            """,
        Statement.DeleteLease => "DELETE FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId = @InstanceId",
        Statement.ByName => "WorkflowName = @Value",
        Statement.ByStatus => "Status = @Value",
        Statement.ByCorrelation => "CorrelationId = @Value",
        Statement.ByParent => "ParentInstanceId = @Value",
        Statement.DeleteAllParts => "DELETE FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId",
        Statement.DeleteParts => """
                    DELETE FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId AND PartKey IN @Keys
                    """,
        _ => RetentionSql(statement)
    };

    internal override async Task CreateSchemaAsync(DbConnection connection)
    {
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
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                WorkflowName NVARCHAR(200) NOT NULL,
                Status NVARCHAR(50) NOT NULL,
                CorrelationId NVARCHAR(100) NULL,
                RunnableAtUtc DATETIME2 NULL,
                EndedAtUtc DATETIME2 NULL,
                StateJson NVARCHAR(MAX) NOT NULL,
                CancellationJson NVARCHAR(MAX) NULL,
                CancellationAcknowledged BIT NOT NULL DEFAULT 0,
                Revision BIGINT NOT NULL DEFAULT 0,
                InboxSequence BIGINT NOT NULL CONSTRAINT DF_IxIFlowWorkflowInstances_InboxSequence DEFAULT 0,
                InboxReadThrough BIGINT NOT NULL CONSTRAINT DF_IxIFlowWorkflowInstances_InboxReadThrough DEFAULT 0,
                Incarnation UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_IxIFlowWorkflowInstances_Incarnation DEFAULT NEWID(),
                ParentInstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL
            );
            CREATE INDEX IX_IxIFlowWorkflowInstances_Status
                ON dbo.IxIFlowWorkflowInstances (Status);
            CREATE INDEX IX_IxIFlowWorkflowInstances_WorkflowName
                ON dbo.IxIFlowWorkflowInstances (WorkflowName);
            CREATE INDEX IX_IxIFlowWorkflowInstances_CorrelationId
                ON dbo.IxIFlowWorkflowInstances (CorrelationId);
            EXEC(N'CREATE INDEX IX_IxIFlowWorkflowInstances_Parent
                ON dbo.IxIFlowWorkflowInstances (ParentInstanceId) WHERE ParentInstanceId IS NOT NULL');
            EXEC(N'CREATE INDEX IX_IxIFlowWorkflowInstances_Runnable
                ON dbo.IxIFlowWorkflowInstances (RunnableAtUtc, InstanceId)
                WHERE RunnableAtUtc IS NOT NULL');
            EXEC(N'CREATE INDEX IX_IxIFlowInstances_Ended
                ON dbo.IxIFlowWorkflowInstances (EndedAtUtc, InstanceId) WHERE EndedAtUtc IS NOT NULL');
        END
        IF OBJECT_ID(N'dbo.IxIFlowWorkflowEvents', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.IxIFlowWorkflowEvents (
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                EventId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Sequence BIGINT NOT NULL,
                EventType NVARCHAR(400) NOT NULL,
                EventKey NVARCHAR(400) NULL,
                TargetWaitId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
                Subject NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
                PayloadJson NVARCHAR(MAX) NOT NULL,
                PayloadHash BINARY(32) NOT NULL,
                ReceivedAtUtc DATETIME2 NOT NULL,
                Outcome TINYINT NOT NULL,
                WaitId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
                Rejection TINYINT NULL,
                SettledAtUtc DATETIME2 NULL,
                CONSTRAINT PK_IxIFlowWorkflowEvents PRIMARY KEY (InstanceId, EventId)
            );
            EXEC(N'CREATE INDEX IX_IxIFlowWorkflowEvents_Pending
                ON dbo.IxIFlowWorkflowEvents (InstanceId, Sequence) WHERE Outcome = 0');
        END
        IF OBJECT_ID(N'dbo.IxIFlowWorkflowParts', N'U') IS NULL
            CREATE TABLE dbo.IxIFlowWorkflowParts (
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PartKey NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Json NVARCHAR(MAX) NOT NULL,
                CONSTRAINT PK_IxIFlowWorkflowParts PRIMARY KEY (InstanceId, PartKey)
            );
        IF OBJECT_ID(N'dbo.IxIFlowWorkflowTraces', N'U') IS NULL
            CREATE TABLE dbo.IxIFlowWorkflowTraces (
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Sequence BIGINT NOT NULL,
                EntryJson NVARCHAR(MAX) NOT NULL,
                CONSTRAINT PK_IxIFlowWorkflowTraces PRIMARY KEY (InstanceId, Sequence)
            );
        IF OBJECT_ID(N'dbo.IxIFlowWorkflowWaits', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.IxIFlowWorkflowWaits (
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                WaitId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Kind TINYINT NOT NULL,
                WaitKey NVARCHAR(400) NOT NULL,
                EventType NVARCHAR(400) NOT NULL,
                DefaultPayloadJson NVARCHAR(MAX) NULL,
                DeadlineUtc DATETIME2 NULL,
                OpenedSequence BIGINT NOT NULL,
                OpenedAtUtc DATETIME2 NOT NULL,
                CloseReason TINYINT NULL,
                ClosedAtUtc DATETIME2 NULL,
                CONSTRAINT PK_IxIFlowWorkflowWaits PRIMARY KEY (InstanceId, WaitId)
            );
            EXEC(N'CREATE INDEX IX_IxIFlowWorkflowWaits_Open
                ON dbo.IxIFlowWorkflowWaits (WaitKey, EventType, WaitId) WHERE CloseReason IS NULL');
        END
        IF OBJECT_ID(N'dbo.IxIFlowWorkflowCommits', N'U') IS NULL
            CREATE TABLE dbo.IxIFlowWorkflowCommits (
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommitId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ExpectedRevision BIGINT NOT NULL,
                Revision BIGINT NOT NULL,
                PayloadHash BINARY(32) NOT NULL,
                CONSTRAINT PK_IxIFlowWorkflowCommits PRIMARY KEY (InstanceId, CommitId)
            );
        IF OBJECT_ID(N'dbo.IxIFlowWorkflowLeases', N'U') IS NULL
        BEGIN
            CREATE SEQUENCE dbo.IxIFlowExecutionEpoch AS BIGINT
                START WITH 1 INCREMENT BY 1 NO CYCLE;
            CREATE TABLE dbo.IxIFlowWorkflowLeases (
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                Token NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ExpiresAtUtc DATETIME2 NOT NULL,
                Epoch BIGINT NOT NULL,
                CrashCount INT NOT NULL CONSTRAINT DF_IxIFlowWorkflowLeases_CrashCount DEFAULT 0
            );
            CREATE INDEX IX_IxIFlowWorkflowLeases_ExpiresAt
                ON dbo.IxIFlowWorkflowLeases (ExpiresAtUtc);
        END
        """, transaction: transaction);
    await connection.ExecuteAsync(OutboxSchema, transaction: transaction);
    await connection.ExecuteAsync(RetentionSchema, transaction: transaction);
    await transaction.CommitAsync();
    }
}
