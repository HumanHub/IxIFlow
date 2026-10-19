using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

/// <summary>Runs the relational store protocol. Providers supply SQL and database behavior.</summary>
public abstract partial class RelationalWorkflowStateRepository :
    IWorkflowStateRepository
{
    // Only first-party friend assemblies can construct or derive from this protocol.
    internal RelationalWorkflowStateRepository(OutboxSettings? outboxSettings = null)
    {
        var options = outboxSettings ?? new OutboxSettings();
        options.Validate();
        _retryHorizon = options.RetryHorizon;
    }

    private readonly TimeSpan _retryHorizon;

    /// <summary>The connection, transaction and admission policy for a store operation.</summary>
    internal enum Operation
    {
        Schema, Read, Query, OutboxRead, OutboxClaim, OutboxMark, Purge, Registry,
        CancellationRequest, CancellationRead, LeaseClaim, LeaseRenew, LeaseRelease,
        Append, InboxPage, Commit, Delete
    }

    /// <summary>SQL steps in the shared protocol. Optional steps return null.</summary>
    internal enum Statement
    {
        WebhookDeliveryRead, WebhookDeliveryDelete, WebhookDeliveryInsert, WebhookDeliveryExpire,
        AdmissionLock, AdmissionRead, AdmissionDelete, AdmissionInsert, OwnershipRead, OwnershipInsert, OwnershipDelete, AdmissionExpire, OwnershipExpire,
        RegistryLock, RegisterMember, MemberHeartbeat, UnregisterMember, PruneMembers,
        Members, MemberCapabilities, DeleteCapabilities, InsertCapability,
        RetentionLock, RetentionCandidates, RetentionBlockedPublishers, RetentionEligible, StampEnd, Tombstone, WriteTombstone, ExpireTombstones,
        OutboxClock, PublisherLock, PublisherSharedLock, UnregisterPublisher, PublisherUpsert, Publishers, PublisherRead, BackfillDeliveries,
        InsertOutboxEvent, InsertDelivery, ClaimDeliveries, DeliveryLock, DeliveryUpdate, DeliveryRead, DisablePublisher, ExpireUnattemptedDeliveries,
        Cancellation, Cancellations, Running, ClaimLease, RenewLease, ReleaseLease,
        LockIdentity, LockInbox, ParentInbox, CommitState, Receipt, Lease, TakenChild,
        EventHash, CandidateWait, AdvanceInbox, InsertEvent, PendingEvents, Event, InboxSequences,
        Wait, OpenWaits, EarliestDeadline, DeleteAllParts, DeleteParts, InsertCommit, UpdateCommit, FinishInbox, UpdateLease,
        ReadInstance, ReadParts, Query, ByName, ByStatus, ByCorrelation, ByParent,
        LockAndReadPage, DeleteLease,
        DeleteState, DeleteParentInbox, DeleteRows, Trace
    }

    /// <summary>The result of the provider's atomic cancellation SQL.</summary>
    internal enum CancellationOutcome { Unchanged, Recorded, MadeRunnable, Gone }

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;
    private SemaphoreSlim? _admissions;

    /// <summary>Creates tables and indexes on an open connection, including cross-host schema locking.</summary>
    internal abstract Task CreateSchemaAsync(DbConnection connection);

    /// <summary>Checks provider lifecycle constraints before any operation, including initialized schemas.</summary>
    internal virtual void ThrowIfUnavailable() { }

    private async Task EnsureSchemaAsync()
    {
        ThrowIfUnavailable();
        if (_schemaReady)
            return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady)
                return;
            await ExecuteOperationAsync(Operation.Schema, async () =>
            {
                await using var connection = await OpenConnectionAsync();
                await CreateSchemaAsync(connection);
                return true;
            });
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    // A different application name reserves a driver pool for ownership traffic (B-09).
    internal string ConfigureOwnershipPool(DbConnectionStringBuilder ownership, int ownershipPoolSize,
        string maxPoolSizeKey, string minPoolSizeKey, int maxApplicationNameLength = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ownershipPoolSize, 1);
        const string suffix = " IxIFlow ownership";
        var name = (string?)ownership["Application Name"] ?? "";
        var limit = maxApplicationNameLength - suffix.Length;
        ownership["Application Name"] = (name.Length > limit ? name[..limit] : name) + suffix;
        ownership[maxPoolSizeKey] = ownershipPoolSize;
        ownership[minPoolSizeKey] = Math.Min((int)ownership[minPoolSizeKey], ownershipPoolSize);
        // Claims and cancellation requests can wait on commit locks; together they use at most half
        // the reserved pool, so renewals and the cancellation watch always find a connection.
        _admissions = new SemaphoreSlim(Math.Max(1, ownershipPoolSize / 2));
        return ownership.ConnectionString;
    }
    /// <summary>Opens the main or reserved ownership connection; the caller disposes it.</summary>
    internal abstract Task<DbConnection> OpenConnectionAsync(bool ownership = false);
    /// <summary>Chooses the operation transaction or autocommit; successful writes are committed by the core.</summary>
    internal abstract ValueTask<DbTransaction?> BeginTransactionAsync(DbConnection connection, Operation operation);
    /// <summary>Supplies fixed SQL; only explicitly optional protocol steps may be absent.</summary>
    internal abstract string? Sql(Statement statement);
    /// <summary>Reads a trunk and selected parts, with stamp rechecks where snapshots do not join them.</summary>
    internal abstract string ReadInstanceSql(string filter, bool wholly);
    /// <summary>Builds a provider query from an internal predicate; caller values remain parameters.</summary>
    internal abstract string QuerySql(string filter);

    private string RequiredSql(Statement statement) => RequireSql(statement, Sql(statement));

    private string RequireSql(Statement statement, string? sql) => CheckSql(statement.ToString(), sql);

    private string CheckSql(string statement, string? sql) => !string.IsNullOrWhiteSpace(sql)
        ? sql : throw new InvalidOperationException($"Relational provider '{GetType().FullName}' is missing required statement '{statement}'.");

    /// <summary>
    /// Locks and rechecks a possibly changed
    /// parent under the identity lock; an absent locked row must leave any later insertion whole.
    /// </summary>
    internal abstract string? DeleteRevalidationSql { get; }

    /// <summary>Supplies commit time only when the lease SQL compares expiry against that captured clock.</summary>
    internal virtual object LeaseParameters(string instanceId, DateTime now) =>
        WithTimes(new { InstanceId = instanceId }, ("Now", now));
    /// <summary>Converts a UTC timestamp to the driver representation without losing its precision.</summary>
    internal abstract object? At(DateTime? value);
    /// <summary>Wire type used for all timestamp parameters, for checkpoint and lease operations.</summary>
    internal virtual DbType? TimeDbType => null;
    /// <summary>Wire type for wait kinds, event rejections and close reasons.</summary>
    internal virtual DbType SmallIntDbType => DbType.Int16;
    /// <summary>Converts protocol small integers to the driver representation.</summary>
    internal virtual object? SmallInt(short? value) => value;
    /// <summary>Normalizes timestamps returned by the driver to the provider's UTC representation.</summary>
    internal virtual DateTime ReadTime(DateTime value) => Utc(value);
    /// <summary>The database has no server clock; sample LocalNow only after its write or inbox lock.</summary>
    internal virtual bool UsesLocalClock => false;
    /// <summary>SQLite's owned repository may advertise one host and no coordinator.</summary>
    internal virtual bool SingleHostRegistry => false;
    /// <summary>Reads the injected local UTC clock while the operation holds the required lock.</summary>
    internal virtual DateTime LocalNow() => throw new InvalidOperationException("The provider uses its server clock");
    /// <summary>Reads need revision and incarnation rechecks because no transaction snapshot joins trunk and parts.</summary>
    internal virtual bool ReadsUseStamps => true;
    /// <summary>Driver timestamps in runnable columns need conversion or stamping to UTC on instance reads.</summary>
    internal virtual bool NormalizeRunnableTime => true;
    /// <summary>Lease release returns a nullable due flag for a released instance that may need a wake-up.</summary>
    internal virtual bool ReleaseReturnsDue => false;
    /// <summary>Bulk child insertion needs ordinal key order to avoid deadlocks between overlapping child sets.</summary>
    internal virtual bool SortChildStarts => false;
    /// <summary>Maximum rows per statement under the driver's parameter limit.</summary>
    internal virtual int StatementBatchSize => int.MaxValue;
    /// <summary>Maximum IDs per ownership read under the driver's parameter limit.</summary>
    internal virtual int OwnershipReadBatchSize => int.MaxValue;
    /// <summary>Maps database concurrency failures that require retrying the entire atomic commit.</summary>
    internal virtual bool IsRetryableCommit(Exception error) => false;
    /// <summary>Applies admission around the whole operation; SQLite instead serializes writes.</summary>
    internal virtual async Task<T> ExecuteOperationAsync<T>(Operation operation, Func<Task<T>> call)
    {
        if (_admissions == null || operation is not (Operation.LeaseClaim or Operation.CancellationRequest or Operation.CancellationRead))
            return await call();
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
    /// <summary>Publishes a transactional wake-up when this provider supports runnable notifications.</summary>
    internal virtual Task NotifyRunnableAsync(DbConnection connection, DbTransaction? transaction) => Task.CompletedTask;
    internal abstract Task<CancellationOutcome> RequestCancellationAsync(DbConnection connection,
        DbTransaction? transaction, string instanceId, string reasonJson, DateTime? now);
    internal abstract Task<int> SettleAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<EventSettlement> settlements, DateTime now);
    internal abstract Task InsertWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<OpenedWait> waits, long inboxSequence, DateTime now);
    internal abstract Task ArmWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ArmedWait> waits, DateTime now);
    internal abstract Task CloseWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ClosedWait> waits, DateTime now);
    internal abstract Task InsertPartsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<CheckpointPart> parts);
    internal abstract Task AppendTraceAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<string> entries);
    internal static string StampStartPayload(string payload, DateTime now) => WorkflowDemands.StampStartPayload(payload, now);

    private async Task InsertDemandsAsync(DbConnection connection, DbTransaction transaction, string payload)
    {
        var instance = JsonSerializer.Deserialize<WorkflowInstance>(payload)!;
        foreach (var demand in WorkflowDemands.Saved(instance))
            await connection.ExecuteAsync(InsertDemandSql, WithTimes(new
            {
                instance.InstanceId, demand.Key, demand.Value, demand.Inherit
            }, ("ExpiresAtUtc", demand.ExpiresAtUtc)), transaction);
    }

    private sealed class DemandRow
    {
        public string Key { get; set; } = "";
        public string? Value { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public bool Inherit { get; set; }
    }

    public async Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<DemandRow>(ReadDemandsSql, new { InstanceId = instanceId });
        return rows.Select(row => new WorkflowDemand(row.Key, row.Value,
            row.ExpiresAtUtc is { } expiry ? ReadTime(expiry) : null, row.Inherit))
            .OrderBy(demand => demand.Key, StringComparer.Ordinal).ToArray();
    }

    internal abstract string ReadDemandsSql { get; }
    internal abstract string InsertDemandSql { get; }

    internal abstract Task InsertChildrenAsync(DbConnection connection, DbTransaction transaction,
        string parentInstanceId, IReadOnlyList<ChildStart> children, DateTime now);

    // Values keep the provider's wire type and precision, including SQL Server datetime2.
    internal DynamicParameters WithTimes(object values, params (string Name, DateTime? Value)[] times)
    {
        var parameters = new DynamicParameters(values);
        foreach (var (name, value) in times)
            parameters.Add(name, At(value), TimeDbType);
        return parameters;
    }

    private DynamicParameters WithIntegers(object values, params (string Name, short? Value)[] integers)
    {
        var parameters = new DynamicParameters(values);
        foreach (var (name, value) in integers)
            parameters.Add(name, SmallInt(value), SmallIntDbType);
        return parameters;
    }

    private DynamicParameters WithLocalTime(object values, TimeSpan? duration = null)
    {
        var parameters = new DynamicParameters(values);
        if (UsesLocalClock)
        {
            var now = LocalNow();
            parameters.Add("Now", At(now), TimeDbType);
            if (duration != null)
                parameters.Add("Until", At(now + duration), TimeDbType);
        }
        return parameters;
    }

    private DynamicParameters ClaimParameters(string? instanceId, string token, TimeSpan duration)
    {
        var parameters = WithLocalTime(new { InstanceId = instanceId, Token = token }, duration);
        if (!UsesLocalClock)
            parameters.Add("Milliseconds", (int)Math.Ceiling(duration.TotalMilliseconds));
        return parameters;
    }

    private static Task CompleteAsync(DbTransaction? transaction) => transaction?.CommitAsync() ?? Task.CompletedTask;

    private Task ExecuteOptionalAsync(DbConnection connection, DbTransaction? transaction,
        Statement statement, object? values = null, CancellationToken cancellationToken = default) => Sql(statement) is { } sql
            ? connection.ExecuteAsync(new CommandDefinition(RequireSql(statement, sql), values, transaction,
                cancellationToken: cancellationToken)) : Task.CompletedTask;

    private async Task<(T? Row, DateTime Now)> LockAsync<T>(DbConnection connection, DbTransaction transaction,
        Statement statement, string instanceId, bool clock = true, CancellationToken cancellationToken = default)
    {
        using var read = await connection.QueryMultipleAsync(new CommandDefinition(RequiredSql(statement),
            new { InstanceId = instanceId }, transaction, cancellationToken: cancellationToken));
        var row = await read.ReadSingleOrDefaultAsync<T>();
        var now = clock ? UsesLocalClock ? LocalNow() : ReadTime(await read.ReadSingleAsync<DateTime>()) : default;
        return (row, now);
    }

    public async Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.CancellationRequest, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true);
            await using var transaction = await BeginTransactionAsync(connection, Operation.CancellationRequest);
            var outcome = await RequestCancellationAsync(connection, transaction, instanceId,
                JsonSerializer.Serialize(reason), UsesLocalClock ? LocalNow() : null);
            if (outcome == CancellationOutcome.MadeRunnable)
                await NotifyRunnableAsync(connection, transaction);
            await CompleteAsync(transaction);
            return outcome != CancellationOutcome.Gone && outcome != CancellationOutcome.Unchanged;
        });
    }

    public async Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.CancellationRead, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true);
            var json = await connection.QuerySingleOrDefaultAsync<string>(RequiredSql(Statement.Cancellation), new { InstanceId = instanceId });
            return json == null ? null : JsonSerializer.Deserialize<CancellationReason>(json);
        });
    }

    public async Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        var requests = new Dictionary<string, CancellationReason>(StringComparer.Ordinal);
        if (instanceIds.Count == 0)
            return requests;
        DbConnection connection;
        try
        {
            await EnsureSchemaAsync();
            connection = await OpenConnectionAsync(ownership: true);
        }
        catch (Exception error)
        {
            throw new AggregateException("No cancellation request could be read", error);
        }
        await using var ownedConnection = connection;
        var ids = instanceIds.Distinct(StringComparer.Ordinal).ToArray();
        var failures = new List<Exception>();
        var failedReads = 0;
        foreach (var batch in ids.Chunk(OwnershipReadBatchSize))
        {
            IEnumerable<(string InstanceId, string CancellationJson)> rows;
            try
            {
                rows = await connection.QueryAsync<(string InstanceId, string CancellationJson)>(
                    RequiredSql(Statement.Cancellations), new { InstanceIds = batch });
            }
            catch (Exception error)
            {
                failures.Add(error);
                failedReads += batch.Length;
                continue;
            }
            foreach (var row in rows)
                try
                {
                    if (JsonSerializer.Deserialize<CancellationReason>(row.CancellationJson) is { } reason)
                        requests[row.InstanceId] = reason;
                }
                catch (Exception error)
                {
                    failures.Add(error);
                    failedReads++;
                }
        }
        if (failedReads == ids.Length)
            throw new AggregateException("No cancellation request could be read", failures);
        return requests;
    }

    public async Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit == 0) return [];
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var now = await OutboxNowAsync(connection, null);
        var rows = await connection.QueryAsync<RunnableRow>(RunnableSql,
            PlacementParameters(query, now, query.Limit));
        return rows.Select(row =>
            new RunnableInstance(row.InstanceId, ReadTime(row.RunnableAtUtc))).ToArray();
    }

    public async Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync<ExecutionLeaseGrant?>(Operation.LeaseClaim, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true);
            await using var transaction = (await BeginTransactionAsync(connection, Operation.LeaseClaim))!;
            await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity, new { InstanceId = instanceId });
            var running = await connection.ExecuteScalarAsync<int>(RequiredSql(Statement.Running),
                new { InstanceId = instanceId }, transaction) > 0;
            var now = await OutboxNowAsync(connection, transaction);
            var eligibility = PlacementParameters(query, now, query.Limit);
            eligibility.Add("InstanceId", instanceId);
            eligibility.Add("Operator", kind == ExecutionClaimKind.Operator);
            if (await TombstoneAsync(connection, transaction, instanceId, now) != null ||
                await connection.ExecuteScalarAsync<int>(ClaimEligibilitySql, eligibility, transaction) != 1)
            {
                await transaction.CommitAsync();
                return null;
            }
            // Capture SQLite's lease clock only after its writer and instance locks.
            var parameters = ClaimParameters(instanceId, token, TimeSpan.FromMilliseconds(milliseconds));
            parameters.Add("Running", running);
            var grant = await connection.QuerySingleOrDefaultAsync<LeaseGrantRow>(RequiredSql(Statement.ClaimLease), parameters, transaction);
            if (grant != null)
            {
                await connection.ExecuteAsync(SetLeaseHostSql, new { InstanceId = instanceId, query.HostId }, transaction);
                await connection.ExecuteAsync(StampHostSql, new { InstanceId = instanceId }, transaction);
            }
            await transaction.CommitAsync();
            return grant == null ? null : new ExecutionLeaseGrant(grant.Epoch, grant.CrashCount);
        });
    }

    public async Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration)
    {
        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.LeaseRenew, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true);
            await using var transaction = await BeginTransactionAsync(connection, Operation.LeaseRenew);
            var changed = await connection.ExecuteAsync(RequiredSql(Statement.RenewLease),
                ClaimParameters(instanceId, token, TimeSpan.FromMilliseconds(milliseconds)), transaction);
            await CompleteAsync(transaction);
            return changed == 1;
        });
    }

    public async Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.LeaseRelease, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true);
            await using var transaction = await BeginTransactionAsync(connection, Operation.LeaseRelease);
            var parameters = WithLocalTime(new { InstanceId = instanceId, Token = token });
            bool released;
            if (ReleaseReturnsDue)
            {
                var due = await connection.QuerySingleOrDefaultAsync<bool?>(RequiredSql(Statement.ReleaseLease), parameters, transaction);
                released = due != null;
                if (due == true)
                    await NotifyRunnableAsync(connection, transaction);
            }
            else
                released = await connection.ExecuteAsync(RequiredSql(Statement.ReleaseLease), parameters, transaction) == 1;
            await CompleteAsync(transaction);
            return released;
        });
    }

    public async Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        workflowEvent.Validate();
        var hash = workflowEvent.ContentHash();
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.Append, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = (await BeginTransactionAsync(connection, Operation.Append))!;
            if (workflowEvent.DeliveryIdentity is { } delivery)
            {
                await ExecuteOptionalAsync(connection, transaction, Statement.AdmissionLock, new { Identity = delivery });
                if (await ReadWebhookDeliveryAsync(connection, transaction, workflowEvent, hash) is { } duplicate)
                {
                    await transaction.CommitAsync();
                    return duplicate;
                }
            }
            var (instance, now) = await LockAsync<InboxRow>(connection, transaction, Statement.LockInbox, workflowEvent.InstanceId);
            var result = await AppendAsync(connection, transaction, workflowEvent, hash, instance, now);
            if (workflowEvent.DeliveryIdentity != null && result.Status is EventAppendStatus.Stored or EventAppendStatus.Duplicate or EventAppendStatus.Rejected)
                await WriteWebhookDeliveryAsync(connection, transaction, workflowEvent, hash, result, now);
            if (result.MadeRunnable)
                await NotifyRunnableAsync(connection, transaction);
            await transaction.CommitAsync();
            return result;
        });
    }

    private async Task<EventAppendResult> AppendAsync(DbConnection connection, DbTransaction transaction,
        WorkflowEvent workflowEvent, byte[] hash, InboxRow? instance, DateTime now)
    {
        var existing = await connection.QuerySingleOrDefaultAsync<EventHashRow>(RequiredSql(Statement.EventHash),
            new { workflowEvent.InstanceId, workflowEvent.EventId }, transaction);
        if (existing != null)
            return existing.PayloadHash.SequenceEqual(hash)
                ? new EventAppendResult(EventAppendStatus.Duplicate, existing.Sequence)
                : new EventAppendResult(EventAppendStatus.Conflict);
        if (instance == null)
            return new EventAppendResult(await TombstoneAsync(connection, transaction, workflowEvent.InstanceId, now) != null
                ? EventAppendStatus.Closed : EventAppendStatus.NotFound);
        var finished = instance.Status is "Completed" or "Failed" or "Cancelled" or "Terminated" or "TimedOut";
        if (finished && workflowEvent.TargetWaitId == null)
        {
            await TraceRefusalAsync(connection, transaction, workflowEvent, EventRejection.InstanceFinished, now, instance.TracingEnabled);
            return new EventAppendResult(EventAppendStatus.Closed);
        }
        var waits = (await connection.QueryAsync<WaitRow>(RequiredSql(Statement.CandidateWait), WithTimes(WithIntegers(new
        {
            workflowEvent.InstanceId,
            workflowEvent.TargetWaitId,
            workflowEvent.Key
        }, ("Event", (short)WaitKind.Event)), ("Now", now)), transaction)).Select(row => row.Read(ReadTime)).ToArray();
        var rejection = WorkflowStoreRules.RejectOnArrival(workflowEvent, waits.SingleOrDefault(), waits, now)
            ?? (finished ? EventRejection.InstanceFinished : null);
        var sequence = rejection == null ? instance.InboxSequence + 1 : 0;
        var madeRunnable = rejection == null && !instance.Stopped &&
            instance.Status is "Running" or "Suspended" or "NeedsResolution";
        var advanceSql = Sql(Statement.AdvanceInbox);
        var parameters = WithTimes(WithIntegers(new
        {
            workflowEvent.InstanceId,
            workflowEvent.EventId,
            Sequence = sequence,
            workflowEvent.EventType,
            workflowEvent.Key,
            workflowEvent.TargetWaitId,
            workflowEvent.Subject,
            workflowEvent.PayloadJson,
            Hash = hash
        }, ("Rejection", (short?)rejection)), ("Now", now));
        if (advanceSql == null)
            parameters.Add("MadeRunnable", madeRunnable);
        if (rejection == null && advanceSql != null)
            await connection.ExecuteAsync(RequireSql(Statement.AdvanceInbox, advanceSql),
                WithTimes(new { workflowEvent.InstanceId, Sequence = sequence, MadeRunnable = madeRunnable }, ("Now", now)), transaction);
        await connection.ExecuteAsync(RequiredSql(Statement.InsertEvent), parameters, transaction);
        // A refusal on arrival never reaches an executor pass. Store its one entry atomically with the
        // refusal, using the shared trace append and the same instance lock as checkpoint commits.
        if (rejection is { } refused)
            await TraceRefusalAsync(connection, transaction, workflowEvent, refused, now, instance.TracingEnabled);
        return rejection == null
            ? new EventAppendResult(EventAppendStatus.Stored, sequence, madeRunnable)
            : new EventAppendResult(EventAppendStatus.Rejected, Rejection: rejection);
    }

    private async Task TraceRefusalAsync(DbConnection connection, DbTransaction transaction,
        WorkflowEvent workflowEvent, EventRejection rejection, DateTime now, bool tracingEnabled)
    {
        if (tracingEnabled)
            await AppendTraceAsync(connection, transaction, workflowEvent.InstanceId,
                [WorkflowStoreRules.SerializeTrace(WorkflowStoreRules.RefusedEventTrace(workflowEvent, rejection, now))]);
    }

    public async Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.InboxPage, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = (await BeginTransactionAsync(connection, Operation.InboxPage))!;
            var through = 0L;
            var now = default(DateTime);
            var combinedPageSql = Sql(Statement.LockAndReadPage);
            var pageSql = combinedPageSql == null ? null : RequireSql(Statement.LockAndReadPage, combinedPageSql);
            var parameters = new DynamicParameters(new { InstanceId = instanceId, AfterSequence = afterSequence, Limit = limit });
            if (pageSql == null)
            {
                var locked = await LockAsync<InboxRow>(connection, transaction, Statement.LockInbox, instanceId);
                through = locked.Row?.InboxSequence ?? 0;
                now = locked.Now;
                parameters.Add("Through", through);
                pageSql = RequiredSql(Statement.PendingEvents);
            }
            StoredWorkflowEvent[] events;
            PageWait[] waits;
            using (var results = await connection.QueryMultipleAsync(pageSql, parameters, transaction))
            {
                if (combinedPageSql != null)
                {
                    var page = await results.ReadSingleAsync<PageBoundary>();
                    through = page.Through;
                    now = ReadTime(page.Now);
                }
                events = (await results.ReadAsync<EventRow>()).Select(row => row.Read(ReadTime)).ToArray();
                waits = (await results.ReadAsync<PageWait>()).ToArray();
            }
            await transaction.CommitAsync();
            return InboxPage.Of(events, limit, afterSequence, through,
                waits.ToDictionary(wait => wait.WaitId, wait => wait.OpenedSequence, StringComparer.Ordinal),
                waits.Where(wait => wait.DeadlineUtc != null).ToDictionary(wait => wait.WaitId,
                    wait => ReadTime(wait.DeadlineUtc!.Value), StringComparer.Ordinal), now);
        });
    }

    public async Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<EventRow>(RequiredSql(Statement.Event), new { InstanceId = instanceId, EventId = eventId });
        return row?.Read(ReadTime);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);
        if (instanceIds.Count == 0)
            return sequences;
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync(ownership: true);
        foreach (var batch in instanceIds.Distinct(StringComparer.Ordinal).Chunk(OwnershipReadBatchSize))
            foreach (var row in await connection.QueryAsync<(string InstanceId, long InboxSequence)>(
                RequiredSql(Statement.InboxSequences), new { InstanceIds = batch }))
                sequences[row.InstanceId] = row.InboxSequence;
        return sequences;
    }

    public async Task<StoredWait?> GetWaitAsync(string instanceId, string waitId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var row = await connection.QuerySingleOrDefaultAsync<WaitRow>(RequiredSql(Statement.Wait), new { InstanceId = instanceId, WaitId = waitId });
        return row?.Read(ReadTime);
    }

    public async Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<WaitRow>(RequiredSql(Statement.OpenWaits), WithIntegers(new
        {
            query.Limit,
            query.InstanceId,
            query.Key,
            query.EventType,
            query.AfterWaitId
        }, ("Kind", (short?)query.Kind), ("Delay", (short)WaitKind.Delay), ("Execution", (short)WaitKind.Execution)));
        return rows.Select(row => row.Read(ReadTime)).ToArray();
    }

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsureSchemaAsync();
        try
        {
            return await ExecuteOperationAsync(Operation.Commit, () => CommitAsync(commit));
        }
        catch (Exception error) when (IsRetryableCommit(error))
        {
            return new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0);
        }
    }

    // Lock order invariant: parent, then identity, then instance, then lease. A child completing into
    // its parent inbox and a parent cancelling its children must acquire these locks in the same order.
    private async Task<WorkflowCommitResult> CommitAsync(CheckpointCommit commit, WorkflowAdmission? admission = null)
    {
        var snapshot = commit.Instance;
        var revision = checked(commit.ExpectedRevision + 1);
        await using var connection = await OpenConnectionAsync();
        await using var transaction = (await BeginTransactionAsync(connection, Operation.Commit))!;
        WorkflowAdmissionRecord? admitted = null;
        if (admission != null)
        {
            await ExecuteOptionalAsync(connection, transaction, Statement.AdmissionLock, new { Identity = AdmissionLockId(admission) });
            var admissionNow = await OutboxNowAsync(connection, transaction);
            if (await ReadAdmissionAsync(connection, transaction, admission.Sender, admission.Trigger, admission.Key, admissionNow) is { } winner)
            {
                await transaction.CommitAsync();
                return new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, 0) { Admission = winner };
            }
            admitted = new(admission.Sender, admission.Trigger, admission.Key, admission.Fingerprint,
                commit.InstanceId, snapshot.WorkflowName, commit.ReadTrunk().WorkflowVersion, admissionNow + admission.KeyRetention, commit.Incarnation);
        }
        // A child's terminal commit appends to its parent's inbox. Rows are locked parent first, as a
        // parent's commit that cancels its children does, so the two cannot deadlock.
        // SQLite already holds its write lock. Server clocks are read after the row locks below.
        var now = UsesLocalClock ? LocalNow() : default;
        var parent = commit.ParentNotice is { } notice
            ? (await LockAsync<InboxRow>(connection, transaction, Statement.ParentInbox,
                notice.ParentInstanceId, clock: false)).Row : null;
        await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity, new { commit.InstanceId });
        CommitState? current;
        if (UsesLocalClock)
            current = await connection.QuerySingleOrDefaultAsync<CommitState>(RequiredSql(Statement.CommitState),
                new { commit.InstanceId }, transaction);
        else
            (current, now) = await LockAsync<CommitState>(connection, transaction, Statement.CommitState, commit.InstanceId);
        await RefuseRemovedAsync(connection, transaction, commit.InstanceId, now);
        var currentRevision = current?.Revision ?? 0;
        var receipt = await connection.QuerySingleOrDefaultAsync<CommitReceipt>(RequiredSql(Statement.Receipt), new { commit.InstanceId, commit.CommitId }, transaction);
        // Receipts are deleted with the instance, so a present receipt belongs to its current creation.
        if (receipt == null && commit.ExpectedRevision > 0 &&
            current != null && current.Incarnation != commit.Incarnation)
        {
            await transaction.CommitAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, currentRevision);
        }
        // Replays answer their receipt without re-admitting children that may already be removed.
        if (receipt == null && commit.ChildStarts.Count > 0)
        {
            foreach (var child in commit.ChildStarts.OrderBy(c => c.ChildInstanceId, StringComparer.Ordinal))
            {
                // Child insertion must not wait for the child's advisory identity lock: a delete
                // waiting there can discover this new child and retry with its parent locked first.
                // Lock an existing child row before checking its tombstone. If purge deletes that
                // row while we wait, the following read sees the tombstone it committed atomically.
                var (_, childNow) = await LockAsync<CommitState>(connection, transaction, Statement.CommitState, child.ChildInstanceId);
                await RefuseRemovedAsync(connection, transaction, child.ChildInstanceId, childNow);
            }
            // Child row locks may have waited; fencing must use fresh store time.
            now = await OutboxNowAsync(connection, transaction);
        }
        var lease = await connection.QuerySingleOrDefaultAsync<LeaseRecord>(RequiredSql(Statement.Lease), LeaseParameters(commit.InstanceId, now), transaction);
        var owns = admission != null ? current == null && lease is not { IsActive: true }
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
        if (commit.ChildStarts.Count > 0 && await connection.ExecuteScalarAsync<string?>(RequiredSql(Statement.TakenChild), new { Ids = commit.ChildStarts.Select(child => child.ChildInstanceId).ToArray() }, transaction)
            is { } taken)
            throw new InvalidOperationException(
                $"Child workflow '{taken}' cannot start: an instance with its ID exists");

        // An event this commit settles must still be pending (D-04).
        var settled = 0;
        foreach (var batch in commit.Settlements.Chunk(StatementBatchSize))
            settled += await SettleAsync(connection, transaction, commit.InstanceId, batch, now);
        if (settled != commit.Settlements.Count)
        {
            await transaction.RollbackAsync();
            return new WorkflowCommitResult(WorkflowCommitStatus.EventsChanged, currentRevision);
        }
        var unacknowledged = current is { CancellationJson: not null, CancellationAcknowledged: false };
        // A run that has not read every arrival neither parks nor goes idle out of reach.
        var unread = commit.InboxReadThrough is { } readThrough && current?.InboxSequence > readThrough;
        var parked = commit.Parks && !unacknowledged && !unread;
        foreach (var batch in commit.OpenedWaits.Chunk(StatementBatchSize))
            await InsertWaitsAsync(connection, transaction, commit.InstanceId, batch, current?.InboxSequence ?? 0, now);
        foreach (var batch in commit.ArmedWaits.Chunk(StatementBatchSize))
            await ArmWaitsAsync(connection, transaction, commit.InstanceId, batch, now);
        foreach (var batch in commit.ClosedWaits.Chunk(StatementBatchSize))
            await CloseWaitsAsync(connection, transaction, commit.InstanceId, batch, now);
        // An idle instance is next due at the earliest deadline of its open waits (D-31, D-35).
        var runnableNow = (commit.Parks && !parked) || (unread && !commit.Stopped &&
            snapshot.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution) ||
            WorkflowStoreRules.UnwindsNow(snapshot.Status, snapshot.AcknowledgesCancellation, unacknowledged);
        var earliestDeadline = !runnableNow && !commit.Stopped &&
                               snapshot.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution
            ? await connection.ExecuteScalarAsync<DateTime?>(RequiredSql(Statement.EarliestDeadline), new { commit.InstanceId }, transaction)
            : null;
        var runnableAt = WorkflowStoreRules.CommittedRunnableAt(snapshot.Status, commit, runnableNow, now,
            earliestDeadline is { } deadline && NormalizeRunnableTime ? Utc(deadline) : earliestDeadline);
        var values = WithTimes(new
        {
            snapshot.InstanceId,
            snapshot.WorkflowName,
            snapshot.CorrelationId,
            snapshot.ParentInstanceId,
            commit.Incarnation,
            Status = snapshot.Status.ToString(),
            commit.InboxReadThrough,
            StateJson = current == null ? StampStartPayload(commit.Payload, now) : commit.Payload,
            Revision = revision,
            commit.ExpectedRevision,
            commit.CommitId,
            PayloadHash = commit.ContentHash,
            snapshot.AcknowledgesCancellation
        }, ("RunnableAtUtc", runnableAt));
        // A missing row is inserted, so a commit racing to create the same instance violates its key and
        // is retried, rather than overwriting it.
        await connection.ExecuteAsync(current == null
            ? RequiredSql(Statement.InsertCommit)
            : RequiredSql(Statement.UpdateCommit), values, transaction);
        if (admission == null)
            await connection.ExecuteAsync(StampHostSql, new { commit.InstanceId }, transaction);
        if (current == null)
            await InsertDemandsAsync(connection, transaction, StampStartPayload(commit.Payload, now));
        await ApplyPartsAsync(connection, transaction, commit);
        await RecordCompletionAsync(connection, transaction, WorkflowStoreRules.IsTerminal(snapshot.Status) ? commit.ReadTrunk() : null, revision, now);
        if (commit.TraceEntries.Count > 0)
            await AppendTraceAsync(connection, transaction, commit.InstanceId, commit.TraceEntries);
        // The lease row is written only when something changes, so most commits touch only their
        // instance row. An idle owner that has not read every arrival keeps the lease and reads them.
        var keepsLease = unread && snapshot.Status == WorkflowStatus.Suspended;
        var releases = (snapshot.Status != WorkflowStatus.Running || parked) && !keepsLease;
        // A finished instance takes no more events; those still pending are closed, and so are its waits.
        if (WorkflowStoreRules.IsTerminal(snapshot.Status))
            await connection.ExecuteAsync(RequiredSql(Statement.FinishInbox), WithTimes(WithIntegers(new
            {
                snapshot.InstanceId
            }, ("Reason", (short)WorkflowStoreRules.FinalWaitCloseReason(snapshot.Status))), ("Now", now)), transaction);
        var clears = commit.ClearsCrashCount && lease?.CrashCount > 0;
        if (lease != null && (releases || clears))
            await connection.ExecuteAsync(RequiredSql(Statement.UpdateLease), new { snapshot.InstanceId, Released = releases, ClearsCrashCount = clears }, transaction);
        var madeRunnable = new List<string>();
        // Each child starts as its own instance, runnable now, with no owner yet (D-17).
        // The payload is stored as the start carries it; the child's revision and runnable time live in their columns.
        if (commit.ChildStarts.Count > 0)
        {
            var children = SortChildStarts
                ? commit.ChildStarts.OrderBy(child => child.Instance!.InstanceId, StringComparer.Ordinal).ToArray()
                : commit.ChildStarts.ToArray();
            await InsertChildrenAsync(connection, transaction, commit.InstanceId, children, now);
            foreach (var child in children)
                await InsertDemandsAsync(connection, transaction, StampStartPayload(child.Payload, now));
            madeRunnable.AddRange(children.Select(child => child.Instance!.InstanceId));
        }
        // Each event this commit appends to its own inbox takes the next sequence.
        var inbox = new InboxRow { Status = snapshot.Status.ToString(), InboxSequence = current?.InboxSequence ?? 0 };
        var woken = false;
        foreach (var cancellation in commit.ChildCancellations)
        {
            var outcome = await RequestCancellationAsync(connection, transaction, cancellation.ChildInstanceId,
                JsonSerializer.Serialize(cancellation.Reason), now);
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
        // and nobody holds it, because the commit let it go or woke it with an event.
        if (madeRunnable.Count > 0 || woken || (runnableAt <= now && releases))
            await NotifyRunnableAsync(connection, transaction);
        if (admitted != null)
        {
            var identity = new { admission!.Sender, admission.Trigger, admission.Key };
            await connection.ExecuteAsync(RequiredSql(Statement.AdmissionDelete), identity, transaction);
            await connection.ExecuteAsync(RequiredSql(Statement.AdmissionInsert), WithTimes(new
            {
                admission.Sender, admission.Trigger, admission.Key, admission.Fingerprint, commit.InstanceId, commit.Incarnation,
                admitted.WorkflowName, admitted.WorkflowVersion
            }, ("ExpiresAtUtc", admitted.ExpiresAtUtc)), transaction);
            await connection.ExecuteAsync(RequiredSql(Statement.OwnershipDelete), new { commit.InstanceId }, transaction);
            await connection.ExecuteAsync(RequiredSql(Statement.OwnershipInsert),
                new { commit.InstanceId, commit.Incarnation, admission.Sender, admission.Trigger, admission.Owner, admission.Tenant, admission.OwnerKind }, transaction);
        }
        await transaction.CommitAsync();
        return new WorkflowCommitResult(WorkflowCommitStatus.Applied, revision,
            ReadReason(current?.CancellationJson), parked, unread, madeRunnable) { Admission = admitted };
    }

    // Replaced and removed parts go before inserts, in the same transaction as the trunk.
    private async Task ApplyPartsAsync(DbConnection connection, DbTransaction transaction, CheckpointCommit commit)
    {
        if (commit.ReplacesParts)
            await connection.ExecuteAsync(RequiredSql(Statement.DeleteAllParts), new { commit.InstanceId }, transaction);
        else
            foreach (var keys in commit.DeletedParts.Concat(commit.Parts.Select(part => part.Key)).Chunk(StatementBatchSize))
                await connection.ExecuteAsync(RequiredSql(Statement.DeleteParts), new { commit.InstanceId, Keys = keys }, transaction);
        foreach (var parts in commit.Parts.Chunk(StatementBatchSize))
            await InsertPartsAsync(connection, transaction, commit.InstanceId, parts);
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await BeginTransactionAsync(connection, Operation.Read);
        return await ReadAtOneRevisionAsync(connection, transaction, instanceId, true, "", null);
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await BeginTransactionAsync(connection, Operation.Read);
        return await ReadAtOneRevisionAsync(connection, transaction, instanceId, false, "", null);
    }

    internal const string EventColumns =
        "instance_id AS InstanceId, event_id AS EventId, sequence AS Sequence, event_type AS EventType, " +
        "event_key AS EventKey, target_wait_id AS TargetWaitId, payload_json AS PayloadJson, " +
        "received_at_utc AS ReceivedAtUtc, outcome AS Outcome, wait_id AS WaitId, rejection AS Rejection, " +
        "subject AS Subject";

    internal const string WaitColumns =
        "instance_id AS InstanceId, wait_id AS WaitId, kind AS Kind, wait_key AS WaitKey, event_type AS EventType, " +
        "deadline_utc AS DeadlineUtc, opened_sequence AS OpenedSequence, opened_at_utc AS OpenedAtUtc, " +
        "close_reason AS CloseReason, closed_at_utc AS ClosedAtUtc, default_payload_json AS DefaultPayloadJson";
    internal const string RowColumns =
        "instance_id AS InstanceId, state_json AS StateJson, runnable_at_utc AS RunnableAtUtc, revision AS Revision, last_host_id AS LastHostId";

    internal const string StampedRowColumns = RowColumns + ", incarnation AS Incarnation";

    private const int MaxReadAttempts = 20;

    // Server reads verify revision and incarnation. Snapshot reads already hold trunk and parts together.
    private async Task<WorkflowInstance?> ReadAtOneRevisionAsync(DbConnection connection, DbTransaction? transaction,
        string instanceId, bool wholly, string filter, object? filterValues)
    {
        var parameters = new DynamicParameters(new { InstanceId = instanceId });
        if (!wholly)
            parameters.Add("DataKey", CheckpointParts.DataKey);
        if (filterValues != null)
            parameters.AddDynamicParams(filterValues);
        for (var attempt = 1; ; attempt++)
        {
            StoredRow? row;
            StoredPart[] parts;
            StoredStamp? stamp = null;
            using (var read = await connection.QueryMultipleAsync(RequireSql(Statement.ReadInstance, ReadInstanceSql(filter, wholly)), parameters, transaction))
            {
                row = await read.ReadSingleOrDefaultAsync<StoredRow>();
                parts = (await read.ReadAsync<StoredPart>()).ToArray();
                if (ReadsUseStamps)
                    stamp = await read.ReadSingleOrDefaultAsync<StoredStamp>();
            }
            if (row == null)
                return null;
            if (!ReadsUseStamps || stamp != null && row.Same(stamp))
                return wholly ? row.Read(parts, NormalizeRunnableTime) : row.Summarize(parts.SingleOrDefault()?.Json, NormalizeRunnableTime);
            if (attempt == MaxReadAttempts)
                throw new InvalidOperationException($"Workflow instance '{instanceId}' changed in each of {MaxReadAttempts} reads");
            await Task.Delay(Random.Shared.Next(1, 5 * attempt));
        }
    }

    private async Task<WorkflowInstance[]> AssembleAsync(DbConnection connection, IReadOnlyList<StoredRow> rows,
        DbTransaction? transaction, string filter, object? filterValues)
    {
        var instances = new List<WorkflowInstance>();
        foreach (var batch in rows.Chunk(StatementBatchSize))
        {
            ILookup<string, StoredPart> parts;
            Dictionary<string, StoredStamp>? stamps = null;
            using (var read = await connection.QueryMultipleAsync(RequiredSql(Statement.ReadParts),
                new { Ids = batch.Select(row => row.InstanceId).ToArray() }, transaction))
            {
                parts = (await read.ReadAsync<StoredPart>()).ToLookup(part => part.InstanceId, StringComparer.Ordinal);
                if (ReadsUseStamps)
                    stamps = (await read.ReadAsync<StoredStamp>()).ToDictionary(stored => stored.InstanceId, StringComparer.Ordinal);
            }
            foreach (var row in batch)
                if (!ReadsUseStamps || stamps!.TryGetValue(row.InstanceId, out var stamp) && row.Same(stamp))
                    instances.Add(row.Read(parts[row.InstanceId], NormalizeRunnableTime));
                else if (await ReadAtOneRevisionAsync(connection, transaction, row.InstanceId, true, filter, filterValues) is { } moved)
                    instances.Add(moved);
        }
        return instances.ToArray();
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
        QueryAsync(RequiredSql(Statement.ByName), workflowName);

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
        QueryAsync(RequiredSql(Statement.ByStatus), status.ToString());

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
        QueryAsync(RequiredSql(Statement.ByCorrelation), correlationId);

    public async Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentInstanceId);
        return await QueryAsync(RequiredSql(Statement.ByParent), parentInstanceId);
    }

    private async Task<IEnumerable<WorkflowInstance>> QueryAsync(string predicate, string value)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await BeginTransactionAsync(connection, Operation.Query);
        var rows = await connection.QueryAsync<StoredRow>(RequireSql(Statement.Query, QuerySql(predicate)), new { Value = value }, transaction);
        return await AssembleAsync(connection, rows.ToArray(), transaction, $"AND {predicate}", new { Value = value });
    }

    public async Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null)
    {
        var period = tombstoneRetention ?? RetentionSettings.Default.TombstoneRetention;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        await EnsureSchemaAsync();
        await ExecuteOperationAsync(Operation.Delete, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            var parameters = new { InstanceId = instanceId };
            while (true)
            {
                await using var transaction = (await BeginTransactionAsync(connection, Operation.Delete))!;
                var json = await connection.QuerySingleOrDefaultAsync<string>(RequiredSql(Statement.DeleteState), parameters, transaction);
                var seen = WaitingParent(json);
                var parent = seen is { } waiting
                    ? await LockAsync<InboxRow>(connection, transaction, Statement.DeleteParentInbox, waiting.ParentId)
                    : default;
                await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity, parameters);
                if (DeleteRevalidationSql is { } lockedSql)
                {
                    json = await connection.QuerySingleOrDefaultAsync<string>(CheckSql(nameof(DeleteRevalidationSql), lockedSql), parameters, transaction);
                    if (WaitingParent(json) != seen)
                    {
                        await transaction.RollbackAsync();
                        continue;
                    }
                }
                if (seen is { } answering)
                {
                    var gone = ToEvent(WorkflowStoreRules.ChildGone(instanceId, answering.ParentId, answering.WaitId));
                    if ((await AppendAsync(connection, transaction, gone, gone.ContentHash(), parent.Row, parent.Now)).MadeRunnable)
                        await NotifyRunnableAsync(connection, transaction);
                }
                await connection.ExecuteAsync(RequiredSql(Statement.DeleteLease), parameters, transaction);
                // A provider that rechecks the row leaves an instance inserted after that read whole.
                if (json != null || DeleteRevalidationSql == null)
                {
                    if (json != null)
                    {
                        var removedAt = await OutboxNowAsync(connection, transaction);
                        await connection.ExecuteAsync(RequiredSql(Statement.WriteTombstone),
                            WithTimes(parameters, ("Now", removedAt), ("Expires", removedAt + period)), transaction);
                    }
                    await connection.ExecuteAsync(RequiredSql(Statement.DeleteRows), parameters, transaction);
                }
                await transaction.CommitAsync();
                return true;
            }
        });
    }

    public async Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<(long Sequence, string EntryJson)>(RequiredSql(Statement.Trace),
            new { InstanceId = instanceId, AfterSequence = afterSequence, Limit = limit });
        return rows.Select(row => new StoredTraceEntry(row.Sequence,
            JsonSerializer.Deserialize<ExecutionTraceEntry>(row.EntryJson)!)).ToArray();
    }

    private static WorkflowEvent ToEvent(ParentNotice notice) =>
        new(notice.ParentInstanceId, notice.EventId, notice.EventType, notice.PayloadJson,
            TargetWaitId: notice.ParentWaitId);

    private sealed class InboxRow
    {
        public bool Stopped { get; set; }
        public bool TracingEnabled { get; set; }
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

        public StoredWorkflowEvent Read(Func<DateTime, DateTime> readTime) => new(InstanceId, EventId, Sequence, EventType, EventKey,
            TargetWaitId, PayloadJson, readTime(ReceivedAtUtc), (EventOutcome)Outcome, WaitId,
            (EventRejection?)Rejection, Subject);
    }

    private sealed class WaitRow
    {
        public string InstanceId { get; set; } = "";
        public string WaitId { get; set; } = "";
        public short Kind { get; set; }
        public string WaitKey { get; set; } = "";
        public string EventType { get; set; } = "";
        public string? DefaultPayloadJson { get; set; }
        public DateTime? DeadlineUtc { get; set; }
        public long OpenedSequence { get; set; }
        public DateTime OpenedAtUtc { get; set; }
        public short? CloseReason { get; set; }
        public DateTime? ClosedAtUtc { get; set; }

        public StoredWait Read(Func<DateTime, DateTime> readTime) => new(InstanceId, WaitId, (WaitKind)Kind, WaitKey, EventType,
            DeadlineUtc is { } deadline ? readTime(deadline) : null, OpenedSequence, readTime(OpenedAtUtc),
            (WaitCloseReason?)CloseReason, ClosedAtUtc is { } closed ? readTime(closed) : null, DefaultPayloadJson);
    }

    private sealed class RunnableRow
    {
        public string InstanceId { get; set; } = "";
        public DateTime RunnableAtUtc { get; set; }
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

    internal sealed class CommitState
    {
        public Guid Incarnation { get; set; }
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

    private static (string ParentId, string WaitId)? WaitingParent(string? json) =>
        json != null && JsonSerializer.Deserialize<WorkflowInstance>(json) is
        { ParentInstanceId: { } parentId, ParentWaitId: { } waitId } instance &&
        !WorkflowStoreRules.IsTerminal(instance.Status)
            ? (parentId, waitId)
            : null;
    private sealed class StoredRow
    {
        public string InstanceId { get; set; } = "";
        public string StateJson { get; set; } = "";
        public DateTime? RunnableAtUtc { get; set; }
        public long Revision { get; set; }
        public Guid Incarnation { get; set; }
        public Guid? LastHostId { get; set; }

        public bool Same(StoredStamp stamp) => stamp.Revision == Revision && stamp.Incarnation == Incarnation;

        public WorkflowInstance Read(IEnumerable<StoredPart> parts, bool normalize) => Runnable(CheckpointParts.Assemble(StateJson,
            parts.Select(part => new CheckpointPart(part.PartKey, part.Json)), Revision), normalize);

        public WorkflowInstance Summarize(string? data, bool normalize) => Runnable(CheckpointParts.Summarize(StateJson, data, Revision), normalize);

        private WorkflowInstance Runnable(WorkflowInstance instance, bool normalize)
        {
            instance.Incarnation = Incarnation;
            instance.LastHostId = LastHostId;
            instance.RunnableAtUtc = normalize && RunnableAtUtc is { } runnableAt ? Utc(runnableAt) : RunnableAtUtc;
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

    internal static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    private static int LeaseMilliseconds(string instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return checked((int)Math.Ceiling(duration.TotalMilliseconds));
    }

    private sealed class PageWait
    {
        public string WaitId { get; set; } = "";
        public long OpenedSequence { get; set; }
        public DateTime? DeadlineUtc { get; set; }
    }
    private sealed class PageBoundary
    {
        public long Through { get; set; }
        public DateTime Now { get; set; }
    }

    private sealed class LeaseGrantRow
    {
        public long Epoch { get; set; }
        public int CrashCount { get; set; }
    }
}
