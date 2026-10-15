using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>
    /// True when a saved instance is runnable from the moment it is saved, whatever time it carries
    /// (C-01): an idle save that leaves a cancellation request unacknowledged, so the request is
    /// unwound, and a running instance saved without a time, so it can be claimed as soon as its
    /// owner's lease ends. Stores stamp these with their own clock.
    /// </summary>
    public static bool IsRunnableNow(WorkflowInstance instance, bool unacknowledgedCancellation) =>
        (unacknowledgedCancellation && instance.CancellationReason == null &&
         instance.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution) ||
        (instance.RunnableAtUtc == null && instance.Status == WorkflowStatus.Running);

    /// <summary>When a saved instance becomes runnable, with <paramref name="now"/> as the store's clock.</summary>
    public static DateTime? RunnableAt(WorkflowInstance instance, bool unacknowledgedCancellation, DateTime now) =>
        IsRunnableNow(instance, unacknowledgedCancellation) ? now : instance.RunnableAtUtc;

    /// <summary>Why the waits still open when an instance finishes are closed (D-33).</summary>
    public static WaitCloseReason FinalWaitCloseReason(WorkflowStatus status) =>
        status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut
            ? WaitCloseReason.Cancelled
            : WaitCloseReason.InstanceFinished;

    /// <summary>
    /// Why an event that arrives is refused before anyone reads it, or null when it is kept for the
    /// owner (D-33). <paramref name="target"/> is the wait the event names, if it names one; otherwise
    /// <paramref name="openWaits"/> are the instance's open waits. Any other event needs an open event
    /// wait with its key (any key when it has none), or a child wait the child may still match.
    /// </summary>
    public static EventRejection? RejectOnArrival(WorkflowEvent workflowEvent, StoredWait? target,
        IEnumerable<StoredWait> openWaits)
    {
        if (workflowEvent.TargetWaitId != null)
            return target == null ? EventRejection.UnknownWait : target.IsOpen ? null : target.Rejection;
        return openWaits.Any(wait => wait.Kind == WaitKind.Child ||
                                     (wait.Kind == WaitKind.Event && (workflowEvent.Key == null ||
                                      string.Equals(wait.Key, workflowEvent.Key, StringComparison.Ordinal))))
            ? null
            : EventRejection.NoOpenWait;
    }
}

public interface IWorkflowStateRepository
{
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
    /// passed and nobody holds their lease. The earliest come first (C-01, C-05).
    /// </summary>
    Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit);

    /// <summary>
    /// Atomically saves the commit's payload at its expected revision + 1. Revision zero also
    /// represents an absent instance. Repeating a commit ID with the same payload returns its
    /// original receipt, even after subsequent commits. A commit from an epoch older than the
    /// instance's latest lease, or from a lease that is no longer active while the instance runs,
    /// returns <see cref="WorkflowCommitStatus.StaleEpoch"/>. A store that knows nothing was
    /// written may return <see cref="WorkflowCommitStatus.Retry"/>; any exception means the
    /// outcome is unknown and the same commit may be sent again.
    /// </summary>
    Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit);

    /// <summary>
    /// Acquires an absent or expired execution lease for one instance. The grant's epoch is greater
    /// than every epoch granted for the instance before. When the previous lease expired without
    /// being released while the instance was <see cref="WorkflowStatus.Running"/>, its owner lost the
    /// instance with work in flight, and the grant's crash count is one higher, in the same atomic
    /// step. Returns null while another owner holds the lease.
    /// </summary>
    Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration);

    /// <summary>Extends a lease held by the same execution token.</summary>
    Task<bool> RenewExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration);

    /// <summary>Releases a lease only when the caller still owns it.</summary>
    Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token);

    /// <summary>
    /// Makes a suspended instance due for recovery now, for example when a child it waits for finishes.
    /// Stores that cannot do this leave the instance on its own schedule.
    /// </summary>
    Task MakeRunnableAsync(string instanceId) => Task.CompletedTask;

    /// <summary>
    ///     Save workflow instance state
    /// </summary>
    Task SaveWorkflowInstanceAsync(WorkflowInstance instance);

    /// <summary>
    ///     Get workflow instance by ID
    /// </summary>
    Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId);

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
    ///     Delete workflow instance
    /// </summary>
    Task DeleteWorkflowInstanceAsync(string instanceId);
}

/// <summary>Tracks terminal checkpoints whose completion event still needs publication.</summary>
public interface IWorkflowCompletionOutbox
{
    Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId);
    Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync();
    Task<IEnumerable<WorkflowInstance>> ClaimUnpublishedCompletionsAsync(
        string? instanceId, string token, TimeSpan duration);
    Task<bool> MarkCompletionPublishedAsync(string instanceId, long revision, string token);
    Task ReleaseCompletionClaimAsync(string instanceId, string token);
}

/// <summary>
///     In-memory implementation of workflow state repository
/// </summary>
public class InMemoryWorkflowStateRepository : IWorkflowStateRepository, IWorkflowCompletionOutbox, IWorkflowInbox
{
    private readonly TimeProvider _time;

    public InMemoryWorkflowStateRepository() : this(TimeProvider.System)
    {
    }

    public InMemoryWorkflowStateRepository(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private readonly ConcurrentDictionary<string, WorkflowInstance> _instances = new();
    private readonly object _claimLock = new();
    private readonly Dictionary<(string InstanceId, string CommitId), (long ExpectedRevision, byte[] ContentHash, long Revision)> _commits = new();
    // A lease record outlives its lease, so a later owner always gets a higher epoch. It also keeps
    // the instance's crash count. A released lease expires at DateTime.MinValue.
    private readonly Dictionary<string, (string Token, DateTime ExpiresAtUtc, long Epoch, int CrashCount)> _leases =
        new();
    private readonly Dictionary<string, CancellationReason> _cancellations = new();
    private readonly Dictionary<(string InstanceId, string EventId), (StoredWorkflowEvent Event, byte[] Hash)> _events = new();
    private readonly Dictionary<string, long> _inboxSequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _inboxReadThrough = new(StringComparer.Ordinal);
    // Each instance's waits by wait ID, so appends and reads touch only that instance's waits.
    private readonly Dictionary<string, Dictionary<string, StoredWait>> _waits = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledgedCancellations = new();
    private readonly HashSet<string> _publishedCompletions = new();
    private readonly Dictionary<string, (string Token, DateTime ExpiresAtUtc)> _completionClaims = new();

    public Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId)
    {
        lock (_claimLock)
        {
            return Task.FromResult(_instances.TryGetValue(instanceId, out var instance) &&
                IsTerminal(instance.Status) && !_publishedCompletions.Contains(instanceId)
                ? SnapshotStructured(instance) : null);
        }
    }

    public Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync()
    {
        lock (_claimLock)
        {
            var instances = _instances.Values
                .Where(instance => IsTerminal(instance.Status) &&
                    !_publishedCompletions.Contains(instance.InstanceId))
                .Take(100)
                .Select(SnapshotStructured)
                .ToArray();
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }
    }

    public Task<IEnumerable<WorkflowInstance>> ClaimUnpublishedCompletionsAsync(
        string? instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_claimLock)
        {
            var now = Now;
            var instances = _instances.Values
                .Where(instance => (instanceId == null || instance.InstanceId == instanceId) &&
                    IsTerminal(instance.Status) && !_publishedCompletions.Contains(instance.InstanceId) &&
                    (!_completionClaims.TryGetValue(instance.InstanceId, out var claim) ||
                     claim.ExpiresAtUtc <= now))
                .Take(100).ToArray();
            foreach (var instance in instances)
                _completionClaims[instance.InstanceId] = (token, now.Add(duration));
            return Task.FromResult<IEnumerable<WorkflowInstance>>(
                instances.Select(SnapshotStructured).ToArray());
        }
    }

    public Task<bool> MarkCompletionPublishedAsync(string instanceId, long revision, string token)
    {
        lock (_claimLock)
        {
            if (!_instances.TryGetValue(instanceId, out var instance) ||
                instance.Revision != revision || !IsTerminal(instance.Status) ||
                !_completionClaims.TryGetValue(instanceId, out var claim) ||
                claim.Token != token || claim.ExpiresAtUtc <= Now)
                return Task.FromResult(false);
            _completionClaims.Remove(instanceId);
            return Task.FromResult(_publishedCompletions.Add(instanceId));
        }
    }

    public Task ReleaseCompletionClaimAsync(string instanceId, string token)
    {
        lock (_claimLock)
        {
            if (_completionClaims.TryGetValue(instanceId, out var claim) && claim.Token == token)
                _completionClaims.Remove(instanceId);
        }
        return Task.CompletedTask;
    }

    private static bool IsTerminal(WorkflowStatus status) => status is
        WorkflowStatus.Completed or WorkflowStatus.Failed or
        WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut;

    public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        lock (_claimLock)
        {
            if (!_instances.TryGetValue(instanceId, out var instance) ||
                instance.Status is not (WorkflowStatus.Running or WorkflowStatus.Suspended or
                    WorkflowStatus.NeedsResolution))
                return Task.FromResult(false);
            if (!_cancellations.ContainsKey(instanceId))
            {
                _cancellations.Add(instanceId,
                    JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))!);
                _acknowledgedCancellations.Remove(instanceId);
            }
            // A request still to be taken up makes the instance runnable now, in the same step (C-01);
            // repeating one already unwound changes nothing.
            if (!_acknowledgedCancellations.Contains(instanceId))
                instance.RunnableAtUtc = Now;
            return Task.FromResult(true);
        }
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
            var exists = _leases.TryGetValue(instanceId, out var lease);
            if (exists && lease.ExpiresAtUtc > now)
                return Task.FromResult<ExecutionLeaseGrant?>(null);
            var crashed = exists && lease.ExpiresAtUtc != DateTime.MinValue &&
                _instances.TryGetValue(instanceId, out var instance) && instance.Status == WorkflowStatus.Running;
            var grant = new ExecutionLeaseGrant((exists ? lease.Epoch : 0) + 1,
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

    public Task MakeRunnableAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        lock (_claimLock)
        {
            if (_instances.TryGetValue(instanceId, out var instance) &&
                instance.Status == WorkflowStatus.Suspended)
                instance.RunnableAtUtc = Now;
        }
        return Task.CompletedTask;
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
        var snapshot = commit.ReadInstance();
        snapshot.Revision = checked(commit.ExpectedRevision + 1);
        lock (_claimLock)
        {
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
            var unacknowledged = _cancellations.ContainsKey(commit.InstanceId) &&
                !_acknowledgedCancellations.Contains(commit.InstanceId);
            // A run that has not read every arrival neither parks nor goes idle out of reach.
            var unread = Unread(commit);
            var parked = commit.Parks && !unacknowledged && !unread;
            snapshot.RunnableAtUtc = (commit.Parks && !parked) || (unread && snapshot.Status == WorkflowStatus.Suspended)
                ? Now
                : WorkflowStoreRules.RunnableAt(snapshot, unacknowledged, Now);
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
            // A wait opens at the inbox's current sequence; opening it again keeps its first record.
            var now = Now;
            foreach (var opened in commit.OpenedWaits)
                WaitsOf(commit.InstanceId).TryAdd(opened.WaitId, new StoredWait(commit.InstanceId, opened.WaitId,
                    opened.Kind, opened.Key, opened.EventType, opened.DeadlineUtc,
                    _inboxSequence.GetValueOrDefault(commit.InstanceId), now));
            foreach (var closed in commit.ClosedWaits)
                CloseWait(commit.InstanceId, closed.WaitId, closed.Reason, now);
            // A finished instance takes no more events; those still pending are closed, and so are its waits.
            if (snapshot.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or
                WorkflowStatus.Terminated or WorkflowStatus.TimedOut)
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
            _instances[commit.InstanceId] = snapshot;
            _commits.Add(key, (commit.ExpectedRevision, commit.ContentHash, snapshot.Revision));
            if (snapshot.CancellationReason != null && _cancellations.ContainsKey(commit.InstanceId))
                _acknowledgedCancellations.Add(commit.InstanceId);
            if (_leases.TryGetValue(commit.InstanceId, out var lease))
                _leases[commit.InstanceId] = lease with
                {
                    ExpiresAtUtc = (snapshot.Status == WorkflowStatus.Running && !parked) || keepsLease
                        ? lease.ExpiresAtUtc : DateTime.MinValue,
                    CrashCount = commit.ClearsCrashCount ? 0 : lease.CrashCount
                };
            return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.Applied, snapshot.Revision,
                PendingCancellation(commit.InstanceId), parked, unread));
        }
    }

    // Caller holds _claimLock.
    private bool Unread(CheckpointCommit commit) =>
        commit.InboxReadThrough is { } readThrough &&
        _inboxSequence.GetValueOrDefault(commit.InstanceId) > readThrough;

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);
        workflowEvent.Validate();
        var hash = workflowEvent.ContentHash();
        lock (_claimLock)
        {
            // A known event ID answers the same way whatever happened to the instance since.
            if (_events.TryGetValue((workflowEvent.InstanceId, workflowEvent.EventId), out var existing))
                return Task.FromResult(existing.Hash.SequenceEqual(hash)
                    ? new EventAppendResult(EventAppendStatus.Duplicate, existing.Event.Sequence)
                    : new EventAppendResult(EventAppendStatus.Conflict));
            if (!_instances.TryGetValue(workflowEvent.InstanceId, out var instance))
                return Task.FromResult(new EventAppendResult(EventAppendStatus.NotFound));
            if (instance.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or
                WorkflowStatus.Terminated or WorkflowStatus.TimedOut)
                return Task.FromResult(new EventAppendResult(EventAppendStatus.Closed));
            var now = Now;
            var rejection = WorkflowStoreRules.RejectOnArrival(workflowEvent,
                workflowEvent.TargetWaitId == null ? null
                    : WaitsOf(workflowEvent.InstanceId).GetValueOrDefault(workflowEvent.TargetWaitId),
                OpenWaits(workflowEvent.InstanceId));
            // A refused event is recorded so a resend is recognized, but takes no place in the inbox.
            if (rejection != null)
            {
                _events[(workflowEvent.InstanceId, workflowEvent.EventId)] = (Stored(workflowEvent, 0, now) with
                {
                    Outcome = EventOutcome.Rejected, Rejection = rejection
                }, hash);
                return Task.FromResult(new EventAppendResult(EventAppendStatus.Rejected, Rejection: rejection));
            }
            var sequence = _inboxSequence.GetValueOrDefault(workflowEvent.InstanceId) + 1;
            _inboxSequence[workflowEvent.InstanceId] = sequence;
            _events[(workflowEvent.InstanceId, workflowEvent.EventId)] = (Stored(workflowEvent, sequence, now), hash);
            // A stopped instance waits for its operator; the event is consumed after the release.
            var madeRunnable = instance.Status is WorkflowStatus.Running or WorkflowStatus.Suspended;
            if (madeRunnable)
                instance.RunnableAtUtc = now;
            return Task.FromResult(new EventAppendResult(EventAppendStatus.Stored, sequence, madeRunnable));
        }
    }

    private static StoredWorkflowEvent Stored(WorkflowEvent workflowEvent, long sequence, DateTime now) =>
        new(workflowEvent.InstanceId, workflowEvent.EventId, sequence, workflowEvent.EventType, workflowEvent.Key,
            workflowEvent.TargetWaitId, workflowEvent.PayloadJson, now, EventOutcome.Pending);

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
            return Task.FromResult(InboxPage.Of(events, limit, afterSequence, through,
                OpenWaits(instanceId).ToDictionary(wait => wait.WaitId, wait => wait.OpenedSequence,
                    StringComparer.Ordinal)));
        }
    }

    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId)
    {
        lock (_claimLock)
            return Task.FromResult(_events.TryGetValue((instanceId, eventId), out var stored)
                ? stored.Event : null);
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
                               (query.Kind == null || wait.Kind == query.Kind) &&
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
            if (instance.Revision != 0 ||
                (_instances.TryGetValue(instance.InstanceId, out var stored) && stored.Revision != 0))
                throw new InvalidOperationException("Revisioned instances require an atomic commit");
            var saved = SnapshotStructured(instance);
            saved.RunnableAtUtc = WorkflowStoreRules.RunnableAt(saved, false, Now);
            _instances[instance.InstanceId] = saved;
        }
        return Task.CompletedTask;
    }

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        _instances.TryGetValue(instanceId, out var instance);
        return Task.FromResult(instance == null ? null : SnapshotStructured(instance));
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName)
    {
        var instances = _instances.Values.Where(i => i.WorkflowName == workflowName).Select(SnapshotStructured).ToList();
        return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status)
    {
        var instances = _instances.Values.Where(i => i.Status == status).Select(SnapshotStructured).ToList();
        return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId)
    {
        var instances = _instances.Values.Where(i => i.CorrelationId == correlationId).Select(SnapshotStructured).ToList();
        return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
    }

    public Task DeleteWorkflowInstanceAsync(string instanceId)
    {
        lock (_claimLock)
        {
            _instances.TryRemove(instanceId, out _);
            // The lease record stays so a recreated instance never reuses an epoch; its crashes go.
            if (_leases.TryGetValue(instanceId, out var lease))
                _leases[instanceId] = lease with { ExpiresAtUtc = DateTime.MinValue, CrashCount = 0 };
            _cancellations.Remove(instanceId);
            _acknowledgedCancellations.Remove(instanceId);
            _publishedCompletions.Remove(instanceId);
            foreach (var key in _events.Keys.Where(key => key.InstanceId == instanceId).ToArray())
                _events.Remove(key);
            _inboxSequence.Remove(instanceId);
            _inboxReadThrough.Remove(instanceId);
            _waits.Remove(instanceId);
            foreach (var key in _commits.Keys.Where(key => key.InstanceId == instanceId).ToArray())
                _commits.Remove(key);
        }
        return Task.CompletedTask;
    }

    private static WorkflowInstance SnapshotStructured(WorkflowInstance instance) =>
        instance.Revision != 0 || ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson)
            ? JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(instance))!
            : instance;

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
