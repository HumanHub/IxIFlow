using System.Text.Json.Serialization;

namespace IxIFlow.Core.Runtime;

internal enum ScopePhase { Opening, Open, Closing, Preparing }

internal sealed class ScopeFrameState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ContinuationId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string InvocationId { get; set; } = Guid.NewGuid().ToString("N");
    public ActivityInputSnapshot Inputs { get; set; } = new();
    public SerializedValue? Activity { get; set; }
    public ScopePhase Phase { get; set; }
    public int RecordedOpens { get; set; }
    public ScopeCloseReason? CloseReason { get; set; }
    public PendingFailure? CloseError { get; set; }
    public List<string> EnclosingScopeIds { get; set; } = [];
    public int OpenFailures { get; set; }
    public DateTime? OpenAfterUtc { get; set; }
    [JsonIgnore] public object? Value { get; set; }
    [JsonIgnore] public object? LiveActivity { get; set; }
    [JsonIgnore] public RunningActivity? InFlight { get; set; }
    [JsonIgnore] public ScopeThreadScheduler? Scheduler { get; set; }
    [JsonIgnore] public Task? Permit { get; set; }
    [JsonIgnore] public bool Quarantined { get; set; }
    [JsonIgnore] public ScopeRelease? Release { get; set; }
}

internal sealed class ScopeFailureBoundary
{
    [JsonRequired] public required string OwnerContinuationId { get; init; }
    [JsonRequired] public required string BodyActivationId { get; init; }

    public bool Contains(ContinuationState continuation, int index)
    {
        if (continuation.Id != OwnerContinuationId) return continuation.InheritedScopes?.Contains(BodyActivationId) == true;
        var body = continuation.Stack.FindIndex(frame => frame.ActivationId == BodyActivationId);
        return body >= 0 && index >= body;
    }
}

internal sealed class PendingFailure
{
    private SerializedException _error = null!;
    [JsonRequired] public required SerializedException Error { get => _error; init => _error = value ?? throw new System.Text.Json.JsonException("Missing pending failure error"); }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScopeFailureBoundary? ScopeBoundary { get; init; }
    [JsonIgnore] public string Type => Error.Type;
    [JsonIgnore] public string Message => Error.Message;
    public Exception ForPropagation(ExecutionCheckpoint checkpoint) => Error.ForPropagation(checkpoint);
    public void Propagate(ExecutionCheckpoint checkpoint) => Error.Propagate(checkpoint);
    public static PendingFailure Capture(Exception error, WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint, ContinuationState? continuation, bool newOccurrence) => new()
    {
        Error = SerializedException.Capture(error, scopes, checkpoint, continuation, newOccurrence)
    };
}

internal sealed class DispatchAuthorization
{
    public string StepId { get; set; } = "";
    public string ActivationId { get; set; } = "";
    public int Index { get; set; }
    public int Iteration { get; set; }
    public string? InvocationId { get; set; }
    public bool Matches(ContinuationState continuation, string step) => continuation.Stack.LastOrDefault() is { } frame &&
        StepId == step && ActivationId == frame.ActivationId && Index == frame.NextStepIndex && Iteration == frame.LoopIterationCount &&
        (InvocationId == null || continuation.PendingActivity?.Id == InvocationId);
    public static DispatchAuthorization For(ContinuationState continuation, string step) => new()
    {
        StepId = step, ActivationId = continuation.Stack[^1].ActivationId, Index = continuation.Stack[^1].NextStepIndex,
        Iteration = continuation.Stack[^1].LoopIterationCount, InvocationId = continuation.PendingActivity?.Id
    };
}
