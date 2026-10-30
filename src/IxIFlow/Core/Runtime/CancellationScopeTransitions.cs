using System.Text.Json.Serialization;

namespace IxIFlow.Core.Runtime;

internal enum CancellationScopePhase
{
    Body,
    Handler
}

internal sealed class FailureHandlerRoute
{
    [JsonRequired] public string ContinuationId { get; set; } = "";
    [JsonRequired] public string ActivationId { get; set; } = "";
    [JsonRequired] public string StepId { get; set; } = "";
    [JsonRequired] public int HandlerIndex { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasCancellationScopes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SagaRestartPoint? SagaRestart { get; set; }
}

internal sealed class CancellationScopeState
{
    [JsonRequired] public CancellationScopePhase Phase { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CancellationUnwindCause? Cause { get; set; }

    [JsonRequired] public string ActivationId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SerializedValue? EntryPrevious { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingFailure? Failure { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCancellationCleanup { get; set; }

    [JsonIgnore] public object? RuntimeEntryPrevious { get; set; }
}

/// <summary>One stack boundary in the existing failure and cancellation walks.</summary>
internal static class CancellationScopeTransitions
{
    public static bool IsInsideHandler(ContinuationState continuation) =>
        continuation.Stack.Any(frame => frame.CancellationState?.Phase == CancellationScopePhase.Handler);

    public static void Enter(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, WorkflowStep step,
        ScopePosition owner, ContinuationState continuation)
    {
        var fresh = owner.CancellationState == null;

        var state = owner.CancellationState ??= new CancellationScopeState
        {
            EntryPrevious = continuation.Previous, RuntimeEntryPrevious = continuation.RuntimePrevious
        };

        if (fresh)
            checkpoint.Trace.Record(TraceEntryType.StepEntered, step, continuation);

        var handler = state.Phase == CancellationScopePhase.Handler;

        var child = new ScopePosition
        {
            ScopeId = handler ? scopes.CancellationScope(step) : scopes.SequenceScope(step),
            Kind = handler ? FrameKind.CancellationHandler : FrameKind.Body,
            EntryPrevious = state.EntryPrevious, RuntimeEntryPrevious = state.RuntimeEntryPrevious,
            RestorePreviousOnExit = true
        };

        continuation.Stack.Add(child);

        if (handler)
            checkpoint.Trace.CancellationHandler(TraceEntryType.CancellationHandlerEntered, step, continuation, state, child.ScopeId);
    }

    public static bool Exit(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ScopePosition owner,
        ScopePosition child, WorkflowStep step, ContinuationState continuation, out PendingFailure? failure)
    {
        failure = null;

        if (step.StepType != WorkflowStepType.CancellationScope || owner.CancellationState is not { } state)
            return false;

        if (state.Phase == CancellationScopePhase.Handler)
        {
            checkpoint.Trace.CancellationHandler(TraceEntryType.CancellationHandlerCompleted, step, continuation, state, child.ScopeId);
            failure = state.IsCancellationCleanup ? null : state.Failure;
        }

        owner.CancellationState = null;
        owner.NextStepIndex++;
        continuation.Previous = state.EntryPrevious;
        continuation.RuntimePrevious = state.RuntimeEntryPrevious;

        // Only the occurrence whose handler just finished owns this unwind. A normal
        // nested body must return to the cleanup that called it, even during cancellation.
        if (state.IsCancellationCleanup)
            TryScopeTransitions.ContinueCancellation(scopes, checkpoint, continuation);

        return true;
    }

    public static bool BeginCancellation(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, int index)
    {
        var state = continuation.Stack[index].CancellationState;

        if (state == null || state.Phase == CancellationScopePhase.Handler)
            return false;

        state.Cause = continuation.UnwindCause ?? CancellationUnwindCause.Cancelled;

        // A failed branch still finishes as Failed; only a selected business handler makes
        // its unfinished body eligible. The scheduling boolean alone is insufficient.
        if (state.Cause == CancellationUnwindCause.Faulted)
            return false;

        Begin(scopes, checkpoint, continuation, index, continuation.UnwindFailure, state.Cause.Value, isCancellationCleanup: true);

        return true;
    }

    public static void BeginFault(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, int index, PendingFailure failure) =>
        Begin(scopes, checkpoint, continuation, index, failure, CancellationUnwindCause.HandledFaultCancellation,
            isCancellationCleanup: false);

    private static void Begin(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, int index, PendingFailure? failure, CancellationUnwindCause cause, bool isCancellationCleanup)
    {
        var owner = continuation.Stack[index];
        var state = owner.CancellationState!;

        checkpoint.Trace.DropFrames(continuation, index + 1,
            continuation.ItemUnwindCause ?? (failure == null ? ItemEndCause.Cancelled : ItemEndCause.UnwoundByOuterHandler), failure);

        state.Phase = CancellationScopePhase.Handler;
        state.Cause = cause;
        state.Failure = failure;
        state.IsCancellationCleanup = isCancellationCleanup;
        continuation.Previous = state.EntryPrevious;
        continuation.RuntimePrevious = state.RuntimeEntryPrevious;
        continuation.Status = ContinuationStatus.Active;
        checkpoint.Touch(continuation);
    }

    public static int FaultBoundary(ContinuationState continuation, PendingFailure failure, int minimum)
    {
        if (failure.Route == null || failure.Route.ContinuationId != continuation.Id)
            return -1;

        var target = failure.Route.ContinuationId == continuation.Id
            ? continuation.Stack.FindIndex(frame => frame.ActivationId == failure.Route.ActivationId)
            : -1;

        for (var index = continuation.Stack.Count - 1; index > Math.Max(minimum, target); index--)
            if (continuation.Stack[index].CancellationState is { Phase: CancellationScopePhase.Body })
                return index;

        return -1;
    }

    public static FailureHandlerRoute? FindRoute(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, PendingFailure failure)
    {
        var errorType = SerializedException.CatchType(failure.ForPropagation(checkpoint));
        var unfinishedScope = false;

        while (true)
        {
            for (var index = continuation.Stack.Count - 1; index >= 0; index--)
            {
                var owner = continuation.Stack[index];
                unfinishedScope |= owner.CancellationState is { Phase: CancellationScopePhase.Body };

                if (failure.ScopeBoundary?.Contains(continuation, index) == true)
                    continue;

                if (owner.TryState?.Phase != TryPhase.Try && owner.SagaState?.Phase != SagaPhase.Forward)
                    continue;

                var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
                var selected = step.CatchBlocks.FindIndex(handler => handler.ExceptionType?.IsAssignableFrom(errorType) == true);

                if (selected >= 0)
                    return !unfinishedScope
                        ? null
                        : new FailureHandlerRoute
                        {
                            ContinuationId = continuation.Id, ActivationId = owner.ActivationId,
                            StepId = scopes.Id(step), HandlerIndex = selected, HasCancellationScopes = unfinishedScope,
                            SagaRestart = unfinishedScope && owner.SagaState is { } saga
                                ? SagaScopeTransitions.RecordFailurePoint(scopes, continuation, index, saga)
                                : null
                        };
            }

            if (continuation.ParentJoinId == null)
                return null;

            continuation = checkpoint.ForkParent(continuation);
        }
    }
}