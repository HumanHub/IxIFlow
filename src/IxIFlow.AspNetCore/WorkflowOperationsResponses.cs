using IxIFlow.Core;

namespace IxIFlow.AspNetCore;

public sealed record WorkflowTraceResponse(long Sequence, string TraceId, DateTime Timestamp, TraceClock Clock,
    int StepNumber, string ActivityName, TraceEntryType EntryType, TimeSpan? Duration, string? ErrorMessage, TracePayload? Payload)
{
    public InspectionError? Error { get; init; }
}
public sealed record WorkflowTracePage(IReadOnlyList<WorkflowTraceResponse> Items, long? NextAfter);
public sealed record WorkflowOpenWaitResponse(string InstanceId, string WaitRef, WaitKind Kind, string Key,
    string EventType, string? EventSchema, DateTime? DeadlineUtc, DateTime OpenedAtUtc, string Token,
    string? DefaultPayloadJson);
public sealed record WorkflowWaitPage(IReadOnlyList<WorkflowOpenWaitResponse> Items, string? NextAfter);
public sealed record WorkflowSnapshotResponse(string InstanceId, string WorkflowName, int WorkflowVersion,
    WorkflowStatus Status, DateTime CapturedAtUtc, IReadOnlyList<WorkflowPointerResponse> Pointers,
    IReadOnlyList<WorkflowFrameResponse> Frames)
{
    public IReadOnlyList<InspectionError> Errors { get; init; } = [];
}
public sealed record WorkflowPointerResponse(string PointerId, string StepId, int StepIndex, string Status, string? FrameId);
public sealed record WorkflowFrameResponse(string FrameId, string Kind, string StepId, string? ParentFrameId);
public sealed record WorkflowOutboxEventResponse(string Id, string Type, string InstanceId, string WorkflowName,
    int WorkflowVersion, string CorrelationId, DateTime OccurredAtUtc);
public sealed record WorkflowDeliveryResponse(WorkflowOutboxEventResponse Event, string PublisherName,
    OutboxDeliveryStatus Status, int Attempts, DateTime? FirstAttemptAtUtc, DateTime NextAttemptAtUtc,
    string? LastError, OutboxGivenUpReason? GivenUpReason, DateTime? RetriedAtUtc, string? RetriedBy);
public sealed record WorkflowDeliveryPage(IReadOnlyList<WorkflowDeliveryResponse> Items, string? NextCursor);
