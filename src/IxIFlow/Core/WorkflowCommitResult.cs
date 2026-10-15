using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IxIFlow.Core;

public enum WorkflowCommitStatus
{
    /// <summary>The checkpoint was saved.</summary>
    Applied,

    /// <summary>The same commit was saved before; this is its original receipt.</summary>
    AlreadyApplied,

    /// <summary>The stored revision is not the expected one.</summary>
    Conflict,

    /// <summary>The commit's execution epoch is no longer the instance's current owner.</summary>
    StaleEpoch,

    /// <summary>The store did not apply the commit and it can be sent again unchanged.</summary>
    Retry,

    /// <summary>
    /// An inbox event the commit settles is no longer pending. Nothing was written; the owner still
    /// owns the instance and reads its inbox again.
    /// </summary>
    EventsChanged
}

/// <summary>
/// A durable commit receipt. On conflict or a stale epoch, Revision is the current stored revision.
/// An applied commit also reports the instance's pending cancellation request, if any, so the owner
/// learns of it with the commit instead of polling (B-10).
/// </summary>
public sealed record WorkflowCommitResult(
    WorkflowCommitStatus Status,
    long Revision,
    CancellationReason? PendingCancellation = null,
    bool Parked = false,
    bool UnreadEvents = false);

/// <summary>
/// An execution lease granted by the store. <see cref="Epoch"/> fences the owner's commits.
/// <see cref="CrashCount"/> counts how many owners in a row lost the instance while it was running,
/// including the one before this grant; a commit that records progress resets it (B-08).
/// </summary>
public sealed record ExecutionLeaseGrant(long Epoch, int CrashCount);

/// <summary>
/// One checkpoint write: the serialized instance, fenced by the owner's execution epoch and the
/// revision it was based on. A commit is immutable, so sending the same commit again is safe.
/// </summary>
public sealed class CheckpointCommit
{
    private CheckpointCommit(string instanceId, string payload, long epoch, string commitId,
        long expectedRevision, bool clearsCrashCount, bool parks, IReadOnlyList<EventSettlement> settlements,
        long? inboxReadThrough, IReadOnlyList<OpenedWait> openedWaits, IReadOnlyList<ArmedWait> armedWaits,
        IReadOnlyList<ClosedWait> closedWaits, TimeSpan? wakeAfter, bool stopped)
    {
        InstanceId = instanceId;
        Payload = payload;
        Epoch = epoch;
        CommitId = commitId;
        ExpectedRevision = expectedRevision;
        ClearsCrashCount = clearsCrashCount;
        Parks = parks;
        Settlements = settlements;
        InboxReadThrough = inboxReadThrough;
        OpenedWaits = openedWaits;
        ArmedWaits = armedWaits;
        ClosedWaits = closedWaits;
        WakeAfter = wakeAfter;
        Stopped = stopped;
        _contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{(clearsCrashCount ? 1 : 0)}{(parks ? 1 : 0)}{(stopped ? 1 : 0)}\n" +
            $"{inboxReadThrough?.ToString() ?? "-"}\n{wakeAfter?.Ticks.ToString() ?? "-"}\n" +
            $"{JsonSerializer.Serialize(settlements)}\n{JsonSerializer.Serialize(openedWaits)}\n" +
            $"{JsonSerializer.Serialize(armedWaits)}\n{JsonSerializer.Serialize(closedWaits)}\n{payload}"));
    }

    /// <summary>The longest timeout, delay or park a commit may carry.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(36500);

    private readonly byte[] _contentHash;

    public string InstanceId { get; }

    /// <summary>The instance as JSON, to be stored at <see cref="ExpectedRevision"/> + 1.</summary>
    public string Payload { get; }

    /// <summary>The execution epoch granted with the owner's lease; zero when the instance was never leased.</summary>
    public long Epoch { get; }

    public string CommitId { get; }

    public long ExpectedRevision { get; }

    /// <summary>
    /// True when the commit records progress, so the instance's crash count starts again from zero.
    /// A commit that only claims an activity or child before it starts, or only reschedules a check,
    /// leaves the count, so work that keeps killing its owner is still counted (B-08).
    /// </summary>
    public bool ClearsCrashCount { get; }

    /// <summary>
    /// True when the run stops for <see cref="WakeAfter"/>, for example a retry backoff, while the
    /// instance stays Running: the store releases the lease in the same step (C-03). If a cancellation
    /// request is waiting to be taken up, the store keeps the lease and makes the instance runnable now
    /// instead, and the receipt says it did not park.
    /// </summary>
    public bool Parks { get; }

    /// <summary>
    /// For a park, how long the run stops. For an idle instance, the longest it may wait before it is
    /// runnable again even with no deadline due, so it checks its children. The store adds it to its own
    /// time at this commit (D-35).
    /// </summary>
    public TimeSpan? WakeAfter { get; }

    /// <summary>True when the instance is stopped for an operator: only a release or a cancellation moves it.</summary>
    public bool Stopped { get; }

    /// <summary>
    /// Inbox events this commit settles, in the same transaction: consumed by the wait that took each,
    /// or rejected. Each must still be pending, or the commit is a conflict (D-04).
    /// </summary>
    public IReadOnlyList<EventSettlement> Settlements { get; }

    /// <summary>
    /// The highest inbox sequence the run has read, or null for a run that does not read the inbox.
    /// An idle commit that has not read every arrival leaves the instance runnable now and does not park.
    /// </summary>
    public long? InboxReadThrough { get; }

    /// <summary>
    /// Waits this commit opens. The store records each with the instance's inbox sequence at this
    /// commit, so an event addressed by key that arrived earlier never satisfies it (D-33).
    /// </summary>
    public IReadOnlyList<OpenedWait> OpenedWaits { get; }

    /// <summary>Open waits this commit gives their deadline.</summary>
    public IReadOnlyList<ArmedWait> ArmedWaits { get; }

    /// <summary>Waits this commit closes. A finished instance's open waits close without being listed.</summary>
    public IReadOnlyList<ClosedWait> ClosedWaits { get; }

    /// <summary>
    /// SHA-256 of <see cref="Payload"/> and every other field that changes what the commit does; a reused commit ID must
    /// carry the same hash. Returns a copy.
    /// </summary>
    public byte[] ContentHash => (byte[])_contentHash.Clone();

    public static CheckpointCommit Create(WorkflowInstance instance, long epoch, long expectedRevision,
        string? commitId = null, bool clearsCrashCount = true, bool parks = false,
        IReadOnlyList<EventSettlement>? settlements = null, long? inboxReadThrough = null,
        IReadOnlyList<OpenedWait>? openedWaits = null, IReadOnlyList<ClosedWait>? closedWaits = null,
        IReadOnlyList<ArmedWait>? armedWaits = null, TimeSpan? wakeAfter = null, bool stopped = false)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.InstanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        commitId ??= Guid.NewGuid().ToString("N");
        ArgumentException.ThrowIfNullOrWhiteSpace(commitId);
        if (commitId.Length > 100)
            throw new ArgumentException("Commit IDs must be at most 100 characters", nameof(commitId));
        if (inboxReadThrough < 0)
            throw new ArgumentOutOfRangeException(nameof(inboxReadThrough), "An inbox read mark cannot be negative");
        // A copy, so the settlements cannot change after the commit is hashed.
        var settled = (settlements ?? []).ToArray();
        foreach (var settlement in settled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settlement.EventId, nameof(settlements));
            if (settlement.Outcome is not (EventOutcome.Consumed or EventOutcome.Rejected))
                throw new ArgumentException("A commit settles an event only as consumed or rejected",
                    nameof(settlements));
            if (settlement.Outcome == EventOutcome.Consumed && string.IsNullOrWhiteSpace(settlement.WaitId))
                throw new ArgumentException("A consumed event names the wait that took it", nameof(settlements));
            if (settlement.Outcome == EventOutcome.Rejected && settlement.Rejection == null)
                throw new ArgumentException("A rejected event says why", nameof(settlements));
        }
        if (settled.Select(settlement => settlement.EventId).Distinct(StringComparer.Ordinal).Count() != settled.Length)
            throw new ArgumentException("A commit settles each event once", nameof(settlements));
        var opened = (openedWaits ?? []).ToArray();
        foreach (var wait in opened)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(wait.WaitId, nameof(openedWaits));
            ArgumentNullException.ThrowIfNull(wait.Key, nameof(openedWaits));
            ArgumentNullException.ThrowIfNull(wait.EventType, nameof(openedWaits));
            // An answerable wait names what answers it; a delay or an execution deadline is only its time.
            var timed = wait.Kind is WaitKind.Delay or WaitKind.Execution;
            if (!timed && string.IsNullOrWhiteSpace(wait.EventType))
                throw new ArgumentException("A wait an event can answer names its event type", nameof(openedWaits));
            if (timed && wait.Timeout == null || wait.Kind == WaitKind.Child && wait.Timeout != null)
                throw new ArgumentException("Delays and execution deadlines have a timeout; a child's wait has none",
                    nameof(openedWaits));
            if (wait.WaitId.Length > 100 || wait.Key.Length > 400 || wait.EventType.Length > 400)
                throw new ArgumentException("Wait IDs are at most 100 characters, keys and event types 400",
                    nameof(openedWaits));
            ValidateDuration(wait.Timeout, nameof(openedWaits));
        }
        var armed = (armedWaits ?? []).ToArray();
        foreach (var wait in armed)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(wait.WaitId, nameof(armedWaits));
            ValidateDuration(wait.Timeout, nameof(armedWaits));
        }
        var closed = (closedWaits ?? []).ToArray();
        foreach (var wait in closed)
            ArgumentException.ThrowIfNullOrWhiteSpace(wait.WaitId, nameof(closedWaits));
        if (opened.Select(wait => wait.WaitId).Concat(armed.Select(wait => wait.WaitId))
                .Concat(closed.Select(wait => wait.WaitId))
                .Distinct(StringComparer.Ordinal).Count() != opened.Length + armed.Length + closed.Length)
            throw new ArgumentException("A commit opens, arms or closes each wait once", nameof(openedWaits));
        ValidateDuration(wakeAfter, nameof(wakeAfter));
        if (parks && wakeAfter == null)
            throw new ArgumentException("A park says how long the run stops", nameof(wakeAfter));
        return new CheckpointCommit(instance.InstanceId, JsonSerializer.Serialize(instance), epoch, commitId,
            expectedRevision, clearsCrashCount, parks, Array.AsReadOnly(settled), inboxReadThrough,
            Array.AsReadOnly(opened), Array.AsReadOnly(armed), Array.AsReadOnly(closed), wakeAfter, stopped);
    }

    private static void ValidateDuration(TimeSpan? duration, string parameter)
    {
        if (duration < TimeSpan.Zero || duration > MaxDuration)
            throw new ArgumentOutOfRangeException(parameter, "Durations are between zero and 100 years");
    }

    /// <summary>A fresh copy of the committed instance.</summary>
    public WorkflowInstance ReadInstance() =>
        JsonSerializer.Deserialize<WorkflowInstance>(Payload)
        ?? throw new InvalidOperationException("A checkpoint payload could not be read");
}
