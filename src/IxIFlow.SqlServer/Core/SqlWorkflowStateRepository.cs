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

    public async Task MakeRunnableAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync("""
            UPDATE dbo.IxIFlowWorkflowInstances
            SET RunnableAtUtc = SYSUTCDATETIME()
            WHERE InstanceId = @InstanceId AND Status = 'Suspended'
            """, new { InstanceId = instanceId });
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
        // exactly those committed before this event arrives.
        var instance = await connection.QuerySingleOrDefaultAsync<InboxRow>("""
            SELECT Status, InboxSequence FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE InstanceId = @InstanceId
            """, new { workflowEvent.InstanceId }, transaction);
        // A known event ID answers the same way whatever happened to the instance since.
        var existing = await connection.QuerySingleOrDefaultAsync<EventHashRow>("""
            SELECT Sequence, PayloadHash FROM dbo.IxIFlowWorkflowEvents
            WHERE InstanceId = @InstanceId AND EventId = @EventId
            """, new { workflowEvent.InstanceId, workflowEvent.EventId }, transaction);
        if (existing != null)
        {
            await transaction.CommitAsync();
            return existing.PayloadHash.SequenceEqual(hash)
                ? new EventAppendResult(EventAppendStatus.Duplicate, existing.Sequence)
                : new EventAppendResult(EventAppendStatus.Conflict);
        }
        if (instance == null)
        {
            await transaction.CommitAsync();
            return new EventAppendResult(EventAppendStatus.NotFound);
        }
        if (instance.Status is "Completed" or "Failed" or "Cancelled" or "Terminated" or "TimedOut")
        {
            await transaction.CommitAsync();
            return new EventAppendResult(EventAppendStatus.Closed);
        }
        var waits = (await connection.QueryAsync<WaitRow>($"""
            SELECT TOP (1) {WaitColumns} FROM dbo.IxIFlowWorkflowWaits
            WHERE InstanceId = @InstanceId AND (@TargetWaitId IS NOT NULL AND WaitId = @TargetWaitId
                OR @TargetWaitId IS NULL AND CloseReason IS NULL
                    AND (Kind = @Child OR Kind = @Event AND (@Key IS NULL OR WaitKey = @Key)))
            """, new
        {
            workflowEvent.InstanceId, workflowEvent.TargetWaitId, workflowEvent.Key,
            Child = (byte)WaitKind.Child, Event = (byte)WaitKind.Event
        }, transaction)).Select(row => row.Read()).ToArray();
        var rejection = WorkflowStoreRules.RejectOnArrival(workflowEvent, waits.SingleOrDefault(), waits);
        // A refused event is recorded so a resend is recognized, but takes no place in the inbox.
        var sequence = rejection == null ? instance.InboxSequence + 1 : 0;
        // A stopped instance waits for its operator; the event is consumed after the release.
        var madeRunnable = rejection == null && instance.Status is "Running" or "Suspended";
        await connection.ExecuteAsync("""
            IF @Rejection IS NULL
                UPDATE dbo.IxIFlowWorkflowInstances
                SET InboxSequence = @Sequence,
                    RunnableAtUtc = CASE WHEN @MadeRunnable = 1 THEN SYSUTCDATETIME() ELSE RunnableAtUtc END
                WHERE InstanceId = @InstanceId;
            INSERT INTO dbo.IxIFlowWorkflowEvents
                (InstanceId, EventId, Sequence, EventType, EventKey, TargetWaitId, PayloadJson,
                 PayloadHash, ReceivedAtUtc, Outcome, Rejection, SettledAtUtc)
            VALUES (@InstanceId, @EventId, @Sequence, @EventType, @Key, @TargetWaitId, @PayloadJson,
                @Hash, SYSUTCDATETIME(), CASE WHEN @Rejection IS NULL THEN 0 ELSE 2 END, @Rejection,
                CASE WHEN @Rejection IS NULL THEN NULL ELSE SYSUTCDATETIME() END);
            """, new
        {
            workflowEvent.InstanceId, workflowEvent.EventId, Sequence = sequence, MadeRunnable = madeRunnable,
            workflowEvent.EventType, workflowEvent.Key, workflowEvent.TargetWaitId,
            workflowEvent.PayloadJson, Hash = hash, Rejection = (byte?)rejection
        }, transaction);
        await transaction.CommitAsync();
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
        // The inbox sequence is read first and bounds the page, so the page covers everything up to it.
        // The open waits are read after it: a wait opened in between carries a sequence at least as high
        // as every event on the page, so none of them can satisfy it.
        using var results = await connection.QueryMultipleAsync($"""
            DECLARE @Through BIGINT = COALESCE(
                (SELECT InboxSequence FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId), 0);
            SELECT @Through;
            SELECT TOP (@Limit) {EventColumns}
            FROM dbo.IxIFlowWorkflowEvents
            WHERE InstanceId = @InstanceId AND Outcome = 0 AND Sequence > @AfterSequence AND Sequence <= @Through
            ORDER BY Sequence;
            SELECT WaitId, OpenedSequence FROM dbo.IxIFlowWorkflowWaits
            WHERE InstanceId = @InstanceId AND CloseReason IS NULL;
            """, new { InstanceId = instanceId, AfterSequence = afterSequence, Limit = limit });
        var through = await results.ReadSingleAsync<long>();
        var events = (await results.ReadAsync<EventRow>()).Select(row => row.Read()).ToArray();
        var openWaits = (await results.ReadAsync<(string WaitId, long OpenedSequence)>())
            .ToDictionary(wait => wait.WaitId, wait => wait.OpenedSequence, StringComparer.Ordinal);
        return InboxPage.Of(events, limit, afterSequence, through, openWaits);
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
              AND (@Kind IS NULL OR Kind = @Kind)
              AND (@AfterWaitId IS NULL OR WaitId > @AfterWaitId)
            ORDER BY WaitId
            """, new
        {
            query.Limit, query.InstanceId, query.Key, query.EventType, Kind = (byte?)query.Kind, query.AfterWaitId
        });
        return rows.Select(row => row.Read()).ToArray();
    }

    private const string EventColumns =
        "InstanceId, EventId, Sequence, EventType, EventKey, TargetWaitId, PayloadJson, ReceivedAtUtc, " +
        "Outcome, WaitId, Rejection";

    private const string WaitColumns =
        "InstanceId, WaitId, Kind, WaitKey, EventType, DeadlineUtc, OpenedSequence, OpenedAtUtc, CloseReason, " +
        "ClosedAtUtc";

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

        public StoredWorkflowEvent Read() => new(InstanceId, EventId, Sequence, EventType, EventKey,
            TargetWaitId, PayloadJson, DateTime.SpecifyKind(ReceivedAtUtc, DateTimeKind.Utc),
            (EventOutcome)Outcome, WaitId, (EventRejection?)Rejection);
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
        await connection.ExecuteAsync("""
            MERGE dbo.IxIFlowWorkflowInstances WITH (HOLDLOCK) AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED AND target.Revision = 0 THEN UPDATE SET
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId, SuspensionExpiresAt = @SuspensionExpiresAt,
                SuspensionId = @SuspensionId,
                RunnableAtUtc = CASE WHEN @RunnableNow = 1 THEN SYSUTCDATETIME() ELSE @RunnableAtUtc END,
                StateJson = @StateJson
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, SuspensionExpiresAt,
                 SuspensionId, RunnableAtUtc, StateJson)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId,
                        @SuspensionExpiresAt, @SuspensionId,
                        CASE WHEN @RunnableNow = 1 THEN SYSUTCDATETIME() ELSE @RunnableAtUtc END, @StateJson);
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
            instance.RunnableAtUtc,
            RunnableNow = WorkflowStoreRules.IsRunnableNow(instance, false),
            StateJson = JsonSerializer.Serialize(instance)
        });
    }

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var snapshot = commit.ReadInstance();
        snapshot.Revision = checked(commit.ExpectedRevision + 1);
        var json = JsonSerializer.Serialize(snapshot);

        await EnsureSchemaAsync();
        try
        {
            return await CommitAsync(commit, snapshot, json);
        }
        catch (SqlException error) when (error.Number is DeadlockVictim or LockTimeout)
        {
            // The transaction was rolled back, so nothing was written.
            return new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0);
        }
    }

    private const int DeadlockVictim = 1205;
    private const int LockTimeout = 1222;

    private async Task<WorkflowCommitResult> CommitAsync(CheckpointCommit commit, WorkflowInstance snapshot,
        string json)
    {
        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var current = await connection.QuerySingleOrDefaultAsync<CommitState>("""
            SELECT Revision, CancellationJson, CancellationAcknowledged, InboxSequence
            FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, HOLDLOCK)
            WHERE InstanceId = @InstanceId
            """, new { commit.InstanceId }, transaction);
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
            (current?.CancellationJson != null && snapshot.CancellationReason == null &&
             snapshot.Status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut))
        {
            await transaction.CommitAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.Conflict, currentRevision);
        }

        // An event this commit settles must still be pending (D-04). One statement settles them all,
        // so their row locks are taken in one order.
        if (commit.Settlements.Count > 0)
        {
            var parameters = new DynamicParameters(new { commit.InstanceId });
            var rows = new List<string>();
            for (var index = 0; index < commit.Settlements.Count; index++)
            {
                var settlement = commit.Settlements[index];
                rows.Add($"(@E{index}, @O{index}, @W{index}, @R{index})");
                parameters.Add($"E{index}", settlement.EventId);
                parameters.Add($"O{index}", (byte)settlement.Outcome);
                parameters.Add($"W{index}", settlement.WaitId);
                parameters.Add($"R{index}", (byte?)settlement.Rejection);
            }
            var settled = await connection.ExecuteAsync($"""
                UPDATE stored
                SET Outcome = settled.Outcome, WaitId = settled.WaitId, Rejection = settled.Rejection,
                    SettledAtUtc = SYSUTCDATETIME()
                FROM dbo.IxIFlowWorkflowEvents AS stored
                JOIN (VALUES {string.Join(", ", rows)}) AS settled (EventId, Outcome, WaitId, Rejection)
                    ON stored.EventId = settled.EventId
                WHERE stored.InstanceId = @InstanceId AND stored.Outcome = 0
                """, parameters, transaction);
            if (settled != commit.Settlements.Count)
            {
                await transaction.RollbackAsync();
                return new WorkflowCommitResult(WorkflowCommitStatus.EventsChanged, currentRevision);
            }
        }
        var unacknowledged = current is { CancellationJson: not null, CancellationAcknowledged: false };
        // A run that has not read every arrival neither parks nor goes idle out of reach.
        var unread = commit.InboxReadThrough is { } readThrough && current?.InboxSequence > readThrough;
        var parked = commit.Parks && !unacknowledged && !unread;
        await connection.ExecuteAsync("""
            MERGE dbo.IxIFlowWorkflowInstances AS target
            USING (SELECT @InstanceId AS InstanceId) AS source
            ON target.InstanceId = source.InstanceId
            WHEN MATCHED THEN UPDATE SET
                WorkflowName = @WorkflowName, Status = @Status,
                CorrelationId = @CorrelationId, SuspensionExpiresAt = @SuspensionExpiresAt,
                SuspensionId = @SuspensionId,
                RunnableAtUtc = CASE WHEN @RunnableNow = 1 THEN SYSUTCDATETIME() ELSE @RunnableAtUtc END,
                InboxReadThrough = COALESCE(@InboxReadThrough, target.InboxReadThrough),
                StateJson = @StateJson, Revision = @Revision,
                CancellationAcknowledged = CASE WHEN @AcknowledgesCancellation = 1 AND
                    target.CancellationJson IS NOT NULL THEN 1 ELSE target.CancellationAcknowledged END
            WHEN NOT MATCHED THEN INSERT
                (InstanceId, WorkflowName, Status, CorrelationId, SuspensionExpiresAt,
                 SuspensionId, RunnableAtUtc, StateJson, Revision)
                VALUES (@InstanceId, @WorkflowName, @Status, @CorrelationId,
                        @SuspensionExpiresAt, @SuspensionId,
                        CASE WHEN @RunnableNow = 1 THEN SYSUTCDATETIME() ELSE @RunnableAtUtc END,
                        @StateJson, @Revision);
            INSERT INTO dbo.IxIFlowWorkflowCommits (InstanceId, CommitId, ExpectedRevision, Revision, PayloadHash)
            VALUES (@InstanceId, @CommitId, @ExpectedRevision, @Revision, @PayloadHash);
            """, new
        {
            snapshot.InstanceId, snapshot.WorkflowName, snapshot.CorrelationId,
            Status = snapshot.Status.ToString(),
            SuspensionExpiresAt = snapshot.SuspensionInfo?.ExpiresAt,
            SuspensionId = snapshot.SuspensionInfo?.SuspensionId,
            snapshot.RunnableAtUtc,
            RunnableNow = (commit.Parks && !parked) || (unread && snapshot.Status == WorkflowStatus.Suspended) ||
                WorkflowStoreRules.IsRunnableNow(snapshot, unacknowledged),
            commit.InboxReadThrough,
            StateJson = json, snapshot.Revision,
            commit.ExpectedRevision, commit.CommitId, PayloadHash = commit.ContentHash,
            AcknowledgesCancellation = snapshot.CancellationReason != null
        }, transaction);
        // The lease row is written only when something changes, so most commits touch only their
        // instance row; a write to a missing lease row would take range locks other claims need.
        // An idle owner that has not read every arrival keeps the lease and reads them.
        var keepsLease = unread && snapshot.Status == WorkflowStatus.Suspended;
        var releases = (snapshot.Status != WorkflowStatus.Running || parked) && !keepsLease;
        await ApplyWaitChangesAsync(connection, transaction, commit, current?.InboxSequence ?? 0);
        // A finished instance takes no more events; those still pending are closed, and so are its waits.
        if (snapshot.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or
            WorkflowStatus.Terminated or WorkflowStatus.TimedOut)
            await connection.ExecuteAsync("""
                UPDATE dbo.IxIFlowWorkflowEvents SET Outcome = 4, SettledAtUtc = SYSUTCDATETIME()
                WHERE InstanceId = @InstanceId AND Outcome = 0;
                UPDATE dbo.IxIFlowWorkflowWaits SET CloseReason = @Reason, ClosedAtUtc = SYSUTCDATETIME()
                WHERE InstanceId = @InstanceId AND CloseReason IS NULL;
                """, new
            {
                snapshot.InstanceId, Reason = (byte)WorkflowStoreRules.FinalWaitCloseReason(snapshot.Status)
            }, transaction);
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
        await transaction.CommitAsync();
        return new WorkflowCommitResult(WorkflowCommitStatus.Applied, snapshot.Revision,
            ReadReason(current?.CancellationJson), parked, unread);
    }

    // A wait opens at the inbox sequence read under the instance row's lock, which appends also take,
    // so it is ordered exactly against every event. Opening it again keeps its first record.
    private static async Task ApplyWaitChangesAsync(SqlConnection connection, SqlTransaction transaction,
        CheckpointCommit commit, long inboxSequence)
    {
        if (commit.OpenedWaits.Count == 0 && commit.ClosedWaits.Count == 0)
            return;
        var parameters = new DynamicParameters(new { commit.InstanceId, Sequence = inboxSequence });
        var batch = new System.Text.StringBuilder();
        if (commit.OpenedWaits.Count > 0)
        {
            var rows = new List<string>();
            for (var index = 0; index < commit.OpenedWaits.Count; index++)
            {
                var wait = commit.OpenedWaits[index];
                rows.Add($"(@OW{index}, @OK{index}, @OY{index}, @OT{index}, @OD{index})");
                parameters.Add($"OW{index}", wait.WaitId);
                parameters.Add($"OK{index}", (byte)wait.Kind);
                parameters.Add($"OY{index}", wait.Key);
                parameters.Add($"OT{index}", wait.EventType);
                parameters.Add($"OD{index}", wait.DeadlineUtc);
            }
            batch.Append($"""
                INSERT INTO dbo.IxIFlowWorkflowWaits
                    (InstanceId, WaitId, Kind, WaitKey, EventType, DeadlineUtc, OpenedSequence, OpenedAtUtc)
                SELECT @InstanceId, opened.WaitId, opened.Kind, opened.WaitKey, opened.EventType, opened.DeadlineUtc,
                    @Sequence, SYSUTCDATETIME()
                FROM (VALUES {string.Join(", ", rows)}) AS opened (WaitId, Kind, WaitKey, EventType, DeadlineUtc)
                WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowWaits AS stored
                    WHERE stored.InstanceId = @InstanceId AND stored.WaitId = opened.WaitId);

                """);
        }
        if (commit.ClosedWaits.Count > 0)
        {
            var rows = new List<string>();
            for (var index = 0; index < commit.ClosedWaits.Count; index++)
            {
                rows.Add($"(@CW{index}, @CR{index})");
                parameters.Add($"CW{index}", commit.ClosedWaits[index].WaitId);
                parameters.Add($"CR{index}", (byte)commit.ClosedWaits[index].Reason);
            }
            batch.Append($"""
                UPDATE stored SET CloseReason = closed.Reason, ClosedAtUtc = SYSUTCDATETIME()
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
        var row = await connection.QuerySingleOrDefaultAsync<StoredRow>(
            "SELECT StateJson, RunnableAtUtc FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId",
            new { InstanceId = instanceId });
        return row == null ? null : row.Read();
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
        // The lease row stays so a recreated instance never reuses an epoch; its crashes go.
        await connection.ExecuteAsync(
            "UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc = CAST('0001-01-01' AS DATETIME2), CrashCount = 0 WHERE InstanceId = @InstanceId",
            new { InstanceId = instanceId });
        // The instance row is locked first, as commits and appends lock it, and its rows in the other
        // tables are deleted by key. A cascading delete would take range locks in those tables, which
        // deadlock with commits of other instances inserting into the same ranges.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("""
            SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE InstanceId = @InstanceId;
            DELETE FROM dbo.IxIFlowWorkflowWaits WHERE InstanceId = @InstanceId;
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
            $"SELECT StateJson, RunnableAtUtc FROM dbo.IxIFlowWorkflowInstances WHERE {predicate}",
            new { Value = value });
        return rows.Select(row => row.Read()).ToArray();
    }

    // The runnable time is kept in its column, which the store may change without rewriting the state.
    private sealed class StoredRow
    {
        public string StateJson { get; set; } = "";
        public DateTime? RunnableAtUtc { get; set; }

        public WorkflowInstance Read()
        {
            var instance = Deserialize(StateJson);
            instance.RunnableAtUtc = RunnableAtUtc;
            return instance;
        }
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
                        SuspensionExpiresAt DATETIME2 NULL,
                        RunnableAtUtc DATETIME2 NULL,
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
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'InboxSequence') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances
                        ADD InboxSequence BIGINT NOT NULL CONSTRAINT DF_IxIFlowWorkflowInstances_InboxSequence DEFAULT 0;
                IF COL_LENGTH(N'dbo.IxIFlowWorkflowInstances', N'InboxReadThrough') IS NULL
                    ALTER TABLE dbo.IxIFlowWorkflowInstances
                        ADD InboxReadThrough BIGINT NOT NULL
                            CONSTRAINT DF_IxIFlowWorkflowInstances_InboxReadThrough DEFAULT 0;
                IF OBJECT_ID(N'dbo.IxIFlowWorkflowEvents', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.IxIFlowWorkflowEvents (
                        InstanceId NVARCHAR(100) NOT NULL,
                        EventId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
                        Sequence BIGINT NOT NULL,
                        EventType NVARCHAR(400) NOT NULL,
                        EventKey NVARCHAR(400) NULL,
                        TargetWaitId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
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
