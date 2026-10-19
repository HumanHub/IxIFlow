namespace IxIFlow.Core.Runtime;

/// <summary>The four requests an assigned executor can make. Every request and reply names its assignment.</summary>
internal interface IExecutionHost
{
    Task<CheckpointReply> CheckpointAsync(Checkpoint request);
    Task<StartChildReply> StartChildAsync(StartChild request);
    Task<ReadInboxReply> ReadInboxAsync(ReadInbox request);
    Task<ReadWaitReply> ReadWaitAsync(ReadWait request);
}

internal sealed record Checkpoint(Guid AssignmentId, CheckpointCommit Commit);
internal sealed record StartChild(Guid AssignmentId, string InstanceId, long Epoch, string StepId,
    ChildStart Start, string StructureHash);
internal sealed record ReadInbox(Guid AssignmentId, string InstanceId, long AfterSequence, int Limit);
internal sealed record ReadWait(Guid AssignmentId, string InstanceId, string WaitId);
internal sealed record StartChildReply(Guid AssignmentId, ChildStart? Start = null,
    WorkflowErrorKind? Refusal = null, string? Message = null, ConflictReason? Conflict = null);
internal sealed record ReadInboxReply(Guid AssignmentId, InboxPage? Page = null, ConflictReason? Conflict = null);
internal sealed record ReadWaitReply(Guid AssignmentId, StoredWait? Wait = null, ConflictReason? Conflict = null);

internal sealed class ExecutionOwnershipLostException(Exception cause) : Exception(
    "Workflow execution ownership was lost", cause);

internal enum CheckpointReplyKind { Ack, Conflict, Retry, Unknown }
internal enum ConflictReason { Revision, StaleEpoch, EventsChanged, NotAssigned }
internal sealed record CheckpointReply(Guid AssignmentId, CheckpointReplyKind Kind, long Revision = 0,
    Exception? Error = null, CancellationReason? PendingCancellation = null, bool Parked = false,
    bool UnreadEvents = false, ConflictReason? Conflict = null);
