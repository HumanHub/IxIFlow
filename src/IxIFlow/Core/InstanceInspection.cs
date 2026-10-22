using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>A saved instance view. Applications must authorize access to workflow data.</summary>
public sealed record InstanceInspection(InspectionSummary Summary, long Revision, string StructureHash,
    InspectionDebugState DebugState, IReadOnlyList<Breakpoint> Breakpoints,
    IReadOnlyList<InspectionThread> Threads, IReadOnlyList<InspectionAttempt> PendingAttempts,
    IReadOnlyList<InspectionWait> Waits, IReadOnlyList<InspectionData> Data)
{
    public IReadOnlyList<InspectionCatchFault> CatchFaults { get; init; } = [];
    public IReadOnlyList<InspectionInvocation> Invocations { get; init; } = [];
}

/// <summary>A classified, read-only DTO for one live lexical Catch occurrence.</summary>
public sealed record InspectionCatchFault(string ContinuationId, string FrameId, string CatchId,
    string FaultType, JsonElement Value, WorkflowFaultLocation? OriginalLocation);

public sealed record InspectionInvocation(string InvocationId, string StepId, string ContinuationId,
    string WorkflowName, int WorkflowVersion, string? DataType, ArtifactDigest? BundleDigest,
    string? StructureHash, string? ContractHash, string? Disposition, int ScheduledRetries,
    DateTime? RetryAfterUtc, IReadOnlyList<InspectionInvocationAttempt> Attempts);
public sealed record InspectionInvocationAttempt(string PolicyAttemptId, int Number, string Phase,
    DateTime PreparedAtUtc, DateTime? StartedAtUtc, DateTime? EndedAtUtc, string Host,
    string? ChildInstanceId, Guid? ChildIncarnation, WorkflowStatus? ChildStatus,
    string? Disposition, InspectionError? Error, int? PolicyIndex, StepErrorAction? Action,
    DateTime? RetryAfterUtc, ActivityResolutionKind? OperatorDecision);

public sealed record InspectionSummary(string InstanceId, string WorkflowName, int WorkflowVersion,
    WorkflowStatus Status, string? ParentInstanceId, DateTime CreatedAt, DateTime? CompletedAt, string? Error)
{
    public Guid Incarnation { get; init; }
}
// DebugCommand deliberately does not cross this projection: it contains edit values.
public sealed record InspectionDebugState(long PauseGeneration, long AppliedOrder, BreakpointStop? Stop,
    DebugStepping? Stepping)
{
    public BreakpointEvaluation? Evaluation { get; init; }
    public IReadOnlyList<BreakpointStop> SkipOnce { get; init; } = [];
}
public sealed record InspectionThread(string ContinuationId, string Name, string? ParentContinuationId,
    string? ParentJoinId, int Sequence, string? ForkActivationId, string StepId, string AuthorStepId,
    int StepIndex, string Status, string FrameId, IReadOnlyList<InspectionFrame> Frames);
/// <summary>A saved scope position, including graph phase and visit metadata.</summary>
public sealed record InspectionFrame(string ActivationId, string Name, string Kind, string ScopeId,
    string? ParentActivationId, int NextStepIndex, int LoopIteration, int? ForEachItemIndex)
{
    public string? DataType { get; init; }
    public string? RethrowOwnerId { get; init; }
    /// <summary>The derived graph phase, when this frame belongs to a graph.</summary>
    public string? Phase { get; init; }
    public long ArmOccurrence { get; init; }
    /// <summary>The entry count of the enclosing graph activation.</summary>
    public long? Visit { get; init; }
    /// <summary>The edge that entered the current node; null for the start.</summary>
    public string? Via { get; init; }
    /// <summary>The enclosing graph's catalog step and current node or state key.</summary>
    public string? GraphId { get; init; }
    public string? Node { get; init; }
    /// <summary>The selected choice, without its event or activity payload.</summary>
    public InspectionGraphChoice? Pending { get; init; }
}
public sealed record InspectionGraphChoice(string Via, string? Trigger, string? Target, string Cause);
public sealed record InspectionAttempt(string ContinuationId, string InvocationId, string StepId,
    string? AttemptId, string? Kind, string? Host, DateTime StartedAtUtc, string? Reason)
{
    public string? PolicyAttemptId { get; init; }
}
public sealed record InspectionWait(string WaitRef, string ContinuationId, string StepId, WaitKind Kind,
    string? Key, DateTime? DeadlineUtc, string? ChildInstanceId);
public sealed record InspectionData(string ContinuationId, JsonElement Value)
{
    public string? FrameId { get; init; }
    public string? DataType { get; init; }
}
