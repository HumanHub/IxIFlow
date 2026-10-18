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

    /// <summary>
    /// True when a saved instance is runnable from the moment it is saved, whatever time it carries:
    /// an idle save that leaves a cancellation request unacknowledged, so the request is
    /// unwound, and a running instance saved without a time, so it can be claimed as soon as its
    /// owner's lease ends. Stores stamp these with their own clock.
    /// </summary>
    public static bool IsRunnableNow(WorkflowInstance instance, bool unacknowledgedCancellation) =>
        UnwindsNow(instance, unacknowledgedCancellation) ||
        (instance.RunnableAtUtc == null && instance.Status == WorkflowStatus.Running);

    /// <summary>True when an idle save leaves a cancellation request unacknowledged, so the instance must unwind now.</summary>
    public static bool UnwindsNow(WorkflowInstance instance, bool unacknowledgedCancellation) =>
        UnwindsNow(instance.Status, instance.CancellationReason != null, unacknowledgedCancellation);

    public static bool UnwindsNow(WorkflowStatus status, bool acknowledgesCancellation,
        bool unacknowledgedCancellation) =>
        unacknowledgedCancellation && !acknowledgesCancellation &&
        status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution;

    /// <summary>
    /// When an instance saved without a commit becomes runnable, with <paramref name="now"/> as the
    /// store's clock. Such a save carries its own time; a commit never does.
    /// </summary>
    public static DateTime? RunnableAt(WorkflowInstance instance, bool unacknowledgedCancellation, DateTime now) =>
        IsRunnableNow(instance, unacknowledgedCancellation) ? now : instance.RunnableAtUtc;

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
/// Stores workflow instances. First commits carry a fresh incarnation; saves and child starts allocate
/// one in the store on insertion. Every instance read round-trips it separately from serialized state.
/// </summary>
public interface IWorkflowStateRepository : IWorkflowInbox, IWorkflowOutbox
{
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
    /// Returns false when the instance is absent or terminal.
    /// </summary>
    Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason);

    /// <summary>Reads a durable cancellation request for an instance.</summary>
    Task<CancellationReason?> GetCancellationRequestAsync(string instanceId);

    /// <summary>
    /// Reads the durable cancellation requests of several instances at once; instances without a
    /// request are left out. A host watches all its running instances with one call per interval.
    /// This fallback reads each instance separately, a few at a time, and leaves out instances whose
    /// read failed; it fails only when every read fails. Stores should override it with one query.
    /// </summary>
    async Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds)
    {
        var requests = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationReason>(
            StringComparer.Ordinal);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        await Parallel.ForEachAsync(instanceIds.Distinct(StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (instanceId, _) =>
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
            });
        if (instanceIds.Count > 0 && failures.Count == instanceIds.Distinct(StringComparer.Ordinal).Count())
            throw new AggregateException("No cancellation request could be read", failures);
        return requests;
    }

    /// <summary>
    /// Lists up to <paramref name="limit"/> instances a host may claim now: their runnable time has
    /// passed and nobody holds their lease. The earliest come first.
    /// </summary>
    Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit);

    /// <summary>
    /// Atomically saves the commit's payload at its expected revision + 1. Expected revision zero
    /// is a first commit and bypasses the incarnation check: an absent row is inserted, a saved
    /// revision-zero row is updated, and either takes the commit's incarnation under the existing
    /// lease and revision rules. An existing row above revision zero returns a revision conflict.
    /// Later commits preserve the row's incarnation. Child starts allocate fresh incarnations in the store.
    /// Repeating a commit ID with the same payload returns its original receipt, even after subsequent
    /// commits. Receipt lookup precedes incarnation and revision checks. Every commit above expected
    /// revision zero must carry the row's incarnation, including an unleased (epoch-zero) commit.
    /// A different incarnation or a leased commit without an active lease at exactly its epoch returns
    /// <see cref="WorkflowCommitStatus.StaleEpoch"/>. An unleased commit requires no active lease,
    /// with revision checks ordering the write. A store that knows nothing was
    /// written may return <see cref="WorkflowCommitStatus.Retry"/>; any exception means the
    /// outcome is unknown and the same commit may be sent again.
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
    /// checked atomically with the claim. An absent ID without a live tombstone may be claimed.
    /// </summary>
    Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration);

    /// <summary>Extends a lease held by the same execution token.</summary>
    Task<bool> RenewExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration);

    /// <summary>Releases a lease only when the caller still owns it.</summary>
    Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token);

    /// <summary>
    /// Saves revision-zero state. An insert allocates a fresh incarnation in the store; an update
    /// preserves the existing one. Sets the supplied instance's Incarnation to the row's value.
    /// </summary>
    Task SaveWorkflowInstanceAsync(WorkflowInstance instance);

    /// <summary>
    /// The whole instance: its trunk and every part of its state, read at one revision.
    /// Every instance read, including summaries, queries and child lists, must round-trip the row's
    /// Incarnation separately from serialized state.
    /// </summary>
    Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId);

    /// <summary>
    /// The instance without its branches: its fields, its workflow data and the trunk of its execution state.
    /// Enough for a status, a result or a stop check, never to run it. This fallback reads the whole instance; a
    /// store that keeps parts reads only the trunk and the data.
    /// </summary>
    Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) => GetWorkflowInstanceAsync(instanceId);

    /// <summary>
    ///     Get workflow instances by workflow name
    /// </summary>
    Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName);

    /// <summary>
    ///     Get workflow instances by status
    /// </summary>
    Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status);

    /// <summary>
    ///     Get workflow instances by correlation ID
    /// </summary>
    Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId);

    /// <summary>
    /// The instance's trace entries after <paramref name="afterSequence"/>, in order, at most
    /// <paramref name="limit"/>. Commits append them; deleting the instance deletes them.
    /// </summary>
    Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000);

    /// <summary>
    /// The child workflows a parent started, for operators. This fallback reads the instances that
    /// share the parent's correlation ID; stores should override it with one query.
    /// </summary>
    async Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId) =>
        await GetWorkflowInstanceAsync(parentInstanceId) is { } parent
            ? (await GetWorkflowInstancesByCorrelationIdAsync(parent.CorrelationId))
            .Where(instance => instance.ParentInstanceId == parentInstanceId).ToArray()
            : [];

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
    private readonly object _claimLock = new();
    private readonly Dictionary<(string InstanceId, string CommitId), (long ExpectedRevision, byte[] ContentHash, long Revision)> _commits = new();
    private long _executionEpoch;
    // A lease record keeps the instance's crash count until deletion. Epochs come from the store-wide
    // counter and survive lease-row deletion. A released lease expires at DateTime.MinValue.
    private readonly Dictionary<string, (string Token, DateTime ExpiresAtUtc, long Epoch, int CrashCount)> _leases =
        new();
    private readonly Dictionary<string, CancellationReason> _cancellations = new();
    private readonly Dictionary<(string InstanceId, string EventId), (StoredWorkflowEvent Event, byte[] Hash)> _events = new();
    private readonly Dictionary<string, long> _inboxSequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _inboxReadThrough = new(StringComparer.Ordinal);
    // Each instance's stored parts by key, beside its trunk in _instances, at its revision (A-08).
    private readonly Dictionary<string, Dictionary<string, string>> _parts = new(StringComparer.Ordinal);
    // Each instance's waits by wait ID, so appends and reads touch only that instance's waits.
    private readonly Dictionary<string, Dictionary<string, StoredWait>> _waits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _traces = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledgedCancellations = new();

    public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        lock (_claimLock)
            return Task.FromResult(RequestCancellation(instanceId, reason, Now));
    }

    // Caller holds _claimLock.
    private bool RequestCancellation(string instanceId, CancellationReason reason, DateTime now)
    {
        if (!_instances.TryGetValue(instanceId, out var instance) ||
            instance.Status is not (WorkflowStatus.Running or WorkflowStatus.Suspended or
                WorkflowStatus.NeedsResolution))
            return false;
        if (!_cancellations.ContainsKey(instanceId))
        {
            _cancellations.Add(instanceId,
                JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))!);
            _acknowledgedCancellations.Remove(instanceId);
        }
        // A request still to be taken up makes the instance runnable now, in the same step (C-01);
        // repeating one already unwound changes nothing.
        if (!_acknowledgedCancellations.Contains(instanceId))
            instance.RunnableAtUtc = now;
        return true;
    }

    public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        lock (_claimLock)
        {
            return Task.FromResult(_cancellations.TryGetValue(instanceId, out var reason)
                ? JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))
                : null);
        }
    }

    public Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        lock (_claimLock)
        {
            IReadOnlyDictionary<string, CancellationReason> requests = instanceIds
                .Where(_cancellations.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(instanceId => instanceId, instanceId =>
                    JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(_cancellations[instanceId]))!,
                    StringComparer.Ordinal);
            return Task.FromResult(requests);
        }
    }

    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        lock (_claimLock)
        {
            var now = Now;
            var runnable = _instances.Values
                .Where(instance => instance.RunnableAtUtc <= now &&
                    !(_leases.TryGetValue(instance.InstanceId, out var lease) && lease.ExpiresAtUtc > now))
                .OrderBy(instance => instance.RunnableAtUtc)
                .ThenBy(instance => instance.InstanceId, StringComparer.Ordinal)
                .Take(limit)
                .Select(instance => new RunnableInstance(instance.InstanceId, instance.RunnableAtUtc!.Value))
                .ToArray();
            return Task.FromResult<IReadOnlyList<RunnableInstance>>(runnable);
        }
    }

    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        ValidateLeaseArguments(instanceId, token, duration);
        lock (_claimLock)
        {
            var now = Now;
            if (ActiveTombstone(instanceId, now) != null)
                return Task.FromResult<ExecutionLeaseGrant?>(null);
            var exists = _leases.TryGetValue(instanceId, out var lease);
            if (exists && lease.ExpiresAtUtc > now)
                return Task.FromResult<ExecutionLeaseGrant?>(null);
            var crashed = exists && lease.ExpiresAtUtc != DateTime.MinValue &&
                _instances.TryGetValue(instanceId, out var instance) && instance.Status == WorkflowStatus.Running;
            var grant = new ExecutionLeaseGrant(++_executionEpoch,
                (exists ? lease.CrashCount : 0) + (crashed ? 1 : 0));
            _leases[instanceId] = (token, now.Add(duration), grant.Epoch, grant.CrashCount);
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
                lease.Token != token || lease.ExpiresAtUtc <= now)
                return Task.FromResult(false);
            _leases[instanceId] = lease with { ExpiresAtUtc = now.Add(duration) };
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_claimLock)
        {
            if (!_leases.TryGetValue(instanceId, out var lease) || lease.Token != token ||
                lease.ExpiresAtUtc <= Now)
                return Task.FromResult(false);
            _leases[instanceId] = lease with { ExpiresAtUtc = DateTime.MinValue };
            return Task.FromResult(true);
        }
    }

    public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var snapshot = commit.ReadTrunk();
        lock (_claimLock)
        {
            RefuseRemoved(commit.InstanceId, Now);
            var key = (commit.InstanceId, commit.CommitId);
            var revision = _instances.TryGetValue(commit.InstanceId, out var stored) ? stored.Revision : 0;
            var owns = OwnsInstance(commit);
            if (_commits.TryGetValue(key, out var receipt))
            {
                if (receipt.ExpectedRevision != commit.ExpectedRevision ||
                    !receipt.ContentHash.SequenceEqual(commit.ContentHash))
                    throw new InvalidOperationException("A commit ID cannot be reused for a different transition");
                // A terminal or parking commit released the lease; only a running owner must still hold it.
                if (snapshot.Status == WorkflowStatus.Running && !owns && !commit.Parks)
                    return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, revision));
                // The same commit gets the same answer, including arrivals it has not read.
                return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, receipt.Revision,
                    PendingCancellation(commit.InstanceId), commit.Parks && !owns, Unread(commit)));
            }

            // Receipts are deleted with the instance, so any replay above belongs to its current creation.
            if (commit.ExpectedRevision > 0 && stored != null &&
                stored.Incarnation != commit.Incarnation)
                return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, revision));
            if (!owns)
                return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, revision));
            // An event this commit settles must still be pending.
            if (commit.Settlements.Any(settlement =>
                    !_events.TryGetValue((commit.InstanceId, settlement.EventId), out var pending) ||
                    pending.Event.Outcome != EventOutcome.Pending))
                return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.EventsChanged, revision));
            if (revision != commit.ExpectedRevision ||
                ((snapshot.Status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or
                    WorkflowStatus.TimedOut) &&
                 _cancellations.ContainsKey(commit.InstanceId) &&
                 snapshot.CancellationReason == null))
                return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.Conflict, revision));
            foreach (var child in commit.ChildStarts)
                RefuseRemoved(child.ChildInstanceId, Now);
            // A child's ID is new; a commit that names an existing instance changes nothing.
            if (commit.ChildStarts.FirstOrDefault(child => _instances.ContainsKey(child.ChildInstanceId)) is { } taken)
                throw new InvalidOperationException(
                    $"Child workflow '{taken.ChildInstanceId}' cannot start: an instance with its ID exists");
            // One store time for the whole commit: its deadlines, its runnable time and its records (D-35).
            var now = Now;
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
                }, settled.Hash);
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
            snapshot.RunnableAtUtc = WorkflowStoreRules.CommittedRunnableAt(snapshot.Status, commit,
                (commit.Parks && !parked) || (unread && snapshot.Status == WorkflowStatus.Suspended) ||
                WorkflowStoreRules.UnwindsNow(snapshot, unacknowledged),
                now, OpenWaits(commit.InstanceId).Min(open => open.DeadlineUtc));
            // A finished instance takes no more events; those still pending are closed, and so are its waits.
            if (WorkflowStoreRules.IsTerminal(snapshot.Status))
            {
                foreach (var open in _events.Where(item => item.Key.InstanceId == commit.InstanceId &&
                                                           item.Value.Event.Outcome == EventOutcome.Pending)
                             .Select(item => item.Key).ToArray())
                    _events[open] = (_events[open].Event with { Outcome = EventOutcome.Closed }, _events[open].Hash);
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
            snapshot.Incarnation = commit.ExpectedRevision == 0
                ? commit.Incarnation : stored!.Incarnation;
            _instances[commit.InstanceId] = snapshot;
            RecordCompletion(snapshot, now);
            _commits.Add(key, (commit.ExpectedRevision, commit.ContentHash, snapshot.Revision));
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
            foreach (var start in commit.ChildStarts)
            {
                var child = JsonSerializer.Deserialize<WorkflowInstance>(start.Payload)!;
                child.Incarnation = Guid.NewGuid();
                child.Revision = 1;
                child.RunnableAtUtc = now;
                _instances[child.InstanceId] = child;
                _parts.Remove(child.InstanceId);
                madeRunnable.Add(child.InstanceId);
            }
            foreach (var cancellation in commit.ChildCancellations)
            {
                if (RequestCancellation(cancellation.ChildInstanceId, cancellation.Reason, now) &&
                    !_acknowledgedCancellations.Contains(cancellation.ChildInstanceId))
                    madeRunnable.Add(cancellation.ChildInstanceId);
                // A child that is gone will never answer; its parent's wait ends as its termination.
                else if (!_instances.ContainsKey(cancellation.ChildInstanceId))
                    Append(ToEvent(WorkflowStoreRules.ChildGone(cancellation.ChildInstanceId, commit.InstanceId,
                        cancellation.ParentWaitId)), now);
            }
            // The child's completion goes to its parent's inbox; whatever the parent's state, the child's
            // commit stands.
            if (commit.ParentNotice is { } notice &&
                Append(ToEvent(notice), now) is { Status: EventAppendStatus.Stored, MadeRunnable: true })
                madeRunnable.Add(notice.ParentInstanceId);
            return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.Applied, snapshot.Revision,
                PendingCancellation(commit.InstanceId), parked, unread, madeRunnable));
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
            return Task.FromResult(Append(workflowEvent, Now));
    }

    // Caller holds _claimLock.
    private EventAppendResult Append(WorkflowEvent workflowEvent, DateTime now)
    {
        var hash = workflowEvent.ContentHash();
        // A known event ID answers the same way whatever happened to the instance since.
        if (_events.TryGetValue((workflowEvent.InstanceId, workflowEvent.EventId), out var existing))
            return existing.Hash.SequenceEqual(hash)
                ? new EventAppendResult(EventAppendStatus.Duplicate, existing.Event.Sequence)
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
            }, hash);
            TraceRefusal(instance, workflowEvent, rejection.Value, now);
            return new EventAppendResult(EventAppendStatus.Rejected, Rejection: rejection);
        }
        var sequence = _inboxSequence.GetValueOrDefault(workflowEvent.InstanceId) + 1;
        _inboxSequence[workflowEvent.InstanceId] = sequence;
        _events[(workflowEvent.InstanceId, workflowEvent.EventId)] = (Stored(workflowEvent, sequence, now), hash);
        // A stopped instance waits for its operator; the event is consumed after the release.
        var madeRunnable = instance.Status is WorkflowStatus.Running or WorkflowStatus.Suspended;
        if (madeRunnable)
            instance.RunnableAtUtc = now;
        return new EventAppendResult(EventAppendStatus.Stored, sequence, madeRunnable);
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
        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<StoredWait>>((query.InstanceId == null
                    ? _waits.Values.SelectMany(waits => waits.Values)
                    : OpenWaits(query.InstanceId))
                .Where(wait => wait.IsOpen &&
                               (query.InstanceId == null || wait.InstanceId == query.InstanceId) &&
                               (query.Key == null || wait.Key == query.Key) &&
                               (query.EventType == null || wait.EventType == query.EventType) &&
                               (query.Kind == null
                                   ? wait.Kind is not (WaitKind.Delay or WaitKind.Execution)
                                   : wait.Kind == query.Kind) &&
                               (query.AfterWaitId == null ||
                                string.CompareOrdinal(wait.WaitId, query.AfterWaitId) > 0))
                .OrderBy(wait => wait.WaitId, StringComparer.Ordinal)
                .Take(query.Limit)
                .ToArray());
    }

    // Caller holds _claimLock.
    private CancellationReason? PendingCancellation(string instanceId) =>
        _cancellations.TryGetValue(instanceId, out var reason)
            ? JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))
            : null;

    // A leased commit needs the instance's current, active lease. An unleased commit (epoch zero)
    // is accepted only while nobody holds the lease; the revision check still orders it.
    private bool OwnsInstance(CheckpointCommit commit)
    {
        var hasLease = _leases.TryGetValue(commit.InstanceId, out var lease);
        var active = hasLease && lease.ExpiresAtUtc > Now;
        return commit.Epoch == 0
            ? !active
            : active && lease.Epoch == commit.Epoch;
    }

    public Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        lock (_claimLock)
        {
            RefuseRemoved(instance.InstanceId, Now);
            if (instance.Revision != 0 ||
                (_instances.TryGetValue(instance.InstanceId, out var stored) && stored.Revision != 0))
                throw new InvalidOperationException("Revisioned instances require an atomic commit");
            var saved = instance.Revision != 0 || ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson)
                ? JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(instance))!
                : instance;
            instance.Incarnation = stored?.Incarnation ?? Guid.NewGuid();
            saved.Incarnation = instance.Incarnation;
            saved.RunnableAtUtc = WorkflowStoreRules.RunnableAt(saved, false, Now);
            _instances[instance.InstanceId] = saved;
            RecordCompletion(saved, Now);
            _parts.Remove(instance.InstanceId);
        }
        return Task.CompletedTask;
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
            if (instance.Revision == 0 && !ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson))
                return Task.FromResult<WorkflowInstance?>(instance);
            var summary = CheckpointParts.Summarize(JsonSerializer.Serialize(instance),
                _parts.GetValueOrDefault(instanceId)?.GetValueOrDefault(CheckpointParts.DataKey), instance.Revision);
            summary.Incarnation = instance.Incarnation;
            return Task.FromResult<WorkflowInstance?>(summary);
        }
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName)
    {
        lock (_claimLock)
            return Task.FromResult<IEnumerable<WorkflowInstance>>(_instances.Values
                .Where(i => i.WorkflowName == workflowName).Select(SnapshotStructured).ToList());
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status)
    {
        lock (_claimLock)
            return Task.FromResult<IEnumerable<WorkflowInstance>>(_instances.Values
                .Where(i => i.Status == status).Select(SnapshotStructured).ToList());
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId)
    {
        lock (_claimLock)
            return Task.FromResult<IEnumerable<WorkflowInstance>>(_instances.Values
                .Where(i => i.CorrelationId == correlationId).Select(SnapshotStructured).ToList());
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

    public Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId)
    {
        lock (_claimLock)
            return Task.FromResult<IEnumerable<WorkflowInstance>>(_instances.Values
                .Where(instance => instance.ParentInstanceId == parentInstanceId).Select(SnapshotStructured).ToArray());
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
        if (instance.Revision == 0 && !ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson))
            return instance;
        var snapshot = CheckpointParts.Assemble(JsonSerializer.Serialize(instance),
            (_parts.GetValueOrDefault(instance.InstanceId) ?? new Dictionary<string, string>())
            .Select(part => new CheckpointPart(part.Key, part.Value)), instance.Revision);
        snapshot.Incarnation = instance.Incarnation;
        return snapshot;
    }

    private static void ValidateLeaseArguments(string instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
    }
}

/// <summary>
///     Workflow version registry interface
/// </summary>
public interface IWorkflowVersionRegistry
{
    /// <summary>
    ///     Register a workflow version
    /// </summary>
    Task RegisterWorkflowAsync(WorkflowDefinition definition);

    /// <summary>
    ///     Get workflow definition by name and version
    /// </summary>
    Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version);

    /// <summary>
    ///     Get latest workflow definition by name
    /// </summary>
    Task<WorkflowDefinition?> GetLatestWorkflowDefinitionAsync(string name);

    /// <summary>
    ///     Get all versions of a workflow
    /// </summary>
    Task<IEnumerable<WorkflowDefinition>> GetWorkflowVersionsAsync(string name);

    /// <summary>
    ///     Get all registered workflows
    /// </summary>
    Task<IEnumerable<WorkflowDefinition>> GetAllWorkflowDefinitionsAsync();

    /// <summary>
    ///     Deactivate a workflow version
    /// </summary>
    Task DeactivateWorkflowVersionAsync(string name, int version);

    /// <summary>
    ///     Set default workflow version
    /// </summary>
    Task SetDefaultWorkflowVersionAsync(string name, int version);
}

/// <summary>
///     Concrete implementation of workflow version registry
/// </summary>
public class WorkflowVersionRegistry : IWorkflowVersionRegistry
{
    private readonly ConcurrentDictionary<string, WorkflowDefinition> _workflows = new();

    public WorkflowVersionRegistry() { }

    public WorkflowVersionRegistry(IEnumerable<WorkflowDefinition> definitions)
    {
        foreach (var definition in definitions)
            RegisterWorkflow(definition);
    }

    public Task RegisterWorkflowAsync(WorkflowDefinition definition)
    {
        RegisterWorkflow(definition);
        return Task.CompletedTask;
    }

    private void RegisterWorkflow(WorkflowDefinition definition)
    {
        WorkflowDefinitionValidator.Validate(definition);
        Runtime.WorkflowDefinitionIdentity.AssignImplicitName(definition);
        var key = $"{definition.Name}:{definition.Version}";
        var published = _workflows.GetOrAdd(key, definition);
        if (!ReferenceEquals(published, definition) &&
            (published.Description != definition.Description ||
             published.WorkflowDataType != definition.WorkflowDataType ||
             !published.Tags.SequenceEqual(definition.Tags) ||
             new WorkflowScopeCatalog(published).Fingerprint !=
             new WorkflowScopeCatalog(definition).Fingerprint))
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow {definition.Name} v{definition.Version} is already registered");
        }
    }

    public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version)
    {
        var key = $"{name}:{version}";
        _workflows.TryGetValue(key, out var definition);
        return Task.FromResult(definition);
    }

    public Task<WorkflowDefinition?> GetLatestWorkflowDefinitionAsync(string name)
    {
        var versions = _workflows.Values
            .Where(w => w.Name == name)
            .OrderByDescending(w => w.Version)
            .FirstOrDefault();
        return Task.FromResult(versions);
    }

    public Task<IEnumerable<WorkflowDefinition>> GetWorkflowVersionsAsync(string name)
    {
        var versions = _workflows.Values
            .Where(w => w.Name == name)
            .OrderByDescending(w => w.Version)
            .AsEnumerable();
        return Task.FromResult(versions);
    }

    public Task<IEnumerable<WorkflowDefinition>> GetAllWorkflowDefinitionsAsync()
    {
        return Task.FromResult(_workflows.Values.AsEnumerable());
    }

    public Task DeactivateWorkflowVersionAsync(string name, int version)
    {
        var key = $"{name}:{version}";
        if (_workflows.TryGetValue(key, out var definition))
        {
            // Create a copy with IsActive = false
            var updatedDefinition = new WorkflowDefinition
            {
                Id = definition.Id,
                Name = definition.Name,
                Version = definition.Version,
                Description = definition.Description,
                WorkflowDataType = definition.WorkflowDataType,
                CreatedAt = definition.CreatedAt,
                CreatedBy = definition.CreatedBy,
                Tags = definition.Tags,
                EstimatedStepCount = definition.EstimatedStepCount,
                SupportsSuspension = definition.SupportsSuspension,
                UsesSagaPattern = definition.UsesSagaPattern,
                SupportsParallelExecution = definition.SupportsParallelExecution,
                WorkflowFactory = definition.WorkflowFactory,
                Steps = definition.Steps,
                Metadata = new Dictionary<string, object>(definition.Metadata)
                {
                    ["IsActive"] = false
                }
            };
            WorkflowDefinitionValidator.Validate(updatedDefinition);
            _workflows.TryUpdate(key, updatedDefinition, definition);
        }

        return Task.CompletedTask;
    }

    public Task SetDefaultWorkflowVersionAsync(string name, int version)
    {
        // First, unset any existing default
        var existingVersions = _workflows.Values.Where(w => w.Name == name).ToList();
        foreach (var existing in existingVersions)
            if (existing.Metadata.ContainsKey("IsDefault") && (bool)existing.Metadata["IsDefault"])
                existing.Metadata["IsDefault"] = false;

        // Set the new default
        var key = $"{name}:{version}";
        if (_workflows.TryGetValue(key, out var definition)) definition.Metadata["IsDefault"] = true;

        return Task.CompletedTask;
    }
}
