namespace IxIFlow.Core.Runtime;

/// <summary>
/// Moves a continuation through Try, Catch, and Finally without losing its phase at a wait.
/// </summary>
internal static class TryScopeTransitions
{
    public static void Enter(WorkflowScopeCatalog scopes, WorkflowStep step,
        ScopePosition owner, ContinuationState continuation)
    {
        var state = owner.TryState ??= new TryScopeState
        {
            EntryPrevious = continuation.Previous,
            RuntimeEntryPrevious = continuation.RuntimePrevious
        };
        var scopeId = state.Phase switch
        {
            TryPhase.Try => scopes.SequenceScope(step),
            TryPhase.Catch => scopes.CatchBodyScope(step.CatchBlocks[state.CatchIndex]),
            TryPhase.Finally => scopes.FinallyScope(step),
            _ => throw new ArgumentOutOfRangeException(nameof(state.Phase))
        };
        continuation.Stack.Add(new ScopePosition
        {
            ScopeId = scopeId,
            EntryPrevious = continuation.Previous,
            RuntimeEntryPrevious = continuation.RuntimePrevious,
            RestorePreviousOnExit = true
        });
    }

    public static bool Exit(WorkflowScopeCatalog scopes, ScopePosition owner,
        ScopePosition child, WorkflowStep step, ContinuationState continuation)
    {
        var state = owner.TryState;
        if (step.StepType != WorkflowStepType.TryCatch || state == null)
            return false;

        var expectedScope = state.Phase switch
        {
            TryPhase.Try => scopes.SequenceScope(step),
            TryPhase.Catch => scopes.CatchBodyScope(step.CatchBlocks[state.CatchIndex]),
            TryPhase.Finally => scopes.FinallyScope(step),
            _ => throw new ArgumentOutOfRangeException(nameof(state.Phase))
        };
        if (child.ScopeId != expectedScope)
            return false;

        if (state.Phase == TryPhase.Finally)
        {
            var unhandled = state.GetError();
            var isCancellationCleanup = state.IsCancellationCleanup;
            owner.TryState = null;
            owner.NextStepIndex++;
            if (isCancellationCleanup)
            {
                ContinueCancellation(scopes, continuation);
                return true;
            }
            if (unhandled != null)
                throw unhandled;
            return true;
        }

        if (state.Phase == TryPhase.Catch)
        {
            state.Error = null;
            state.RuntimeError = null;
        }
        if (step.FinallySteps.Count > 0)
            state.Phase = TryPhase.Finally;
        else
        {
            owner.TryState = null;
            owner.NextStepIndex++;
        }
        return true;
    }

    public static bool Capture(WorkflowScopeCatalog scopes, ContinuationState continuation, Exception error)
    {
        var index = HandlerIndex(scopes, continuation, error);
        if (index < 0)
            return false;
        var owner = continuation.Stack[index];
        var state = owner.TryState!;
        var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var catchIndex = FindCatchIndex(step, state, error);
        var selectedCatch = catchIndex >= 0 ? step.CatchBlocks[catchIndex] : null;
        SerializedException savedError;
        if (selectedCatch == null)
            savedError = SerializedException.From(error);
        else
        {
            try
            {
                savedError = SerializedException.FromForCatch(error,
                    selectedCatch.ExceptionType!, selectedCatch.FaultType!);
            }
            catch (Exception projectionError)
            {
                // The handler cannot receive the fault it declared. Finish this Try's
                // cleanup, then let an outer handler see the capture failure.
                error = new InvalidOperationException(
                    $"Fault projection for '{error.GetType().Name}' failed: " +
                    projectionError.GetBaseException().Message);
                savedError = SerializedException.From(error);
                catchIndex = -1;
            }
        }
        continuation.Stack.RemoveRange(index + 1, continuation.Stack.Count - index - 1);
        continuation.Previous = state.EntryPrevious;
        continuation.RuntimePrevious = state.RuntimeEntryPrevious;
        state.RuntimeError = error;
        state.Error = savedError;
        state.CatchIndex = catchIndex;
        state.Phase = catchIndex >= 0 ? TryPhase.Catch : TryPhase.Finally;
        return true;
    }

    public static int HandlerIndex(WorkflowScopeCatalog scopes, ContinuationState continuation, Exception error)
    {
        for (var index = continuation.Stack.Count - 1; index >= 0; index--)
        {
            var owner = continuation.Stack[index];
            var state = owner.TryState;
            if (state == null || state.Phase == TryPhase.Finally)
                continue;

            var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            if (step.StepType != WorkflowStepType.TryCatch)
                continue;

            var catchIndex = FindCatchIndex(step, state, error);
            if (catchIndex < 0 && step.FinallySteps.Count == 0)
                continue;
            return index;
        }
        return -1;
    }

    private static int FindCatchIndex(WorkflowStep step, TryScopeState state, Exception error)
    {
        var first = state.Phase == TryPhase.Catch ? state.CatchIndex + 1 : 0;
        for (var index = first; index < step.CatchBlocks.Count; index++)
            if (step.CatchBlocks[index].ExceptionType?.IsAssignableFrom(error.GetType()) == true)
                return index;
        return -1;
    }

    public static object? CatchFault(ExecutionCheckpoint checkpoint, ContinuationState continuation,
        IServiceProvider services)
    {
        while (true)
        {
            for (var index = continuation.Stack.Count - 1; index >= 0; index--)
            {
                var state = continuation.Stack[index].TryState;
                if (state?.Phase == TryPhase.Catch)
                    return state.Error?.Fault?.Read(services);
                var saga = continuation.Stack[index].SagaState;
                if (saga?.Phase == SagaPhase.Compensating && saga.ErrorHandlerStarted)
                    return saga.Error?.Fault?.Read(services);
            }
            if (continuation.ParentJoinId == null)
                return null;
            var join = checkpoint.Joins.Single(item => item.Id == continuation.ParentJoinId);
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        }
    }

    public static void EnsureCanWait(ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        RejectUnrestorable(checkpoint.UnhandledError);
        while (true)
        {
            foreach (var frame in continuation.Stack)
            {
                RejectUnrestorable(frame.TryState?.Error,
                    frame.TryState?.Phase == TryPhase.Catch);
                var saga = frame.SagaState;
                RejectUnrestorable(saga?.Error,
                    saga?.Phase == SagaPhase.Compensating && saga.ErrorHandlerStarted &&
                    saga.ErrorAction != null);
            }
            if (continuation.ParentJoinId == null)
                return;
            var join = checkpoint.Joins.Single(item => item.Id == continuation.ParentJoinId);
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        }
    }

    private static void RejectUnrestorable(SerializedException? error, bool caught = false)
    {
        if (error?.IsRestorable == false && !(caught && error.Fault != null))
            throw new InvalidOperationException(
                $"Exception '{error.Type}' cannot be restored after a wait: " +
                (error.RestorationFailureReason ?? "constructor values were unavailable"));
    }

    public static bool IsInsideFinally(ContinuationState continuation) =>
        continuation.Stack.Any(frame => frame.TryState?.Phase == TryPhase.Finally);

    public static void AbortFailedFinally(ContinuationState continuation)
    {
        var index = continuation.Stack.FindIndex(frame => frame.TryState?.Phase == TryPhase.Finally);
        if (index < 0)
            return;

        continuation.Stack.RemoveRange(index + 1, continuation.Stack.Count - index - 1);
        var owner = continuation.Stack[index];
        owner.TryState = null;
        owner.NextStepIndex++;
    }

    public static void PreserveExistingFinally(ContinuationState continuation)
    {
        var outermost = continuation.Stack
            .Select(frame => frame.TryState)
            .FirstOrDefault(state => state?.Phase == TryPhase.Finally);
        if (outermost != null)
            outermost.IsCancellationCleanup = true;
    }

    public static void ContinueCancellation(WorkflowScopeCatalog scopes, ContinuationState continuation)
    {
        for (var index = continuation.Stack.Count - 1; index >= 0; index--)
        {
            var owner = continuation.Stack[index];
            if (SagaScopeTransitions.BeginCancellation(continuation, index))
                return;
            var state = owner.TryState;
            if (state == null || state.Phase == TryPhase.Finally)
                continue;
            var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            if (step.StepType != WorkflowStepType.TryCatch || step.FinallySteps.Count == 0)
                continue;

            continuation.Stack.RemoveRange(index + 1, continuation.Stack.Count - index - 1);
            continuation.Previous = state.EntryPrevious;
            continuation.RuntimePrevious = state.RuntimeEntryPrevious;
            state.Error = null;
            state.RuntimeError = null;
            state.Phase = TryPhase.Finally;
            state.IsCancellationCleanup = true;
            continuation.Status = ContinuationStatus.Active;
            return;
        }
        continuation.CancellationUnwind = false;
        continuation.Status = ContinuationStatus.Cancelled;
    }
}
