using System.Text.Json.Serialization;

namespace IxIFlow.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(ControlFlowTrace), "control")]
[JsonDerivedType(typeof(GraphTrace), "graph")]
[JsonDerivedType(typeof(InlineTrace), "inline")]
[JsonDerivedType(typeof(ScopeTrace), "scope")]
[JsonDerivedType(typeof(ConditionTrace), "condition")]
[JsonDerivedType(typeof(SwitchTrace), "switch")]
[JsonDerivedType(typeof(CancellationHandlerTrace), "cancellationHandler")]
[JsonDerivedType(typeof(DataAssignedTrace), "assigned")]
[JsonDerivedType(typeof(FaultTrace), "fault")]
[JsonDerivedType(typeof(JoinTrace), "join")]
[JsonDerivedType(typeof(ItemEndTrace), "itemEnd")]
[JsonDerivedType(typeof(TimeoutTrace), "timeout")]
[JsonDerivedType(typeof(RetryTrace), "retry")]
[JsonDerivedType(typeof(InvocationTrace), "invocation")]
[JsonDerivedType(typeof(ReceivedEventTrace), "event")]
[JsonDerivedType(typeof(ActivityResolutionTrace), "resolution")]
[JsonDerivedType(typeof(CancellationTrace), "cancellation")]
[JsonDerivedType(typeof(ReleaseTrace), "release")]
[JsonDerivedType(typeof(TraceTruncation), "truncation")]
[JsonDerivedType(typeof(TraceBudget), "budget")]
[JsonDerivedType(typeof(DataEditedTrace), "dataEdited")]
[JsonDerivedType(typeof(DebugCommandTrace), "debugCommand")]
[JsonDerivedType(typeof(BreakpointHitTrace), "breakpointHit")]
[JsonDerivedType(typeof(ReplayOriginTrace), "replayOrigin")]
[JsonDerivedType(typeof(ThreadPositionTrace), "threadPosition")]
public abstract record TracePayload;

/// <summary>Versioned, value-free replay facts. Position deltas record decisions after execution, never expressions.</summary>
public sealed record ReplayOriginTrace(string InstanceId, string StructureHash, long BaseRevision, TraceDetail Detail) : TracePayload
{
    public IReadOnlyList<InspectionThread> Threads { get; init; } = [];
}

public sealed record ThreadPositionTrace(string ContinuationId, InspectionThread? Thread) : TracePayload;

/// <summary>Identifiers only; item indices start at zero and loop iterations at one.</summary>
public record ControlFlowTrace(
    string StepId,
    WorkflowStepType StepKind,
    string ContinuationId,
    int? Index = null,
    string? ScopeId = null,
    string? JoinId = null) : TracePayload;

/// <summary>The original structural fault site; diagnostic CLR stacks are not persisted.</summary>
public sealed record WorkflowFaultLocation(string StepId, string ContinuationId, string FrameActivationId, string? AttemptId = null);

/// <summary>Value-free fault propagation facts.</summary>
public sealed record FaultTrace(
    string StepId,
    WorkflowStepType StepKind,
    string ContinuationId,
    string ExceptionType,
    WorkflowFaultLocation? OriginalLocation,
    WorkflowFaultLocation CurrentLocation) : TracePayload
{
    public string? DeclaredExceptionType { get; init; }
}

public enum ConditionPath
{
    Then,
    Else
}

public sealed record ConditionTrace(
    string StepId,
    WorkflowStepType StepKind,
    string ContinuationId,
    int Index,
    string ScopeId,
    ConditionPath Path) : TracePayload;

public enum JoinPath
{
    Merged,
    Conflict,
    Unwound
}

public sealed record JoinTrace(
    string StepId,
    WorkflowStepType StepKind,
    string ContinuationId,
    string JoinId,
    JoinPath Path) : TracePayload;

public sealed record InlineTrace(
    string StepId,
    string ContinuationId,
    string ScopeId,
    string ActivationId,
    ItemEndCause? Cause = null) : TracePayload;

public enum ItemEndCause
{
    Completed,
    Failed,
    Cancelled,
    UnwoundByOuterHandler
}

public sealed record ItemEndTrace(
    string StepId,
    WorkflowStepType StepKind,
    string ContinuationId,
    int Index,
    string ScopeId,
    string? JoinId,
    ItemEndCause Cause) : TracePayload;

public sealed record TimeoutTrace(
    string StepId,
    WorkflowStepType StepKind,
    string ContinuationId,
    string WaitRef,
    string? ScopeId,
    bool Replayed = false) : TracePayload;

public sealed record RetryTrace(
    string StepId,
    WorkflowStepType StepKind,
    string ContinuationId,
    int Attempt,
    string? RestartStepId,
    string? ScopeId,
    DateTime? RetryAfterUtc) : TracePayload
{
    public InvocationTrace? Invocation { get; init; }
}

/// <summary>Value-free facts for one durable invocation policy attempt.</summary>
public sealed record InvocationTrace(
    string InvocationId,
    string StepId,
    string ContinuationId,
    string PolicyAttemptId,
    int AttemptNumber,
    string? ChildInstanceId,
    WorkflowStatus? ChildStatus,
    string Disposition,
    int? PolicyIndex,
    StepErrorAction? Action,
    DateTime? RetryAfterUtc) : TracePayload;

/// <summary>An inbox decision, without the event payload.</summary>
public sealed record ReceivedEventTrace(
    string EventId,
    string? Key,
    string EventType,
    string? WaitRef,
    string? StepId,
    EventRejection? RefusalReason = null) : TracePayload;

/// <summary>An operator decision, without outputs, notes or error contents.</summary>
public sealed record ActivityResolutionTrace(
    string InvocationId,
    string StepId,
    ActivityResolutionKind Decision,
    string? DecidedBy) : TracePayload
{
    public string? PolicyAttemptId { get; init; }
}

/// <summary>The entry timestamp is when the cancellation was observed or completed.</summary>
public sealed record CancellationTrace(
    string? Actor,
    DateTime RequestedAt,
    string ReasonCode,
    string? ChildInstanceId = null,
    string? WaitRef = null) : TracePayload;

public sealed record ReleaseTrace(string? Actor) : TracePayload;

/// <summary>Omissions in this commit, including the entry displaced by the marker.</summary>
public sealed record TraceTruncation : TracePayload
{
    public TraceTruncation(long droppedCount) => DroppedCount = droppedCount;
    public long DroppedCount { get; set; }
}

public sealed record TraceBudget(long Limit) : TracePayload
{
    public long DroppedCount { get; set; }
}

/// <summary>Activities keeps activity, inbox, operator and cancellation entries; Full adds control flow.</summary>
public enum TraceDetail
{
    Activities,
    Full
}

public enum TraceClock
{
    Host,
    Store
}

public sealed record DebugCommandTrace(string Actor, string CommandId, long PauseGeneration) : TracePayload;

public sealed record BreakpointHitTrace(
    string BranchId,
    string StepId,
    string BreakpointId,
    long HitCount,
    long PauseGeneration) : TracePayload
{
    public string? Reason { get; init; }
    public IReadOnlyList<BreakpointConditionDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>Value-free graph entry, selection and completion facts; selections are not replay stops.</summary>
public sealed record GraphTrace(
    string StepId,
    string ContinuationId,
    string GraphId,
    string Node,
    long Visit,
    string? Via = null,
    string? Target = null,
    TransitionCause? Cause = null,
    ItemEndCause? ExitCause = null) : TracePayload
{
    public string? Trigger { get; init; }
    public int? Count { get; init; }
}

/// <summary>A value-free switch selection fact; StepEntered is its replay stop.</summary>
public enum SwitchPath
{
    Case,
    Default,
    NoMatch
}

public sealed record SwitchTrace(string StepId, string ContinuationId, SwitchPath Path, int? Index, string? ScopeId) : TracePayload;

/// <summary>A completed data write, without evaluated keys or values.</summary>
public sealed record DataAssignedTrace(string StepId, string ContinuationId, string Target) : TracePayload;

public enum ScopeTraceCause
{
    Closed,
    Interrupted,
    Timeout
}

public sealed record ScopeTrace(
    string StepId,
    WorkflowStepType StepKind,
    string ContinuationId,
    string ActivationId,
    bool Reopen,
    int RecordedOpens,
    ScopeCloseReason? Reason,
    ScopeTraceCause? Cause) : TracePayload
{
    public string InvocationId { get; init; } = "";
}

/// <summary>A cancellation handler phase, containing only structural control facts.</summary>
public sealed record CancellationHandlerTrace(
    string StepId,
    string ContinuationId,
    string BodyScopeId,
    string HandlerScopeId,
    string ActivationId,
    CancellationUnwindCause Cause) : TracePayload;

/// <summary>The reason for unwinding, independent of scheduling and item outcomes.</summary>
public enum CancellationUnwindCause
{
    Cancelled,
    HandledFaultCancellation,
    Faulted
}