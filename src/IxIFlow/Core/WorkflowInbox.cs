using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>
/// An event sent to one workflow instance. <see cref="EventId"/> identifies it for
/// deduplication. <see cref="TargetWaitId"/>, when set, names the one wait it may satisfy;
/// otherwise it may satisfy a wait that is open when it arrives, with <see cref="Key"/> when one is
/// given and with any key otherwise.
/// </summary>
public sealed record WorkflowEvent(
    string InstanceId,
    string EventId,
    string EventType,
    string PayloadJson,
    string? Key = null,
    string? TargetWaitId = null,
    string? Subject = null,
    string? DeliveryIdentity = null,
    string? Source = null,
    TimeSpan? DeliveryRetention = null)
{
    public long? PayloadThresholdBytes { get; init; }
    public IReadOnlyList<PayloadReference> PayloadDependencies { get; init; } = [];
    /// <summary>
    /// SHA-256 of the type, key, target wait, subject and payload, each a separate JSON string. An
    /// event ID sent again with another hash is a different event and is refused. CloudEvents set
    /// Source and deduplicate solely by source and event ID, scoped to the instance.
    /// </summary>
    public byte[] ContentHash() => SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(Source == null
            ? new[] { EventType, Key, TargetWaitId, Subject, PayloadJson }
            : new[] { InstanceId, Source, EventId })));

    /// <summary>Checks the fields every store can hold.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(EventType);
        ArgumentNullException.ThrowIfNull(PayloadJson);
        ArgumentNullException.ThrowIfNull(PayloadDependencies);
        ArgumentOutOfRangeException.ThrowIfNegative(PayloadThresholdBytes ?? 0);
        if (DeliveryIdentity != null && (DeliveryIdentity.Length != 64 || !DeliveryIdentity.All(Uri.IsHexDigit)) ||
            DeliveryRetention is { } retention && (retention <= TimeSpan.Zero || retention > CheckpointCommit.MaxDuration))
            throw new ArgumentException("Invalid delivery identity or retention");
        if (InstanceId.Length > 100 || EventId.Length > 100 || TargetWaitId?.Length > 100 || Subject?.Length > 100)
            throw new ArgumentException("Instance, event and wait IDs and subjects are at most 100 characters");
        if (EventType.Length > 400 || Key?.Length > 400)
            throw new ArgumentException("Event types and keys are at most 400 characters");
    }
}

/// <summary>
/// One page of pending events. <see cref="ReadThrough"/> is the inbox sequence the page covers,
/// including events already settled, so a run that has seen the page can mark the inbox read up to
/// it. <see cref="OpenWaits"/> are the instance's open waits with the inbox sequence each opened at:
/// an event addressed by key alone may satisfy only a wait that opened before it arrived.
/// <see cref="Deadlines"/> are the open waits' deadlines and <see cref="StoreTimeUtc"/> the store's
/// time, read together with the events, so every event received before a deadline that is due at
/// that time is on this page or an earlier one.
/// </summary>
public sealed record InboxPage(
    IReadOnlyList<StoredWorkflowEvent> Events,
    long ReadThrough,
    IReadOnlyDictionary<string, long> OpenWaits,
    IReadOnlyDictionary<string, DateTime>? Deadlines = null,
    DateTime? StoreTimeUtc = null)
{
    /// <summary>True when the open wait's deadline had passed by the store's time when the page was read.</summary>
    public bool IsDue(string waitId) =>
        Deadlines != null && Deadlines.TryGetValue(waitId, out var deadline) && deadline <= StoreTimeUtc;

    /// <summary>
    /// A page of up to <paramref name="limit"/> events read after <paramref name="afterSequence"/> from
    /// an inbox at <paramref name="inboxSequence"/>: a full page reads through its last event, a short
    /// one through the whole inbox.
    /// </summary>
    public static InboxPage Of(IReadOnlyList<StoredWorkflowEvent> events, int limit, long afterSequence,
        long inboxSequence, IReadOnlyDictionary<string, long> openWaits,
        IReadOnlyDictionary<string, DateTime> deadlines, DateTime storeTimeUtc) =>
        new(events, limit == 0 ? afterSequence
            : events.Count == limit ? events[^1].Sequence
            : Math.Max(afterSequence, inboxSequence), openWaits, deadlines, storeTimeUtc);
}

public enum EventAppendStatus
{
    /// <summary>The event is stored as pending; <see cref="EventAppendResult.Sequence"/> is its place in the inbox.</summary>
    Stored,

    /// <summary>The same event was stored before; read it for its outcome.</summary>
    Duplicate,

    /// <summary>The event ID was used before for a different event.</summary>
    Conflict,

    /// <summary>No such instance.</summary>
    NotFound,

    /// <summary>The instance has finished; it takes no more events.</summary>
    Closed,

    /// <summary>
    /// No wait could take the event when it arrived, so it is recorded as rejected with
    /// <see cref="EventAppendResult.Rejection"/>; a resend is still recognized.
    /// </summary>
    Rejected
}

/// <summary>
/// The acknowledgement of an append. <see cref="MadeRunnable"/> is true when the append made a
/// running or suspended instance due now.
/// </summary>
public sealed record EventAppendResult(EventAppendStatus Status, long Sequence = 0, bool MadeRunnable = false,
    EventRejection? Rejection = null);

public enum EventOutcome
{
    /// <summary>Stored and waiting for the instance's owner to take or reject it.</summary>
    Pending,

    /// <summary>Taken by a wait, in the commit that recorded it.</summary>
    Consumed,

    /// <summary>Settled without being taken; <see cref="StoredWorkflowEvent.Rejection"/> says why.</summary>
    Rejected,

    /// <summary>The instance finished before its owner decided the event.</summary>
    Closed = 4
}

/// <summary>Why an event was not taken.</summary>
public enum EventRejection
{
    /// <summary>The event names a key, and no wait with that key was open when it arrived.</summary>
    NoOpenWait = 1,

    /// <summary>The event names a wait the instance never opened.</summary>
    UnknownWait,

    /// <summary>The wait was answered by another event.</summary>
    WaitAnswered,

    /// <summary>The wait reached its deadline.</summary>
    WaitTimedOut,

    /// <summary>The wait's scope ended: its branch or iteration finished or was cancelled, or compensation started.</summary>
    WaitCancelled,

    /// <summary>The wait was replaced by a newer copy of a child's wait.</summary>
    WaitReplaced,

    /// <summary>The instance finished while the wait was open.</summary>
    InstanceFinished,

    /// <summary>An open wait's condition did not accept the event.</summary>
    NotMatched,

    /// <summary>The event matched more than one open wait.</summary>
    Ambiguous,

    /// <summary>The reply's subject differs from the request's.</summary>
    SubjectMismatch
}

/// <summary>An event as the inbox holds it.</summary>
public sealed record StoredWorkflowEvent(
    string InstanceId,
    string EventId,
    long Sequence,
    string EventType,
    string? Key,
    string? TargetWaitId,
    StoredJson PayloadJson,
    DateTime ReceivedAtUtc,
    EventOutcome Outcome,
    string? WaitId = null,
    EventRejection? Rejection = null,
    string? Subject = null);

/// <summary>How a commit settles one pending event: consumed by a wait, or rejected with a reason.</summary>
public sealed record EventSettlement(string EventId, EventOutcome Outcome, string? WaitId = null,
    EventRejection? Rejection = null);

/// <summary>What kind of wait a waits-table row is.</summary>
public enum WaitKind
{
    /// <summary>A <c>WaitFor</c> with no request: answered by key, or by its ID.</summary>
    Event = 1,

    /// <summary>The reply to a request: answered only by its ID.</summary>
    Reply,

    /// <summary>A child's wait shown on its parent; the child decides events forwarded to it.</summary>
    Child,

    /// <summary>A delay: only its deadline ends it. No event can answer it.</summary>
    Delay,

    /// <summary>The instance's execution deadline. No event can answer it.</summary>
    Execution
}

/// <summary>Why a wait closed.</summary>
public enum WaitCloseReason
{
    Answered = 1,
    TimedOut,
    Cancelled,
    Replaced,
    InstanceFinished
}

/// <summary>
/// A wait a commit opens. The store stamps it with the instance's inbox sequence at that commit and,
/// when it has a <paramref name="Timeout"/>, with a deadline: the store's time at that commit plus the
/// timeout.
/// </summary>
public sealed record OpenedWait(string WaitId, WaitKind Kind, string Key, string EventType, TimeSpan? Timeout,
    StoredJson? DefaultPayloadJson = null);

/// <summary>
/// A request's reserved reply wait activated when its <c>WaitFor</c> is reached, with an optional
/// timeout and saved suggested payload. A deadline or suggestion already stored is kept.
/// The deadline is the store's time at that commit plus <paramref name="Timeout"/>.
/// </summary>
public sealed record ArmedWait(string WaitId, TimeSpan? Timeout, StoredJson? DefaultPayloadJson = null);

/// <summary>A wait a commit closes, and why.</summary>
public sealed record ClosedWait(string WaitId, WaitCloseReason Reason);

/// <summary>
/// A child workflow a parent's commit starts: the child's whole initial instance as JSON, which
/// the store saves as the child's revision 1, runnable at once, in the same transaction as the parent's
/// wait for it.
/// </summary>
public sealed record ChildStart(string ChildInstanceId, string Payload, string ParentWaitId)
{
    public Guid Incarnation { get; init; } = Guid.NewGuid();
    internal IReadOnlyList<PayloadReference> PayloadReferences => Child is { } child
        ? Runtime.CheckpointPayloads.References(child.ExecutionStateJson,
            new Dictionary<string, StoredJson> { [CheckpointParts.DataKey] = child.WorkflowDataJson }) : [];
    private readonly WorkflowInstance? _child = Read(Payload);

    /// <summary>
    /// What a store saves the child by, read from the payload once when the start is made, so a store writes the
    /// payload as it comes and takes its columns from here.
    /// </summary>
    public CommittedInstance? Instance => _child is { } child ? CommittedInstance.Of(child) : null;

    /// <summary>The child's instance as the payload holds it, for the commit to check; null when it is not one.</summary>
    internal WorkflowInstance? Child => _child;

    private static WorkflowInstance? Read(string payload)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<WorkflowInstance>(payload);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A cancellation request a parent's commit writes for a child it no longer waits to finish normally, and the
/// parent's wait for that child: if the child is gone, the store ends that wait as the child's termination.
/// Hard termination also targets detached descendants and needs no parent wait.
/// </summary>
public sealed record ChildCancellation(string ChildInstanceId, CancellationReason Reason, string? ParentWaitId);

/// <summary>
/// The completion a child's terminal commit appends to its parent's inbox, addressed to the parent's
/// wait for it. The child's commit applies whatever becomes of it.
/// </summary>
public sealed record ParentNotice(string ParentInstanceId, string ParentWaitId, string EventId, string EventType,
    string PayloadJson)
{
    public IReadOnlyList<PayloadReference> PayloadDependencies { get; init; } = [];
}

/// <summary>A row of the waits table: open, or closed with its reason.</summary>
public sealed record StoredWait(
    string InstanceId,
    string WaitId,
    WaitKind Kind,
    string Key,
    string EventType,
    DateTime? DeadlineUtc,
    long OpenedSequence,
    DateTime OpenedAtUtc,
    WaitCloseReason? CloseReason = null,
    DateTime? ClosedAtUtc = null,
    StoredJson? DefaultPayloadJson = null)
{
    public bool IsOpen => CloseReason == null;

    /// <summary>The rejection an event for this closed wait receives.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public EventRejection Rejection => CloseReason?.Rejection()
        ?? throw new InvalidOperationException("An open wait has no rejection");
}

public static class WaitCloseReasonExtensions
{
    /// <summary>The rejection an event for a wait closed for this reason receives.</summary>
    public static EventRejection Rejection(this WaitCloseReason reason) => reason switch
    {
        WaitCloseReason.Answered => EventRejection.WaitAnswered,
        WaitCloseReason.TimedOut => EventRejection.WaitTimedOut,
        WaitCloseReason.Cancelled => EventRejection.WaitCancelled,
        WaitCloseReason.Replaced => EventRejection.WaitReplaced,
        WaitCloseReason.InstanceFinished => EventRejection.InstanceFinished,
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}

/// <summary>
/// Which open waits to list. Every filter is optional; <see cref="AfterWaitRef"/> pages by diagnostic wait reference
/// within the other filters.
/// </summary>
public sealed record WaitQuery(
    string? InstanceId = null,
    string? Key = null,
    string? EventType = null,
    WaitKind? Kind = null,
    string? AfterWaitRef = null,
    int Limit = 100,
    IReadOnlyCollection<string>? InstanceIds = null)
{
    public bool IncludeClosed { get; init; }
}

/// <summary>
/// The per-instance inbox and waits table. Events are stored before they are
/// acknowledged and settled by the commit that takes them, so each is consumed at most once. Waits are
/// opened and closed by commits. Settled events and closed waits are kept until the instance is
/// deleted, so a redelivery and a late answer are still recognized.
/// </summary>
public interface IWorkflowInbox
{
    /// <summary>
    /// Stores an event before it is acknowledged, received at the store's time. An event for a closed or
    /// unknown wait, one addressed by key when no wait with that key is open, and one that arrives when
    /// every wait it could reach is past its deadline, is recorded as rejected. A pending
    /// event makes a running or suspended instance runnable in the same step; a stopped one waits for
    /// its release.
    /// </summary>
    Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent);

    /// <summary>
    /// Pending events after <paramref name="afterSequence"/>, in arrival order, how far the page reads,
    /// the instance's open waits with the sequence each opened at and their deadlines, and the store's
    /// time. The read is ordered against appends to the instance: an event appended meanwhile is on the
    /// page or received after the page's time.
    /// </summary>
    Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit);

    /// <summary>One event and its outcome, or null.</summary>
    Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId);

    /// <summary>One wait, open or closed, or null.</summary>
    Task<StoredWait?> GetWaitAsync(string instanceId, string waitId);

    /// <summary>
    /// Open waits that match the query, ordered by wait ID. Delays and execution deadlines are listed
    /// only when the query names their kind.
    /// </summary>
    Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query);

    /// <summary>
    /// The inbox sequence of each instance that exists, read in one call, so a host can tell its busy
    /// owners that events arrived.
    /// </summary>
    Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(IReadOnlyCollection<string> instanceIds);
}
