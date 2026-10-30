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
    internal RelationalWorkflowStateRepository(OutboxSettings? outboxSettings = null, PayloadStoreSettings? payloadSettings = null)
    {
        _payloadSettings = payloadSettings ?? new();
        var options = outboxSettings ?? new OutboxSettings();
        options.Validate();
        _retryHorizon = options.RetryHorizon;
    }

    private readonly TimeSpan _retryHorizon;

    private static string ReadAlias(string property) => property == "PublishedDraftGeneration" ? "PublishedDraftGenerationValue" : property;

    private static Guid ReadGuid(object? value) => value is Guid guid ? guid : Guid.Parse((string)value!);
    private static Guid? ReadOptionalGuid(object? value) => value is null or DBNull ? null : ReadGuid(value);


    /// <summary>The connection, transaction and admission policy for a store operation.</summary>
    internal enum Operation
    {
        Schema,
        Catalog,
        Artifact,
        Read,
        RunnableRead,
        RetentionRead,
        OutboxRead,
        OutboxClaim,
        OutboxMark,
        Purge,
        Registry,
        CancellationRequest,
        CancellationRead,
        Termination,
        LeaseClaim,
        LeaseRenew,
        LeaseRelease,
        Append,
        InboxPage,
        Commit,
        Delete
    }

    /// <summary>SQL steps in the shared protocol. Optional steps return null.</summary>
    internal enum Statement
    {
        CatalogPackages,
        CatalogPackage,
        CatalogPackagePage,
        CatalogPackageInsert,
        CatalogLock,
        CatalogContentLock,
        CatalogSharedLock,
        CatalogInstanceDigests,
        CatalogRevision,
        CatalogRevisionAdvance,
        CatalogVersionChanges,
        CatalogVersions,
        CatalogNamedVersions,
        CatalogVersion,
        CatalogUnfinishedCallers,
        CatalogVersionInsert,
        CatalogVersionState,
        CatalogDefinitionPage,
        DraftGenerationInsert,
        PublicationHead,
        PublicationHeadInsert,
        PublicationHeadUpdate,
        Publication,
        PublicationInsert,
        PublicationUpdate,
        PublicationHeartbeat,
        PublicationPage,
        LatestPublication,
        PublicationPurgeCandidates,
        PublicationDelete,
        CatalogDefinition,
        CatalogDefinitionInsert,
        CatalogDefinitionUpdate,
        CatalogDefinitionDelete,
        ArtifactPut,
        ArtifactRead,
        ArtifactExists,
        ArtifactDelete,
        ArtifactDeleteOld,
        ArtifactCandidates,
        WebhookDeliveryRead,
        WebhookDeliveryDelete,
        WebhookDeliveryInsert,
        WebhookDeliveryExpire,
        AdmissionLock,
        AdmissionRead,
        AdmissionDelete,
        AdmissionInsert,
        OwnershipRead,
        OwnershipInsert,
        OwnershipDelete,
        AdmissionExpire,
        OwnershipExpire,
        RegistryLock,
        RegisterMember,
        MemberHeartbeat,
        UnregisterMember,
        PruneMembers,
        Members,
        MemberCapabilities,
        DeleteCapabilities,
        InsertCapability,
        InstalledPackages,
        HostPackages,
        InsertInstalledPackage,
        DeleteHostPackages,
        InsertHostPackage,
        RetentionLock,
        RetentionCandidates,
        RetentionBlockedPublishers,
        RetentionEligible,
        StampEnd,
        Tombstone,
        WriteTombstone,
        ExpireTombstones,
        OutboxClock,
        PublisherLock,
        PublisherSharedLock,
        UnregisterPublisher,
        PublisherUpsert,
        Publishers,
        PublisherRead,
        BackfillDeliveries,
        EnablePublisher,
        RetryDisabledDeliveries,
        InsertOutboxEvent,
        InsertDelivery,
        ClaimDeliveries,
        DeliveryLock,
        DeliveryUpdate,
        DeliveryRead,
        DisablePublisher,
        ExpireUnattemptedDeliveries,
        TerminationState,
        TerminationRequest,
        TerminationChildren,
        Cancellation,
        Cancellations,
        Running,
        ClaimLease,
        ReleaseLease,
        Notice,
        LockIdentity,
        LockInbox,
        ParentInbox,
        CommitState,
        Receipt,
        UpdateReceiptNotices,
        Lease,
        TakenChild,
        EventHash,
        CandidateWait,
        AdvanceInbox,
        InsertEvent,
        UpdateEventNotice,
        PendingEvents,
        Event,
        InboxSequences,
        Wait,
        OpenWaits,
        EarliestDeadline,
        DeleteAllParts,
        DeleteParts,
        InsertCommit,
        UpdateCommit,
        FinishInbox,
        UpdateLease,
        ReadInstance,
        LockAndReadPage,
        DeleteLease,
        DeleteState,
        DeleteParentInbox,
        DeleteRows,
        Trace,
        IncarnationDiagnostics,
        DiagnosticIds,
        InsertDiagnostic,
        DeleteDiagnostics
    }

    /// <summary>The result of the provider's atomic cancellation SQL.</summary>
    internal enum CancellationOutcome
    {
        Unchanged,
        Recorded,
        MadeRunnable,
        Gone
    }

    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;
    private SemaphoreSlim? _admissions;

    /// <summary>Creates tables and indexes on an open connection, including cross-host schema locking.</summary>
    internal abstract Task CreateSchemaAsync(DbConnection connection, CancellationToken cancellationToken = default);

    /// <summary>Checks provider lifecycle constraints before any operation, including initialized schemas.</summary>
    internal virtual void ThrowIfUnavailable()
    {
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();

        if (_schemaReady)
            return;

        await _schemaLock.WaitAsync(cancellationToken: cancellationToken);

        try
        {
            if (_schemaReady)
                return;

            await ExecuteOperationAsync(Operation.Schema, async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);
                await CreateSchemaAsync(connection, cancellationToken: cancellationToken);

                return true;
            }, cancellationToken: cancellationToken);

            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    // A different application name reserves a driver pool for ownership traffic.
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

        // Claims and cancellation requests together use at most half
        // the reserved pool, so renewals and the cancellation watch always find a connection.
        _admissions = new SemaphoreSlim(Math.Max(1, ownershipPoolSize / 2));

        return ownership.ConnectionString;
    }

    /// <summary>Opens the main or reserved ownership connection; the caller disposes it.</summary>
    internal abstract Task<DbConnection> OpenConnectionAsync(bool ownership = false, CancellationToken cancellationToken = default);

    /// <summary>Chooses the operation transaction or autocommit; successful writes are committed by the core.</summary>
    internal abstract ValueTask<DbTransaction?> BeginTransactionAsync(DbConnection connection, Operation operation,
        CancellationToken cancellationToken = default);

    /// <summary>Supplies fixed SQL; only explicitly optional protocol steps may be absent.</summary>
    internal abstract string? Sql(Statement statement);

    /// <summary>Reads a trunk and selected parts, with stamp rechecks where snapshots do not join them.</summary>
    internal abstract string ReadInstanceSql(string filter, bool wholly);

    /// <summary>Builds a provider query from an internal predicate; caller values remain parameters.</summary>
    internal abstract string InstanceTable { get; }

    internal abstract string DeliveryTable { get; }
    internal abstract string OutboxEventTable { get; }
    internal abstract string SearchColumn(string name);
    internal abstract string SearchValues(DynamicParameters parameters, string name, string[] values);
    internal abstract string SearchLimitPrefix { get; }
    internal abstract string SearchLimitSuffix { get; }

    private string RequiredSql(Statement statement) => RequireSql(statement, Sql(statement));

    private string RequireSql(Statement statement, string? sql) => CheckSql(statement.ToString(), sql);

    private string CheckSql(string statement, string? sql) => !string.IsNullOrWhiteSpace(sql)
        ? sql
        : throw new InvalidOperationException($"Relational provider '{GetType().FullName}' is missing required statement '{statement}'.");

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

    internal virtual bool IsClaimRefusal(Exception error) => false;

    // Providers may translate a nonwaiting instance-lock failure before any lease work begins.
    internal virtual Task<int> ReadClaimStateAsync(DbConnection connection, DbTransaction transaction, string instanceId,
        CancellationToken cancellationToken = default) =>
        connection.ExecuteScalarAsync<int>(new CommandDefinition(RequiredSql(Statement.Running), new { InstanceId = instanceId },
            transaction, cancellationToken: cancellationToken));

    /// <summary>Applies admission around the whole operation; SQLite instead serializes writes.</summary>
    internal virtual async Task<T> ExecuteProviderOperationAsync<T>(Operation operation, Func<Task<T>> call,
        CancellationToken cancellationToken = default)
    {
        if (_admissions == null || operation is not (Operation.LeaseClaim or Operation.CancellationRequest or Operation.CancellationRead
                or Operation.Termination))
            return await call();

        await _admissions.WaitAsync(cancellationToken: cancellationToken);

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
    internal virtual Task NotifyRunnableAsync(DbConnection connection, DbTransaction? transaction,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    internal abstract Task<CancellationOutcome> RequestCancellationAsync(DbConnection connection,
        DbTransaction? transaction, string instanceId, string reasonJson, DateTime? now, Guid? expectedIncarnation = null,
        CancellationToken cancellationToken = default);

    internal abstract Task<int> SettleAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<EventSettlement> settlements, DateTime now, CancellationToken cancellationToken = default);

    internal abstract Task InsertWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<OpenedWait> waits, long inboxSequence, DateTime now,
        CancellationToken cancellationToken = default);

    internal abstract Task ArmWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ArmedWait> waits, DateTime now, CancellationToken cancellationToken = default);

    internal abstract Task CloseWaitsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<ClosedWait> waits, DateTime now, CancellationToken cancellationToken = default);

    internal abstract Task InsertPartsAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<CheckpointPart> parts, CancellationToken cancellationToken = default);

    internal abstract Task AppendTraceAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, IReadOnlyList<string> entries, long revision, WorkflowStatus? commitStatus,
        CancellationToken cancellationToken = default);

    internal static string StampStartPayload(string payload, DateTime now) => WorkflowDemands.StampStartPayload(payload, now);

    // The shared lifecycle lock excludes collection. The header insert arbitrates creators of
    // this set, and only its winner writes rows, in the same transaction as the instance reference.
    private async Task<DynamicParameters> InternDemandsAsync(DbConnection connection, DbTransaction transaction, string payload,
        CancellationToken cancellationToken = default)
    {
        var instance = JsonSerializer.Deserialize<WorkflowInstance>(payload)!;
        var set = DemandSet.Create(WorkflowDemands.Saved(instance));
        var identity = new { DemandSetId = set.Id };

        if (await connection.ExecuteScalarAsync<string?>(new CommandDefinition(InsertDemandSetSql, identity, transaction,
                cancellationToken: cancellationToken)) != null)
        {
            foreach (var demand in set.Rows)
                await connection.ExecuteAsync(new CommandDefinition(InsertDemandSql, WithTimes(new
                    {
                        DemandSetId = set.Id, demand.Key, demand.Value, demand.Inherit
                    }, ("ExpiresAtUtc", demand.ExpiresAtUtc), ("ValueExpiresAtUtc", demand.ValueExpiresAtUtc)), transaction,
                    cancellationToken: cancellationToken));
        }

        return WithTimes(new { DemandSetId = set.Id, PinnedHost = set.Pin?.Value, PinInherit = set.Pin?.Inherit ?? false },
            ("PinExpiresAtUtc", set.Pin?.ExpiresAtUtc));
    }

    private async Task<WorkflowInstance> HydrateDemandsAsync(DbConnection connection, DbTransaction? transaction,
        StoredRow row, WorkflowInstance instance, CancellationToken cancellationToken = default)
    {
        var rows = await connection.QueryAsync<DemandRow>(new CommandDefinition(ReadDemandSetSql, new { row.DemandSetId }, transaction,
            cancellationToken: cancellationToken));

        var demands = rows.Select(ToDemand).ToList();

        if (row.PinnedHost != null)
            demands.Add(new("host", row.PinnedHost,
                row.PinExpiresAtUtc is { } expiry ? ReadTime(expiry) : null, row.PinInherit));

        return DemandSet.Hydrate(instance, demands.OrderBy(d => d.Key, StringComparer.Ordinal));
    }

    private WorkflowDemand ToDemand(DemandRow row) => new(row.Key, row.Value,
            row.ExpiresAtUtc is { } expiry ? ReadTime(expiry) : null, row.Inherit)
        { ValueExpiresAtUtc = row.ValueExpiresAtUtc is { } valueExpiry ? ReadTime(valueExpiry) : null };

    private sealed class DemandRow
    {
        public string Key { get; set; } = "";
        public string? Value { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime? ValueExpiresAtUtc { get; set; }
        public bool Inherit { get; set; }
    }

    public async Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);

        return await ExecuteStoreAsync<IReadOnlyList<WorkflowDemand>>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            var rows = await connection.QueryAsync<DemandRow>(new CommandDefinition(ReadDemandsSql, new { InstanceId = instanceId },
                cancellationToken: cancellationToken));

            return rows.Select(ToDemand)
                .OrderBy(demand => demand.Key, StringComparer.Ordinal).ToArray();
        }, cancellationToken);
    }

    internal abstract string CreateDemandSetsLockSql { get; }

    internal abstract string CollectDemandSetsLockSql { get; }

    // Returns the set ID only when this transaction inserted the header.
    internal abstract string InsertDemandSetSql { get; }
    internal abstract string CollectDemandSetsSql { get; }
    internal abstract string ReadDemandSetSql { get; }
    internal abstract string ReadDemandsSql { get; }
    internal abstract string InsertDemandSql { get; }

    internal abstract string InsertChildSql { get; }

    private async Task InsertChildrenAsync(DbConnection connection, DbTransaction transaction,
        string parentInstanceId, IReadOnlyList<ChildStart> children, DateTime now, CancellationToken cancellationToken = default)
    {
        foreach (var child in children)
        {
            var payload = StampStartPayload(child.Payload, now);

            var values = WithTimes(new
            {
                child.Instance!.InstanceId, child.Incarnation, child.Instance.WorkflowName,
                child.Instance.WorkflowVersion, BundleDigest = child.Instance.BundleDigest?.Value,
                StopKind = child.Instance.StopKind?.ToString(), child.Instance.StopCrashCount,
                child.ParentWaitId,
                Status = child.Instance.Status.ToString(), child.Instance.CorrelationId,
                Payload = DemandSet.StripPayload(payload), ParentInstanceId = parentInstanceId
            }, ("Now", now), ("CreatedAtUtc", child.Instance.CreatedAt));

            values.AddDynamicParams(await InternDemandsAsync(connection, transaction, payload, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(InsertChildSql, values, transaction, cancellationToken: cancellationToken));
        }
    }

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

    private static Task CompleteAsync(DbTransaction? transaction, CancellationToken cancellationToken = default) =>
        transaction?.CommitAsync(cancellationToken: cancellationToken) ?? Task.CompletedTask;

    private Task ExecuteOptionalAsync(DbConnection connection, DbTransaction? transaction,
        Statement statement, object? values = null, CancellationToken cancellationToken = default) => Sql(statement) is { } sql
        ? connection.ExecuteAsync(new CommandDefinition(RequireSql(statement, sql), values, transaction,
            cancellationToken: cancellationToken))
        : Task.CompletedTask;

    private async Task<(T? Row, DateTime Now)> LockAsync<T>(DbConnection connection, DbTransaction transaction,
        Statement statement, string instanceId, bool clock = true, CancellationToken cancellationToken = default)
    {
        using var read = await connection.QueryMultipleAsync(new CommandDefinition(RequiredSql(statement),
            new { InstanceId = instanceId }, transaction, cancellationToken: cancellationToken));

        var row = await read.ReadSingleOrDefaultAsync<T>();
        var now = clock ? UsesLocalClock ? LocalNow() : ReadTime(await read.ReadSingleAsync<DateTime>()) : default;

        return (row, now);
    }

    public async Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason,
        Guid? expectedIncarnation = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.CancellationRequest, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true, cancellationToken: cancellationToken);

            await using var transaction =
                (await BeginTransactionAsync(connection, Operation.CancellationRequest, cancellationToken: cancellationToken))!;

            await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity, new { InstanceId = instanceId },
                cancellationToken: cancellationToken);

            var (_, now) = await LockAsync<InboxRow>(connection, transaction, Statement.LockInbox, instanceId,
                cancellationToken: cancellationToken);

            var before = await NoticeAsync(connection, transaction, instanceId, now, cancellationToken: cancellationToken);

            var outcome = await RequestCancellationAsync(connection, transaction, instanceId,
                JsonSerializer.Serialize(reason), now, expectedIncarnation, cancellationToken: cancellationToken);

            var notice = (outcome is CancellationOutcome.MadeRunnable or CancellationOutcome.Recorded) && before?.Claimable != true
                ? await NoticeAsync(connection, transaction, instanceId, now, cancellationToken: cancellationToken)
                : null;

            if (outcome == CancellationOutcome.MadeRunnable)
                await NotifyRunnableAsync(connection, transaction, cancellationToken: cancellationToken);

            await CompleteAsync(transaction, cancellationToken: cancellationToken);

            return new CancellationRequestResult(outcome != CancellationOutcome.Gone && outcome != CancellationOutcome.Unchanged,
                notice?.Claimable == true ? notice : null);
        }, cancellationToken: cancellationToken);
    }

    public async Task<CancellationReason?> GetCancellationRequestAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.CancellationRead, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true, cancellationToken: cancellationToken);

            var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(RequiredSql(Statement.Cancellation),
                new { InstanceId = instanceId }, cancellationToken: cancellationToken));

            return json == null ? null : JsonSerializer.Deserialize<CancellationReason>(json);
        }, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit == 0)
            return [];

        return await ExecuteStoreAsync<IReadOnlyList<RunnableInstance>>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.RunnableRead, cancellationToken: cancellationToken);

            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            var rows = await connection.QueryAsync<RunnableRow>(new CommandDefinition(RunnableSql,
                PlacementParameters(query, now, query.Limit), transaction, cancellationToken: cancellationToken));

            return rows.Select(row =>
                new RunnableInstance(row.InstanceId, ReadTime(row.RunnableAtUtc))).ToArray();
        }, cancellationToken);
    }

    public async Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        ArgumentNullException.ThrowIfNull(query);

        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        var milliseconds = LeaseMilliseconds(instanceId, token, duration);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync<ExecutionLeaseGrant?>(Operation.LeaseClaim, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true, cancellationToken: cancellationToken);

            await using var transaction =
                (await BeginTransactionAsync(connection, Operation.LeaseClaim, cancellationToken: cancellationToken))!;

            try
            {
                // Running returns -1 for contention, 0 for an absent/non-running row, and 1 for Running.
                // A skipped row must never follow the absent-instance admission path.
                var claimState = await ReadClaimStateAsync(connection, transaction, instanceId, cancellationToken: cancellationToken);

                if (claimState < 0)
                    return null;

                var running = claimState > 0;
                var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);
                var eligibility = PlacementParameters(query, now, query.Limit);
                eligibility.Add("InstanceId", instanceId);
                eligibility.Add("Operator", kind == ExecutionClaimKind.Operator);
                bool eligible;

                if (kind == ExecutionClaimKind.Termination)
                {
                    var (command, _) = await LockAsync<TerminationRow>(connection, transaction, Statement.TerminationState, instanceId,
                        cancellationToken: cancellationToken);

                    eligible = command != null && !WorkflowStoreRules.IsTerminal(WorkflowStatuses.Read(command.Status)) &&
                        ReadReason(command.CancellationJson)?.Mode == WorkflowControlMode.Terminate;
                }
                else
                    eligible = await connection.ExecuteScalarAsync<int>(new CommandDefinition(ClaimEligibilitySql, eligibility, transaction,
                        cancellationToken: cancellationToken)) == 1;

                if (await TombstoneAsync(connection, transaction, instanceId, now, cancellationToken: cancellationToken) != null ||
                    !eligible)
                {
                    await transaction.CommitAsync(cancellationToken: cancellationToken);

                    return null;
                }

                // Capture SQLite's lease clock only after its writer and instance locks.
                var parameters = ClaimParameters(instanceId, token, TimeSpan.FromMilliseconds(milliseconds));
                parameters.Add("Running", running);

                var grant = await connection.QuerySingleOrDefaultAsync<LeaseGrantRow>(
                    new CommandDefinition(RequiredSql(Statement.ClaimLease), parameters, transaction,
                        cancellationToken: cancellationToken));

                if (grant != null)
                {
                    await connection.ExecuteAsync(new CommandDefinition(SetLeaseHostSql, new { InstanceId = instanceId, query.HostId },
                        transaction, cancellationToken: cancellationToken));

                    await connection.ExecuteAsync(new CommandDefinition(ClaimStampHostSql, new { InstanceId = instanceId }, transaction,
                        cancellationToken: cancellationToken));
                }

                await transaction.CommitAsync(cancellationToken: cancellationToken);

                return grant == null ? null : new ExecutionLeaseGrant(grant.Epoch, grant.CrashCount);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && IsClaimRefusal(error))
            {
                // A later instance write can refuse after lease arbitration. Undo the entire claim,
                // including the lease and host, even when XACT_ABORT has already ended it.
                if (transaction.Connection != null)
                    await transaction.RollbackAsync(cancellationToken: cancellationToken);

                return null;
            }
        }, cancellationToken: cancellationToken);
    }

    internal abstract int RenewalBatchSize { get; }

    internal abstract string RenewalSql(IReadOnlyList<ExecutionLeaseRenewal> leases,
        IReadOnlySet<string> terminations, DynamicParameters parameters);

    public async Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(
        IReadOnlyList<ExecutionLeaseRenewal> leases, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(leases);

        foreach (var lease in leases)
            LeaseMilliseconds(lease.InstanceId, lease.Token, lease.Duration);

        if (leases.Select(lease => lease.InstanceId).Distinct(StringComparer.Ordinal).Count() != leases.Count)
            throw new ArgumentException("Renewal instance IDs must be distinct", nameof(leases));

        if (leases.Count == 0)
            return [];

        await EnsureSchemaAsync(cancellationToken: cancellationToken);
        var results = new List<ExecutionLeaseRenewalResult>(leases.Count);

        foreach (var batch in leases.Chunk(RenewalBatchSize))
        {
            var outcomes = await ExecuteOperationAsync(Operation.LeaseRenew, async () =>
            {
                await using var connection = await OpenConnectionAsync(ownership: true, cancellationToken: cancellationToken);

                await using var transaction =
                    await BeginTransactionAsync(connection, Operation.LeaseRenew, cancellationToken: cancellationToken);

                // Match single renewal: read pending commands without instance locks in this
                // transaction. Decode each row in C# so corrupt JSON cannot fail the whole chunk.
                var commands = await connection.QueryAsync<(string InstanceId, string CancellationJson)>(new CommandDefinition(
                    RequiredSql(Statement.Cancellations), new { InstanceIds = batch.Select(lease => lease.InstanceId).ToArray() },
                    transaction, cancellationToken: cancellationToken));

                var terminations = commands.Select(command => (command.InstanceId, Reason: ReadReason(command.CancellationJson)))
                    .Where(command => command.Reason?.Mode == WorkflowControlMode.Terminate)
                    .ToDictionary(command => command.InstanceId, command => command.Reason!, StringComparer.Ordinal);

                // SQLite captures its clock only after acquiring the writer lock.
                var parameters = new DynamicParameters();
                var sql = RenewalSql(batch, terminations.Keys.ToHashSet(StringComparer.Ordinal), parameters);

                var rows = (await connection.QueryAsync<RenewalRow>(new CommandDefinition(sql, parameters, transaction,
                    cancellationToken: cancellationToken))).ToArray();

                if (rows.Length != batch.Length)
                    throw new InvalidOperationException("A renewal batch must return one result per lease");

                var replies = rows.Select((row, index) => new ExecutionLeaseRenewalResult(row.Status,
                    row.Status == ExecutionLeaseRenewalStatus.TerminationRequested
                        ? terminations.GetValueOrDefault(batch[index].InstanceId)
                        : null)).ToArray();

                await CompleteAsync(transaction, cancellationToken: cancellationToken);

                return replies;
            }, cancellationToken: cancellationToken);

            results.AddRange(outcomes);
        }

        return results;
    }

    private sealed class RenewalRow
    {
        public ExecutionLeaseRenewalStatus Status { get; set; }
    }

    public async Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.LeaseRelease, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true, cancellationToken: cancellationToken);

            await using var transaction =
                (await BeginTransactionAsync(connection, Operation.LeaseRelease, cancellationToken: cancellationToken))!;

            await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity, new { InstanceId = instanceId },
                cancellationToken: cancellationToken);

            var (instance, now) = await LockAsync<InboxRow>(connection, transaction, Statement.LockInbox, instanceId,
                cancellationToken: cancellationToken);

            var parameters = WithLocalTime(new { InstanceId = instanceId, Token = token });
            bool released;

            if (ReleaseReturnsDue)
            {
                var due = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(RequiredSql(Statement.ReleaseLease),
                    parameters, transaction, cancellationToken: cancellationToken));

                released = due != null;

                if (due == true)
                    await NotifyRunnableAsync(connection, transaction, cancellationToken: cancellationToken);
            }
            else
                released = await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.ReleaseLease), parameters, transaction,
                    cancellationToken: cancellationToken)) == 1;

            var notice = released
                ? await NoticeAsync(connection, transaction, instanceId, now, cancellationToken: cancellationToken)
                : null;

            await CompleteAsync(transaction, cancellationToken: cancellationToken);

            return new ExecutionLeaseReleaseResult(released, notice?.Claimable == true ? notice : null)
            {
                UnreadEvents = released && instance != null && instance.InboxSequence > instance.InboxReadThrough
            };
        }, cancellationToken: cancellationToken);
    }

    public async Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(workflowEvent);
        workflowEvent.Validate();
        var hash = workflowEvent.ContentHash();
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.Append, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            while (true)
            {
                await using var transaction =
                    (await BeginTransactionAsync(connection, Operation.Append, cancellationToken: cancellationToken))!;

                var incarnation = await DeletionIncarnationAsync(connection, transaction, workflowEvent.InstanceId,
                    cancellationToken: cancellationToken);

                var payload = InboxPayloads.Prepare(workflowEvent, _payloadSettings);
                var pins = PayloadStore.Enlist(transaction);

                if (incarnation is { } seen)
                    await pins.LockAsync(InboxPayloads.Owner(workflowEvent.InstanceId, seen),
                        payload.References.Concat(payload.Bodies.SelectMany(b => b.Dependencies)).ToArray(),
                        cancellationToken: cancellationToken);

                if (workflowEvent.DeliveryIdentity is { } delivery)
                {
                    await ExecuteOptionalAsync(connection, transaction, Statement.AdmissionLock, new { Identity = delivery },
                        cancellationToken: cancellationToken);

                    if (await ReadWebhookDeliveryAsync(connection, transaction, workflowEvent, hash, cancellationToken: cancellationToken)
                        is { } duplicate)
                    {
                        await transaction.CommitAsync(cancellationToken: cancellationToken);

                        return duplicate;
                    }
                }

                var (instance, now) = await LockAsync<InboxRow>(connection, transaction, Statement.LockInbox, workflowEvent.InstanceId,
                    cancellationToken: cancellationToken);

                if (await DeletionIncarnationAsync(connection, transaction, workflowEvent.InstanceId,
                        cancellationToken: cancellationToken) != incarnation)
                {
                    await transaction.RollbackAsync(cancellationToken: cancellationToken);

                    continue;
                }

                var result = await AppendAsync(connection, transaction, workflowEvent, hash, instance, now,
                    cancellationToken: cancellationToken);

                if (incarnation is { } ownerId && result.Status is EventAppendStatus.Stored or EventAppendStatus.Rejected)
                    await payload.PublishAsync(pins, InboxPayloads.Owner(workflowEvent.InstanceId, ownerId), cancellationToken);

                if (workflowEvent.DeliveryIdentity != null &&
                    result.Status is EventAppendStatus.Stored or EventAppendStatus.Duplicate or EventAppendStatus.Rejected)
                    await WriteWebhookDeliveryAsync(connection, transaction, workflowEvent, hash, result, now,
                        cancellationToken: cancellationToken);

                if (result.MadeRunnable)
                    await NotifyRunnableAsync(connection, transaction, cancellationToken: cancellationToken);

                await transaction.CommitAsync(cancellationToken: cancellationToken);

                return result;
            }
        }, cancellationToken: cancellationToken);
    }

    private async Task<EventAppendResult> AppendAsync(DbConnection connection, DbTransaction transaction,
        WorkflowEvent workflowEvent, byte[] hash, InboxRow? instance, DateTime now, CancellationToken cancellationToken = default)
    {
        var existing = await connection.QuerySingleOrDefaultAsync<EventHashRow>(new CommandDefinition(RequiredSql(Statement.EventHash),
            new { workflowEvent.InstanceId, workflowEvent.EventId }, transaction, cancellationToken: cancellationToken));

        if (existing != null)
            return existing.PayloadHash.SequenceEqual(hash)
                ? new EventAppendResult(EventAppendStatus.Duplicate, existing.Sequence) { Notice = ReadNotice(existing.NoticeJson) }
                : new EventAppendResult(EventAppendStatus.Conflict);

        if (instance == null)
            return new EventAppendResult(
                await TombstoneAsync(connection, transaction, workflowEvent.InstanceId, now, cancellationToken: cancellationToken) != null
                    ? EventAppendStatus.Closed
                    : EventAppendStatus.NotFound);

        var finished = WorkflowStoreRules.IsTerminal(WorkflowStatuses.Read(instance.Status));

        if (finished && workflowEvent.TargetWaitId == null)
        {
            await TraceRefusalAsync(connection, transaction, workflowEvent, EventRejection.InstanceFinished, now, instance.TracingEnabled,
                instance.Revision, cancellationToken: cancellationToken);

            return new EventAppendResult(EventAppendStatus.Closed);
        }

        var waits = (await connection.QueryAsync<WaitRow>(new CommandDefinition(RequiredSql(Statement.CandidateWait), WithTimes(
                WithIntegers(new
                {
                    workflowEvent.InstanceId,
                    workflowEvent.TargetWaitId,
                    workflowEvent.Key
                }, ("Event", (short)WaitKind.Event)), ("Now", now)), transaction, cancellationToken: cancellationToken)))
            .Select(row => row.Read(ReadTime)).ToArray();

        var rejection = WorkflowStoreRules.RejectOnArrival(workflowEvent, waits.SingleOrDefault(), waits, now)
            ?? (finished ? EventRejection.InstanceFinished : null);

        var sequence = rejection == null ? instance.InboxSequence + 1 : 0;

        var madeRunnable = rejection == null && !instance.Stopped &&
            instance.Status is "Running" or "Suspended" or "NeedsResolution";

        var wasClaimable = madeRunnable &&
            (await NoticeAsync(connection, transaction, workflowEvent.InstanceId, now, cancellationToken: cancellationToken))?.Claimable ==
            true;

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
            PayloadJson = JsonSerializer.Serialize(InboxPayloads.Prepare(workflowEvent, _payloadSettings).Value),
            Hash = hash
        }, ("Rejection", (short?)rejection)), ("Now", now));

        if (advanceSql == null)
            parameters.Add("MadeRunnable", madeRunnable);

        if (rejection == null && advanceSql != null)
            await connection.ExecuteAsync(new CommandDefinition(RequireSql(Statement.AdvanceInbox, advanceSql),
                WithTimes(new { workflowEvent.InstanceId, Sequence = sequence, MadeRunnable = madeRunnable }, ("Now", now)), transaction,
                cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.InsertEvent), parameters, transaction,
            cancellationToken: cancellationToken));

        // A refusal on arrival never reaches an executor pass. Store its one entry atomically with the
        // refusal, using the shared trace append and the same instance lock as checkpoint commits.
        if (rejection is { } refused)
            await TraceRefusalAsync(connection, transaction, workflowEvent, refused, now, instance.TracingEnabled, instance.Revision,
                cancellationToken: cancellationToken);

        var notice = madeRunnable && !wasClaimable
            ? await NoticeAsync(connection, transaction, workflowEvent.InstanceId, now, cancellationToken: cancellationToken)
            : null;

        notice = notice?.Claimable == true ? notice : null;

        if (notice != null)
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.UpdateEventNotice), new
            {
                workflowEvent.InstanceId, workflowEvent.EventId, NoticeJson = JsonSerializer.Serialize(notice)
            }, transaction, cancellationToken: cancellationToken));

        return rejection == null
            ? new EventAppendResult(EventAppendStatus.Stored, sequence, madeRunnable)
                { Notice = notice }
            : new EventAppendResult(EventAppendStatus.Rejected, Rejection: rejection);
    }

    private async Task TraceRefusalAsync(DbConnection connection, DbTransaction transaction,
        WorkflowEvent workflowEvent, EventRejection rejection, DateTime now, bool tracingEnabled, long revision,
        CancellationToken cancellationToken = default)
    {
        if (tracingEnabled)
            await AppendTraceAsync(connection, transaction, workflowEvent.InstanceId,
                [WorkflowStoreRules.SerializeTrace(WorkflowStoreRules.RefusedEventTrace(workflowEvent, rejection, now))], revision, null,
                cancellationToken: cancellationToken);
    }

    public async Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.InboxPage, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction =
                (await BeginTransactionAsync(connection, Operation.InboxPage, cancellationToken: cancellationToken))!;

            var through = 0L;
            var now = default(DateTime);
            var combinedPageSql = Sql(Statement.LockAndReadPage);
            var pageSql = combinedPageSql == null ? null : RequireSql(Statement.LockAndReadPage, combinedPageSql);
            var parameters = new DynamicParameters(new { InstanceId = instanceId, AfterSequence = afterSequence, Limit = limit });

            if (pageSql == null)
            {
                var locked = await LockAsync<InboxRow>(connection, transaction, Statement.LockInbox, instanceId,
                    cancellationToken: cancellationToken);

                through = locked.Row?.InboxSequence ?? 0;
                now = locked.Now;
                parameters.Add("Through", through);
                pageSql = RequiredSql(Statement.PendingEvents);
            }

            StoredWorkflowEvent[] events;
            PageWait[] waits;

            using (var results =
                   await connection.QueryMultipleAsync(new CommandDefinition(pageSql, parameters, transaction,
                       cancellationToken: cancellationToken)))
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

            await transaction.CommitAsync(cancellationToken: cancellationToken);

            return InboxPage.Of(events, limit, afterSequence, through,
                waits.ToDictionary(wait => wait.WaitId, wait => wait.OpenedSequence, StringComparer.Ordinal),
                waits.Where(wait => wait.DeadlineUtc != null).ToDictionary(wait => wait.WaitId,
                    wait => ReadTime(wait.DeadlineUtc!.Value), StringComparer.Ordinal), now);
        }, cancellationToken: cancellationToken);
    }

    public async Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        RuntimeIdentifiers.ValidateText(eventId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(eventId.Length, 100);

        return await ExecuteStoreAsync<StoredWorkflowEvent?>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<EventRow>(new CommandDefinition(RequiredSql(Statement.Event),
                new { InstanceId = instanceId, EventId = eventId }, cancellationToken: cancellationToken));

            return row?.Read(ReadTime);
        }, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(IReadOnlyCollection<string> instanceIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(instanceIds);
        InstanceIds.ValidateAll(instanceIds);
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);

        if (instanceIds.Count == 0)
            return sequences;

        return await ExecuteStoreAsync<IReadOnlyDictionary<string, long>>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(ownership: true, cancellationToken: cancellationToken);

            foreach (var batch in instanceIds.Distinct(StringComparer.Ordinal).Chunk(OwnershipReadBatchSize))
            foreach (var row in await connection.QueryAsync<(string InstanceId, long InboxSequence)>(new CommandDefinition(
                         RequiredSql(Statement.InboxSequences), new { InstanceIds = batch }, cancellationToken: cancellationToken)))
                sequences[row.InstanceId] = row.InboxSequence;

            return sequences;
        }, cancellationToken);
    }

    public async Task<StoredWait?> GetWaitAsync(string instanceId, string waitId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(waitId);
        RuntimeIdentifiers.ValidateText(waitId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(waitId.Length, 100);

        return await ExecuteStoreAsync<StoredWait?>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<WaitRow>(new CommandDefinition(RequiredSql(Statement.Wait),
                new { InstanceId = instanceId, WaitId = waitId }, cancellationToken: cancellationToken));

            return row?.Read(ReadTime);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);

        if (query.InstanceId != null)
            InstanceIds.Validate(query.InstanceId);

        if (query.InstanceIds != null)
            InstanceIds.ValidateAll(query.InstanceIds);

        if (query.Key != null)
            RuntimeIdentifiers.ValidateText(query.Key);

        if (query.EventType != null)
            RuntimeIdentifiers.ValidateText(query.EventType);

        if (query.AfterWaitRef != null)
            RuntimeIdentifiers.ValidateText(query.AfterWaitRef);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);

        return await ExecuteStoreAsync<IReadOnlyList<StoredWait>>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            var parameters = WithIntegers(new
            {
                query.Limit,
                query.InstanceId,
                query.Key,
                query.EventType,
                query.AfterWaitRef
            }, ("Kind", (short?)query.Kind), ("Delay", (short)WaitKind.Delay), ("Execution", (short)WaitKind.Execution));

            var sql = RequiredSql(Statement.OpenWaits);

            if (query.InstanceIds is { } ids)
            {
                if (ids.Count > 1000)
                    throw new ArgumentException("At most 1000 instance IDs", nameof(query));

                var predicate = SearchColumn("instance_id") + " " +
                    SearchValues(parameters, "Ids", ids.Distinct(StringComparer.Ordinal).ToArray());

                sql = sql.Replace("WHERE close_reason IS NULL", "WHERE close_reason IS NULL AND " + predicate)
                    .Replace("WHERE CloseReason IS NULL", "WHERE CloseReason IS NULL AND " + predicate);
            }

            if (query.IncludeClosed)
                sql = sql.Replace("close_reason IS NULL", "1=1").Replace("CloseReason IS NULL", "1=1");

            var rows = await connection.QueryAsync<WaitRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

            return rows.Select(row => row.Read(ReadTime)).ToArray();
        }, cancellationToken);
    }

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(commit);
        WorkflowStatuses.Write(commit.Instance.Status);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        try
        {
            return await ExecuteOperationAsync(Operation.Commit, () => CommitAsync(commit, cancellationToken: cancellationToken),
                cancellationToken: cancellationToken);
        }
        catch (Exception error) when (IsRetryableCommit(error) || error is PayloadLockUnavailableException)
        {
            return new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0);
        }
    }

    // Lock order invariant: parent, then identity, then instance, then lease. A child completing into
    // its parent inbox and a parent cancelling its children must acquire these locks in the same order.
    private async Task<WorkflowCommitResult> CommitAsync(CheckpointCommit commit, WorkflowAdmission? admission = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        WorkflowStatuses.Write(commit.Instance.Status);
        var snapshot = commit.Instance;
        var revision = checked(commit.ExpectedRevision + 1);
        await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);
        await using var transaction = (await BeginTransactionAsync(connection, Operation.Commit, cancellationToken: cancellationToken))!;

        if (commit.ExpectedRevision == 0 || commit.ChildStarts.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition(CreateDemandSetsLockSql, transaction: transaction,
                cancellationToken: cancellationToken));

        if (commit.ExpectedRevision == 0 || commit.ChildStarts.Count > 0)
            await ExecuteOptionalAsync(connection, transaction, Statement.CatalogSharedLock, null, cancellationToken: cancellationToken);

        var payloadPins = PayloadStore.Enlist(transaction);

        var parentIncarnation = commit.ParentNotice is { } incomingNotice
            ? await DeletionIncarnationAsync(connection, transaction, incomingNotice.ParentInstanceId, cancellationToken: cancellationToken)
            : null;

        var noticePayload = commit.ParentNotice is { } preparedNotice
            ? InboxPayloads.Prepare(ToEvent(preparedNotice), _payloadSettings)
            : null;

        if (!commit.PreservesPayloadPins)
        {
            await payloadPins.LockAsync(commit.PayloadOwner, commit.PayloadReferences
                .Concat(commit.PayloadBodies.Select(body => body.Reference))
                .Concat(commit.PayloadBodies.SelectMany(body => body.Dependencies))
                .Concat(commit.ChildStarts.SelectMany(child => child.PayloadReferences))
                .Concat(noticePayload?.References ?? []).Concat(noticePayload?.Bodies.SelectMany(b => b.Dependencies) ?? [])
                .Distinct().ToArray(), cancellationToken: cancellationToken);
        }

        WorkflowAdmissionRecord? admitted = null;

        if (admission != null)
        {
            await ExecuteOptionalAsync(connection, transaction, Statement.AdmissionLock, new { Identity = AdmissionLockId(admission) },
                cancellationToken: cancellationToken);

            var admissionNow = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            if (await ReadAdmissionAsync(connection, transaction, admission.Sender, admission.Trigger, admission.Key, admissionNow,
                    cancellationToken: cancellationToken) is { } winner)
            {
                await transaction.CommitAsync(cancellationToken: cancellationToken);

                return new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, 0, StoreTimeUtc: admissionNow)
                    { Admission = winner, Notice = winner.Notice };
            }

            admitted = new(admission.Sender, admission.Trigger, admission.Key, admission.Fingerprint,
                commit.InstanceId, snapshot.WorkflowName, commit.ReadTrunk().WorkflowVersion, admissionNow + admission.KeyRetention,
                commit.Incarnation);
        }

        // A child's terminal commit appends to its parent's inbox. Rows are locked parent first, as a
        // parent's commit that cancels its children does, so the two cannot deadlock.
        // SQLite already holds its write lock. Server clocks are read after the row locks below.
        var now = UsesLocalClock ? LocalNow() : default;

        var parent = commit.ParentNotice is { } notice
            ? (await LockAsync<InboxRow>(connection, transaction, Statement.ParentInbox,
                notice.ParentInstanceId, clock: false, cancellationToken: cancellationToken)).Row
            : null;

        // Only a first commit can insert this row. Existing-row commits serialize on the row;
        // child starts take each child's ID below, after their parent's row.
        if (commit.ExpectedRevision == 0)
            await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity, new { commit.InstanceId },
                cancellationToken: cancellationToken);

        CommitState? current;

        if (UsesLocalClock)
            current = await connection.QuerySingleOrDefaultAsync<CommitState>(new CommandDefinition(RequiredSql(Statement.CommitState),
                new { commit.InstanceId }, transaction, cancellationToken: cancellationToken));
        else
            (current, now) = await LockAsync<CommitState>(connection, transaction, Statement.CommitState, commit.InstanceId,
                cancellationToken: cancellationToken);

        await RefuseRemovedAsync(connection, transaction, commit.InstanceId, now, cancellationToken: cancellationToken);

        DateTime? CurrentRunnable() => NormalizeRunnableTime && current?.RunnableAtUtc is { } due
            ? Utc(due)
            : current?.RunnableAtUtc;

        var currentRevision = current?.Revision ?? 0;

        var receipt = await connection.QuerySingleOrDefaultAsync<CommitReceipt>(new CommandDefinition(RequiredSql(Statement.Receipt),
            new { commit.InstanceId, commit.CommitId }, transaction, cancellationToken: cancellationToken));

        if (receipt == null && commit.ParentNotice is { } identityNotice &&
            await DeletionIncarnationAsync(connection, transaction, identityNotice.ParentInstanceId,
                cancellationToken: cancellationToken) != parentIncarnation)
            return new WorkflowCommitResult(WorkflowCommitStatus.Retry, currentRevision);

        // Receipts are deleted with the instance, so a present receipt belongs to its current creation.
        if (receipt == null && commit.ExpectedRevision > 0 &&
            current != null && current.Incarnation != commit.Incarnation)
        {
            await transaction.CommitAsync(cancellationToken: cancellationToken);

            return new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, currentRevision,
                RunnableAtUtc: CurrentRunnable(), StoreTimeUtc: now);
        }

        // Replays answer their receipt without re-admitting children that may already be removed.
        if (receipt == null && commit.ChildStarts.Count > 0)
        {
            foreach (var child in commit.ChildStarts.OrderBy(c => c.ChildInstanceId, StringComparer.Ordinal))
            {
                // Creation waits, just like deletion: parent row, child identity, child row.
                // Claims never acquire a parent row while holding a child's identity.
                await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity,
                    new { InstanceId = child.ChildInstanceId }, cancellationToken: cancellationToken);

                // Lock an existing child row before checking its tombstone. If purge deletes that
                // row while we wait, the following read sees the tombstone it committed atomically.
                var (_, childNow) = await LockAsync<CommitState>(connection, transaction, Statement.CommitState, child.ChildInstanceId,
                    cancellationToken: cancellationToken);

                await RefuseRemovedAsync(connection, transaction, child.ChildInstanceId, childNow, cancellationToken: cancellationToken);
            }

            // Child row locks may have waited; fencing must use fresh store time.
            now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);
        }

        var lease = await connection.QuerySingleOrDefaultAsync<LeaseRecord>(new CommandDefinition(RequiredSql(Statement.Lease),
            LeaseParameters(commit.InstanceId, now), transaction, cancellationToken: cancellationToken));

        var owns = admission != null
            ? current == null && lease is not { IsActive: true }
            : lease is { IsActive: true } && lease.Epoch == commit.Epoch;

        if (receipt != null)
        {
            if (receipt.ExpectedRevision != commit.ExpectedRevision ||
                !receipt.PayloadHash.SequenceEqual(commit.ContentHash))
                throw new InvalidOperationException("A commit ID cannot be reused for a different transition");

            await transaction.CommitAsync(cancellationToken: cancellationToken);

            // A terminal or parking commit released the lease; only a running owner must still hold it.
            // The same commit gets the same answer, including arrivals it has not read.
            return snapshot.Status == WorkflowStatus.Running && !owns && !commit.Parks
                ? new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, currentRevision,
                    RunnableAtUtc: CurrentRunnable(), StoreTimeUtc: now)
                : new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, receipt.Revision,
                        ReadReason(current?.CancellationJson), commit.Parks && !owns,
                        commit.InboxReadThrough is { } replayRead && current?.InboxSequence > replayRead,
                        receipt.MadeRunnableJson == null ? [] : JsonSerializer.Deserialize<RunnableNotice[]>(receipt.MadeRunnableJson),
                        RunnableAtUtc: CurrentRunnable(), StoreTimeUtc: now)
                    { Notice = ReadNotice(receipt.NoticeJson) };
        }

        if (!owns)
        {
            await transaction.CommitAsync(cancellationToken: cancellationToken);

            return new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, currentRevision,
                RunnableAtUtc: CurrentRunnable(), StoreTimeUtc: now);
        }

        var command = ReadReason(current?.CancellationJson);

        if (command?.Mode == WorkflowControlMode.Terminate &&
            (snapshot.Status != WorkflowStatus.Terminated || snapshot.CommandVersion != command.CommandVersion))
        {
            await transaction.CommitAsync(cancellationToken: cancellationToken);

            return new WorkflowCommitResult(WorkflowCommitStatus.TerminationRequested, currentRevision, command,
                RunnableAtUtc: CurrentRunnable(), StoreTimeUtc: now);
        }

        if (currentRevision != commit.ExpectedRevision ||
            (current?.CancellationJson != null && !snapshot.AcknowledgesCancellation &&
                snapshot.Status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut))
        {
            await transaction.CommitAsync(cancellationToken: cancellationToken);

            return new WorkflowCommitResult(WorkflowCommitStatus.Conflict, currentRevision,
                RunnableAtUtc: CurrentRunnable(), StoreTimeUtc: now);
        }

        if (current != null && current.BundleDigest != snapshot.BundleDigest?.Value)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "An instance's bundle pin is immutable");

        // A child's ID is new; a commit that names an existing instance changes nothing.
        await CheckCatalogStartsAsync(connection, transaction, commit, current == null, cancellationToken: cancellationToken);

        foreach (var children in commit.ChildStarts.Chunk(StatementBatchSize))
            if (await connection.ExecuteScalarAsync<string?>(new CommandDefinition(RequiredSql(Statement.TakenChild),
                    new { Ids = children.Select(child => child.ChildInstanceId).ToArray() }, transaction,
                    cancellationToken: cancellationToken)) is { } taken)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Child workflow '{taken}' cannot start: an instance with its ID exists");

        // An event this commit settles must still be pending.
        var settled = 0;

        foreach (var batch in commit.Settlements.Chunk(StatementBatchSize))
            settled += await SettleAsync(connection, transaction, commit.InstanceId, batch, now, cancellationToken: cancellationToken);

        if (settled != commit.Settlements.Count)
        {
            await transaction.RollbackAsync(cancellationToken: cancellationToken);

            return new WorkflowCommitResult(WorkflowCommitStatus.EventsChanged, currentRevision,
                RunnableAtUtc: CurrentRunnable(), StoreTimeUtc: now);
        }

        var debugCommands = await ReadDebugCommandsAsync(connection, transaction, commit.InstanceId, cancellationToken: cancellationToken);
        var previousDebugInstance = current?.StateJson == null ? null : JsonSerializer.Deserialize<WorkflowInstance>(current.StateJson);

        if (previousDebugInstance != null)
            previousDebugInstance.Revision = currentRevision;

        var nextDebugInstance = commit.ReadTrunk();

        var acknowledgedDebug =
            WorkflowDebugRules.Acknowledge(previousDebugInstance, nextDebugInstance, debugCommands, revision, commit.TraceEntries);

        var debugWake = WorkflowDebugRules.NeedsWake(snapshot.Status, acknowledgedDebug);
        var unacknowledged = current is { CancellationJson: not null, CancellationAcknowledged: false };

        // A run that has not read every arrival neither parks nor goes idle out of reach.
        var unread = commit.InboxReadThrough is { } readThrough && current?.InboxSequence > readThrough;
        var parked = commit.Parks && !unacknowledged && !unread && !debugWake;

        foreach (var batch in commit.OpenedWaits.Chunk(StatementBatchSize))
            await InsertWaitsAsync(connection, transaction, commit.InstanceId, batch, current?.InboxSequence ?? 0, now,
                cancellationToken: cancellationToken);

        foreach (var batch in commit.ArmedWaits.Chunk(StatementBatchSize))
            await ArmWaitsAsync(connection, transaction, commit.InstanceId, batch, now, cancellationToken: cancellationToken);

        foreach (var batch in commit.ClosedWaits.Chunk(StatementBatchSize))
            await CloseWaitsAsync(connection, transaction, commit.InstanceId, batch, now, cancellationToken: cancellationToken);

        if (!commit.PreservesPayloadPins)
        {
            foreach (var body in commit.PayloadBodies)
                await payloadPins.PutAsync(body.Body, body.Dependencies, cancellationToken);

            await payloadPins.ReplaceAsync(commit.PayloadOwner, commit.PayloadReferences, cancellationToken);

            foreach (var child in commit.ChildStarts)
                await payloadPins.ReplaceAsync(CheckpointOwner(child.ChildInstanceId, child.Incarnation), child.PayloadReferences,
                    cancellationToken);

            if (commit.ParentNotice is { } parentNotice && parentIncarnation is { } parentOwner)
                await noticePayload!.PublishAsync(payloadPins, InboxPayloads.Owner(parentNotice.ParentInstanceId, parentOwner),
                    cancellationToken);
        }

        await ApplyPartsAsync(connection, transaction, commit, cancellationToken: cancellationToken);

        // An idle instance is next due at the earliest deadline of its open waits.
        var runnableNow = debugWake || (commit.Parks && !parked) || (unread && !commit.Stopped &&
                snapshot.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution) ||
            WorkflowStoreRules.UnwindsNow(snapshot.Status, snapshot.AcknowledgesCancellation, unacknowledged);

        var earliestDeadline = !runnableNow && !commit.Stopped &&
            snapshot.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution
                ? await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(RequiredSql(Statement.EarliestDeadline),
                    new { commit.InstanceId }, transaction, cancellationToken: cancellationToken))
                : null;

        var runnableAt = WorkflowStoreRules.CommittedRunnableAt(snapshot.Status, commit, runnableNow, now,
            earliestDeadline is { } deadline && NormalizeRunnableTime ? Utc(deadline) : earliestDeadline);

        var values = WithTimes(new
        {
            StopKind = snapshot.StopKind?.ToString(),
            snapshot.StopCrashCount,
            snapshot.InstanceId,
            snapshot.WorkflowName,
            snapshot.WorkflowVersion,
            BundleDigest = snapshot.BundleDigest?.Value,
            snapshot.CorrelationId,
            snapshot.ParentInstanceId,
            snapshot.ParentWaitId,
            commit.Incarnation,
            Status = snapshot.Status.ToString(),
            commit.InboxReadThrough,
            StateJson = DemandSet.StripPayload(current == null ? StampStartPayload(commit.Payload, now) : commit.Payload),
            Revision = revision,
            commit.ExpectedRevision,
            commit.CommitId,
            PayloadHash = commit.ContentHash,
            snapshot.AcknowledgesCancellation
        }, ("RunnableAtUtc", runnableAt), ("CreatedAtUtc", snapshot.CreatedAt));

        if (current == null)
            values.AddDynamicParams(await InternDemandsAsync(connection, transaction, StampStartPayload(commit.Payload, now),
                cancellationToken: cancellationToken));

        // A missing row is inserted, so a commit racing to create the same instance violates its key and
        // is retried, rather than overwriting it.
        await connection.ExecuteAsync(new CommandDefinition(current == null
            ? RequiredSql(Statement.InsertCommit)
            : RequiredSql(Statement.UpdateCommit), values, transaction, cancellationToken: cancellationToken));

        await WriteDebugAcknowledgementsAsync(connection, transaction, commit.InstanceId, debugCommands, acknowledgedDebug,
            cancellationToken: cancellationToken);

        if (admission == null)
            await connection.ExecuteAsync(new CommandDefinition(StampHostSql, new { commit.InstanceId }, transaction,
                cancellationToken: cancellationToken));

        foreach (var diagnostic in commit.Diagnostics)
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.InsertDiagnostic), new
            {
                commit.InstanceId, diagnostic.DiagnosticId,
                DiagnosticJson = JsonSerializer.Serialize(diagnostic)
            }, transaction, cancellationToken: cancellationToken));

        if (commit.RetainedDiagnosticIds is { } retained)
        {
            // Bound parameter counts even when many branches need operator resolution. Read only IDs;
            // ordinary execution never loads the stored messages or stacks.
            var keep = retained.ToHashSet(StringComparer.Ordinal);

            var ids = await connection.QueryAsync<string>(new CommandDefinition(RequiredSql(Statement.DiagnosticIds),
                new { commit.InstanceId }, transaction, cancellationToken: cancellationToken));

            foreach (var batch in ids.Where(id => !keep.Contains(id)).Chunk(StatementBatchSize))
                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DeleteDiagnostics),
                    new { commit.InstanceId, Ids = batch }, transaction, cancellationToken: cancellationToken));
        }

        await RecordCompletionAsync(connection, transaction, WorkflowStoreRules.IsTerminal(snapshot.Status) ? commit.ReadTrunk() : null,
            revision, now, cancellationToken: cancellationToken);

        if (commit.TraceEntries.Count > 0)
            await AppendTraceAsync(connection, transaction, commit.InstanceId, commit.TraceEntries, revision, snapshot.Status,
                cancellationToken: cancellationToken);

        // The lease row is written only when something changes, so most commits touch only their
        // instance row. An idle owner that has not read every arrival keeps the lease and reads them.
        var keepsLease = unread && snapshot.Status == WorkflowStatus.Suspended;
        var releases = (snapshot.Status != WorkflowStatus.Running || parked) && !keepsLease;

        // A finished instance takes no more events; those still pending are closed, and so are its waits.
        if (WorkflowStoreRules.IsTerminal(snapshot.Status))
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.FinishInbox), WithTimes(WithIntegers(new
                {
                    snapshot.InstanceId
                }, ("Reason", (short)WorkflowStoreRules.FinalWaitCloseReason(snapshot.Status))), ("Now", now)), transaction,
                cancellationToken: cancellationToken));

        var clears = commit.ClearsCrashCount && lease?.CrashCount > 0;

        if (lease != null && (releases || clears))
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.UpdateLease),
                new { snapshot.InstanceId, Released = releases, ClearsCrashCount = clears }, transaction,
                cancellationToken: cancellationToken));

        var madeRunnable = new List<string>();

        // Each child starts as its own instance, runnable now, with no owner yet.
        // The payload is stored as the start carries it; the child's revision and runnable time live in their columns.
        if (commit.ChildStarts.Count > 0)
        {
            var children = SortChildStarts
                ? commit.ChildStarts.OrderBy(child => child.Instance!.InstanceId, StringComparer.Ordinal).ToArray()
                : commit.ChildStarts.ToArray();

            await InsertChildrenAsync(connection, transaction, commit.InstanceId, children, now, cancellationToken: cancellationToken);
            madeRunnable.AddRange(children.Select(child => child.Instance!.InstanceId));
        }

        // Each event this commit appends to its own inbox takes the next sequence.
        var inbox = new InboxRow { Status = snapshot.Status.ToString(), InboxSequence = current?.InboxSequence ?? 0 };
        var woken = false;

        foreach (var cancellation in commit.ChildCancellations)
        {
            await LockAsync<InboxRow>(connection, transaction, Statement.LockInbox, cancellation.ChildInstanceId, clock: false,
                cancellationToken: cancellationToken);

            var wasClaimable =
                (await NoticeAsync(connection, transaction, cancellation.ChildInstanceId, now, cancellationToken: cancellationToken))
                ?.Claimable == true;

            if (cancellation.Reason.Mode == WorkflowControlMode.Terminate)
            {
                await RequestTerminationAsync(connection, transaction, cancellation.ChildInstanceId, cancellation.Reason, madeRunnable,
                    cancellationToken: cancellationToken);

                continue;
            }

            var outcome = await RequestCancellationAsync(connection, transaction, cancellation.ChildInstanceId,
                JsonSerializer.Serialize(cancellation.Reason), now, cancellationToken: cancellationToken);

            if (outcome == CancellationOutcome.MadeRunnable)
            {
                woken = true;

                if (!wasClaimable)
                    madeRunnable.Add(cancellation.ChildInstanceId);
            }

            // A child that is gone will never answer; its parent's wait ends as its termination.
            else if (outcome == CancellationOutcome.Gone)
            {
                var gone = ToEvent(WorkflowStoreRules.ChildGone(cancellation.ChildInstanceId, commit.InstanceId,
                    cancellation.ParentWaitId!));

                var appended = await AppendAsync(connection, transaction, gone, gone.ContentHash(), inbox, now,
                    cancellationToken: cancellationToken);

                if (appended.Status == EventAppendStatus.Stored)
                    inbox.InboxSequence = appended.Sequence;

                woken |= appended.MadeRunnable;

                // This arrival changes the committing instance's own schedule. Waking a child or
                // the parent below only changes that other instance, never this receipt's due time.
                if (appended.MadeRunnable)
                    runnableAt = now;
            }
        }

        // The child's completion goes to its parent's inbox; whatever the parent's state, the child's
        // commit stands.
        if (commit.ParentNotice is { } completion)
        {
            var completed = ToEvent(completion);

            var appended = await AppendAsync(connection, transaction, completed, completed.ContentHash(), parent, now,
                cancellationToken: cancellationToken);

            if (appended.Status == EventAppendStatus.Stored && appended.MadeRunnable)
            {
                woken = true;

                if (appended.Claimable)
                    madeRunnable.Add(completion.ParentInstanceId);
            }
        }

        // Other hosts hear of what this commit made claimable: other instances, and this one when it is due
        // and nobody holds it, because the commit let it go or woke it with an event.
        if (madeRunnable.Count > 0 || woken || (runnableAt <= now && releases))
            await NotifyRunnableAsync(connection, transaction, cancellationToken: cancellationToken);

        // Child termination and inbox appends take their own store-time samples. Their runnable
        // times can be later than this commit's initial sample; judge notices after those writes.
        var noticeTime = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);
        var notices = new List<RunnableNotice>();

        foreach (var id in madeRunnable.Distinct(StringComparer.Ordinal))
            if (await NoticeAsync(connection, transaction, id, noticeTime, cancellationToken: cancellationToken) is
                { Claimable: true } runnable)
                notices.Add(runnable);

        var changed = await NoticeAsync(connection, transaction, commit.InstanceId, noticeTime, cancellationToken: cancellationToken);
        var ownNotice = changed is { Claimable: true } or { Idle: true } ? changed : null;

        await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.UpdateReceiptNotices), new
        {
            commit.InstanceId, commit.CommitId,
            NoticeJson = ownNotice == null ? null : JsonSerializer.Serialize(ownNotice),
            MadeRunnableJson = JsonSerializer.Serialize(notices)
        }, transaction, cancellationToken: cancellationToken));

        if (admitted != null)
        {
            admitted = admitted with { Notice = ownNotice };
            var identity = new { admission!.Sender, admission.Trigger, admission.Key };

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.AdmissionDelete), identity, transaction,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.AdmissionInsert), WithTimes(new
            {
                admission.Sender, admission.Trigger, admission.Key, admission.Fingerprint, commit.InstanceId, commit.Incarnation,
                admitted.WorkflowName, admitted.WorkflowVersion,
                NoticeJson = ownNotice == null ? null : JsonSerializer.Serialize(ownNotice)
            }, ("ExpiresAtUtc", admitted.ExpiresAtUtc)), transaction, cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.OwnershipDelete), new { commit.InstanceId },
                transaction, cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.OwnershipInsert),
                new
                {
                    commit.InstanceId, commit.Incarnation, admission.Sender, admission.Trigger, admission.Owner, admission.Tenant,
                    admission.OwnerKind
                }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken: cancellationToken);

        return new WorkflowCommitResult(WorkflowCommitStatus.Applied, revision,
                ReadReason(current?.CancellationJson), parked, unread, notices, runnableAt, now)
            { Admission = admitted, Notice = ownNotice };
    }

    private static RunnableNotice? ReadNotice(string? json) =>
        json == null ? null : JsonSerializer.Deserialize<RunnableNotice>(json);

    private async Task<RunnableNotice?> NoticeAsync(DbConnection connection, DbTransaction transaction,
        string instanceId, DateTime now, CancellationToken cancellationToken = default)
    {
        var row = await connection.QuerySingleOrDefaultAsync<NoticeRow>(new CommandDefinition(RequiredSql(Statement.Notice),
            WithTimes(new { InstanceId = instanceId }, ("Now", now)), transaction, cancellationToken: cancellationToken));

        return row == null
            ? null
            : new(instanceId, row.DemandSetId,
                row.PinnedHost == null
                    ? null
                    : new WorkflowDemand("host", row.PinnedHost,
                        row.PinExpiresAtUtc is { } expiry ? ReadTime(expiry) : null, row.PinInherit),
                row.LastHostId, row.Claimable, row.Idle);
    }

    private sealed class NoticeRow
    {
        public string DemandSetId { get; set; } = "";
        public string? PinnedHost { get; set; }
        public DateTime? PinExpiresAtUtc { get; set; }
        public bool PinInherit { get; set; }
        public Guid? LastHostId { get; set; }

        public object? LastHostIdValue
        {
            set => LastHostId = ReadOptionalGuid(value);
        }

        public bool Claimable { get; set; }
        public bool Idle { get; set; }
    }

    // Replaced and removed parts go before inserts, in the same transaction as the trunk.
    private async Task ApplyPartsAsync(DbConnection connection, DbTransaction transaction, CheckpointCommit commit,
        CancellationToken cancellationToken = default)
    {
        if (commit.ReplacesParts)
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DeleteAllParts), new { commit.InstanceId },
                transaction, cancellationToken: cancellationToken));
        else
            foreach (var keys in commit.DeletedParts.Concat(commit.Parts.Select(part => part.Key)).Chunk(StatementBatchSize))
                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DeleteParts),
                    new { commit.InstanceId, Keys = keys }, transaction, cancellationToken: cancellationToken));

        foreach (var parts in commit.Parts.Chunk(StatementBatchSize))
            await InsertPartsAsync(connection, transaction, commit.InstanceId, parts, cancellationToken: cancellationToken);
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);

        return await ExecuteStoreAsync<WorkflowInstance?>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);
            await using var transaction = await BeginTransactionAsync(connection, Operation.Read, cancellationToken: cancellationToken);

            return await ReadAtOneRevisionAsync(connection, transaction, instanceId, true, "", null, cancellationToken: cancellationToken);
        }, cancellationToken);
    }

    public async Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);

        return await ExecuteStoreAsync<WorkflowInstance?>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);
            await using var transaction = await BeginTransactionAsync(connection, Operation.Read, cancellationToken: cancellationToken);

            return await ReadAtOneRevisionAsync(connection, transaction, instanceId, false, "", null, cancellationToken: cancellationToken);
        }, cancellationToken);
    }

    internal const string EventColumns =
        "instance_id AS InstanceId, event_id AS EventId, sequence AS Sequence, event_type AS EventType, " +
        "event_key AS EventKey, target_wait_id AS TargetWaitId, stored_payload_json AS PayloadJson, " +
        "received_at_utc AS ReceivedAtUtc, outcome AS Outcome, wait_id AS WaitId, rejection AS Rejection, " +
        "subject AS Subject";

    internal const string WaitColumns =
        "instance_id AS InstanceId, wait_id AS WaitId, kind AS Kind, wait_key AS WaitKey, event_type AS EventType, " +
        "deadline_utc AS DeadlineUtc, opened_sequence AS OpenedSequence, opened_at_utc AS OpenedAtUtc, " +
        "close_reason AS CloseReason, closed_at_utc AS ClosedAtUtc, default_payload_json AS DefaultPayloadJson";

    internal const string RowColumns =
        "instance_id AS InstanceId, status AS Status, state_json AS StateJson, runnable_at_utc AS RunnableAtUtc, revision AS Revision, last_host_id AS LastHostIdValue, bundle_digest AS BundleDigest, demand_set_id AS DemandSetId, pinned_host AS PinnedHost, pin_expires_at_utc AS PinExpiresAtUtc, pin_inherit AS PinInherit";

    internal const string StampedRowColumns = RowColumns + ", incarnation AS IncarnationValue";

    private const int MaxReadAttempts = 20;

    // Server reads verify revision and incarnation. Snapshot reads already hold trunk and parts together.
    private async Task<WorkflowInstance?> ReadAtOneRevisionAsync(DbConnection connection, DbTransaction? transaction,
        string instanceId, bool wholly, string filter, object? filterValues, CancellationToken cancellationToken = default)
    {
        var parameters = new DynamicParameters(new { InstanceId = instanceId });

        if (!wholly)
            parameters.Add("DataKey", CheckpointParts.DataKey);

        if (filterValues != null)
            parameters.AddDynamicParams(filterValues);

        for (var attempt = 1;; attempt++)
        {
            StoredRow? row;
            StoredPart[] parts;
            StoredStamp? stamp = null;

            using (var read = await connection.QueryMultipleAsync(new CommandDefinition(
                       RequireSql(Statement.ReadInstance, ReadInstanceSql(filter, wholly)), parameters, transaction,
                       cancellationToken: cancellationToken)))
            {
                row = await read.ReadSingleOrDefaultAsync<StoredRow>();
                parts = (await read.ReadAsync<StoredPart>()).ToArray();

                if (ReadsUseStamps)
                    stamp = await read.ReadSingleOrDefaultAsync<StoredStamp>();
            }

            if (row == null)
                return null;

            if (!ReadsUseStamps || stamp != null && row.Same(stamp))
                return await HydrateDemandsAsync(connection, transaction, row,
                    wholly ? row.Read(parts, NormalizeRunnableTime) : row.Summarize(parts.SingleOrDefault()?.Json, NormalizeRunnableTime),
                    cancellationToken: cancellationToken);

            if (attempt == MaxReadAttempts)
                throw new InvalidOperationException($"Workflow instance '{instanceId}' changed in each of {MaxReadAttempts} reads");

            await Task.Delay(Random.Shared.Next(1, 5 * attempt));
        }
    }

    public async Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        var period = tombstoneRetention ?? RetentionSettings.Default.TombstoneRetention;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        await ExecuteOperationAsync(Operation.Delete, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);
            var parameters = new { InstanceId = instanceId };

            while (true)
            {
                await using var transaction =
                    (await BeginTransactionAsync(connection, Operation.Delete, cancellationToken: cancellationToken))!;

                var incarnation = await DeletionIncarnationAsync(connection, transaction, instanceId, cancellationToken: cancellationToken);
                var pins = PayloadStore.Enlist(transaction);

                var references = incarnation is { } payloadIncarnation
                    ? await DeletionReferencesAsync(connection, transaction, instanceId, payloadIncarnation,
                        cancellationToken: cancellationToken)
                    : [];

                if (incarnation is { } seenIncarnation)
                {
                    await pins.LockAsync(CheckpointOwner(instanceId, seenIncarnation), references, cancellationToken: cancellationToken);
                }

                var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(RequiredSql(Statement.DeleteState),
                    parameters, transaction, cancellationToken: cancellationToken));

                var seen = WaitingParent(json);

                var parent = seen is { } waiting
                    ? await LockAsync<InboxRow>(connection, transaction, Statement.DeleteParentInbox, waiting.ParentId,
                        cancellationToken: cancellationToken)
                    : default;

                await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity, parameters,
                    cancellationToken: cancellationToken);

                var lockedIncarnation =
                    await DeletionIncarnationAsync(connection, transaction, instanceId, cancellationToken: cancellationToken);

                if (lockedIncarnation != incarnation)
                {
                    await transaction.RollbackAsync(cancellationToken: cancellationToken);

                    continue;
                }

                if (DeleteRevalidationSql is { } lockedSql)
                {
                    json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                        CheckSql(nameof(DeleteRevalidationSql), lockedSql), parameters, transaction, cancellationToken: cancellationToken));

                    if (WaitingParent(json) != seen)
                    {
                        await transaction.RollbackAsync(cancellationToken: cancellationToken);

                        continue;
                    }
                }

                if (incarnation is { } currentIncarnation)
                {
                    var currentReferences = await DeletionReferencesAsync(connection, transaction, instanceId, currentIncarnation,
                        cancellationToken: cancellationToken);

                    if (!references.SequenceEqual(currentReferences))
                    {
                        await transaction.RollbackAsync(cancellationToken: cancellationToken);

                        continue;
                    }
                }

                if (seen is { } answering)
                {
                    var gone = ToEvent(WorkflowStoreRules.ChildGone(instanceId, answering.ParentId, answering.WaitId));

                    if ((await AppendAsync(connection, transaction, gone, gone.ContentHash(), parent.Row, parent.Now,
                            cancellationToken: cancellationToken)).MadeRunnable)
                        await NotifyRunnableAsync(connection, transaction, cancellationToken: cancellationToken);
                }

                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DeleteLease), parameters, transaction,
                    cancellationToken: cancellationToken));

                // A provider that rechecks the row leaves an instance inserted after that read whole.
                if (json != null || DeleteRevalidationSql == null)
                {
                    if (json != null)
                    {
                        await pins.ReleaseAsync(CheckpointOwner(instanceId, incarnation!.Value), cancellationToken);
                        await pins.ReleaseAsync(InboxPayloads.Owner(instanceId, incarnation.Value), cancellationToken);
                        var removedAt = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

                        await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.WriteTombstone),
                            WithTimes(parameters, ("Now", removedAt), ("Expires", removedAt + period)), transaction,
                            cancellationToken: cancellationToken));
                    }

                    await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DeleteRows), parameters, transaction,
                        cancellationToken: cancellationToken));
                }

                await transaction.CommitAsync(cancellationToken: cancellationToken);

                return true;
            }
        }, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return await ExecuteStoreAsync<IReadOnlyList<StoredTraceEntry>>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            var rows =
                await connection.QueryAsync<(long Sequence, string EntryJson, long Revision, long? CommitEndSequence, int? CommitStatus)>(
                    new CommandDefinition(RequiredSql(Statement.Trace),
                        new { InstanceId = instanceId, AfterSequence = afterSequence, Limit = limit },
                        cancellationToken: cancellationToken));

            return rows.Select(row => new StoredTraceEntry(row.Sequence,
                JsonSerializer.Deserialize<ExecutionTraceEntry>(row.EntryJson)!)
            {
                Revision = row.Revision, CommitEndSequence = row.CommitEndSequence,
                CommitStatus = WorkflowStatuses.Read((WorkflowStatus?)row.CommitStatus)
            }).ToArray();
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);

        return await ExecuteStoreAsync<IReadOnlyList<WorkflowDiagnostic>>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            var rows = await connection.QueryAsync<string>(new CommandDefinition(RequiredSql(Statement.IncarnationDiagnostics),
                new { InstanceId = instanceId, Incarnation = incarnation }, cancellationToken: cancellationToken));

            return rows.Select(json => JsonSerializer.Deserialize<WorkflowDiagnostic>(json)!).ToArray();
        }, cancellationToken);
    }

    private static WorkflowEvent ToEvent(ParentNotice notice) =>
        new(notice.ParentInstanceId, notice.EventId, notice.EventType, notice.PayloadJson,
            TargetWaitId: notice.ParentWaitId) { PayloadDependencies = notice.PayloadDependencies, PayloadThresholdBytes = 0 };

    private sealed class InboxRow
    {
        public bool Stopped { get; set; }
        public bool TracingEnabled { get; set; }
        public long Revision { get; set; }
        public string Status { get; set; } = "";
        public long InboxSequence { get; set; }
        public long InboxReadThrough { get; set; }
    }

    private sealed class EventHashRow
    {
        public string? NoticeJson { get; set; }
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
            TargetWaitId, JsonSerializer.Deserialize<StoredJson>(PayloadJson)!, readTime(ReceivedAtUtc), (EventOutcome)Outcome, WaitId,
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
            (WaitCloseReason?)CloseReason, ClosedAtUtc is { } closed ? readTime(closed) : null,
            DefaultPayloadJson is null ? null : JsonSerializer.Deserialize<StoredJson>(DefaultPayloadJson)!);
    }

    private sealed class RunnableRow
    {
        public string InstanceId { get; set; } = "";
        public DateTime RunnableAtUtc { get; set; }
    }

    private sealed class CommitReceipt
    {
        public string? NoticeJson { get; set; }
        public string? MadeRunnableJson { get; set; }
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
        public string? BundleDigest { get; set; }
        public string? StateJson { get; set; }
        public DateTime? RunnableAtUtc { get; set; }
        public Guid Incarnation { get; set; }

        public object? IncarnationValue
        {
            set => Incarnation = ReadGuid(value);
        }

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
        public string Status { get; set; } = "";
        public string StateJson { get; set; } = "";
        public string? BundleDigest { get; set; }
        public DateTime? RunnableAtUtc { get; set; }
        public long Revision { get; set; }
        public Guid Incarnation { get; set; }

        public object? IncarnationValue
        {
            set => Incarnation = ReadGuid(value);
        }

        public Guid? LastHostId { get; set; }

        public object? LastHostIdValue
        {
            set => LastHostId = ReadOptionalGuid(value);
        }

        public string DemandSetId { get; set; } = "";
        public string? PinnedHost { get; set; }
        public DateTime? PinExpiresAtUtc { get; set; }
        public bool PinInherit { get; set; }

        public bool Same(StoredStamp stamp) => stamp.Revision == Revision && stamp.Incarnation == Incarnation;

        public WorkflowInstance Read(IEnumerable<StoredPart> parts, bool normalize) => Runnable(CheckpointParts.Assemble(StateJson,
                parts.Select(part => new CheckpointPart(part.PartKey, JsonSerializer.Deserialize<StoredJson>(part.Json)!)), Revision),
            normalize);

        public WorkflowInstance Summarize(string? data, bool normalize) => Runnable(
            CheckpointParts.Summarize(StateJson, data is null ? null : JsonSerializer.Deserialize<StoredJson>(data)!, Revision), normalize);

        private WorkflowInstance Runnable(WorkflowInstance instance, bool normalize)
        {
            WorkflowStatuses.Read(Status);

            if (instance.BundleDigest?.Value != BundleDigest)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Stored bundle digest differs from instance payload");

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

        public object? IncarnationValue
        {
            set => Incarnation = ReadGuid(value);
        }
    }

    internal static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static int LeaseMilliseconds(string instanceId, string token, TimeSpan duration)
    {
        InstanceIds.Validate(instanceId);
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