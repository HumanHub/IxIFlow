using System.Security.Cryptography;
using System.Text;

namespace IxIFlow.Core;

/// <summary>
/// An event sent to one workflow instance (D-02). <see cref="EventId"/> identifies it for
/// deduplication; <see cref="TargetActivationId"/>, when set, names the one wait activation it may
/// satisfy (D-03).
/// </summary>
public sealed record WorkflowEvent(
    string InstanceId,
    string EventId,
    string EventType,
    string PayloadJson,
    string? Key = null,
    string? TargetActivationId = null)
{
    /// <summary>
    /// SHA-256 of the type, key, target activation and payload. An event ID sent again with another
    /// hash is a different event and is refused.
    /// </summary>
    public byte[] ContentHash() => SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{EventType}\n{(Key == null ? "\0" : "k" + Key)}\n" +
        $"{(TargetActivationId == null ? "\0" : "t" + TargetActivationId)}\n{PayloadJson}"));
}

public enum EventAppendStatus
{
    /// <summary>The event is stored; <see cref="EventAppendResult.Sequence"/> is its place in the inbox.</summary>
    Stored,

    /// <summary>The same event was stored before; this is its original place.</summary>
    Duplicate,

    /// <summary>The event ID was used before for a different event.</summary>
    Conflict,

    /// <summary>No such instance.</summary>
    NotFound,

    /// <summary>The instance has finished; it takes no more events.</summary>
    Closed
}

/// <summary>
/// The acknowledgement of an append. <see cref="MadeRunnable"/> is true when the append made a
/// running or suspended instance due now.
/// </summary>
public sealed record EventAppendResult(EventAppendStatus Status, long Sequence = 0, bool MadeRunnable = false);

public enum EventOutcome
{
    /// <summary>Stored and waiting for a matching wait.</summary>
    Pending,

    /// <summary>Taken by a wait activation, in the commit that recorded it.</summary>
    Consumed,

    /// <summary>Settled without being taken, for example because it matched more than one wait.</summary>
    Rejected,

    /// <summary>No wait took it before it expired.</summary>
    Expired
}

/// <summary>An event as the inbox holds it.</summary>
public sealed record StoredWorkflowEvent(
    string InstanceId,
    string EventId,
    long Sequence,
    string EventType,
    string? Key,
    string? TargetActivationId,
    string PayloadJson,
    DateTime ReceivedAtUtc,
    DateTime ExpiresAtUtc,
    EventOutcome Outcome,
    string? ActivationId = null,
    string? Reason = null);

/// <summary>How a commit settles one pending event: consumed by an activation, or rejected with a reason.</summary>
public sealed record EventSettlement(string EventId, EventOutcome Outcome, string? ActivationId = null,
    string? Reason = null);

/// <summary>
/// The per-instance inbox (D-02, D-32). Events are stored before they are acknowledged and settled
/// by the commit that takes them, so each is consumed at most once. Settled events are kept until
/// the instance is deleted, so a redelivery is still recognized.
/// </summary>
public interface IWorkflowInbox
{
    /// <summary>
    /// Stores an event before it is acknowledged. A running or suspended instance becomes runnable
    /// in the same step; a stopped or finished one does not. The event expires after
    /// <paramref name="retention"/> if no wait takes it.
    /// </summary>
    Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent, TimeSpan retention);

    /// <summary>Pending, unexpired events after <paramref name="afterSequence"/>, in arrival order.</summary>
    Task<IReadOnlyList<StoredWorkflowEvent>> GetPendingEventsAsync(string instanceId, long afterSequence, int limit);

    /// <summary>One event and its outcome, or null.</summary>
    Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId);

    /// <summary>Settles up to <paramref name="limit"/> pending events that have expired; returns how many.</summary>
    Task<int> SettleExpiredEventsAsync(int limit);
}
