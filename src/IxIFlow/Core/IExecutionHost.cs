namespace IxIFlow.Core;

/// <summary>The requests an assigned executor can make. Every request and reply names its assignment.</summary>
public interface IExecutionHost
{
    Task<CheckpointReply> CheckpointAsync(Checkpoint request);
    Task<StartChildReply> StartChildAsync(StartChild request);
    Task<ReadInboxReply> ReadInboxAsync(ReadInbox request);
    Task<ReadWaitReply> ReadWaitAsync(ReadWait request);
    Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request);
}

public sealed record Checkpoint(Guid AssignmentId, CheckpointCommit Commit,
    IReadOnlyList<PayloadBody>? Payloads = null);
/// <summary>A complete new payload, carried with its referencing checkpoint.</summary>
public sealed record PayloadBody(PayloadReference Reference, byte[] Body,
    IReadOnlyList<PayloadReference> Dependencies);
public sealed record ReadPayload(Guid AssignmentId, string InstanceId, PayloadReference Reference);
public sealed record ReadPayloadReply(Guid AssignmentId, byte[]? Body = null,
    WorkflowErrorKind? Refusal = null, ConflictReason? Conflict = null);
public sealed record StartChild(Guid AssignmentId, string InstanceId, long Epoch, string StepId,
    ChildStart Start, string StructureHash);
public sealed record ReadInbox(Guid AssignmentId, string InstanceId, long AfterSequence, int Limit);
public sealed record ReadWait(Guid AssignmentId, string InstanceId, string WaitId);
public sealed record StartChildReply(Guid AssignmentId, ChildStart? Start = null,
    WorkflowErrorKind? Refusal = null, string? Message = null, ConflictReason? Conflict = null);
public sealed record ReadInboxReply(Guid AssignmentId, InboxPage? Page = null, ConflictReason? Conflict = null);
public sealed record ReadWaitReply(Guid AssignmentId, StoredWait? Wait = null, ConflictReason? Conflict = null);

public enum CheckpointReplyKind { Ack, Conflict, Retry, Unknown }
public enum ConflictReason { Revision, StaleEpoch, EventsChanged, NotAssigned }
public sealed record CheckpointReply(Guid AssignmentId, CheckpointReplyKind Kind, long Revision = 0,
    string? Error = null, CancellationReason? PendingCancellation = null, bool Parked = false,
    bool UnreadEvents = false, ConflictReason? Conflict = null);
