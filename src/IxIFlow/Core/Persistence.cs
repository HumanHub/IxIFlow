using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>
///     Repository interface for workflow state persistence
/// </summary>
/// <summary>An instance a host may claim now, and the time it became runnable.</summary>
public sealed record RunnableInstance(string InstanceId, DateTime RunnableAtUtc);

/// <summary>Rules every state store applies when it saves an instance.</summary>
public static class WorkflowStoreRules
{
    private static readonly JsonSerializerOptions TraceJson = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                type =>
                {
                    if (type.Type == typeof(ExecutionTraceEntry))
                    {
                        type.Properties.Single(property => property.Name == nameof(ExecutionTraceEntry.Metadata))
                            .ShouldSerialize = (_, value) => value is Dictionary<string, object> { Count: > 0 };
                        type.Properties.Single(property => property.Name == nameof(ExecutionTraceEntry.ActivityName))
                            .ShouldSerialize = (_, value) => value is string { Length: > 0 };
                    }
                }
            }
        }
    };

    /// <summary>Encodes a stored trace entry, omitting empty metadata while preserving its non-nullable API.</summary>
    public static string SerializeTrace(ExecutionTraceEntry entry) => JsonSerializer.Serialize(entry, TraceJson);

    public static bool IsTerminal(WorkflowStatus status) => status is WorkflowStatus.Completed or WorkflowStatus.Failed
        or WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut;

    public static bool UnwindsNow(WorkflowStatus status, bool acknowledgesCancellation,
        bool unacknowledgedCancellation) =>
        unacknowledgedCancellation && !acknowledgesCancellation &&
        status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution or WorkflowStatus.Paused;

    /// <summary>
    /// When a committed instance becomes runnable, in the store's clock: now when
    /// <paramref name="runnableNow"/>; never while stopped for an operator; at the end of a park, or now,
    /// while it runs; and while it is idle, at the earliest deadline of its open waits or after the
    /// commit's <see cref="CheckpointCommit.WakeAfter"/>, whichever comes first.
    /// </summary>
    public static DateTime? CommittedRunnableAt(WorkflowStatus status, CheckpointCommit commit, bool runnableNow,
        DateTime now, DateTime? earliestDeadline)
    {
        if (runnableNow)
            return now;
        if (commit.Stopped || status == WorkflowStatus.Paused)
            return null;
        return status switch
        {
            WorkflowStatus.Running => commit.Parks ? now + commit.WakeAfter!.Value : now,
            WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution =>
                new[] { earliestDeadline, now + commit.WakeAfter }.Min(),
            _ => null
        };
    }

    /// <summary>
    /// The wait an untargeted event should be judged against among <paramref name="openWaits"/>, at the
    /// store's time <paramref name="now"/>: any that could still take it comes before one past its deadline.
    /// Only event waits take untargeted events; a child wait takes only its child's completion, addressed
    /// to it.
    /// </summary>
    public static IEnumerable<StoredWait> Candidates(WorkflowEvent workflowEvent, IEnumerable<StoredWait> openWaits,
        DateTime now) =>
        openWaits.Where(wait => wait.Kind == WaitKind.Event && (workflowEvent.Key == null ||
                                string.Equals(wait.Key, workflowEvent.Key, StringComparison.Ordinal)))
            .OrderBy(wait => wait.DeadlineUtc <= now);

    /// <summary>
    /// The completion a store appends to a parent's wait when the child it waits for is gone: deleted by an
    /// operator before it ended, or missing when the parent asks it to cancel. The parent's invocation then
    /// ends as the child's termination.
    /// </summary>
    public static ParentNotice ChildGone(string childInstanceId, string parentInstanceId, string parentWaitId) =>
        new(parentInstanceId, parentWaitId, $"child-completed-{childInstanceId}", ChildCompleted.EventType,
            JsonSerializer.Serialize(new ChildCompleted
            {
                ChildInstanceId = childInstanceId,
                Status = WorkflowStatus.Terminated
            }));

    /// <summary>Why the waits still open when an instance finishes are closed.</summary>
    public static WaitCloseReason FinalWaitCloseReason(WorkflowStatus status) =>
        status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut
            ? WaitCloseReason.Cancelled
            : WaitCloseReason.InstanceFinished;

    /// <summary>
    /// An arrival refusal has no executor pass to carry its trace. Append this one entry in the inbox's
    /// transaction when tracing is enabled: once per stored refusal, or per Closed API response.
    /// </summary>
    public static ExecutionTraceEntry RefusedEventTrace(WorkflowEvent workflowEvent, EventRejection rejection,
        DateTime now) => new()
    {
        Timestamp = now,
        EntryType = TraceEntryType.EventRefused,
        Clock = TraceClock.Store,
        Payload = new ReceivedEventTrace(workflowEvent.EventId, workflowEvent.Key, workflowEvent.EventType,
            workflowEvent.TargetWaitId is { } waitId ? WaitReference.Of(workflowEvent.InstanceId, waitId) : null,
            null, rejection)
    };

    /// <summary>
    /// Why an event that arrives at the store's time <paramref name="now"/> is refused before anyone reads
    /// it, or null when it is kept for the owner. <paramref name="target"/> is the wait the
    /// event names, if it names one; otherwise <paramref name="candidates"/> are the open waits it could
    /// reach, as <see cref="Candidates"/> orders them. Any other event needs an open event wait with its
    /// key (any key when it has none). An event that arrives when its deadline, or every candidate's, has
    /// passed is refused as timed out.
    /// </summary>
    public static EventRejection? RejectOnArrival(WorkflowEvent workflowEvent, StoredWait? target,
        IEnumerable<StoredWait> candidates, DateTime now)
    {
        if (workflowEvent.TargetWaitId != null)
            return target is null or { Kind: WaitKind.Delay or WaitKind.Execution } ? EventRejection.UnknownWait
                : !target.IsOpen ? target.Rejection
                : target.DeadlineUtc <= now ? EventRejection.WaitTimedOut
                : null;
        return candidates.FirstOrDefault() switch
        {
            null => EventRejection.NoOpenWait,
            { DeadlineUtc: { } deadline } when deadline <= now => EventRejection.WaitTimedOut,
            _ => null
        };
    }
}

/// <summary>
/// Stores workflow instances. First commits carry a fresh incarnation; child starts allocate
/// one in the store on insertion. Every instance read round-trips it separately from serialized state.
/// </summary>
public interface IWorkflowStateRepository : IWorkflowCatalogStore, IArtifactStore, IWorkflowInbox, IWorkflowOutbox, IWorkflowAdmissionStore, IWorkflowDebugStore
{
    /// <summary>Bodies and pins stored transactionally with this repository's referencing rows.</summary>
    IPayloadStore PayloadStore { get; }
    /// <summary>Due, unleased work no live host meets, ordered by runnable time and ordinal instance ID.</summary>
    Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>Whether one live host satisfies every unexpired demand, evaluated by store time.</summary>
    Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default);

    /// <summary>The resolved placement rows saved in the instance's creating transaction.</summary>
    Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId);

    /// <summary>Registers a member and its capabilities atomically, stamping start and heartbeat with store time.</summary>
    Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default);

    /// <summary>Renews and replaces the capability snapshot. False means the member must register again.</summary>
    Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the member and all its capabilities. Repeated removal is harmless.</summary>
    Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns snapshots younger than three heartbeat intervals by store time. Registry operations
    /// also prune members silent for over an hour, together with their capabilities.
    /// </summary>
    Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one bounded retention pass, giving up never-attempted deliveries whose event age reaches
    /// the store's retry horizon. Remaining pending deliveries and unfinished children keep their instance.
    /// </summary>
    Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>The active retention tombstone, or null when the ID is not reserved.</summary>
    Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId);

    /// <summary>
    /// Records a cancellation request outside the revisioned checkpoint so a remote
    /// caller never overwrites the running owner's state. The first request wins.
    /// Reports acceptance and any claimable change from the same transaction.
    /// </summary>
    Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null);

    /// <summary>Durably upgrades cancellation to termination; repeated termination returns the same command version.</summary>
    Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null);

    /// <summary>Reads a durable cancellation request for an instance.</summary>
    Task<CancellationReason?> GetCancellationRequestAsync(string instanceId);

    /// <summary>
    /// Lists up to the query limit of due, unowned instances whose active demands this host meets.
    /// This host's last-run rows come first, each group ordered by runnable time and instance ID.
    /// </summary>
    Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query);

    /// <summary>
    /// Atomically saves a valid schema-4 checkpoint at its expected revision + 1, under a granted,
    /// active execution lease with the same epoch. The first commit inserts a fresh incarnation;
    /// later commits must carry the row's incarnation. A different incarnation or missing, expired or
    /// mismatched lease returns <see cref="WorkflowCommitStatus.StaleEpoch"/>.
    /// Repeating a commit ID with the same payload returns its original receipt, even after subsequent
    /// commits. Receipt lookup precedes incarnation and revision checks. A store that knows nothing was
    /// written may return <see cref="WorkflowCommitStatus.Retry"/>; an exception means the outcome is
    /// unknown and the same commit may be sent again.
    /// </summary>
    Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit);

    /// <summary>
    /// Acquires an absent or expired execution lease for one instance. An epoch is never granted
    /// twice in a store, and epochs strictly increase for each instance ID, including across deletions
    /// and re-creations. Across instances, epochs follow allocation order, with gaps allowed;
    /// grants may complete in a different order. When the previous lease expired without
    /// being released while the instance was <see cref="WorkflowStatus.Running"/>, its owner lost the
    /// instance with work in flight, and the grant's crash count is one higher, in the same atomic
    /// step. Returns null while another owner holds the lease or the ID has a live tombstone,
    /// checked atomically with the claim. Existing rows must be due and meet the query's capabilities.
    /// Operator claims may also enter an idle resolution or stopped checkpoint, never a future park;
    /// demands still apply. An absent ID without a live tombstone may be claimed for creation.
    /// </summary>
    Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable);

    /// <summary>
    /// Renews execution tokens in one batch, returning one result per request in the same order.
    /// Instance IDs must be distinct. Missing, expired or mismatched tokens are refused individually.
    /// Pending termination refuses renewal and returns its durable reason without extending expiry.
    /// Providers chunk requests within their parameter limits and use their ownership connection pool.
    /// </summary>
    Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(
        IReadOnlyList<ExecutionLeaseRenewal> leases);

    /// <summary>Releases a lease only when the caller still owns it.</summary>
    Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token);

    /// <summary>
    /// The whole instance: its trunk and every part of its state, read at one revision.
    /// Every instance read, including summaries, queries and child lists, must round-trip the row's
    /// Incarnation separately from serialized state.
    /// </summary>
    Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId);

    /// <summary>
    /// The instance without its branches: its fields, its workflow data and the trunk of its execution state.
    /// Enough for a status, a result or a stop check, never to run it. Reads only the trunk and the data.
    /// </summary>
    Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId);

    /// <summary>Searches persisted instance rows without reading checkpoints or parts.</summary>
    Task<InstancePage> QueryInstancesAsync(InstanceQuery query);

    /// <summary>
    /// The instance's trace entries after <paramref name="afterSequence"/>, in order, at most
    /// <paramref name="limit"/>. Commits append them; deleting the instance deletes them.
    /// </summary>
    Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000);

    /// <summary>
    /// Raw immutable diagnostic records, read atomically only while the instance has the authorized incarnation.
    /// Only the authorized diagnostics service exposes this read.
    /// </summary>
    Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation);

    /// <summary>
    /// Deletes an instance and its inbox, waits and receipts. A child whose parent still waits for it ends
    /// that wait as terminated, in the same step. Leaves a tombstone for the supplied retention
    /// period, or 90 days by default. Pass the same TombstoneRetention used for automatic retention.
    /// Deleting an unknown or already removed ID still removes its lease but does not create or extend a tombstone.
    /// </summary>
    Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null);
}

/// <summary>
///     In-memory implementation of workflow state repository
/// </summary>
public partial class InMemoryWorkflowStateRepository : IWorkflowStateRepository
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _retryHorizon;

    public InMemoryWorkflowStateRepository() : this(TimeProvider.System)
    {
    }

    public InMemoryWorkflowStateRepository(TimeProvider time, OutboxSettings? outboxSettings = null, PayloadStoreSettings? payloadSettings = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _payloadSettings = payloadSettings ?? new();
        var options = outboxSettings ?? new OutboxSettings();
        options.Validate();
        _retryHorizon = options.RetryHorizon;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private readonly ConcurrentDictionary<string, WorkflowInstance> _instances = new();
    private readonly Dictionary<string, InstanceSummary> _summaries = new(StringComparer.Ordinal);
    private readonly object _claimLock = new();
    private readonly Dictionary<string, WorkflowDemand[]> _demandSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _demandSetMembers = new(StringComparer.Ordinal);
    // Ordered runnable times exclude dormant members and include future parks as store time advances.
    // Every update is made under _claimLock alongside the instance transition.
    private readonly Dictionary<string, SortedSet<TimedMember>> _runnableSetMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _runnableTimes = new(StringComparer.Ordinal);
    private sealed record TimedMember(DateTime AtUtc, string Id, bool UpperBound = false);
    private static readonly IComparer<TimedMember> TimedOrder = Comparer<TimedMember>.Create((a, b) =>
    {
        var time = a.AtUtc.CompareTo(b.AtUtc);
        if (time != 0) return time;
        if (a.UpperBound || b.UpperBound) return a.UpperBound.CompareTo(b.UpperBound);
        return StringComparer.Ordinal.Compare(a.Id, b.Id);
    });
    private readonly Dictionary<string, Dictionary<string, SortedSet<TimedMember>>> _pinnedSetMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string DemandSetId, WorkflowDemand? Pin)> _placement = new(StringComparer.Ordinal);

    private void SavePlacement(string instanceId, DemandSet set)
    {
        if (!_demandSets.ContainsKey(set.Id))
        {
            _demandSets.Add(set.Id, set.Rows);
            _demandSetMembers.Add(set.Id, new(StringComparer.Ordinal));
            _runnableSetMembers.Add(set.Id, new(TimedOrder));
        }
        _placement.Add(instanceId, (set.Id, set.Pin));
        _demandSetMembers[set.Id].Add(instanceId);
        if (set.Pin != null)
        {
            if (!_pinnedSetMembers.TryGetValue(set.Id, out var pins))
                _pinnedSetMembers.Add(set.Id, pins = new(StringComparer.Ordinal));
            if (!pins.TryGetValue(set.Pin.Value!, out var bucket))
                pins.Add(set.Pin.Value!, bucket = new(TimedOrder));
            bucket.Add(new(set.Pin.ExpiresAtUtc ?? DateTime.MaxValue, instanceId));
        }
    }

    private bool HasDueMembers(string setId, DateTime now) =>
        _runnableSetMembers[setId].Min is { } first && first.AtUtc <= now;

    private IEnumerable<KeyValuePair<string, WorkflowDemand[]>> DueDemandSets(DateTime now) =>
        _demandSets.Where(set => HasDueMembers(set.Key, now));

    private IEnumerable<string> DueMembers(string setId, DateTime now) =>
        _runnableSetMembers[setId].GetViewBetween(new(DateTime.MinValue, ""), new(now, "", true))
            .Select(member => member.Id);

    private void SetRunnableAt(WorkflowInstance instance, DateTime? at)
    {
        var members = _runnableSetMembers[_placement[instance.InstanceId].DemandSetId];
        if (_runnableTimes.Remove(instance.InstanceId, out var previous))
            members.Remove(new(previous, instance.InstanceId));
        instance.RunnableAtUtc = at;
        if (at is { } time)
        {
            _runnableTimes.Add(instance.InstanceId, time);
            members.Add(new(time, instance.InstanceId));
        }
    }

    private IEnumerable<WorkflowDemand> SavedDemands(string id)
    {
        if (!_placement.TryGetValue(id, out var placement)) return [];
        return (placement.Pin is { } pin ? _demandSets[placement.DemandSetId].Append(pin)
            : _demandSets[placement.DemandSetId]).OrderBy(d => d.Key, StringComparer.Ordinal);
    }

    private static void StripDemands(WorkflowInstance instance)
    {
        instance.ExecutionStateJson = DemandSet.StripState(instance.ExecutionStateJson);
    }

    private readonly Dictionary<(string InstanceId, string CommitId), (long ExpectedRevision, byte[] ContentHash, long Revision, RunnableNotice? Notice, IReadOnlyList<RunnableNotice> MadeRunnable)> _commits = new();
    private long _executionEpoch;
    // A lease record keeps the instance's crash count until deletion. Epochs come from the store-wide
    // counter and survive lease-row deletion. A released lease expires at DateTime.MinValue.
    private readonly Dictionary<string, (string Token, DateTime ExpiresAtUtc, long Epoch, int CrashCount, Guid HostId)> _leases =
        new();
    private readonly Dictionary<string, CancellationReason> _cancellations = new();
    private readonly Dictionary<(string InstanceId, string EventId), (StoredWorkflowEvent Event, byte[] Hash, RunnableNotice? Notice)> _events = new();
    private readonly Dictionary<string, long> _inboxSequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _inboxReadThrough = new(StringComparer.Ordinal);
    // Each instance's stored parts by key, beside its trunk in _instances, at its revision (A-08).
    private readonly Dictionary<string, Dictionary<string, StoredJson>> _parts = new(StringComparer.Ordinal);
    // Each instance's waits by wait ID, so appends and reads touch only that instance's waits.
    private readonly Dictionary<string, Dictionary<string, StoredWait>> _waits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Json, long Revision, long? CommitEndSequence, WorkflowStatus? CommitStatus)>> _traces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledgedCancellations = new();

    public Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        lock (_claimLock)
        {
            var now = Now;
            var wasClaimable = Notice(instanceId, now)?.Claimable == true;
            var accepted = (expectedIncarnation == null ||
                _instances.GetValueOrDefault(instanceId)?.Incarnation == expectedIncarnation) &&
                RequestCancellation(instanceId, reason, now);
            var notice = accepted && !wasClaimable ? Notice(instanceId, now) : null;
            return Task.FromResult(new CancellationRequestResult(accepted, notice?.Claimable == true ? notice : null));
        }
    }

    // Caller holds _claimLock; placement and lease state belong to the same write.
    private RunnableNotice? Notice(string instanceId, DateTime now)
    {
        if (!_instances.TryGetValue(instanceId, out var instance)) return null;
        var placement = _placement[instanceId];
        var claimable = instance.RunnableAtUtc <= now &&
            (!_leases.TryGetValue(instanceId, out var lease) || lease.ExpiresAtUtc <= now);
        return new(instanceId, placement.DemandSetId, placement.Pin, instance.LastHostId,
            claimable, instance.Status != WorkflowStatus.Running &&
                (!_leases.TryGetValue(instanceId, out var owner) || owner.ExpiresAtUtc <= now));
    }

    // Caller holds _claimLock.
    private bool RequestCancellation(string instanceId, CancellationReason reason, DateTime now)
    {
        if (!_instances.TryGetValue(instanceId, out var instance) ||
            (reason.Mode == WorkflowControlMode.Terminate ? WorkflowStoreRules.IsTerminal(instance.Status)
                : instance.Status is not (WorkflowStatus.Ready or WorkflowStatus.Running or WorkflowStatus.Suspended or
                    WorkflowStatus.NeedsResolution or WorkflowStatus.Paused)))
            return false;
        if (!_cancellations.ContainsKey(instanceId) ||
            reason.Mode == WorkflowControlMode.Terminate && _cancellations[instanceId].Mode != WorkflowControlMode.Terminate)
        {
            var command = JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))!;
            command.CommandVersion = command.Mode == WorkflowControlMode.Terminate
                ? (_cancellations.GetValueOrDefault(instanceId)?.CommandVersion ?? 0) + 1 : 0;
            _cancellations[instanceId] = command;
            _acknowledgedCancellations.Remove(instanceId);
        }
        // A request still to be taken up makes the instance runnable now, in the same step (C-01);
        // repeating one already unwound changes nothing.
        if (!_acknowledgedCancellations.Contains(instanceId))
            SetRunnableAt(instance, now);
        return true;
    }

    public virtual Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        lock (_claimLock)
        {
            return Task.FromResult(_cancellations.TryGetValue(instanceId, out var reason)
                ? JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))
                : null);
        }
    }

    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_claimLock)
        {
            var now = Now;
            var matchingSets = _demandSets.Where(set => DemandMatching.Meets(set.Value, query, now) && HasDueMembers(set.Key, now));
            var runnable = matchingSets.SelectMany(set => DueMembers(set.Key, now)).Select(id => _instances[id])
                .Where(instance => instance.RunnableAtUtc <= now &&
                    (_placement[instance.InstanceId].Pin is not { } pin || DemandMatching.Meets([pin], query, now)) &&
                    !(_leases.TryGetValue(instance.InstanceId, out var lease) && lease.ExpiresAtUtc > now))
                .OrderByDescending(instance => instance.LastHostId == query.HostId)
                .ThenBy(instance => instance.RunnableAtUtc)
                .ThenBy(instance => instance.InstanceId, StringComparer.Ordinal)
                .Skip((int)Math.Min(int.MaxValue, query.ScanOffset))
                .Take(query.Limit)
                .Select(instance => new RunnableInstance(instance.InstanceId, instance.RunnableAtUtc!.Value))
                .ToArray();
            return Task.FromResult<IReadOnlyList<RunnableInstance>>(runnable);
        }
    }

    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ValidateLeaseArguments(instanceId, token, duration);
        lock (_claimLock)
        {
            var now = Now;
            if (kind == ExecutionClaimKind.Termination &&
                _cancellations.GetValueOrDefault(instanceId)?.Mode != WorkflowControlMode.Terminate)
                return Task.FromResult<ExecutionLeaseGrant?>(null);
            if (_instances.TryGetValue(instanceId, out var candidate) &&
                (WorkflowStoreRules.IsTerminal(candidate.Status) ||
                 kind != ExecutionClaimKind.Termination &&
                 (!(candidate.RunnableAtUtc <= now || kind == ExecutionClaimKind.Operator &&
                    (candidate.Status is WorkflowStatus.NeedsResolution or WorkflowStatus.Paused ||
                        ExecutionCheckpoint.IsStructured(candidate.ExecutionStateJson) && ExecutionCheckpoint.StopOf(candidate) != null)) ||
                 !DemandMatching.Meets(SavedDemands(instanceId), query, now))))
                return Task.FromResult<ExecutionLeaseGrant?>(null);
            if (ActiveTombstone(instanceId, now) != null)
                return Task.FromResult<ExecutionLeaseGrant?>(null);
            var exists = _leases.TryGetValue(instanceId, out var lease);
            if (exists && lease.ExpiresAtUtc > now)
                return Task.FromResult<ExecutionLeaseGrant?>(null);
            var crashed = exists && lease.ExpiresAtUtc != DateTime.MinValue &&
                _instances.TryGetValue(instanceId, out var instance) && instance.Status == WorkflowStatus.Running;
            var grant = new ExecutionLeaseGrant(++_executionEpoch,
                (exists ? lease.CrashCount : 0) + (crashed ? 1 : 0));
            _leases[instanceId] = (token, now.Add(duration), grant.Epoch, grant.CrashCount, query.HostId);
            if (candidate != null) candidate.LastHostId = query.HostId;
            return Task.FromResult<ExecutionLeaseGrant?>(grant);
        }
    }

    public virtual Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(
        IReadOnlyList<ExecutionLeaseRenewal> leases)
    {
        ArgumentNullException.ThrowIfNull(leases);
        foreach (var request in leases)
            ValidateLeaseArguments(request.InstanceId, request.Token, request.Duration);
        if (leases.Select(request => request.InstanceId).Distinct(StringComparer.Ordinal).Count() != leases.Count)
            throw new ArgumentException("Renewal instance IDs must be distinct", nameof(leases));
        lock (_claimLock)
        {
            var now = Now;
            var results = new ExecutionLeaseRenewalResult[leases.Count];
            for (var index = 0; index < leases.Count; index++)
            {
                var request = leases[index];
                if (!_leases.TryGetValue(request.InstanceId, out var lease) ||
                    lease.Token != request.Token || lease.ExpiresAtUtc <= now)
                    results[index] = new(ExecutionLeaseRenewalStatus.Refused);
                else if (_cancellations.GetValueOrDefault(request.InstanceId) is { Mode: WorkflowControlMode.Terminate } reason)
                    results[index] = new(ExecutionLeaseRenewalStatus.TerminationRequested, JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason)));
                else
                {
                    _leases[request.InstanceId] = lease with { ExpiresAtUtc = now.Add(request.Duration) };
                    results[index] = new(ExecutionLeaseRenewalStatus.Renewed);
                }
            }
            return Task.FromResult<IReadOnlyList<ExecutionLeaseRenewalResult>>(results);
        }
    }

    public Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_claimLock)
        {
            var now = Now;
            if (!_leases.TryGetValue(instanceId, out var lease) || lease.Token != token ||
                lease.ExpiresAtUtc <= now)
                return Task.FromResult(new ExecutionLeaseReleaseResult(false));
            _leases[instanceId] = lease with { ExpiresAtUtc = DateTime.MinValue };
            var notice = Notice(instanceId, now);
            return Task.FromResult(new ExecutionLeaseReleaseResult(true, notice?.Claimable == true ? notice : null));
        }
    }

    public virtual Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        return Commit(commit, null);
    }

    private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit, WorkflowAdmission? admission)
    {
        ArgumentNullException.ThrowIfNull(commit);
        Guid? parentIncarnation;
        lock (_claimLock) parentIncarnation = commit.ParentNotice is { } completion &&
            _instances.TryGetValue(completion.ParentInstanceId, out var parent) ? parent.Incarnation : null;
        var noticePayload = commit.ParentNotice is { } preparedNotice
            ? InboxPayloads.Prepare(ToEvent(preparedNotice), _payloadSettings) : null;
        if (commit.PreservesPayloadPins) return await CommitCore(commit, admission, parentIncarnation);
        // No payload transaction is needed until this checkpoint owns references. A previously
        // referenced checkpoint still takes the transaction when its final references are retired.
        bool hadPayloads;
        lock (_claimLock) hadPayloads = _payloadCheckpoints.Contains(commit.InstanceId);
        if (!hadPayloads && commit.PayloadReferences.Count == 0 && commit.PayloadBodies.Count == 0 &&
            commit.ChildStarts.All(child => child.PayloadReferences.Count == 0) &&
            noticePayload is not { References.Count: > 0 } && noticePayload is not { Bodies.Count: > 0 })
            return await CommitCore(commit, admission, parentIncarnation);
        Task<WorkflowCommitResult>? replay;
        lock (_claimLock)
            replay = _commits.ContainsKey((commit.InstanceId, commit.CommitId)) ? CommitCore(commit, admission, parentIncarnation) : null;
        if (replay != null) return await replay;
        WorkflowCommitResult? result = null;
        try
        {
            await PayloadStore.InTransactionAsync(async (pins, ct) =>
            {
                await pins.LockAsync(commit.PayloadOwner, commit.PayloadReferences
                    .Concat(commit.PayloadBodies.Select(b => b.Reference)).Concat(commit.PayloadBodies.SelectMany(b => b.Dependencies))
                    .Concat(commit.ChildStarts.SelectMany(child => child.PayloadReferences))
                    .Concat(noticePayload?.References ?? []).Distinct().ToArray(), ct);
                foreach (var body in commit.PayloadBodies)
                    await pins.PutAsync(body.Body, body.Dependencies, ct);
                await pins.ReplaceAsync(commit.PayloadOwner, commit.PayloadReferences, ct);
                foreach (var child in commit.ChildStarts)
                    await pins.ReplaceAsync(new("checkpoint", $"{child.ChildInstanceId}/{child.Incarnation:N}"), child.PayloadReferences, ct);
                if (commit.ParentNotice is { } notice && parentIncarnation is { } parentId)
                {
                    await noticePayload!.PublishAsync(pins, InboxPayloads.Owner(notice.ParentInstanceId, parentId), ct);
                }
                ct.ThrowIfCancellationRequested();
                result = await CommitCore(commit, admission, parentIncarnation);
                // A receipt replay must never replace a newer checkpoint's pins.
                if (result.Status != WorkflowCommitStatus.Applied) throw new PayloadCommitNotAppliedException();
            });
        }
        catch (PayloadCommitNotAppliedException) { }
        return result!;
    }

    private sealed class PayloadCommitNotAppliedException : Exception;

    private async Task<WorkflowCommitResult> CommitCore(CheckpointCommit commit, WorkflowAdmission? admission, Guid? parentIncarnation)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var catalogStart = commit.ExpectedRevision == 0 || commit.ChildStarts.Count > 0;
        if (catalogStart) await _catalogLock.WaitAsync();
        try { return await CommitUnderClaimLock(commit, admission, parentIncarnation); }
        finally { if (catalogStart) _catalogLock.Release(); }
    }

    private Task<WorkflowCommitResult> CommitUnderClaimLock(CheckpointCommit commit, WorkflowAdmission? admission, Guid? parentIncarnation)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var snapshot = commit.ReadTrunk();
        WorkflowCommitResult Result(WorkflowCommitStatus status, long revision,
            CancellationReason? cancellation = null, bool parked = false, bool unread = false,
            IReadOnlyList<RunnableNotice>? runnable = null) => new(status, revision, cancellation, parked, unread, runnable,
                _instances.TryGetValue(commit.InstanceId, out var current) ? current.RunnableAtUtc : null, Now);
        lock (_claimLock)
        {
            WorkflowAdmissionRecord? admitted = null;
            if (admission != null)
            {
                admission.ValidateCommit(commit);
                var identity = (admission.Sender, admission.Trigger, admission.Key);
                if (_admissions.TryGetValue(identity, out var winner) && winner.ExpiresAtUtc > Now)
                    return Task.FromResult(Result(WorkflowCommitStatus.AlreadyApplied, 0) with { Admission = winner, Notice = winner.Notice });
                if (_instances.ContainsKey(commit.InstanceId) ||
                    _leases.TryGetValue(commit.InstanceId, out var owner) && owner.ExpiresAtUtc > Now)
                    return Task.FromResult(Result(WorkflowCommitStatus.Conflict, 0));
                admitted = new(admission.Sender, admission.Trigger, admission.Key, admission.Fingerprint,
                    commit.InstanceId, snapshot.WorkflowName, snapshot.WorkflowVersion, Now + admission.KeyRetention, commit.Incarnation);
            }
            RefuseRemoved(commit.InstanceId, Now);
            var key = (commit.InstanceId, commit.CommitId);
            var revision = _instances.TryGetValue(commit.InstanceId, out var stored) ? stored.Revision : 0;
            var owns = admission != null || OwnsInstance(commit);
            if (_commits.TryGetValue(key, out var receipt))
            {
                if (receipt.ExpectedRevision != commit.ExpectedRevision ||
                    !receipt.ContentHash.SequenceEqual(commit.ContentHash))
                    throw new InvalidOperationException("A commit ID cannot be reused for a different transition");
                // A terminal or parking commit released the lease; only a running owner must still hold it.
                if (snapshot.Status == WorkflowStatus.Running && !owns && !commit.Parks)
                    return Task.FromResult(Result(WorkflowCommitStatus.StaleEpoch, revision));
                // The same commit gets the same answer, including arrivals it has not read.
                return Task.FromResult(Result(WorkflowCommitStatus.AlreadyApplied, receipt.Revision,
                    PendingCancellation(commit.InstanceId), commit.Parks && !owns, Unread(commit), receipt.MadeRunnable) with
                    { Notice = receipt.Notice });
            }

            // Receipts are deleted with the instance, so any replay above belongs to its current creation.
            if (commit.ParentNotice is { } noticeIdentity &&
                (_instances.TryGetValue(noticeIdentity.ParentInstanceId, out var noticeParent) ? noticeParent.Incarnation : (Guid?)null) != parentIncarnation)
                return Task.FromResult(Result(WorkflowCommitStatus.Retry, revision));
            if (commit.ExpectedRevision > 0 && stored != null &&
                stored.Incarnation != commit.Incarnation)
                return Task.FromResult(Result(WorkflowCommitStatus.StaleEpoch, revision));
            if (!owns)
                return Task.FromResult(Result(WorkflowCommitStatus.StaleEpoch, revision));
            if (_cancellations.TryGetValue(commit.InstanceId, out var command) &&
                command.Mode == WorkflowControlMode.Terminate &&
                (snapshot.Status != WorkflowStatus.Terminated || snapshot.CancellationReason?.CommandVersion != command.CommandVersion))
                return Task.FromResult(Result(WorkflowCommitStatus.TerminationRequested, revision, PendingCancellation(commit.InstanceId)));
            // An event this commit settles must still be pending.
            if (commit.Settlements.Any(settlement =>
                    !_events.TryGetValue((commit.InstanceId, settlement.EventId), out var pending) ||
                    pending.Event.Outcome != EventOutcome.Pending))
                return Task.FromResult(Result(WorkflowCommitStatus.EventsChanged, revision));
            if (revision != commit.ExpectedRevision ||
                ((snapshot.Status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or
                    WorkflowStatus.TimedOut) &&
                 _cancellations.ContainsKey(commit.InstanceId) &&
                 snapshot.CancellationReason == null))
                return Task.FromResult(Result(WorkflowCommitStatus.Conflict, revision));
            if (stored == null) CheckCatalogStart(snapshot);
            foreach (var child in commit.ChildStarts) CheckCatalogStart(JsonSerializer.Deserialize<WorkflowInstance>(child.Payload)!);
            foreach (var child in commit.ChildStarts)
                RefuseRemoved(child.ChildInstanceId, Now);
            if (commit.Diagnostics.Any(item => _diagnostics.TryGetValue(commit.InstanceId, out var rows) &&
                                              rows.ContainsKey(item.DiagnosticId)))
                throw new InvalidOperationException("Diagnostic records are immutable and cannot be rewritten");
            // A child's ID is new; a commit that names an existing instance changes nothing.
            if (commit.ChildStarts.FirstOrDefault(child => _instances.ContainsKey(child.ChildInstanceId)) is { } taken)
                throw new InvalidOperationException(
                    $"Child workflow '{taken.ChildInstanceId}' cannot start: an instance with its ID exists");
            // One store time for the whole commit: its deadlines, its runnable time and its records (D-35).
            var now = Now;
            var debugCommands = WorkflowDebugRules.Acknowledge(stored, snapshot, CommandsOf(commit.InstanceId), snapshot.Revision, commit.TraceEntries);
            var pauseGeneration = WorkflowDebugRules.Checkpoint(snapshot).PauseGeneration;
            var debugWake = WorkflowDebugRules.NeedsWake(snapshot.Status, debugCommands);
            // Prepare all creation projections before mutating any in-memory table.
            if (stored == null)
                snapshot.ExecutionStateJson = JsonSerializer.Deserialize<WorkflowInstance>(WorkflowDemands.StampStartPayload(
                    JsonSerializer.Serialize(snapshot), now))!.ExecutionStateJson;
            var createdDemands = stored == null ? DemandSet.Create(WorkflowDemands.Saved(snapshot)) : null;
            var createdChildren = commit.ChildStarts.Select(start =>
            {
                var child = JsonSerializer.Deserialize<WorkflowInstance>(WorkflowDemands.StampStartPayload(start.Payload, now))!;
                child.Incarnation = start.Incarnation;
                child.Revision = 1;
                child.RunnableAtUtc = now;
                return (Start: start, Instance: child, Demands: DemandSet.Create(WorkflowDemands.Saved(child)));
            }).ToArray();
            var unacknowledged = _cancellations.ContainsKey(commit.InstanceId) &&
                !_acknowledgedCancellations.Contains(commit.InstanceId);
            // A run that has not read every arrival neither parks nor goes idle out of reach.
            var unread = Unread(commit);
            var parked = commit.Parks && !unacknowledged && !unread && !debugWake;
            foreach (var settlement in commit.Settlements)
            {
                var settled = _events[(commit.InstanceId, settlement.EventId)];
                _events[(commit.InstanceId, settlement.EventId)] = (settled.Event with
                {
                    Outcome = settlement.Outcome, WaitId = settlement.WaitId, Rejection = settlement.Rejection
                }, settled.Hash, settled.Notice);
            }
            if (commit.InboxReadThrough is { } read)
                _inboxReadThrough[commit.InstanceId] = read;
            // A wait opens at the inbox's current sequence and gets its deadline from this commit's time;
            // opening or arming it again keeps its first record.
            var waits = WaitsOf(commit.InstanceId);
            foreach (var opened in commit.OpenedWaits)
                waits.TryAdd(opened.WaitId, new StoredWait(commit.InstanceId, opened.WaitId,
                    opened.Kind, opened.Key, opened.EventType, now + opened.Timeout,
                    _inboxSequence.GetValueOrDefault(commit.InstanceId), now,
                    DefaultPayloadJson: opened.DefaultPayloadJson));
            foreach (var armed in commit.ArmedWaits)
                if (waits.TryGetValue(armed.WaitId, out var wait) && wait.IsOpen)
                    waits[armed.WaitId] = wait with
                    {
                        DeadlineUtc = wait.DeadlineUtc ?? now + armed.Timeout,
                        DefaultPayloadJson = wait.DefaultPayloadJson ?? armed.DefaultPayloadJson
                    };
            foreach (var closed in commit.ClosedWaits)
                CloseWait(commit.InstanceId, closed.WaitId, closed.Reason, now);
            snapshot.LastHostId = admission == null && _leases.TryGetValue(commit.InstanceId, out var committingLease)
                ? committingLease.HostId : null;
            snapshot.RunnableAtUtc = WorkflowStoreRules.CommittedRunnableAt(snapshot.Status, commit,
                debugWake || (commit.Parks && !parked) || (unread && !commit.Stopped &&
                    snapshot.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution) ||
                WorkflowStoreRules.UnwindsNow(snapshot.Status, snapshot.CancellationReason != null, unacknowledged),
                now, OpenWaits(commit.InstanceId).Min(open => open.DeadlineUtc));
            // A finished instance takes no more events; those still pending are closed, and so are its waits.
            if (WorkflowStoreRules.IsTerminal(snapshot.Status))
            {
                foreach (var open in _events.Where(item => item.Key.InstanceId == commit.InstanceId &&
                                                           item.Value.Event.Outcome == EventOutcome.Pending)
                             .Select(item => item.Key).ToArray())
                    _events[open] = (_events[open].Event with { Outcome = EventOutcome.Closed }, _events[open].Hash, _events[open].Notice);
                var reason = WorkflowStoreRules.FinalWaitCloseReason(snapshot.Status);
                foreach (var open in OpenWaits(commit.InstanceId).Select(wait => wait.WaitId).ToArray())
                    CloseWait(commit.InstanceId, open, reason, now);
            }
            // An idle owner that has not read every arrival keeps the lease and reads them.
            var keepsLease = unread && snapshot.Status == WorkflowStatus.Suspended;
            // A commit changes only the parts it names, in place.
            if (commit.Diagnostics.Count > 0)
            {
                if (!_diagnostics.TryGetValue(commit.InstanceId, out var rows))
                    _diagnostics[commit.InstanceId] = rows = new(StringComparer.Ordinal);
                foreach (var diagnostic in commit.Diagnostics)
                    rows.Add(diagnostic.DiagnosticId, diagnostic);
            }
            if (commit.RetainedDiagnosticIds is { } retained &&
                _diagnostics.TryGetValue(commit.InstanceId, out var diagnostics))
            {
                var keep = retained.ToHashSet(StringComparer.Ordinal);
                foreach (var id in diagnostics.Keys.Where(id => !keep.Contains(id)).ToArray())
                    diagnostics.Remove(id);
                if (diagnostics.Count == 0)
                    _diagnostics.Remove(commit.InstanceId);
            }
            if (commit.ReplacesParts || !_parts.TryGetValue(commit.InstanceId, out var parts))
                _parts[commit.InstanceId] = parts = new Dictionary<string, StoredJson>(StringComparer.Ordinal);
            foreach (var deleted in commit.DeletedParts) parts.Remove(deleted);
            foreach (var part in commit.Parts) parts[part.Key] = part.Json;
            snapshot.Incarnation = stored?.Incarnation ?? commit.Incarnation;
            if (createdDemands != null)
                SavePlacement(commit.InstanceId, createdDemands);
            SetRunnableAt(snapshot, snapshot.RunnableAtUtc);
            StripDemands(snapshot);
            _instances[commit.InstanceId] = snapshot;
            if (!commit.PreservesPayloadPins)
            {
                if (commit.PayloadReferences.Count > 0) _payloadCheckpoints.Add(commit.InstanceId);
                else if (_payloadCheckpoints.Count > 0) _payloadCheckpoints.Remove(commit.InstanceId);
            }
            if (pauseGeneration != 0) _pauseGenerations[commit.InstanceId] = pauseGeneration;
            else if (_pauseGenerations.Count > 0) _pauseGenerations.Remove(commit.InstanceId);
            if (debugCommands.Count > 0) _debugCommands[commit.InstanceId] = debugCommands.ToList();
            _summaries[commit.InstanceId] = InstanceSummary.Of(snapshot) with
            { EndedAtUtc = WorkflowStoreRules.IsTerminal(snapshot.Status) ? _endedAt.GetValueOrDefault(commit.InstanceId, now) : null };
            RecordCompletion(snapshot, now);
            if (commit.TraceEntries.Count > 0)
            {
                if (!_traces.TryGetValue(commit.InstanceId, out var trace))
                    _traces[commit.InstanceId] = trace = [];
                var end = checked((long)trace.Count + commit.TraceEntries.Count);
                trace.AddRange(commit.TraceEntries.Select(json => (json, snapshot.Revision, (long?)end, (WorkflowStatus?)snapshot.Status)));
            }
            if (snapshot.CancellationReason != null && _cancellations.ContainsKey(commit.InstanceId))
                _acknowledgedCancellations.Add(commit.InstanceId);
            if (_leases.TryGetValue(commit.InstanceId, out var lease))
                _leases[commit.InstanceId] = lease with
                {
                    ExpiresAtUtc = (snapshot.Status == WorkflowStatus.Running && !parked) || keepsLease
                        ? lease.ExpiresAtUtc : DateTime.MinValue,
                    CrashCount = commit.ClearsCrashCount ? 0 : lease.CrashCount
                };
            var madeRunnable = new List<string>();
            // Each child starts as its own instance, runnable now, with no owner yet (D-17).
            foreach (var created in createdChildren)
            {
                var start = created.Start;
                var child = created.Instance;
                SavePlacement(child.InstanceId, created.Demands);
                SetRunnableAt(child, child.RunnableAtUtc);
                StripDemands(child);
                _instances[child.InstanceId] = child;
                var childGeneration = WorkflowDebugRules.Checkpoint(child).PauseGeneration;
                if (childGeneration != 0) _pauseGenerations[child.InstanceId] = childGeneration;
                if (start.PayloadReferences.Count > 0) _payloadCheckpoints.Add(child.InstanceId);
                _summaries[child.InstanceId] = InstanceSummary.Of(child);
                _parts.Remove(child.InstanceId);
                madeRunnable.Add(child.InstanceId);
            }
            foreach (var cancellation in commit.ChildCancellations)
            {
                var wasClaimable = Notice(cancellation.ChildInstanceId, now)?.Claimable == true;
                if (cancellation.Reason.Mode == WorkflowControlMode.Terminate)
                {
                    TerminateDescendant(cancellation.ChildInstanceId, cancellation.Reason, now, madeRunnable, new HashSet<string>(StringComparer.Ordinal));
                    continue;
                }
                if (RequestCancellation(cancellation.ChildInstanceId, cancellation.Reason, now) &&
                    !_acknowledgedCancellations.Contains(cancellation.ChildInstanceId) && !wasClaimable)
                    madeRunnable.Add(cancellation.ChildInstanceId);
                // A child that is gone will never answer; its parent's wait ends as its termination.
                else if (cancellation.Reason.Mode != WorkflowControlMode.Terminate && !_instances.ContainsKey(cancellation.ChildInstanceId))
                    Append(ToEvent(WorkflowStoreRules.ChildGone(cancellation.ChildInstanceId, commit.InstanceId,
                        cancellation.ParentWaitId!)), now);
            }
            // The child's completion goes to its parent's inbox; whatever the parent's state, the child's
            // commit stands.
            if (commit.ParentNotice is { } notice &&
                Append(ToEvent(notice), now) is { Status: EventAppendStatus.Stored, Notice.Claimable: true })
                madeRunnable.Add(notice.ParentInstanceId);
            var changed = Notice(commit.InstanceId, now);
            var ownNotice = changed is { Claimable: true } or { Idle: true } ? changed : null;
            if (admitted != null)
            {
                admitted = admitted with { Notice = ownNotice };
                _admissions[(admission!.Sender, admission.Trigger, admission.Key)] = admitted;
                _ownership[commit.InstanceId] = (commit.Incarnation, new(commit.InstanceId, admission.Sender, admission.Trigger, admission.Owner, admission.Tenant, commit.Incarnation, admission.OwnerKind));
            }
            var notices = madeRunnable.Distinct(StringComparer.Ordinal).Select(id => Notice(id, now)!)
                .Where(notice => notice.Claimable).ToArray();
            _commits.Add(key, (commit.ExpectedRevision, commit.ContentHash, snapshot.Revision, ownNotice, notices));
            return Task.FromResult(Result(WorkflowCommitStatus.Applied, snapshot.Revision,
                PendingCancellation(commit.InstanceId), parked, unread, notices) with
                { Admission = admitted, Notice = ownNotice });
        }
    }

    private static WorkflowEvent ToEvent(ParentNotice notice) =>
        new(notice.ParentInstanceId, notice.EventId, notice.EventType, notice.PayloadJson,
            TargetWaitId: notice.ParentWaitId) { PayloadDependencies = notice.PayloadDependencies, PayloadThresholdBytes = 0 };

    // Caller holds _claimLock.
    private bool Unread(CheckpointCommit commit) =>
        commit.InboxReadThrough is { } readThrough &&
        _inboxSequence.GetValueOrDefault(commit.InstanceId) > readThrough;

    public async Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        workflowEvent.Validate();
        var payload = InboxPayloads.Prepare(workflowEvent, _payloadSettings);
        if (payload.References.Count == 0 && payload.Bodies.Count == 0)
        {
            lock (_claimLock) return AppendReceipt(workflowEvent);
        }
        while (true)
        {
            Guid? incarnation;
            lock (_claimLock) incarnation = _instances.GetValueOrDefault(workflowEvent.InstanceId)?.Incarnation;
            EventAppendResult receipt = null!;
            try
            {
                await PayloadStore.InTransactionAsync(async (pins, ct) =>
                {
                    if (incarnation is { } seen)
                    {
                        var owner = InboxPayloads.Owner(workflowEvent.InstanceId, seen);
                        await pins.LockAsync(owner, payload.References.Concat(payload.Bodies.SelectMany(b => b.Dependencies)).ToArray(), ct);
                        await payload.PublishAsync(pins, owner, ct);
                    }
                    lock (_claimLock)
                    {
                        if (_instances.GetValueOrDefault(workflowEvent.InstanceId)?.Incarnation != incarnation)
                            throw new InboxIdentityChangedException();
                        receipt = AppendReceipt(workflowEvent);
                        if (receipt.Status is not (EventAppendStatus.Stored or EventAppendStatus.Rejected))
                            throw new InboxNotAppendedException();
                    }
                });
                return receipt;
            }
            catch (InboxIdentityChangedException) { }
            catch (InboxNotAppendedException) { return receipt; }
        }
    }
    private sealed class InboxIdentityChangedException : Exception;
    private sealed class InboxNotAppendedException : Exception;

    // Caller holds _claimLock. Inline appends need only this, with no payload transaction.
    private EventAppendResult AppendReceipt(WorkflowEvent workflowEvent)
    {
        var now = Now;
        var hash = workflowEvent.ContentHash();
        if (workflowEvent.DeliveryIdentity is { } identity && _webhookDeliveries.TryGetValue(identity, out var saved) && saved.Expires > now)
            return saved.Hash.SequenceEqual(hash)
                ? saved.Receipt with { Status = EventAppendStatus.Duplicate, MadeRunnable = false }
                : new EventAppendResult(EventAppendStatus.Conflict);
        var receipt = Append(workflowEvent, now);
        if (workflowEvent.DeliveryIdentity is { } key && receipt.Status is EventAppendStatus.Stored or EventAppendStatus.Duplicate or EventAppendStatus.Rejected)
            _webhookDeliveries[key] = (hash, receipt, now + (workflowEvent.DeliveryRetention ?? TimeSpan.FromHours(24)));
        return receipt;
    }

    // Caller holds _claimLock.
    private EventAppendResult Append(WorkflowEvent workflowEvent, DateTime now)
    {
        var hash = workflowEvent.ContentHash();
        // A known event ID answers the same way whatever happened to the instance since.
        if (_events.TryGetValue((workflowEvent.InstanceId, workflowEvent.EventId), out var existing))
            return existing.Hash.SequenceEqual(hash)
                ? new EventAppendResult(EventAppendStatus.Duplicate, existing.Event.Sequence) { Notice = existing.Notice }
                : new EventAppendResult(EventAppendStatus.Conflict);
        if (!_instances.TryGetValue(workflowEvent.InstanceId, out var instance))
            return new EventAppendResult(ActiveTombstone(workflowEvent.InstanceId, now) != null
                ? EventAppendStatus.Closed : EventAppendStatus.NotFound);
        var finished = WorkflowStoreRules.IsTerminal(instance.Status);
        // A finished instance takes no events; one sent to a wait is told why that wait closed.
        if (finished && workflowEvent.TargetWaitId == null)
        {
            TraceRefusal(instance, workflowEvent, EventRejection.InstanceFinished, now);
            return new EventAppendResult(EventAppendStatus.Closed);
        }
        var rejection = WorkflowStoreRules.RejectOnArrival(workflowEvent,
            workflowEvent.TargetWaitId == null ? null
                : WaitsOf(workflowEvent.InstanceId).GetValueOrDefault(workflowEvent.TargetWaitId),
            WorkflowStoreRules.Candidates(workflowEvent, OpenWaits(workflowEvent.InstanceId), now), now)
            ?? (finished ? EventRejection.InstanceFinished : null);
        // A refused event is recorded so a resend is recognized, but takes no place in the inbox.
        if (rejection != null)
        {
            _events[(workflowEvent.InstanceId, workflowEvent.EventId)] = (Stored(workflowEvent, 0, now) with
            {
                Outcome = EventOutcome.Rejected, Rejection = rejection
            }, hash, null);
            TraceRefusal(instance, workflowEvent, rejection.Value, now);
            return new EventAppendResult(EventAppendStatus.Rejected, Rejection: rejection);
        }
        var wasClaimable = Notice(workflowEvent.InstanceId, now)?.Claimable == true;
        var sequence = _inboxSequence.GetValueOrDefault(workflowEvent.InstanceId) + 1;
        _inboxSequence[workflowEvent.InstanceId] = sequence;
        // Resolution can hold one branch while another consumes this event. The host still refuses an operator stop.
        var madeRunnable = (instance.Status is WorkflowStatus.Running or WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution) &&
            (!ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson) || ExecutionCheckpoint.StopOf(instance) == null);
        if (madeRunnable)
            SetRunnableAt(instance, now);
        var notice = madeRunnable && !wasClaimable ? Notice(workflowEvent.InstanceId, now) : null;
        notice = notice?.Claimable == true ? notice : null;
        _events[(workflowEvent.InstanceId, workflowEvent.EventId)] = (Stored(workflowEvent, sequence, now), hash, notice);
        return new EventAppendResult(EventAppendStatus.Stored, sequence, madeRunnable)
            { Notice = notice };
    }

    // Caller holds _claimLock. A refusal's single entry is atomic with its inbox receipt (G-01).
    private void TraceRefusal(WorkflowInstance instance, WorkflowEvent workflowEvent, EventRejection rejection, DateTime now)
    {
        if (!instance.TracingEnabled)
            return;
        if (!_traces.TryGetValue(workflowEvent.InstanceId, out var trace))
            _traces[workflowEvent.InstanceId] = trace = [];
        trace.Add((WorkflowStoreRules.SerializeTrace(WorkflowStoreRules.RefusedEventTrace(workflowEvent, rejection, now)),
            instance.Revision, null, null));
    }

    private StoredWorkflowEvent Stored(WorkflowEvent workflowEvent, long sequence, DateTime now) =>
        new(workflowEvent.InstanceId, workflowEvent.EventId, sequence, workflowEvent.EventType, workflowEvent.Key,
            workflowEvent.TargetWaitId, InboxPayloads.Prepare(workflowEvent, _payloadSettings).Value, now, EventOutcome.Pending,
            Subject: workflowEvent.Subject);

    // Caller holds _claimLock.
    private IEnumerable<StoredWait> OpenWaits(string instanceId) =>
        _waits.TryGetValue(instanceId, out var waits) ? waits.Values.Where(wait => wait.IsOpen) : [];

    // Caller holds _claimLock.
    private Dictionary<string, StoredWait> WaitsOf(string instanceId)
    {
        if (!_waits.TryGetValue(instanceId, out var waits))
            _waits[instanceId] = waits = new Dictionary<string, StoredWait>(StringComparer.Ordinal);
        return waits;
    }

    // Caller holds _claimLock.
    private void CloseWait(string instanceId, string waitId, WaitCloseReason reason, DateTime now)
    {
        var waits = WaitsOf(instanceId);
        if (waits.TryGetValue(waitId, out var wait) && wait.IsOpen)
            waits[waitId] = wait with { CloseReason = reason, ClosedAtUtc = now };
    }

    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        lock (_claimLock)
        {
            var through = _inboxSequence.GetValueOrDefault(instanceId);
            var events = _events.Values
                .Select(item => item.Event)
                .Where(item => item.InstanceId == instanceId && item.Outcome == EventOutcome.Pending &&
                               item.Sequence > afterSequence && item.Sequence <= through)
                .OrderBy(item => item.Sequence)
                .Take(limit)
                .ToArray();
            var open = OpenWaits(instanceId).ToArray();
            return Task.FromResult(InboxPage.Of(events, limit, afterSequence, through,
                open.ToDictionary(wait => wait.WaitId, wait => wait.OpenedSequence, StringComparer.Ordinal),
                open.Where(wait => wait.DeadlineUtc != null)
                    .ToDictionary(wait => wait.WaitId, wait => wait.DeadlineUtc!.Value, StringComparer.Ordinal),
                Now));
        }
    }

    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId)
    {
        lock (_claimLock)
            return Task.FromResult(_events.TryGetValue((instanceId, eventId), out var stored)
                ? stored.Event : null);
    }

    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        lock (_claimLock)
            return Task.FromResult<IReadOnlyDictionary<string, long>>(instanceIds
                .Where(_instances.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(id => id, id => _inboxSequence.GetValueOrDefault(id), StringComparer.Ordinal));
    }

    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId)
    {
        lock (_claimLock)
            return Task.FromResult(_waits.TryGetValue(instanceId, out var waits)
                ? waits.GetValueOrDefault(waitId) : null);
    }

    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.Limit);
        if (query.InstanceIds?.Count > 1000) throw new ArgumentException("At most 1000 instance IDs", nameof(query));
        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<StoredWait>>((query.InstanceId != null
                    ? WaitsOf(query.InstanceId).Values
                    : query.InstanceIds != null
                        ? query.InstanceIds.Distinct(StringComparer.Ordinal).SelectMany(id => WaitsOf(id).Values)
                        : _waits.Values.SelectMany(waits => waits.Values))
                .Where(wait => (query.IncludeClosed || wait.IsOpen) &&
                               (query.InstanceId == null || wait.InstanceId == query.InstanceId) &&
                               (query.InstanceIds == null || query.InstanceIds.Contains(wait.InstanceId)) &&
                               (query.Key == null || wait.Key == query.Key) &&
                               (query.EventType == null || wait.EventType == query.EventType) &&
                               (query.Kind == null
                                   ? wait.Kind is not (WaitKind.Delay or WaitKind.Execution)
                                   : wait.Kind == query.Kind) &&
                               (query.AfterWaitRef == null ||
                                string.CompareOrdinal(WaitReference.Of(wait.InstanceId, wait.WaitId), query.AfterWaitRef) > 0))
                .OrderBy(wait => WaitReference.Of(wait.InstanceId, wait.WaitId), StringComparer.Ordinal)
                .Take(query.Limit)
                .ToArray());
    }

    // Caller holds _claimLock.
    private CancellationReason? PendingCancellation(string instanceId) =>
        _cancellations.TryGetValue(instanceId, out var reason)
            ? JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))
            : null;

    // Caller holds _claimLock. Every new transition needs its granted, active execution lease.
    private bool OwnsInstance(CheckpointCommit commit) =>
        _leases.TryGetValue(commit.InstanceId, out var lease) && lease.ExpiresAtUtc > Now &&
        lease.Epoch == commit.Epoch;

    public Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId)
    {
        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<WorkflowDemand>>(SavedDemands(instanceId).ToArray());
    }

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        lock (_claimLock)
            return Task.FromResult(_instances.TryGetValue(instanceId, out var instance)
                ? SnapshotStructured(instance) : null);
    }

    public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId)
    {
        lock (_claimLock)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
                return Task.FromResult<WorkflowInstance?>(null);
            var summary = CheckpointParts.Summarize(JsonSerializer.Serialize(instance),
                _parts.GetValueOrDefault(instanceId)?.GetValueOrDefault(CheckpointParts.DataKey), instance.Revision);
            summary.Incarnation = instance.Incarnation;
            summary.LastHostId = instance.LastHostId;
            return Task.FromResult<WorkflowInstance?>(DemandSet.Hydrate(summary, SavedDemands(instanceId)));
        }
    }

    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<StoredTraceEntry>>(_traces.TryGetValue(instanceId, out var trace)
                ? trace.Skip((int)Math.Min(afterSequence, trace.Count)).Take(limit)
                    .Select((stored, index) => new StoredTraceEntry(afterSequence + index + 1,
                        JsonSerializer.Deserialize<ExecutionTraceEntry>(stored.Json)!)
                        { Revision = stored.Revision, CommitEndSequence = stored.CommitEndSequence, CommitStatus = stored.CommitStatus }).ToArray()
                : []);
    }

    public async Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null)
    {
        var period = tombstoneRetention ?? RetentionSettings.Default.TombstoneRetention;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        while (!await DeleteWithPayloadsAsync(instanceId, period)) { }
    }

    private async Task<bool> DeleteWithPayloadsAsync(string instanceId, TimeSpan period,
        DateTime? cutoff = null, CancellationToken cancellationToken = default)
    {
        Guid incarnation;
        long revision;
        int eventCount;
        PayloadReference[] inboxReferences;
        lock (_claimLock)
        {
            if (!_instances.TryGetValue(instanceId, out var seen))
            { _leases.Remove(instanceId); return cutoff == null; }
            incarnation = seen.Incarnation;
            revision = seen.Revision;
            var events = _events.Where(pair => pair.Key.InstanceId == instanceId).Select(pair => pair.Value.Event).ToArray();
            eventCount = events.Length;
            inboxReferences = events.SelectMany(e => e.PayloadJson.Reference is { } r
                ? e.PayloadJson.Dependencies.Append(r) : e.PayloadJson.Dependencies).Distinct().ToArray();
        }
        try
        {
            await PayloadStore.InTransactionAsync(async (pins, ct) =>
            {
                var owner = new PayloadOwner("checkpoint", $"{instanceId}/{incarnation:N}");
                await pins.LockAsync(owner, inboxReferences, ct);
                var inboxOwner = InboxPayloads.Owner(instanceId, incarnation);
                await pins.ReleaseAsync(owner, ct);
                await pins.ReleaseAsync(inboxOwner, ct);
                lock (_claimLock)
                {
                    if (!_instances.TryGetValue(instanceId, out var current) || current.Incarnation != incarnation ||
                        current.Revision != revision || _events.Keys.Count(key => key.InstanceId == instanceId) != eventCount ||
                        cutoff is { } end && (!WorkflowStoreRules.IsTerminal(current.Status) ||
                            !_endedAt.TryGetValue(instanceId, out var ended) || ended > end ||
                            _deliveries.Values.Any(d => d.Event.InstanceId == instanceId && d.Status == OutboxDeliveryStatus.Pending) ||
                            _instances.Values.Any(child => child.ParentInstanceId == instanceId)))
                        throw new PayloadDeleteNotAppliedException();
                    ct.ThrowIfCancellationRequested();
                    var now = Now;
                    DeleteInstance(instanceId);
                    _tombstones[instanceId] = new WorkflowTombstone(instanceId, now, now + period);
                }
            }, cancellationToken);
            return true;
        }
        catch (PayloadDeleteNotAppliedException) { return false; }
    }

    private sealed class PayloadDeleteNotAppliedException : Exception;

    // Caller holds _claimLock.
    private void DeleteInstance(string instanceId)
    {
        // A child its parent still waits for ends that wait as terminated.
        if (_instances.TryGetValue(instanceId, out var deleted) && !WorkflowStoreRules.IsTerminal(deleted.Status) &&
            deleted is { ParentInstanceId: { } parent, ParentWaitId: { } wait })
            Append(ToEvent(WorkflowStoreRules.ChildGone(instanceId, parent, wait)), Now);
        if (_instances.TryRemove(instanceId, out var removed))
            SetRunnableAt(removed, null);
        if (_placement.Remove(instanceId, out var placement))
        {
            _demandSetMembers[placement.DemandSetId].Remove(instanceId);
            if (placement.Pin is { } pin && _pinnedSetMembers.TryGetValue(placement.DemandSetId, out var pins) &&
                pins.TryGetValue(pin.Value!, out var bucket))
            {
                bucket.Remove(new(pin.ExpiresAtUtc ?? DateTime.MaxValue, instanceId));
                if (bucket.Count == 0) pins.Remove(pin.Value!);
            }
        }
        _summaries.Remove(instanceId);
        _leases.Remove(instanceId);
        _endedAt.Remove(instanceId);
        _cancellations.Remove(instanceId);
        _debugCommands.Remove(instanceId);
        _pauseGenerations.Remove(instanceId);
        _payloadCheckpoints.Remove(instanceId);
        _acknowledgedCancellations.Remove(instanceId);
        DeleteOutbox(instanceId);
        foreach (var key in _events.Keys.Where(key => key.InstanceId == instanceId).ToArray())
            _events.Remove(key);
        _inboxSequence.Remove(instanceId);
        _inboxReadThrough.Remove(instanceId);
        _waits.Remove(instanceId);
        _traces.Remove(instanceId);
        _diagnostics.Remove(instanceId);
        _parts.Remove(instanceId);
        foreach (var key in _commits.Keys.Where(key => key.InstanceId == instanceId).ToArray())
            _commits.Remove(key);
    }

    // The whole instance, its trunk with its parts, as a copy. Caller holds _claimLock.
    private WorkflowInstance SnapshotStructured(WorkflowInstance instance)
    {
        var snapshot = CheckpointParts.Assemble(JsonSerializer.Serialize(instance),
            (_parts.GetValueOrDefault(instance.InstanceId) ?? new Dictionary<string, StoredJson>())
            .Select(part => new CheckpointPart(part.Key, part.Value)), instance.Revision);
        snapshot.Incarnation = instance.Incarnation;
        snapshot.LastHostId = instance.LastHostId;
        return DemandSet.Hydrate(snapshot, SavedDemands(instance.InstanceId));
    }

    private static void ValidateLeaseArguments(string instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
    }
}
