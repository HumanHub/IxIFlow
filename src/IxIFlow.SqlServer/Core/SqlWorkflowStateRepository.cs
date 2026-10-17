using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Core;

/// <summary>
/// Stores workflow instances in SQL Server so host processes share the same state.
/// </summary>
public sealed class SqlWorkflowStateRepository : IWorkflowStateRepository, IWorkflowCompletionOutbox, IWorkflowInbox
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public async Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<StoredRow>($"""
            SELECT {RowColumns} FROM dbo.IxIFlowWorkflowInstances
            WHERE InstanceId = @InstanceId {UnpublishedCompletion}
            """, new { InstanceId = instanceId });
        return row == null ? null
            : (await AssembleAsync(connection, [row], UnpublishedCompletion, null)).SingleOrDefault();
    }

    public async Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync()
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<StoredRow>($"""
            SELECT TOP (100) {RowColumns} FROM dbo.IxIFlowWorkflowInstances
            WHERE 1 = 1 {UnpublishedCompletion}
            ORDER BY InstanceId
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
        var rows = await connection.QueryAsync<StoredRow>("""
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
            OUTPUT inserted.InstanceId, inserted.StateJson, inserted.RunnableAtUtc, inserted.Revision,
                CAST(inserted.RowStamp AS BIGINT) AS Stamp
            FROM dbo.IxIFlowWorkflowInstances AS instance
            INNER JOIN pending ON pending.InstanceId = instance.InstanceId;
            """, new { InstanceId = instanceId, Token = token,
                Milliseconds = (int)Math.Ceiling(duration.TotalMilliseconds) });
        // An instance read again is still one this claim holds.
        return await AssembleAsync(connection, rows.ToArray(),
            UnpublishedCompletion + " AND CompletionClaimToken = @Token", new { Token = token });
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

    public const int DefaultOwnershipPoolSize = 16;

    /// <summary>
    /// Creates a store on <paramref name="connectionString"/>. Lease and cancellation traffic uses a
    /// second connection pool of at most <paramref name="ownershipPoolSize"/> connections, so a host
    /// whose runs and the application's own work exhaust the main pool still renews its leases and
    /// delivers cancellation (B-09). With pooling turned off the reservation has no effect.
    /// </summary>
    public SqlWorkflowStateRepository(string connectionString, int ownershipPoolSize = DefaultOwnershipPoolSize)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A SQL Server connection string is required", nameof(connectionString))
            : connectionString;
        ArgumentOutOfRangeException.ThrowIfLessThan(ownershipPoolSize, 1);
        // A different application name keeps a separate pool in the client; names are at most 128.
        var ownership = new SqlConnectionStringBuilder(connectionString);
        const string suffix = " IxIFlow ownership";
        var name = ownership.ApplicationName ?? "";
        ownership.ApplicationName =
            (name.Length > 128 - suffix.Length ? name[..(128 - suffix.Length)] : name) + suffix;
        ownership.MaxPoolSize = ownershipPoolSize;
        ownership.MinPoolSize = Math.Min(ownership.MinPoolSize, ownershipPoolSize);
        _ownershipConnectionString = ownership.ConnectionString;
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

    private readonly string _ownershipConnectionString;

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
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.IxIFlowWorkflowInstances
            SET CancellationJson = COALESCE(CancellationJson, @ReasonJson),
                CancellationAcknowledged = CASE WHEN CancellationJson IS NULL
                    THEN 0 ELSE CancellationAcknowledged END,
                RunnableAtUtc = CASE WHEN CancellationJson IS NULL OR CancellationAcknowledged = 0
                    THEN SYSUTCDATETIME() ELSE RunnableAtUtc END
            WHERE InstanceId = @InstanceId AND Status IN ('Running', 'Suspended', 'NeedsResolution')
            """, new { InstanceId = instanceId, ReasonJson = JsonSerializer.Serialize(reason) });
        return changed == 1;
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
            SELECT CancellationJson FROM dbo.IxIFlowWorkflowInstances
            WHERE InstanceId = @InstanceId
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
        // SQL Server allows about 2,100 parameters per statement.
        foreach (var batch in instanceIds.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var rows = await connection.QueryAsync<(string InstanceId, string CancellationJson)>("""
                SELECT InstanceId, CancellationJson FROM dbo.IxIFlowWorkflowInstances
                WHERE InstanceId IN @InstanceIds AND CancellationJson IS NOT NULL
                """, new { InstanceIds = batch });
            // One unreadable request does not hide the others.
            foreach (var row in rows)
                if (ReadReason(row.CancellationJson) is { } reason)
                    requests[row.InstanceId] = reason;
        }
        return requests;
    }

    public async Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<RunnableRow>("""
            SELECT TOP (@Limit) instance.InstanceId, instance.RunnableAtUtc
            FROM dbo.IxIFlowWorkflowInstances AS instance
            LEFT JOIN dbo.IxIFlowWorkflowLeases AS lease
                ON lease.InstanceId = instance.InstanceId
            WHERE instance.RunnableAtUtc <= SYSUTCDATETIME()
              AND (lease.InstanceId IS NULL OR lease.ExpiresAtUtc <= SYSUTCDATETIME())
            ORDER BY instance.RunnableAtUtc, instance.InstanceId
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        // The instance row is locked before the lease row, in the same order as a commit. No range
        // lock: a missing row is not a running instance, and a range lock would block other claims.
        var running = await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE InstanceId = @InstanceId AND Status = 'Running'
            """, new { InstanceId = instanceId }, transaction) > 0;
        // The lease row outlives its lease, so a later owner always gets a higher epoch. A lease that
        // expired without release while the instance was running lost work in flight: one more crash.
        var grant = await connection.QuerySingleOrDefaultAsync<LeaseGrantRow>("""
            MERGE dbo.IxIFlowWorkflowLeases WITH (UPDLOCK, HOLDLOCK) AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED AND target.ExpiresAtUtc <= SYSUTCDATETIME() THEN
                UPDATE SET Token = @Token,
                    ExpiresAtUtc = DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME()),
                    Epoch = target.Epoch + 1,
                    CrashCount = target.CrashCount + CASE
                        WHEN @Running = 1 AND target.ExpiresAtUtc > CAST('0001-01-01' AS DATETIME2) THEN 1
                        ELSE 0 END
            WHEN NOT MATCHED THEN
                INSERT (InstanceId, Token, ExpiresAtUtc, Epoch, CrashCount)
                VALUES (@InstanceId, @Token,
                    DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME()), 1, 0)
            OUTPUT inserted.Epoch, inserted.CrashCount;
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
            UPDATE dbo.IxIFlowWorkflowLeases
            SET ExpiresAtUtc = DATEADD(MILLISECOND, @Milliseconds, SYSUTCDATETIME())
            WHERE InstanceId = @InstanceId AND Token = @Token
                AND ExpiresAtUtc > SYSUTCDATETIME()
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        // The instance row is locked first, in the same order as a commit, so the waits read below are
        // exactly those committed before this event arrives. The event is received at the time read once
        // that lock is held, which orders it against every read of the inbox (D-35); a statement reads
        // its time when it starts, so the time is read by the statement after the lock.
        InboxRow? instance;
        DateTime lockedAt;
        using (var locked = await connection.QueryMultipleAsync("""
            SELECT Status, InboxSequence FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE InstanceId = @InstanceId;
            SELECT SYSUTCDATETIME();
            """, new { workflowEvent.InstanceId }, transaction))
        {
            instance = await locked.ReadSingleOrDefaultAsync<InboxRow>();
            lockedAt = await locked.ReadSingleAsync<DateTime>();
        }
        var result = await AppendAsync(connection, transaction, workflowEvent, hash, instance,
            DateTime.SpecifyKind(lockedAt, DateTimeKind.Utc));
        await transaction.CommitAsync();
        return result;
    }

    // Appends an event to an instance whose row this transaction has locked, read as instance; null when
    // it does not exist.
    private static async Task<EventAppendResult> AppendAsync(SqlConnection connection, SqlTransaction transaction,
        WorkflowEvent workflowEvent, byte[] hash, InboxRow? instance, DateTime now)
    {
        // A known event ID answers the same way whatever happened to the instance since.
        var existing = await connection.QuerySingleOrDefaultAsync<EventHashRow>("""
            SELECT Sequence, PayloadHash FROM dbo.IxIFlowWorkflowEvents
            WHERE InstanceId = @InstanceId AND EventId = @EventId
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
            SELECT TOP (1) {WaitColumns} FROM dbo.IxIFlowWorkflowWaits
            WHERE InstanceId = @InstanceId AND (@TargetWaitId IS NOT NULL AND WaitId = @TargetWaitId
                OR @TargetWaitId IS NULL AND CloseReason IS NULL
                    AND Kind = @Event AND (@Key IS NULL OR WaitKey = @Key))
            ORDER BY CASE WHEN DeadlineUtc <= @Now THEN 1 ELSE 0 END
            """, WithTimes(new
        {
            workflowEvent.InstanceId, workflowEvent.TargetWaitId, workflowEvent.Key, Event = (byte)WaitKind.Event
        }, ("Now", now)), transaction)).Select(row => row.Read()).ToArray();
        var rejection = WorkflowStoreRules.RejectOnArrival(workflowEvent, waits.SingleOrDefault(), waits, now)
            ?? (finished ? EventRejection.InstanceFinished : null);
        // A refused event is recorded so a resend is recognized, but takes no place in the inbox.
        var sequence = rejection == null ? instance.InboxSequence + 1 : 0;
        // A stopped instance waits for its operator; the event is consumed after the release.
        var madeRunnable = rejection == null && instance.Status is "Running" or "Suspended";
        var appendParameters = WithTimes(new
        {
            workflowEvent.InstanceId, workflowEvent.EventId, Sequence = sequence, MadeRunnable = madeRunnable,
            workflowEvent.EventType, workflowEvent.Key, workflowEvent.TargetWaitId, workflowEvent.Subject,
            workflowEvent.PayloadJson, Hash = hash, Rejection = (byte?)rejection
        }, ("Now", now));
        await connection.ExecuteAsync("""
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
            """, appendParameters, transaction);
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        // The instance row is locked as appends and commits lock it, so the page is read between them:
        // the inbox sequence and the store's time bound the page, and every event received before that
        // time is on it (D-35). The open waits are read after the sequence: a wait opened in between
        // carries a sequence at least as high as every event on the page, so none of them can satisfy it.
        Page page;
        using (var results = await connection.QueryMultipleAsync($"""
            DECLARE @Through BIGINT = 0, @Now DATETIME2 = SYSUTCDATETIME();
            SELECT @Through = InboxSequence
            FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK) WHERE InstanceId = @InstanceId;
            SET @Now = SYSUTCDATETIME();
            SELECT @Through AS Through, @Now AS Now;
            SELECT TOP (@Limit) {EventColumns}
            FROM dbo.IxIFlowWorkflowEvents
            WHERE InstanceId = @InstanceId AND Outcome = 0 AND Sequence > @AfterSequence AND Sequence <= @Through
            ORDER BY Sequence;
            SELECT WaitId, OpenedSequence, DeadlineUtc FROM dbo.IxIFlowWorkflowWaits
            WHERE InstanceId = @InstanceId AND CloseReason IS NULL;
            """, new { InstanceId = instanceId, AfterSequence = afterSequence, Limit = limit }, transaction))
        {
            page = await results.ReadSingleAsync<Page>();
            page.Events = (await results.ReadAsync<EventRow>()).Select(row => row.Read()).ToArray();
            page.Waits = (await results.ReadAsync<PageWait>()).ToArray();
        }
        await transaction.CommitAsync();
        return InboxPage.Of(page.Events, limit, afterSequence, page.Through,
            page.Waits.ToDictionary(wait => wait.WaitId, wait => wait.OpenedSequence, StringComparer.Ordinal),
            page.Waits.Where(wait => wait.DeadlineUtc != null).ToDictionary(wait => wait.WaitId,
                wait => DateTime.SpecifyKind(wait.DeadlineUtc!.Value, DateTimeKind.Utc), StringComparer.Ordinal),
            DateTime.SpecifyKind(page.Now, DateTimeKind.Utc));
    }

    private sealed class Page
    {
        public long Through { get; set; }
        public DateTime Now { get; set; }
        public StoredWorkflowEvent[] Events { get; set; } = [];
        public PageWait[] Waits { get; set; } = [];
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
            FROM dbo.IxIFlowWorkflowEvents WHERE InstanceId = @InstanceId AND EventId = @EventId
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
        foreach (var batch in instanceIds.Distinct(StringComparer.Ordinal).Chunk(1000))
            foreach (var row in await connection.QueryAsync<(string InstanceId, long InboxSequence)>("""
                         SELECT InstanceId, InboxSequence FROM dbo.IxIFlowWorkflowInstances
                         WHERE InstanceId IN @InstanceIds
                         """, new { InstanceIds = batch }))
                sequences[row.InstanceId] = row.InboxSequence;
        return sequences;
    }

    public async Task<StoredWait?> GetWaitAsync(string instanceId, string waitId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<WaitRow>($"""
            SELECT {WaitColumns} FROM dbo.IxIFlowWorkflowWaits
            WHERE InstanceId = @InstanceId AND WaitId = @WaitId
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
            SELECT TOP (@Limit) {WaitColumns} FROM dbo.IxIFlowWorkflowWaits
            WHERE CloseReason IS NULL
              AND (@InstanceId IS NULL OR InstanceId = @InstanceId)
              AND (@Key IS NULL OR WaitKey = @Key)
              AND (@EventType IS NULL OR EventType = @EventType)
              AND (@Kind IS NULL AND Kind NOT IN (@Delay, @Execution) OR Kind = @Kind)
              AND (@AfterWaitId IS NULL OR WaitId > @AfterWaitId)
            ORDER BY WaitId
            """, new
        {
            query.Limit, query.InstanceId, query.Key, query.EventType, Kind = (byte?)query.Kind, query.AfterWaitId,
            Delay = (byte)WaitKind.Delay, Execution = (byte)WaitKind.Execution
        });
        return rows.Select(row => row.Read()).ToArray();
    }

    private const string EventColumns =
        "InstanceId, EventId, Sequence, EventType, EventKey, TargetWaitId, PayloadJson, ReceivedAtUtc, " +
        "Outcome, WaitId, Rejection, Subject";

    private const string WaitColumns =
        "InstanceId, WaitId, Kind, WaitKey, EventType, DeadlineUtc, OpenedSequence, OpenedAtUtc, CloseReason, " +
        "ClosedAtUtc";

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
        public byte Outcome { get; set; }
        public string? WaitId { get; set; }
        public byte? Rejection { get; set; }
        public string? Subject { get; set; }

        public StoredWorkflowEvent Read() => new(InstanceId, EventId, Sequence, EventType, EventKey,
            TargetWaitId, PayloadJson, DateTime.SpecifyKind(ReceivedAtUtc, DateTimeKind.Utc),
            (EventOutcome)Outcome, WaitId, (EventRejection?)Rejection, Subject);
    }

    private sealed class WaitRow
    {
        public string InstanceId { get; set; } = "";
        public string WaitId { get; set; } = "";
        public byte Kind { get; set; }
        public string WaitKey { get; set; } = "";
        public string EventType { get; set; } = "";
        public DateTime? DeadlineUtc { get; set; }
        public long OpenedSequence { get; set; }
        public DateTime OpenedAtUtc { get; set; }
        public byte? CloseReason { get; set; }
        public DateTime? ClosedAtUtc { get; set; }

        public StoredWait Read() => new(InstanceId, WaitId, (WaitKind)Kind, WaitKey, EventType,
            DeadlineUtc is { } deadline ? DateTime.SpecifyKind(deadline, DateTimeKind.Utc) : null, OpenedSequence,
            DateTime.SpecifyKind(OpenedAtUtc, DateTimeKind.Utc), (WaitCloseReason?)CloseReason,
            ClosedAtUtc is { } closed ? DateTime.SpecifyKind(closed, DateTimeKind.Utc) : null);
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
        var changed = await connection.ExecuteAsync("""
            UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc = CAST('0001-01-01' AS DATETIME2)
            WHERE InstanceId = @InstanceId AND Token = @Token AND ExpiresAtUtc > SYSUTCDATETIME()
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
        // The instance is saved whole, so any parts it was stored in go.
        await connection.ExecuteAsync("""
            DELETE FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId
                AND EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId AND Revision = 0);
            MERGE dbo.IxIFlowWorkflowInstances WITH (HOLDLOCK) AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED AND target.Revision = 0 THEN UPDATE SET
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId,
                RunnableAtUtc = CASE WHEN @RunnableNow = 1 THEN SYSUTCDATETIME() ELSE @RunnableAtUtc END,
                StateJson = @StateJson
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, RunnableAtUtc, StateJson)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId,
                        CASE WHEN @RunnableNow = 1 THEN SYSUTCDATETIME() ELSE @RunnableAtUtc END, @StateJson);
            IF @@ROWCOUNT = 0
                THROW 51000, 'Revisioned instances require an atomic commit', 1;
            """, new
        {
            instance.InstanceId,
            instance.WorkflowName,
            Status = instance.Status.ToString(),
            instance.CorrelationId,
            instance.RunnableAtUtc,
            RunnableNow = WorkflowStoreRules.IsRunnableNow(instance, false),
            StateJson = JsonSerializer.Serialize(instance)
        });
    }

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsureSchemaAsync();
        try
        {
            return await CommitAsync(commit);
        }
        catch (SqlException error) when (error.Number is DeadlockVictim or LockTimeout)
        {
            // The transaction was rolled back, so nothing was written.
            return new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0);
        }
    }

    private const int DeadlockVictim = 1205;
    private const int LockTimeout = 1222;

    // The payload is stored as the commit carries it; the revision and runnable time live in their columns
    // and are set on the instance when it is read.
    private async Task<WorkflowCommitResult> CommitAsync(CheckpointCommit commit)
    {
        var snapshot = commit.Instance;
        var revision = checked(commit.ExpectedRevision + 1);
        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        // A child's terminal commit appends to its parent's inbox. Rows are locked parent first, as a
        // parent's commit that cancels its children does, so the two cannot deadlock.
        var parent = commit.ParentNotice is { } notice
            ? await connection.QuerySingleOrDefaultAsync<InboxRow>("""
                SELECT Status, InboxSequence FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
                WHERE InstanceId = @ParentInstanceId
                """, new { notice.ParentInstanceId }, transaction)
            : null;
        // One store time for the whole commit, read once the instance row's lock is held: its deadlines,
        // its runnable time and its records (D-35).
        CommitState? current;
        DateTime now;
        using (var locked = await connection.QueryMultipleAsync("""
            SELECT Revision, CancellationJson, CancellationAcknowledged, InboxSequence
            FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId;
            SELECT SYSUTCDATETIME();
            """, new { commit.InstanceId }, transaction))
        {
            current = await locked.ReadSingleOrDefaultAsync<CommitState>();
            now = DateTime.SpecifyKind(await locked.ReadSingleAsync<DateTime>(), DateTimeKind.Utc);
        }
        var currentRevision = current?.Revision ?? 0;
        var receipt = await connection.QuerySingleOrDefaultAsync<CommitReceipt>("""
            SELECT ExpectedRevision, Revision, PayloadHash FROM dbo.IxIFlowWorkflowCommits
            WHERE InstanceId = @InstanceId AND CommitId = @CommitId
            """, new { commit.InstanceId, commit.CommitId }, transaction);
        var lease = await connection.QuerySingleOrDefaultAsync<LeaseRecord>("""
            SELECT Epoch, CrashCount,
                CAST(CASE WHEN ExpiresAtUtc > SYSUTCDATETIME() THEN 1 ELSE 0 END AS BIT) AS IsActive
            FROM dbo.IxIFlowWorkflowLeases WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId
            """, new { commit.InstanceId }, transaction);
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
                SELECT TOP (1) InstanceId FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId IN @Ids
                """, new { Ids = commit.ChildStarts.Select(child => child.ChildInstanceId).ToArray() }, transaction)
            is { } taken)
            throw new InvalidOperationException(
                $"Child workflow '{taken}' cannot start: an instance with its ID exists");

        // An event this commit settles must still be pending (D-04). One statement settles them all,
        // so their row locks are taken in one order.
        // Batches keep each statement under the server's parameter limit; they share the transaction.
        var settled = 0;
        foreach (var batch in commit.Settlements.Chunk(StatementBatch))
        {
            var parameters = new DynamicParameters(new { commit.InstanceId });
            var rows = new List<string>();
            for (var index = 0; index < batch.Length; index++)
            {
                var settlement = batch[index];
                rows.Add($"(@E{index}, @O{index}, @W{index}, @R{index})");
                parameters.Add($"E{index}", settlement.EventId);
                parameters.Add($"O{index}", (byte)settlement.Outcome);
                parameters.Add($"W{index}", settlement.WaitId);
                parameters.Add($"R{index}", (byte?)settlement.Rejection);
            }
            settled += await connection.ExecuteAsync($"""
                UPDATE stored
                SET Outcome = settled.Outcome, WaitId = settled.WaitId, Rejection = settled.Rejection,
                    SettledAtUtc = SYSUTCDATETIME()
                FROM dbo.IxIFlowWorkflowEvents AS stored
                JOIN (VALUES {string.Join(", ", rows)}) AS settled (EventId, Outcome, WaitId, Rejection)
                    ON stored.EventId = settled.EventId
                WHERE stored.InstanceId = @InstanceId AND stored.Outcome = 0
                """, parameters, transaction);
        }
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
                SELECT MIN(DeadlineUtc) FROM dbo.IxIFlowWorkflowWaits
                WHERE InstanceId = @InstanceId AND CloseReason IS NULL
                """, new { commit.InstanceId }, transaction)
            : null;
        var runnableAt = WorkflowStoreRules.CommittedRunnableAt(snapshot.Status, commit, runnableNow, now,
            earliestDeadline);
        await connection.ExecuteAsync("""
            MERGE dbo.IxIFlowWorkflowInstances AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED THEN UPDATE SET
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId,
                RunnableAtUtc = @RunnableAtUtc,
                InboxReadThrough = COALESCE(@InboxReadThrough, target.InboxReadThrough),
                StateJson = @StateJson, Revision = @Revision,
                CancellationAcknowledged = CASE WHEN @AcknowledgesCancellation = 1 AND
                    target.CancellationJson IS NOT NULL THEN 1 ELSE target.CancellationAcknowledged END
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, RunnableAtUtc, StateJson, Revision)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @RunnableAtUtc, @StateJson,
                        @Revision);
            INSERT INTO dbo.IxIFlowWorkflowCommits (InstanceId, CommitId, ExpectedRevision, Revision, PayloadHash)
            VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
            """, WithTimes(new
        {
            snapshot.InstanceId, snapshot.WorkflowName, snapshot.CorrelationId,
            Status = snapshot.Status.ToString(),
            commit.InboxReadThrough,
            StateJson = commit.Payload, Revision = revision,
            commit.ExpectedRevision, commit.CommitId, PayloadHash = commit.ContentHash,
            snapshot.AcknowledgesCancellation
        }, ("RunnableAtUtc", runnableAt)), transaction);
        await ApplyPartsAsync(connection, transaction, commit);
        await AppendTraceAsync(connection, transaction, commit);
        // The lease row is written only when something changes, so most commits touch only their
        // instance row; a write to a missing lease row would take range locks other claims need.
        // An idle owner that has not read every arrival keeps the lease and reads them.
        var keepsLease = unread && snapshot.Status == WorkflowStatus.Suspended;
        var releases = (snapshot.Status != WorkflowStatus.Running || parked) && !keepsLease;
        // A finished instance takes no more events; those still pending are closed, and so are its waits.
        if (snapshot.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or
            WorkflowStatus.Terminated or WorkflowStatus.TimedOut)
            await connection.ExecuteAsync("""
                UPDATE dbo.IxIFlowWorkflowEvents SET Outcome = 4, SettledAtUtc = SYSUTCDATETIME()
                WHERE InstanceId = @InstanceId AND Outcome = 0;
                UPDATE dbo.IxIFlowWorkflowWaits SET CloseReason = @Reason, ClosedAtUtc = @Now
                WHERE InstanceId = @InstanceId AND CloseReason IS NULL;
                """, WithTimes(new
            {
                snapshot.InstanceId, Reason = (byte)WorkflowStoreRules.FinalWaitCloseReason(snapshot.Status)
            }, ("Now", now)), transaction);
        var clears = commit.ClearsCrashCount && lease?.CrashCount > 0;
        if (lease != null && (releases || clears))
            await connection.ExecuteAsync("""
                UPDATE dbo.IxIFlowWorkflowLeases
                SET ExpiresAtUtc = CASE WHEN @Released = 1 THEN CAST('0001-01-01' AS DATETIME2)
                        ELSE ExpiresAtUtc END,
                    CrashCount = CASE WHEN @ClearsCrashCount = 1 THEN 0 ELSE CrashCount END
                WHERE InstanceId = @InstanceId
                """, new
            {
                snapshot.InstanceId, Released = releases, ClearsCrashCount = clears
            }, transaction);
        var madeRunnable = new List<string>();
        // Each child starts as its own instance, runnable now, with no owner yet (D-17).
        foreach (var start in commit.ChildStarts)
        {
            var child = JsonSerializer.Deserialize<WorkflowInstance>(start.Payload)!;
            child.Revision = 1;
            child.RunnableAtUtc = now;
            await connection.ExecuteAsync("""
                INSERT INTO dbo.IxIFlowWorkflowInstances
                    (InstanceId, WorkflowName, Status, CorrelationId, RunnableAtUtc, StateJson, Revision, ParentInstanceId)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId, @Now, @StateJson, 1, @ParentInstanceId)
                """, WithTimes(new
            {
                child.InstanceId, child.WorkflowName, Status = child.Status.ToString(), child.CorrelationId,
                StateJson = JsonSerializer.Serialize(child), ParentInstanceId = commit.InstanceId
            }, ("Now", now)), transaction);
            madeRunnable.Add(child.InstanceId);
        }
        foreach (var cancellation in commit.ChildCancellations)
        {
            var outcome = await connection.ExecuteScalarAsync<byte>("""
                UPDATE dbo.IxIFlowWorkflowInstances
                SET CancellationJson = COALESCE(CancellationJson, @ReasonJson),
                    CancellationAcknowledged = CASE WHEN CancellationJson IS NULL
                        THEN 0 ELSE CancellationAcknowledged END,
                    RunnableAtUtc = CASE WHEN CancellationJson IS NULL OR CancellationAcknowledged = 0
                        THEN @Now ELSE RunnableAtUtc END
                WHERE InstanceId = @InstanceId AND Status IN ('Running', 'Suspended', 'NeedsResolution');
                DECLARE @Changed INT = @@ROWCOUNT;
                SELECT CAST(CASE
                    WHEN @Changed = 1 AND EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances
                        WHERE InstanceId = @InstanceId AND CancellationAcknowledged = 0) THEN 1
                    WHEN NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId) THEN 2
                    ELSE 0 END AS TINYINT);
                """, WithTimes(new
            {
                InstanceId = cancellation.ChildInstanceId, ReasonJson = JsonSerializer.Serialize(cancellation.Reason)
            }, ("Now", now)), transaction);
            if (outcome == 1)
                madeRunnable.Add(cancellation.ChildInstanceId);
            // A child that is gone will never answer; its parent's wait ends as its termination.
            else if (outcome == 2)
            {
                var gone = ToEvent(WorkflowStoreRules.ChildGone(cancellation.ChildInstanceId, commit.InstanceId,
                    cancellation.ParentWaitId));
                await AppendAsync(connection, transaction, gone, gone.ContentHash(),
                    new InboxRow { Status = snapshot.Status.ToString(), InboxSequence = current?.InboxSequence ?? 0 },
                    now);
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
        await transaction.CommitAsync();
        return new WorkflowCommitResult(WorkflowCommitStatus.Applied, revision,
            ReadReason(current?.CancellationJson), parked, unread, madeRunnable);
    }

    // A wait opens at the inbox sequence read under the instance row's lock, which appends also take,
    // so it is ordered exactly against every event. Its deadline is the commit's store time plus its
    // timeout (D-35). Opening or arming it again keeps its first record.
    private static async Task ApplyWaitChangesAsync(SqlConnection connection, SqlTransaction transaction,
        CheckpointCommit commit, long inboxSequence, DateTime now)
    {
        foreach (var opened in commit.OpenedWaits.Chunk(StatementBatch))
            await ApplyWaitChangesAsync(connection, transaction, commit.InstanceId, inboxSequence, now, opened, [], []);
        foreach (var armed in commit.ArmedWaits.Chunk(StatementBatch))
            await ApplyWaitChangesAsync(connection, transaction, commit.InstanceId, inboxSequence, now, [], armed, []);
        foreach (var closed in commit.ClosedWaits.Chunk(StatementBatch))
            await ApplyWaitChangesAsync(connection, transaction, commit.InstanceId, inboxSequence, now, [], [], closed);
    }

    // Store times go to the server as datetime2, so they keep the precision deadlines are compared at (D-35).
    private static DynamicParameters WithTimes(object values, params (string Name, DateTime? Value)[] times)
    {
        var parameters = new DynamicParameters(values);
        foreach (var (name, value) in times)
            parameters.Add(name, value, DbType.DateTime2);
        return parameters;
    }

    // At most this many rows per statement: five parameters each stays under the server's limit of 2,100.
    private const int StatementBatch = 400;

    private static async Task ApplyWaitChangesAsync(SqlConnection connection, SqlTransaction transaction,
        string instanceId, long inboxSequence, DateTime now, IReadOnlyList<OpenedWait> openedWaits,
        IReadOnlyList<ArmedWait> armedWaits, IReadOnlyList<ClosedWait> closedWaits)
    {
        var parameters = WithTimes(new { InstanceId = instanceId, Sequence = inboxSequence }, ("Now", now));
        var batch = new System.Text.StringBuilder();
        if (openedWaits.Count > 0)
        {
            var rows = new List<string>();
            for (var index = 0; index < openedWaits.Count; index++)
            {
                var wait = openedWaits[index];
                rows.Add($"(@OW{index}, @OK{index}, @OY{index}, @OT{index}, @OD{index})");
                parameters.Add($"OW{index}", wait.WaitId);
                parameters.Add($"OK{index}", (byte)wait.Kind);
                parameters.Add($"OY{index}", wait.Key);
                parameters.Add($"OT{index}", wait.EventType);
                parameters.Add($"OD{index}", now + wait.Timeout, DbType.DateTime2);
            }
            batch.Append($"""
                INSERT INTO dbo.IxIFlowWorkflowWaits
                    (InstanceId, WaitId, Kind, WaitKey, EventType, DeadlineUtc, OpenedSequence, OpenedAtUtc)
                SELECT @InstanceId, opened.WaitId, opened.Kind, opened.WaitKey, opened.EventType, opened.DeadlineUtc,
                    @Sequence, @Now
                FROM (VALUES {string.Join(", ", rows)}) AS opened (WaitId, Kind, WaitKey, EventType, DeadlineUtc)
                WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowWaits AS stored
                    WHERE stored.InstanceId = @InstanceId AND stored.WaitId = opened.WaitId);

                """);
        }
        if (armedWaits.Count > 0)
        {
            var rows = new List<string>();
            for (var index = 0; index < armedWaits.Count; index++)
            {
                rows.Add($"(@AW{index}, @AD{index})");
                parameters.Add($"AW{index}", armedWaits[index].WaitId);
                parameters.Add($"AD{index}", now + armedWaits[index].Timeout, DbType.DateTime2);
            }
            batch.Append($"""
                UPDATE stored SET DeadlineUtc = armed.DeadlineUtc
                FROM dbo.IxIFlowWorkflowWaits AS stored
                JOIN (VALUES {string.Join(", ", rows)}) AS armed (WaitId, DeadlineUtc) ON stored.WaitId = armed.WaitId
                WHERE stored.InstanceId = @InstanceId AND stored.CloseReason IS NULL AND stored.DeadlineUtc IS NULL;

                """);
        }
        if (closedWaits.Count > 0)
        {
            var rows = new List<string>();
            for (var index = 0; index < closedWaits.Count; index++)
            {
                rows.Add($"(@CW{index}, @CR{index})");
                parameters.Add($"CW{index}", closedWaits[index].WaitId);
                parameters.Add($"CR{index}", (byte)closedWaits[index].Reason);
            }
            batch.Append($"""
                UPDATE stored SET CloseReason = closed.Reason, ClosedAtUtc = @Now
                FROM dbo.IxIFlowWorkflowWaits AS stored
                JOIN (VALUES {string.Join(", ", rows)}) AS closed (WaitId, Reason) ON stored.WaitId = closed.WaitId
                WHERE stored.InstanceId = @InstanceId AND stored.CloseReason IS NULL;
                """);
        }
        await connection.ExecuteAsync(batch.ToString(), parameters, transaction);
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

    // An instance row's columns as a read takes them. Its stamp changes with every write to the row and never repeats,
    // not even when the instance is deleted and created again.
    private const string RowColumns = "InstanceId, StateJson, RunnableAtUtc, Revision, CAST(RowStamp AS BIGINT) AS Stamp";

    private const string UnpublishedCompletion =
        "AND Status IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut') AND CompletionPublished = 0";

    // The row, then its parts, then its stamp again: when the stamp moved, a commit, or a delete and a new instance
    // with the same ID, may have changed the parts after the row was read, and the read starts again. The reads hold
    // no lock between statements; under row versioning they take none. A summary reads only the data part.
    // <paramref name="filter"/> is the condition the row was selected by, which a row read again must still meet.
    private static async Task<WorkflowInstance?> ReadAtOneRevisionAsync(SqlConnection connection, string instanceId,
        bool wholly, string filter, object? filterValues)
    {
        var parameters = new DynamicParameters(new { InstanceId = instanceId, CheckpointParts.DataKey });
        if (filterValues != null)
            parameters.AddDynamicParams(filterValues);
        for (var attempt = 1; ; attempt++)
        {
            StoredRow? row;
            StoredPart[] parts;
            long? stamp;
            using (var read = await connection.QueryMultipleAsync($"""
                SELECT {RowColumns} FROM dbo.IxIFlowWorkflowInstances
                WHERE InstanceId = @InstanceId {filter};
                SELECT InstanceId, PartKey, Json FROM dbo.IxIFlowWorkflowParts
                WHERE InstanceId = @InstanceId {(wholly ? "" : "AND PartKey = @DataKey")};
                SELECT CAST(RowStamp AS BIGINT) FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId;
                """, parameters))
            {
                row = await read.ReadSingleOrDefaultAsync<StoredRow>();
                parts = (await read.ReadAsync<StoredPart>()).ToArray();
                stamp = await read.ReadSingleOrDefaultAsync<long?>();
            }
            if (row == null)
                return null;
            if (stamp == row.Stamp)
                return wholly ? row.Read(parts) : row.Summarize(parts.SingleOrDefault()?.Json);
            if (attempt == MaxReadAttempts)
                throw new InvalidOperationException(
                    $"Workflow instance '{instanceId}' changed in each of {MaxReadAttempts} reads");
            // A commit is in progress; reading again at once would most likely meet the next one.
            await Task.Delay(Random.Shared.Next(1, 5 * attempt));
        }
    }

    // Each row with the parts stored with it. The parts and the stamps are read after the rows; a row whose stamp
    // moved since is read again on its own, and kept only if it still meets <paramref name="filter"/>, the condition
    // it was selected by, so no instance mixes two commits or is listed for a state it has left.
    private static async Task<WorkflowInstance[]> AssembleAsync(SqlConnection connection, IReadOnlyList<StoredRow> rows,
        string filter, object? filterValues)
    {
        var instances = new List<WorkflowInstance>();
        foreach (var batch in rows.Chunk(StatementBatch))
        {
            ILookup<string, StoredPart> parts;
            Dictionary<string, long> stamps;
            using (var read = await connection.QueryMultipleAsync("""
                SELECT InstanceId, PartKey, Json FROM dbo.IxIFlowWorkflowParts WHERE InstanceId IN @Ids;
                SELECT InstanceId, CAST(RowStamp AS BIGINT) AS Stamp FROM dbo.IxIFlowWorkflowInstances
                WHERE InstanceId IN @Ids;
                """, new { Ids = batch.Select(row => row.InstanceId).ToArray() }))
            {
                parts = (await read.ReadAsync<StoredPart>()).ToLookup(part => part.InstanceId, StringComparer.Ordinal);
                stamps = (await read.ReadAsync<StoredStamp>())
                    .ToDictionary(stored => stored.InstanceId, stored => stored.Stamp, StringComparer.Ordinal);
            }
            foreach (var row in batch)
                if (stamps.TryGetValue(row.InstanceId, out var stamp) && stamp == row.Stamp)
                    instances.Add(row.Read(parts[row.InstanceId]));
                else if (await ReadAtOneRevisionAsync(connection, row.InstanceId, wholly: true, filter, filterValues)
                         is { } moved)
                    instances.Add(moved);
        }
        return instances.ToArray();
    }

    // A commit deletes the parts it removes and those it rewrites, then inserts what it writes (A-08); a commit that
    // replaces its parts deletes them all first.
    private static async Task ApplyPartsAsync(SqlConnection connection, SqlTransaction transaction,
        CheckpointCommit commit)
    {
        if (commit.ReplacesParts)
            await connection.ExecuteAsync("DELETE FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId",
                new { commit.InstanceId }, transaction);
        else
            foreach (var keys in commit.DeletedParts.Concat(commit.Parts.Select(part => part.Key)).Chunk(StatementBatch))
                await connection.ExecuteAsync("""
                    DELETE FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId AND PartKey IN @Keys
                    """, new { commit.InstanceId, Keys = keys }, transaction);
        foreach (var batch in commit.Parts.Chunk(StatementBatch))
        {
            var parameters = new DynamicParameters(new { commit.InstanceId });
            var rows = new List<string>();
            for (var index = 0; index < batch.Length; index++)
            {
                rows.Add($"(@InstanceId, @K{index}, @J{index})");
                parameters.Add($"K{index}", batch[index].Key);
                parameters.Add($"J{index}", batch[index].Json, DbType.String, size: -1);
            }
            await connection.ExecuteAsync($"""
                INSERT INTO dbo.IxIFlowWorkflowParts (InstanceId, PartKey, Json) VALUES {string.Join(", ", rows)}
                """, parameters, transaction);
        }
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
        QueryAsync("WorkflowName = @Value", workflowName);

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
        QueryAsync("Status = @Value", status.ToString());

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
        QueryAsync("CorrelationId = @Value", correlationId);

    public async Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentInstanceId);
        return await QueryAsync("ParentInstanceId = @Value", parentInstanceId);
    }

    public async Task DeleteWorkflowInstanceAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        // The lease row stays so a recreated instance never reuses an epoch; its crashes go.
        await connection.ExecuteAsync(
            "UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc = CAST('0001-01-01' AS DATETIME2), CrashCount = 0 WHERE InstanceId = @InstanceId",
            new { InstanceId = instanceId });
        // The instance row is locked first, as commits and appends lock it, and its rows in the other
        // tables are deleted by key. A cascading delete would take range locks in those tables, which
        // deadlock with commits of other instances inserting into the same ranges.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        // A child its parent still waits for ends that wait as terminated. The parent's row is locked before
        // the child's, as a child's terminal commit locks them.
        var deleted = await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId
            """, new { InstanceId = instanceId }, transaction) is { } json
            ? JsonSerializer.Deserialize<WorkflowInstance>(json) : null;
        if (deleted is { ParentInstanceId: { } parentId, ParentWaitId: { } waitId } &&
            deleted.Status is not (WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or
                WorkflowStatus.Terminated or WorkflowStatus.TimedOut))
        {
            InboxRow? parent;
            DateTime now;
            using (var locked = await connection.QueryMultipleAsync("""
                SELECT Status, InboxSequence FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
                WHERE InstanceId = @ParentId;
                SELECT SYSUTCDATETIME();
                """, new { ParentId = parentId }, transaction))
            {
                parent = await locked.ReadSingleOrDefaultAsync<InboxRow>();
                now = DateTime.SpecifyKind(await locked.ReadSingleAsync<DateTime>(), DateTimeKind.Utc);
            }
            var gone = ToEvent(WorkflowStoreRules.ChildGone(instanceId, parentId, waitId));
            await AppendAsync(connection, transaction, gone, gone.ContentHash(), parent, now);
        }
        await connection.ExecuteAsync("""
            SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowWaits WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowTraces WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowEvents WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowCommits WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId;
            """, new { InstanceId = instanceId }, transaction);
        await transaction.CommitAsync();
    }

    private async Task<IEnumerable<WorkflowInstance>> QueryAsync(string predicate, string value)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<StoredRow>(
            $"SELECT {RowColumns} FROM dbo.IxIFlowWorkflowInstances WHERE {predicate}", new { Value = value });
        return await AssembleAsync(connection, rows.ToArray(), $"AND {predicate}", new { Value = value });
    }

    // A commit's trace entries follow those already stored, numbered in order (G-01). The instance row is locked,
    // so no other commit numbers entries at the same time.
    private static async Task AppendTraceAsync(SqlConnection connection, SqlTransaction transaction,
        CheckpointCommit commit)
    {
        if (commit.TraceEntries.Count == 0)
            return;
        var last = await connection.ExecuteScalarAsync<long>("""
            SELECT ISNULL(MAX(Sequence), 0) FROM dbo.IxIFlowWorkflowTraces WHERE InstanceId = @InstanceId
            """, new { commit.InstanceId }, transaction);
        var offset = 0;
        foreach (var batch in commit.TraceEntries.Chunk(StatementBatch))
        {
            var parameters = new DynamicParameters(new { commit.InstanceId });
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

    public async Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<(long Sequence, string EntryJson)>("""
            SELECT TOP (@Limit) Sequence, EntryJson FROM dbo.IxIFlowWorkflowTraces
            WHERE InstanceId = @InstanceId AND Sequence > @AfterSequence
            ORDER BY Sequence
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
        public long Stamp { get; set; }

        public WorkflowInstance Read(IEnumerable<StoredPart> parts) => Runnable(CheckpointParts.Assemble(StateJson,
            parts.Select(part => new CheckpointPart(part.PartKey, part.Json)), Revision));

        public WorkflowInstance Summarize(string? data) => Runnable(CheckpointParts.Summarize(StateJson, data, Revision));

        private WorkflowInstance Runnable(WorkflowInstance instance)
        {
            instance.RunnableAtUtc = RunnableAtUtc;
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
        public long Stamp { get; set; }
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    // Leases and cancellation use the reserved pool.
    private async Task<SqlConnection> OpenOwnershipConnectionAsync()
    {
        var connection = new SqlConnection(_ownershipConnectionString);
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
                DECLARE @RunnableAdded BIT = 0;
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowInstances', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.IxIFlowWorkflowInstances (
                        InstanceId NVARCHAR(100) NOT NULL PRIMARY KEY,
                        WorkflowName NVARCHAR(200) NOT NULL,
                        Status NVARCHAR(50) NOT NULL,
                        CorrelationId NVARCHAR(100) NULL,
                        RunnableAtUtc DATETIME2 NULL,
                        StateJson NVARCHAR(MAX) NOT NULL,
                        CancellationJson NVARCHAR(MAX) NULL,
                        CancellationAcknowledged BIT NOT NULL DEFAULT 0,
                        CompletionPublished BIT NOT NULL DEFAULT 0,
                        CompletionClaimToken NVARCHAR(100) NULL,
                        CompletionClaimUntil DATETIME2 NULL,
                        Revision BIGINT NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IX_IxIFlowWorkflowInstances_Status
                        ON dbo.IxIFlowWorkflowInstances (Status);
                    CREATE INDEX IX_IxIFlowWorkflowInstances_WorkflowName
                        ON dbo.IxIFlowWorkflowInstances (WorkflowName);
                    CREATE INDEX IX_IxIFlowWorkflowInstances_CorrelationId
                        ON dbo.IxIFlowWorkflowInstances (CorrelationId);
                END
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'InboxSequence') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances
                        ADD InboxSequence BIGINT NOT NULL CONSTRAINT DF_IxIFlowWorkflowInstances_InboxSequence DEFAULT 0;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'InboxReadThrough') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances
                        ADD InboxReadThrough BIGINT NOT NULL
                            CONSTRAINT DF_IxIFlowWorkflowInstances_InboxReadThrough DEFAULT 0;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'RowStamp') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances ADD RowStamp ROWVERSION;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'ParentInstanceId') IS NULL
                BEGIN
                    ALTER TABLE dbo.IxIFlowWorkflowInstances ADD ParentInstanceId NVARCHAR(100) NULL;
                    EXEC(N'CREATE INDEX IX_IxIFlowWorkflowInstances_Parent
                        ON dbo.IxIFlowWorkflowInstances (ParentInstanceId) WHERE ParentInstanceId IS NOT NULL');
                END
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowEvents', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.IxIFlowWorkflowEvents (
                        InstanceId NVARCHAR(100) NOT NULL,
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
                        InstanceId NVARCHAR(100) NOT NULL,
                        PartKey NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        Json NVARCHAR(MAX) NOT NULL,
                        CONSTRAINT PK_IxIFlowWorkflowParts PRIMARY KEY (InstanceId, PartKey)
                    );
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowTraces', N'U') IS NULL
                    CREATE TABLE dbo.IxIFlowWorkflowTraces (
                        InstanceId NVARCHAR(100) NOT NULL,
                        Sequence BIGINT NOT NULL,
                        EntryJson NVARCHAR(MAX) NOT NULL,
                        CONSTRAINT PK_IxIFlowWorkflowTraces PRIMARY KEY (InstanceId, Sequence)
                    );
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowWaits', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.IxIFlowWorkflowWaits (
                        InstanceId NVARCHAR(100) NOT NULL,
                        WaitId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        Kind TINYINT NOT NULL,
                        WaitKey NVARCHAR(400) NOT NULL,
                        EventType NVARCHAR(400) NOT NULL,
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
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'RunnableAtUtc') IS NULL
                BEGIN
                    ALTER TABLE dbo.IxIFlowWorkflowInstances ADD RunnableAtUtc DATETIME2 NULL;
                    SET @RunnableAdded = 1;
                END
                IF NOT EXISTS (SELECT 1 FROM sys.indexes
                    WHERE name = N'IX_IxIFlowWorkflowInstances_Runnable'
                      AND object_id = OBJECT_ID(N'dbo.IxIFlowWorkflowInstances'))
                    EXEC(N'CREATE INDEX IX_IxIFlowWorkflowInstances_Runnable
                        ON dbo.IxIFlowWorkflowInstances (RunnableAtUtc, InstanceId)
                        WHERE RunnableAtUtc IS NOT NULL');
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
                        CONSTRAINT PK_IxIFlowWorkflowCommits PRIMARY KEY (InstanceId, CommitId)
                    );
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowLeases', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.IxIFlowWorkflowLeases (
                        InstanceId NVARCHAR(100) NOT NULL PRIMARY KEY,
                        Token NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        ExpiresAtUtc DATETIME2 NOT NULL,
                        Epoch BIGINT NOT NULL
                    );
                    CREATE INDEX IX_IxIFlowWorkflowLeases_ExpiresAt
                        ON dbo.IxIFlowWorkflowLeases (ExpiresAtUtc);
                END
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowLeases', N'Epoch') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowLeases
                        ADD Epoch BIGINT NOT NULL CONSTRAINT DF_IxIFlowWorkflowLeases_Epoch DEFAULT 1;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowLeases', N'CrashCount') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowLeases
                        ADD CrashCount INT NOT NULL CONSTRAINT DF_IxIFlowWorkflowLeases_CrashCount DEFAULT 0;
                -- Rows saved before the runnable time existed, once every column above is in place:
                -- running ones and ones with a cancellation to unwind are runnable now, idle ones at
                -- their earlier due time.
                IF @RunnableAdded = 1
                BEGIN
                    IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'NextDueAtUtc') IS NOT NULL
                        EXEC(N'UPDATE dbo.IxIFlowWorkflowInstances
                            SET RunnableAtUtc = CASE WHEN Status = ''Running''
                                    OR (CancellationJson IS NOT NULL AND CancellationAcknowledged = 0)
                                THEN SYSUTCDATETIME() ELSE NextDueAtUtc END
                            WHERE Status IN (''Running'', ''Suspended'', ''NeedsResolution'')');
                    ELSE
                        EXEC(N'UPDATE dbo.IxIFlowWorkflowInstances SET RunnableAtUtc = SYSUTCDATETIME()
                            WHERE Status = ''Running''
                                OR (Status IN (''Suspended'', ''NeedsResolution'')
                                    AND CancellationJson IS NOT NULL AND CancellationAcknowledged = 0)');
                END
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
