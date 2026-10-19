using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>A saved instance view. Applications must authorize access to workflow data.</summary>
public sealed record InstanceInspection(InspectionSummary Summary, long Revision, string StructureHash,
    InspectionDebugState DebugState, IReadOnlyList<Breakpoint> Breakpoints,
    IReadOnlyList<InspectionThread> Threads, IReadOnlyList<InspectionAttempt> PendingAttempts,
    IReadOnlyList<InspectionWait> Waits, IReadOnlyList<InspectionData> Data);

public sealed record InspectionSummary(string InstanceId, string WorkflowName, int WorkflowVersion,
    WorkflowStatus Status, string? ParentInstanceId, DateTime CreatedAt, DateTime? CompletedAt, string? Error);
// TimerFreeze and DebugCommand deliberately do not cross this projection: they contain wait keys and edit values.
public sealed record InspectionDebugState(long PauseGeneration, long AppliedOrder, BreakpointStop? Stop,
    DebugStepping? Stepping, DateTime? TimersFrozenAtUtc)
{
    public BreakpointEvaluation? Evaluation { get; init; }
    public IReadOnlyList<BreakpointStop> SkipOnce { get; init; } = [];
}
public sealed record InspectionThread(string ContinuationId, string Name, string? ParentContinuationId,
    string? ParentJoinId, int Sequence, string? ForkActivationId, string StepId, string AuthorStepId,
    int StepIndex, string Status, string FrameId, IReadOnlyList<InspectionFrame> Frames);
public sealed record InspectionFrame(string ActivationId, string Name, string Kind, string ScopeId,
    string? ParentActivationId, int NextStepIndex, int LoopIteration, int? ForEachItemIndex);
public sealed record InspectionAttempt(string ContinuationId, string InvocationId, string StepId,
    string? AttemptId, string? Kind, string? Host, DateTime StartedAtUtc, string? Reason);
public sealed record InspectionWait(string WaitRef, string ContinuationId, string StepId, WaitKind Kind,
    string? Key, DateTime? DeadlineUtc, string? ChildInstanceId);
public sealed record InspectionData(string ContinuationId, JsonElement Value);
