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
    bool UnreadEvents = false,
    IReadOnlyList<string>? MadeRunnable = null)
{
    /// <summary>
    /// Other instances the commit made runnable: the children it started, and a parent whose inbox took
    /// the child's completion. A host may run them at once; the poll finds them otherwise.
    /// </summary>
    public IReadOnlyList<string> MadeRunnable { get; init; } = MadeRunnable ?? [];

    /// <summary>Receipts are equal when every field is, the instances made runnable compared in order.</summary>
    public bool Equals(WorkflowCommitResult? other) =>
        other is not null && Status == other.Status && Revision == other.Revision &&
        Equals(PendingCancellation, other.PendingCancellation) && Parked == other.Parked &&
        UnreadEvents == other.UnreadEvents && MadeRunnable.SequenceEqual(other.MadeRunnable, StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Status, Revision, PendingCancellation, Parked, UnreadEvents, MadeRunnable.Count);
}

/// <summary>
/// An execution lease granted by the store. <see cref="Epoch"/> fences the owner's commits.
/// <see cref="CrashCount"/> counts how many owners in a row lost the instance while it was running,
/// including the one before this grant; a commit that records progress resets it (B-08).
/// </summary>
public sealed record ExecutionLeaseGrant(long Epoch, int CrashCount);

/// <summary>
/// One checkpoint write: the instance's trunk and the parts of its state that changed (A-08), fenced by the owner's
/// execution epoch and the revision it was based on. A commit is immutable, so sending the same commit again is safe.
/// </summary>
public sealed class CheckpointCommit
{
    private CheckpointCommit(CommittedInstance instance, string payload, IReadOnlyList<CheckpointPart> parts,
        IReadOnlyList<string> deletedParts, bool replacesParts, IReadOnlyDictionary<string, string> storedParts,
        long epoch, string commitId,
        long expectedRevision, bool clearsCrashCount, bool parks, IReadOnlyList<EventSettlement> settlements,
        long? inboxReadThrough, IReadOnlyList<OpenedWait> openedWaits, IReadOnlyList<ArmedWait> armedWaits,
        IReadOnlyList<ClosedWait> closedWaits, TimeSpan? wakeAfter, bool stopped,
        IReadOnlyList<ChildStart> childStarts, IReadOnlyList<ChildCancellation> childCancellations,
        ParentNotice? parentNotice, IReadOnlyList<string> traceEntries)
    {
        Instance = instance;
        InstanceId = instance.InstanceId;
        Payload = payload;
        Parts = parts;
        DeletedParts = deletedParts;
        ReplacesParts = replacesParts;
        StoredParts = storedParts;
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
        ChildStarts = childStarts;
        ChildCancellations = childCancellations;
        ParentNotice = parentNotice;
        TraceEntries = traceEntries;
        _contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{(clearsCrashCount ? 1 : 0)}{(parks ? 1 : 0)}{(stopped ? 1 : 0)}\n" +
            $"{inboxReadThrough?.ToString() ?? "-"}\n{wakeAfter?.Ticks.ToString() ?? "-"}\n" +
            $"{JsonSerializer.Serialize(settlements)}\n{JsonSerializer.Serialize(openedWaits)}\n" +
            $"{JsonSerializer.Serialize(armedWaits)}\n{JsonSerializer.Serialize(closedWaits)}\n" +
            $"{JsonSerializer.Serialize(childStarts)}\n{JsonSerializer.Serialize(childCancellations)}\n" +
            $"{JsonSerializer.Serialize(parentNotice)}\n{JsonSerializer.Serialize(traceEntries)}\n" +
            $"{(replacesParts ? 1 : 0)}\n{JsonSerializer.Serialize(deletedParts)}\n{JsonSerializer.Serialize(parts)}\n" +
            payload));
    }

    /// <summary>The longest timeout, delay or park a commit may carry.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(36500);

    private readonly byte[] _contentHash;

    public string InstanceId { get; }

    /// <summary>
    /// The instance's trunk as JSON, to be stored at <see cref="ExpectedRevision"/> + 1: its fields and the part of
    /// its execution state that belongs to no branch. Its revision and runnable time are the store's to set; a store
    /// keeps them beside it rather than rewriting it. <see cref="CheckpointParts.Assemble"/> reads it back with its
    /// parts.
    /// </summary>
    public string Payload { get; }

    /// <summary>The parts this commit writes, in key order: new ones, and those whose JSON changed (A-08).</summary>
    public IReadOnlyList<CheckpointPart> Parts { get; }

    /// <summary>The keys of the parts this commit deletes, in order.</summary>
    public IReadOnlyList<string> DeletedParts { get; }

    /// <summary>
    /// True when the commit's parts are all the instance has: the store deletes any others. A commit replaces them
    /// when it was not made from an instance whose stored parts are known, such as one just started.
    /// </summary>
    public bool ReplacesParts { get; }

    /// <summary>Every part the instance has once the commit is applied.</summary>
    internal IReadOnlyDictionary<string, string> StoredParts { get; }

    /// <summary>The bytes of state the commit carries: its payload and the parts it writes, as UTF-8 (J-04).</summary>
    public long StateBytes => Encoding.UTF8.GetByteCount(Payload) +
                              Parts.Sum(part => (long)Encoding.UTF8.GetByteCount(part.Json));

    /// <summary>What a store reads from the instance to apply the commit, so it need not parse the payload.</summary>
    public CommittedInstance Instance { get; }

    /// <summary>
    /// The trace entries recorded since the last commit, as JSON, for the store to append to the instance's trace
    /// in order (G-01).
    /// </summary>
    public IReadOnlyList<string> TraceEntries { get; }

    /// <summary>The trace entries this commit carries.</summary>
    public IReadOnlyList<ExecutionTraceEntry> ReadTraceEntries() =>
        TraceEntries.Select(entry => JsonSerializer.Deserialize<ExecutionTraceEntry>(entry)!).ToArray();

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
    /// Children this commit starts, in the same transaction as the waits it opens for them (D-17). Each
    /// is saved as revision 1 of a new instance, runnable now, with this instance as its parent.
    /// </summary>
    public IReadOnlyList<ChildStart> ChildStarts { get; }

    /// <summary>Cancellation requests this commit writes for its children; the first request for a child wins.</summary>
    public IReadOnlyList<ChildCancellation> ChildCancellations { get; }

    /// <summary>
    /// For a child's terminal commit, the completion appended to its parent's inbox in the same
    /// transaction. Whatever the parent's state, the child's commit applies: an absent parent drops the
    /// notice, a finished parent or a closed wait records it as rejected.
    /// </summary>
    public ParentNotice? ParentNotice { get; }

    /// <summary>
    /// SHA-256 of <see cref="Payload"/> and every other field that changes what the commit does; a reused commit ID must
    /// carry the same hash. Returns a copy.
    /// </summary>
    public byte[] ContentHash => (byte[])_contentHash.Clone();

    public static CheckpointCommit Create(WorkflowInstance instance, long epoch, long expectedRevision,
        string? commitId = null, bool clearsCrashCount = true, bool parks = false,
        IReadOnlyList<EventSettlement>? settlements = null, long? inboxReadThrough = null,
        IReadOnlyList<OpenedWait>? openedWaits = null, IReadOnlyList<ClosedWait>? closedWaits = null,
        IReadOnlyList<ArmedWait>? armedWaits = null, TimeSpan? wakeAfter = null, bool stopped = false,
        IReadOnlyList<ChildStart>? childStarts = null, IReadOnlyList<ChildCancellation>? childCancellations = null,
        ParentNotice? parentNotice = null, IReadOnlyList<ExecutionTraceEntry>? traceEntries = null)
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
        var started = (childStarts ?? []).ToArray();
        foreach (var child in started)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(child.ChildInstanceId, nameof(childStarts));
            ArgumentException.ThrowIfNullOrWhiteSpace(child.ParentWaitId, nameof(childStarts));
            var childInstance = child.Child
                ?? throw new ArgumentException("A child start carries the child's instance", nameof(childStarts));
            if (childInstance.InstanceId != child.ChildInstanceId || childInstance.ParentInstanceId != instance.InstanceId ||
                childInstance.ParentWaitId != child.ParentWaitId || childInstance.Status != WorkflowStatus.Running ||
                child.ChildInstanceId == instance.InstanceId)
                throw new ArgumentException(
                    "A child start names a new running instance whose parent and wait are this commit's",
                    nameof(childStarts));
            if (!opened.Any(wait => wait.WaitId == child.ParentWaitId && wait.Kind == WaitKind.Child))
                throw new ArgumentException("A child starts with the parent's wait for it", nameof(childStarts));
        }
        if (started.Select(child => child.ChildInstanceId).Distinct(StringComparer.Ordinal).Count() != started.Length)
            throw new ArgumentException("A commit starts each child once", nameof(childStarts));
        // Copies, so a reason changed after the commit is hashed does not change what the store writes.
        var cancelled = (childCancellations ?? []).Select(child =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(child.ChildInstanceId, nameof(childCancellations));
            ArgumentException.ThrowIfNullOrWhiteSpace(child.ParentWaitId, nameof(childCancellations));
            ArgumentNullException.ThrowIfNull(child.Reason, nameof(childCancellations));
            return child with
            {
                Reason = JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(child.Reason))!
            };
        }).ToArray();
        if (parentNotice != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentNotice.ParentInstanceId, nameof(parentNotice));
            ArgumentException.ThrowIfNullOrWhiteSpace(parentNotice.ParentWaitId, nameof(parentNotice));
            ArgumentException.ThrowIfNullOrWhiteSpace(parentNotice.EventId, nameof(parentNotice));
            ArgumentException.ThrowIfNullOrWhiteSpace(parentNotice.EventType, nameof(parentNotice));
            if (!WorkflowStoreRules.IsTerminal(instance.Status) ||
                instance.ParentInstanceId != parentNotice.ParentInstanceId ||
                instance.ParentWaitId != parentNotice.ParentWaitId)
                throw new ArgumentException("Only a child's terminal commit notifies its own parent", nameof(parentNotice));
        }
        // Only the parts that differ from those the instance was last stored with are written (A-08).
        var stored = Runtime.StoredCheckpoint.Of(instance);
        var saved = instance.PartsAt(expectedRevision);
        var written = stored.Parts
            .Where(part => saved == null || !saved.TryGetValue(part.Key, out var json) || json != part.Value)
            .OrderBy(part => part.Key, StringComparer.Ordinal)
            .Select(part => new CheckpointPart(part.Key, part.Value)).ToArray();
        var deleted = saved == null ? [] : saved.Keys.Where(key => !stored.Parts.ContainsKey(key))
            .Order(StringComparer.Ordinal).ToArray();
        return new CheckpointCommit(CommittedInstance.Of(instance), JsonSerializer.Serialize(instance.AsTrunk(stored.Trunk)),
            Array.AsReadOnly(written), Array.AsReadOnly(deleted), saved == null, stored.Parts.AsReadOnly(), epoch, commitId,
            expectedRevision, clearsCrashCount, parks, Array.AsReadOnly(settled), inboxReadThrough,
            Array.AsReadOnly(opened), Array.AsReadOnly(armed), Array.AsReadOnly(closed), wakeAfter, stopped,
            Array.AsReadOnly(started), Array.AsReadOnly(cancelled), parentNotice,
            Array.AsReadOnly((traceEntries ?? []).Select(entry => JsonSerializer.Serialize(entry)).ToArray()));
    }

    private static void ValidateDuration(TimeSpan? duration, string parameter)
    {
        if (duration < TimeSpan.Zero || duration > MaxDuration)
            throw new ArgumentOutOfRangeException(parameter, "Durations are between zero and 100 years");
    }

    /// <summary>
    /// The committed instance's fields and the trunk of its state, at its new revision, without its parts: its
    /// workflow data and its branches are in them.
    /// </summary>
    public WorkflowInstance ReadTrunk()
    {
        return CheckpointParts.Summarize(Payload, null, checked(ExpectedRevision + 1));
    }

    /// <summary>
    /// The parts the instance has once the commit is applied to <paramref name="stored"/>, those it had at the
    /// revision the commit was based on. A commit that replaces its parts needs none.
    /// </summary>
    public IReadOnlyDictionary<string, string> ApplyParts(IReadOnlyDictionary<string, string>? stored)
    {
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ReplacesParts)
        {
            ArgumentNullException.ThrowIfNull(stored);
            foreach (var (key, json) in stored)
                parts[key] = json;
            foreach (var key in DeletedParts)
                parts.Remove(key);
        }
        foreach (var part in Parts)
            parts[part.Key] = part.Json;
        return parts.AsReadOnly();
    }

    /// <summary>
    /// The whole committed instance, at its new revision: its trunk with the commit applied to
    /// <paramref name="basedOn"/>'s parts, the instance as read before the commit. Applying a commit to parts that
    /// already hold it changes nothing, so an instance read after the commit applied serves too. A commit that replaces
    /// its parts needs none.
    /// </summary>
    public WorkflowInstance ReadInstance(WorkflowInstance? basedOn = null)
    {
        var based = basedOn?.SavedParts;
        if (!ReplacesParts && based == null)
            throw new InvalidOperationException(
                "The commit writes only the parts that changed; read it with the instance it was based on");
        return CheckpointParts.Assemble(Payload, ApplyParts(based)
            .Select(part => new CheckpointPart(part.Key, part.Value)), checked(ExpectedRevision + 1));
    }
}

/// <summary>The fields of a committed instance a store applies the commit by (D-03).</summary>
public sealed record CommittedInstance(string InstanceId, string WorkflowName, string CorrelationId,
    WorkflowStatus Status, bool AcknowledgesCancellation)
{
    public string? ParentInstanceId { get; init; }

    public static CommittedInstance Of(WorkflowInstance instance) => new(instance.InstanceId, instance.WorkflowName,
        instance.CorrelationId, instance.Status, instance.CancellationReason != null)
        { ParentInstanceId = instance.ParentInstanceId };
}
