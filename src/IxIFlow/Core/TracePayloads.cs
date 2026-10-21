using System.Text.Json.Serialization;

namespace IxIFlow.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(ControlFlowTrace), "control")]
[JsonDerivedType(typeof(GraphTrace), "graph")]
[JsonDerivedType(typeof(ConditionTrace), "condition")]
[JsonDerivedType(typeof(JoinTrace), "join")]
[JsonDerivedType(typeof(ItemEndTrace), "itemEnd")]
[JsonDerivedType(typeof(TimeoutTrace), "timeout")]
[JsonDerivedType(typeof(RetryTrace), "retry")]
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
public record ControlFlowTrace(string StepId, WorkflowStepType StepKind, string ContinuationId,
    int? Index = null, string? ScopeId = null, string? JoinId = null) : TracePayload;

public enum ConditionPath { Then, Else, Outcome }
public sealed record ConditionTrace(string StepId, WorkflowStepType StepKind, string ContinuationId,
    int Index, string ScopeId, ConditionPath Path) : TracePayload;

public enum JoinPath { Merged, Conflict, Unwound }
public sealed record JoinTrace(string StepId, WorkflowStepType StepKind, string ContinuationId,
    string JoinId, JoinPath Path) : TracePayload;

public enum ItemEndCause { Completed, Failed, Cancelled, UnwoundByOuterHandler }
public sealed record ItemEndTrace(string StepId, WorkflowStepType StepKind, string ContinuationId,
    int Index, string ScopeId, string? JoinId, ItemEndCause Cause) : TracePayload;

public sealed record TimeoutTrace(string StepId, WorkflowStepType StepKind, string ContinuationId,
    string WaitRef, string? ScopeId, bool Replayed = false) : TracePayload;

public sealed record RetryTrace(string StepId, WorkflowStepType StepKind, string ContinuationId,
    int Attempt, string? RestartStepId, string? ScopeId, DateTime? RetryAfterUtc) : TracePayload;

/// <summary>An inbox decision, without the event payload.</summary>
public sealed record ReceivedEventTrace(string EventId, string? Key, string EventType, string? WaitRef,
    string? StepId, EventRejection? RefusalReason = null) : TracePayload;

/// <summary>An operator decision, without outputs, notes or error contents.</summary>
public sealed record ActivityResolutionTrace(string InvocationId, string StepId, ActivityResolutionKind Decision,
    string? DecidedBy) : TracePayload;

/// <summary>The entry timestamp is when the cancellation was observed or completed.</summary>
public sealed record CancellationTrace(string? Actor, DateTime RequestedAt, string ReasonCode,
    string? ChildInstanceId = null, string? WaitRef = null) : TracePayload;

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
public enum TraceDetail { Activities, Full }

public enum TraceClock { Host, Store }

public sealed record DebugCommandTrace(string Actor, string CommandId, long PauseGeneration) : TracePayload;
public sealed record BreakpointHitTrace(string BranchId, string StepId, string BreakpointId,
    long HitCount, long PauseGeneration) : TracePayload
{
    public string? Reason { get; init; }
    public IReadOnlyList<BreakpointConditionDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>Value-free graph entry, selection and completion facts; selections are not replay stops.</summary>
public sealed record GraphTrace(string StepId, string ContinuationId, string GraphId, string Node,
    long Visit, string? Via = null, string? Target = null, TransitionCause? Cause = null,
    ItemEndCause? ExitCause = null) : TracePayload;
