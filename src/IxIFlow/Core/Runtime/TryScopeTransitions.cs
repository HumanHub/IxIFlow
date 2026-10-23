namespace IxIFlow.Core.Runtime;

/// <summary>
/// Moves a continuation through Try, Catch, and Finally without losing its phase at a wait.
/// </summary>
internal static class TryScopeTransitions
{
    public static void Enter(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, WorkflowStep step,
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
        checkpoint.Trace.Record(state.Phase switch
        {
            TryPhase.Catch => TraceEntryType.CatchEntered,
            TryPhase.Finally => TraceEntryType.FinallyEntered,
            _ => TraceEntryType.StepEntered
        }, step, continuation, state.Phase == TryPhase.Catch ? state.CatchIndex : null, scopeId);
        continuation.Stack.Add(new ScopePosition
        {
            ScopeId = scopeId,
            EntryPrevious = continuation.Previous,
            RuntimeEntryPrevious = continuation.RuntimePrevious,
            RestorePreviousOnExit = true
        });
    }

    public static bool Exit(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ScopePosition owner,
        ScopePosition child, WorkflowStep step, ContinuationState continuation, out PendingFailure? failure)
    {
        failure = null;
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
            var unhandled = state.Error;
            var isCancellationCleanup = state.IsCancellationCleanup;
            owner.TryState = null;
            owner.NextStepIndex++;
            if (isCancellationCleanup)
            {
                ContinueCancellation(scopes, checkpoint, continuation);
                return true;
            }
            failure = unhandled;
            return true;
        }

        if (state.Phase == TryPhase.Catch)
        {
            state.Error = null;
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

    public static bool Capture(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState continuation, PendingFailure failure)
    {
        var error = failure.ForPropagation(checkpoint);
        var index = HandlerIndex(scopes, checkpoint, continuation, failure);
        if (index < 0)
            return false;
        var owner = continuation.Stack[index];
        var state = owner.TryState!;
        var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var catchIndex = FindCatchIndex(step, state, error, failure.ScopeBoundary?.Contains(continuation, continuation.Stack.IndexOf(owner)) == true);
        var selectedCatch = catchIndex >= 0 ? step.CatchBlocks[catchIndex] : null;
        SerializedException savedError;
        if (selectedCatch == null)
            savedError = SerializedException.From(error, checkpoint);
        else
        {
            try
            {
                savedError = SerializedException.FromForCatch(error,
                    selectedCatch.ExceptionType!, selectedCatch.FaultType!, checkpoint);
            }
            catch (Exception projectionError)
            {
                // The handler cannot receive the fault it declared. Finish this Try's
                // cleanup, then let an outer handler see the capture failure.
                error = FaultCapture.ProjectionFailure(error, projectionError);
                failure = PendingFailure.Capture(error, scopes, checkpoint, continuation, newOccurrence: true);
                savedError = failure.Error;
                catchIndex = -1;
            }
        }
        checkpoint.Trace.DropFrames(continuation, index + 1, ItemEndCause.UnwoundByOuterHandler, failure);
        continuation.Previous = state.EntryPrevious;
        continuation.RuntimePrevious = state.RuntimeEntryPrevious;

        state.Error = new PendingFailure { Error = savedError, ScopeBoundary = failure.ScopeBoundary };
        state.CatchIndex = catchIndex;
        state.Phase = catchIndex >= 0 ? TryPhase.Catch : TryPhase.Finally;
        return true;
    }

    public static int HandlerIndex(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState continuation, PendingFailure failure)
    {
        var error = failure.ForPropagation(checkpoint);
        for (var index = continuation.Stack.Count - 1; index >= 0; index--)
        {
            var owner = continuation.Stack[index];
            var state = owner.TryState;
            if (state == null || state.Phase == TryPhase.Finally)
                continue;

            var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            if (step.StepType != WorkflowStepType.TryCatch)
                continue;

            var catchIndex = FindCatchIndex(step, state, error, failure.ScopeBoundary?.Contains(continuation, continuation.Stack.IndexOf(owner)) == true);
            if (catchIndex < 0 && step.FinallySteps.Count == 0)
                continue;
            return index;
        }
        return -1;
    }

    private static int FindCatchIndex(WorkflowStep step, TryScopeState state, Exception error, bool suppressed)
    {
        if (suppressed || state.Phase != TryPhase.Try)
            return -1;
        var errorType = SerializedException.CatchType(error);
        for (var index = 0; index < step.CatchBlocks.Count; index++)
            if (step.CatchBlocks[index].ExceptionType?.IsAssignableFrom(errorType) == true)
                return index;
        return -1;
    }

    internal static PendingFailure RethrowError(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep step)
    {
        while (true)
        {
            foreach (var frame in continuation.Stack.AsEnumerable().Reverse())
                if (frame.TryState is { Phase: TryPhase.Catch } state &&
                    scopes.Id(scopes.Steps(frame.ScopeId)[frame.NextStepIndex].CatchBlocks[state.CatchIndex]) == step.RethrowOwnerId)
                    return state.Error ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The bound Catch has no saved error");
            if (continuation.ParentJoinId == null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The bound Catch occurrence is unavailable");
            continuation = checkpoint.ForkParent(continuation);
        }
    }

    public static object? CatchFault(ExecutionCheckpoint checkpoint, ContinuationState continuation,
        IServiceProvider services, WorkflowStep? expressionStep = null, WorkflowScopeCatalog? scopes = null)
    {
        var expected = expressionStep?.LexicalFaultOwnerId;
        while (true)
        {
            for (var index = continuation.Stack.Count - 1; index >= 0; index--)
            {
                var frame = continuation.Stack[index];
                var state = frame.TryState;
                if (state?.Phase == TryPhase.Catch && (expected == null ||
                    scopes!.Id(scopes.Steps(frame.ScopeId)[frame.NextStepIndex].CatchBlocks[state.CatchIndex]) == expected))
                    return state.Error?.Error.Fault?.Read(services);
                var saga = frame.SagaState;
                if (saga?.Phase == SagaPhase.Compensating && saga.ErrorHandlerStarted && (expected == null ||
                    scopes!.Id(scopes.Steps(frame.ScopeId)[frame.NextStepIndex].CatchBlocks[saga.ErrorHandlerIndex]) == expected))
                    return saga.Error?.Error.Fault?.Read(services);
            }
            if (continuation.ParentJoinId == null) return null;
            var join = checkpoint.Joins.Single(item => item.Id == continuation.ParentJoinId);
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        }
    }

    public static void EnsureCanWait(ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        RejectUnrestorable(checkpoint.UnhandledError?.Error);
        while (true)
        {
            RejectUnrestorable(continuation.DeferredFailure?.Error);
            foreach (var frame in continuation.Stack)
            {
                var tryState = frame.TryState;
                RejectUnrestorable(tryState?.Error?.Error);
                var saga = frame.SagaState;
                RejectUnrestorable(saga?.Error?.Error);
                RejectUnrestorable(saga?.HandlerError?.Error);
            }
            if (continuation.ParentJoinId == null)
                return;
            var join = checkpoint.Joins.Single(item => item.Id == continuation.ParentJoinId);
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        }
    }

    private static void RejectUnrestorable(SerializedException? error)
    {
        if (error?.IsRestorable == false && !error.HasDurableProperties)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Exception '{error.Type}' cannot be restored after a wait: " +
                (error.RestorationFailureReason ?? "constructor values were unavailable"));
    }

    public static bool IsInsideFinally(ContinuationState continuation) =>
        continuation.Stack.Any(frame => frame.TryState?.Phase == TryPhase.Finally);

    public static void AbortFailedFinally(ExecutionCheckpoint checkpoint, ContinuationState continuation, PendingFailure? failure = null)
    {
        var index = continuation.Stack.FindIndex(frame => frame.TryState?.Phase == TryPhase.Finally);
        if (index < 0)
            return;

        checkpoint.Trace.DropFrames(continuation, index + 1, ItemEndCause.Failed, failure);
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

    public static void ContinueCancellation(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        for (var index = continuation.Stack.Count - 1; index >= 0; index--)
        {
            var owner = continuation.Stack[index];
            if (SagaScopeTransitions.BeginCancellation(checkpoint, continuation, index) ||
                SagaScopeTransitions.CancelRetryHandler(scopes, checkpoint, continuation, index))
                return;
            var state = owner.TryState;
            if (state == null || state.Phase == TryPhase.Finally)
                continue;
            var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            if (step.StepType != WorkflowStepType.TryCatch || step.FinallySteps.Count == 0)
                continue;

            checkpoint.Trace.DropFrames(continuation, index + 1, continuation.CancellationUnwind
            ? continuation.ItemUnwindCause ?? ItemEndCause.Cancelled : ItemEndCause.UnwoundByOuterHandler, continuation.UnwindFailure);
            continuation.Previous = state.EntryPrevious;
            continuation.RuntimePrevious = state.RuntimeEntryPrevious;
            state.Error = null;
            state.Phase = TryPhase.Finally;
            state.IsCancellationCleanup = true;
            continuation.Status = ContinuationStatus.Active;
            return;
        }
        checkpoint.Trace.DropFrames(continuation, 0, continuation.ItemUnwindCause ?? ItemEndCause.Cancelled, continuation.UnwindFailure);
        continuation.UnwindFailure = null;
        continuation.CancellationUnwind = false;
        continuation.Status = ContinuationStatus.Cancelled;
    }
}
