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
        status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution;

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
        if (commit.Stopped)
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
public interface IWorkflowStateRepository : IWorkflowCatalogStore, IArtifactStore, IWorkflowInbox, IWorkflowOutbox, IWorkflowAdmissionStore
{
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
    /// Reads cancellation requests for the distinct instances. Instances without a request are omitted.
    /// Successful reads survive failures of other reads; if every read fails, throws AggregateException.
    /// </summary>
    Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds);

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

    /// <summary>Extends a lease held by the same execution token. Pending termination refuses renewal so recovery can claim at expiry.</summary>
    Task<bool> RenewExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration);

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

    public InMemoryWorkflowStateRepository(TimeProvider time, OutboxSettings? outboxSettings = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
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
    private sealed record PinMember(DateTime Expiry, string Id, bool UpperBound = false);
    private static readonly IComparer<PinMember> PinOrder = Comparer<PinMember>.Create((a, b) =>
    {
        var time = a.Expiry.CompareTo(b.Expiry);
        if (time != 0) return time;
        if (a.UpperBound || b.UpperBound) return a.UpperBound.CompareTo(b.UpperBound);
        return StringComparer.Ordinal.Compare(a.Id, b.Id);
    });
    private readonly Dictionary<string, Dictionary<string, SortedSet<PinMember>>> _pinnedSetMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string DemandSetId, WorkflowDemand? Pin)> _placement = new(StringComparer.Ordinal);

    private void SavePlacement(string instanceId, DemandSet set)
    {
        if (!_demandSets.ContainsKey(set.Id))
        {
            _demandSets.Add(set.Id, set.Rows);
            _demandSetMembers.Add(set.Id, new(StringComparer.Ordinal));
        }
        _placement.Add(instanceId, (set.Id, set.Pin));
        _demandSetMembers[set.Id].Add(instanceId);
        if (set.Pin != null)
        {
            if (!_pinnedSetMembers.TryGetValue(set.Id, out var pins))
                _pinnedSetMembers.Add(set.Id, pins = new(StringComparer.Ordinal));
            if (!pins.TryGetValue(set.Pin.Value!, out var bucket))
                pins.Add(set.Pin.Value!, bucket = new(PinOrder));
            bucket.Add(new(set.Pin.ExpiresAtUtc ?? DateTime.MaxValue, instanceId));
        }
    }

    private IEnumerable<KeyValuePair<string, WorkflowDemand[]>> DueDemandSets(DateTime now) =>
        _demandSets.Where(set => _demandSetMembers[set.Key].Any(id => _instances[id].RunnableAtUtc <= now));

    private IEnumerable<WorkflowDemand> SavedDemands(string id)
    {
        if (!_placement.TryGetValue(id, out var placement)) return [];
        return (placement.Pin is { } pin ? _demandSets[placement.DemandSetId].Append(pin)
            : _demandSets[placement.DemandSetId]).OrderBy(d => d.Key, StringComparer.Ordinal);
    }

    private static void StripDemands(WorkflowInstance instance)
    {
        instance.ExecutionStateJson = JsonSerializer.Deserialize<WorkflowInstance>(
            DemandSet.StripPayload(JsonSerializer.Serialize(instance)))!.ExecutionStateJson;
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
    private readonly Dictionary<string, Dictionary<string, string>> _parts = new(StringComparer.Ordinal);
    // Each instance's waits by wait ID, so appends and reads touch only that instance's waits.
    private readonly Dictionary<string, Dictionary<string, StoredWait>> _waits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _traces = new(StringComparer.Ordinal);
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
                : instance.Status is not (WorkflowStatus.Running or WorkflowStatus.Suspended or
                    WorkflowStatus.NeedsResolution)))
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
            instance.RunnableAtUtc = now;
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

    public async Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        var requests = new Dictionary<string, CancellationReason>(StringComparer.Ordinal);
        var failures = new List<Exception>();
        foreach (var instanceId in instanceIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (await GetCancellationRequestAsync(instanceId) is { } reason)
                    requests[instanceId] = reason;
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
        if (instanceIds.Count > 0 && failures.Count == instanceIds.Distinct(StringComparer.Ordinal).Count())
            throw new AggregateException("No cancellation request could be read", failures);
        return requests;
    }

    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_claimLock)
        {
            var now = Now;
            var matchingSets = DueDemandSets(now).Where(set => DemandMatching.Meets(set.Value, query, now));
            var runnable = matchingSets.SelectMany(set => _demandSetMembers[set.Key]).Select(id => _instances[id])
                .Where(instance => instance.RunnableAtUtc <= now &&
                    (_placement[instance.InstanceId].Pin is not { } pin || DemandMatching.Meets([pin], query, now)) &&
                    !(_leases.TryGetValue(instance.InstanceId, out var lease) && lease.ExpiresAtUtc > now))
                .OrderByDescending(instance => instance.LastHostId == query.HostId)
                .ThenBy(instance => instance.RunnableAtUtc)
                .ThenBy(instance => instance.InstanceId, StringComparer.Ordinal)
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
                    (candidate.Status == WorkflowStatus.NeedsResolution ||
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

    public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        ValidateLeaseArguments(instanceId, token, duration);
        lock (_claimLock)
        {
            var now = Now;
            if (!_leases.TryGetValue(instanceId, out var lease) ||
                lease.Token != token || lease.ExpiresAtUtc <= now ||
                _cancellations.GetValueOrDefault(instanceId)?.Mode == WorkflowControlMode.Terminate)
                return Task.FromResult(false);
            _leases[instanceId] = lease with { ExpiresAtUtc = now.Add(duration) };
            return Task.FromResult(true);
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

    public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        return Commit(commit, null);
    }

    private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit, WorkflowAdmission? admission)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var catalogStart = commit.ExpectedRevision == 0 || commit.ChildStarts.Count > 0;
        if (catalogStart) await _catalogLock.WaitAsync();
        try { return await CommitUnderClaimLock(commit, admission); }
        finally { if (catalogStart) _catalogLock.Release(); }
    }

    private Task<WorkflowCommitResult> CommitUnderClaimLock(CheckpointCommit commit, WorkflowAdmission? admission)
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
            // A child's ID is new; a commit that names an existing instance changes nothing.
            if (commit.ChildStarts.FirstOrDefault(child => _instances.ContainsKey(child.ChildInstanceId)) is { } taken)
                throw new InvalidOperationException(
                    $"Child workflow '{taken.ChildInstanceId}' cannot start: an instance with its ID exists");
            // One store time for the whole commit: its deadlines, its runnable time and its records (D-35).
            var now = Now;
            // Prepare all creation projections before mutating any in-memory table.
            if (stored == null)
                snapshot.ExecutionStateJson = JsonSerializer.Deserialize<WorkflowInstance>(WorkflowDemands.StampStartPayload(
                    JsonSerializer.Serialize(snapshot), now))!.ExecutionStateJson;
            var createdDemands = stored == null ? DemandSet.Create(WorkflowDemands.Saved(snapshot)) : null;
            var createdChildren = commit.ChildStarts.Select(start =>
            {
                var child = JsonSerializer.Deserialize<WorkflowInstance>(WorkflowDemands.StampStartPayload(start.Payload, now))!;
                child.Incarnation = Guid.NewGuid();
                child.Revision = 1;
                child.RunnableAtUtc = now;
                return (Instance: child, Demands: DemandSet.Create(WorkflowDemands.Saved(child)));
            }).ToArray();
            var unacknowledged = _cancellations.ContainsKey(commit.InstanceId) &&
                !_acknowledgedCancellations.Contains(commit.InstanceId);
            // A run that has not read every arrival neither parks nor goes idle out of reach.
            var unread = Unread(commit);
            var parked = commit.Parks && !unacknowledged && !unread;
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
                (commit.Parks && !parked) || (unread && !commit.Stopped &&
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
            if (commit.ReplacesParts || !_parts.TryGetValue(commit.InstanceId, out var parts))
                _parts[commit.InstanceId] = parts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var deleted in commit.DeletedParts)
                parts.Remove(deleted);
            foreach (var part in commit.Parts)
                parts[part.Key] = part.Json;
            snapshot.Incarnation = stored?.Incarnation ?? commit.Incarnation;
            if (createdDemands != null)
                SavePlacement(commit.InstanceId, createdDemands);
            StripDemands(snapshot);
            _instances[commit.InstanceId] = snapshot;
            _summaries[commit.InstanceId] = InstanceSummary.Of(snapshot) with
            { EndedAtUtc = WorkflowStoreRules.IsTerminal(snapshot.Status) ? _endedAt.GetValueOrDefault(commit.InstanceId, now) : null };
            RecordCompletion(snapshot, now);
            if (commit.TraceEntries.Count > 0)
            {
                if (!_traces.TryGetValue(commit.InstanceId, out var trace))
                    _traces[commit.InstanceId] = trace = [];
                trace.AddRange(commit.TraceEntries);
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
                var child = created.Instance;
                SavePlacement(child.InstanceId, created.Demands);
                StripDemands(child);
                _instances[child.InstanceId] = child;
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
            TargetWaitId: notice.ParentWaitId);

    // Caller holds _claimLock.
    private bool Unread(CheckpointCommit commit) =>
        commit.InboxReadThrough is { } readThrough &&
        _inboxSequence.GetValueOrDefault(commit.InstanceId) > readThrough;

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        workflowEvent.Validate();
        lock (_claimLock)
        {
            var now = Now;
            var hash = workflowEvent.ContentHash();
            if (workflowEvent.DeliveryIdentity is { } identity && _webhookDeliveries.TryGetValue(identity, out var saved) && saved.Expires > now)
                return Task.FromResult(saved.Hash.SequenceEqual(hash)
                    ? saved.Receipt with { Status = EventAppendStatus.Duplicate, MadeRunnable = false }
                    : new EventAppendResult(EventAppendStatus.Conflict));
            var receipt = Append(workflowEvent, now);
            if (workflowEvent.DeliveryIdentity is { } key && receipt.Status is EventAppendStatus.Stored or EventAppendStatus.Duplicate or EventAppendStatus.Rejected)
                _webhookDeliveries[key] = (hash, receipt, now + (workflowEvent.DeliveryRetention ?? TimeSpan.FromHours(24)));
            return Task.FromResult(receipt);
        }
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
            instance.RunnableAtUtc = now;
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
        trace.Add(WorkflowStoreRules.SerializeTrace(WorkflowStoreRules.RefusedEventTrace(workflowEvent, rejection, now)));
    }

    private static StoredWorkflowEvent Stored(WorkflowEvent workflowEvent, long sequence, DateTime now) =>
        new(workflowEvent.InstanceId, workflowEvent.EventId, sequence, workflowEvent.EventType, workflowEvent.Key,
            workflowEvent.TargetWaitId, workflowEvent.PayloadJson, now, EventOutcome.Pending,
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
                    ? OpenWaits(query.InstanceId)
                    : query.InstanceIds != null
                        ? query.InstanceIds.Distinct(StringComparer.Ordinal).SelectMany(OpenWaits)
                        : _waits.Values.SelectMany(waits => waits.Values))
                .Where(wait => wait.IsOpen &&
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
                    .Select((json, index) => new StoredTraceEntry(afterSequence + index + 1,
                        JsonSerializer.Deserialize<ExecutionTraceEntry>(json)!)).ToArray()
                : []);
    }

    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null)
    {
        var period = tombstoneRetention ?? RetentionSettings.Default.TombstoneRetention;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        lock (_claimLock)
        {
            if (_instances.ContainsKey(instanceId))
            {
                var now = Now;
                DeleteInstance(instanceId);
                _tombstones[instanceId] = new WorkflowTombstone(instanceId, now, now + period);
            }
            else
                _leases.Remove(instanceId);
        }
        return Task.CompletedTask;
    }

    // Caller holds _claimLock.
    private void DeleteInstance(string instanceId)
    {
        // A child its parent still waits for ends that wait as terminated.
        if (_instances.TryGetValue(instanceId, out var deleted) && !WorkflowStoreRules.IsTerminal(deleted.Status) &&
            deleted is { ParentInstanceId: { } parent, ParentWaitId: { } wait })
            Append(ToEvent(WorkflowStoreRules.ChildGone(instanceId, parent, wait)), Now);
        _instances.TryRemove(instanceId, out _);
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
        _acknowledgedCancellations.Remove(instanceId);
        DeleteOutbox(instanceId);
        foreach (var key in _events.Keys.Where(key => key.InstanceId == instanceId).ToArray())
            _events.Remove(key);
        _inboxSequence.Remove(instanceId);
        _inboxReadThrough.Remove(instanceId);
        _waits.Remove(instanceId);
        _traces.Remove(instanceId);
        _parts.Remove(instanceId);
        foreach (var key in _commits.Keys.Where(key => key.InstanceId == instanceId).ToArray())
            _commits.Remove(key);
    }

    // The whole instance, its trunk with its parts, as a copy. Caller holds _claimLock.
    private WorkflowInstance SnapshotStructured(WorkflowInstance instance)
    {
        var snapshot = CheckpointParts.Assemble(JsonSerializer.Serialize(instance),
            (_parts.GetValueOrDefault(instance.InstanceId) ?? new Dictionary<string, string>())
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
