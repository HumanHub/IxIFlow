namespace IxIFlow.Core.Runtime;

internal sealed record CompensationInputs(object? PreviousStep, object? PreviousCompensation);

/// <summary>
/// Tracks committed saga activities and drives their compensations in reverse order.
/// </summary>
internal static class SagaScopeTransitions
{
    public static void Enter(WorkflowScopeCatalog scopes, WorkflowStep step,
        ScopePosition owner, ContinuationState continuation, IServiceProvider services)
    {
        var state = owner.SagaState ??= new SagaScopeState();
        if (state.Phase == SagaPhase.Forward)
        {
            continuation.Stack.Add(new ScopePosition
            {
                ScopeId = scopes.SagaScope(step),
                EntryPrevious = continuation.Previous,
                RuntimeEntryPrevious = continuation.RuntimePrevious,
                RestorePreviousOnExit = true
            });
            return;
        }

        if (state.CompensationCursor < state.CompensationFloor)
        {
            var error = state.GetError();
            var compensationErrors = state.CompensationErrors;
            var isCancellationCleanup = state.IsCancellationCleanup;
            if (compensationErrors.Count == 0 && !isCancellationCleanup && state.ErrorAction is { } action)
            {
                if (action == SagaContinuationAction.Continue)
                {
                    owner.SagaState = null;
                    owner.NextStepIndex++;
                    return;
                }
                if (action == SagaContinuationAction.Retry)
                {
                    if (state.RetryCount >= state.MaximumRetries)
                        throw new InvalidOperationException("Saga retry exhausted", error);
                    state.RetryCount++;
                    state.AcceptedWaitCursor = 0;
                    state.CompletedSteps.Clear();
                    state.CompensationErrors.Clear();
                    state.Error = null;
                    state.RuntimeError = null;
                    state.Phase = SagaPhase.Forward;
                    state.CompensationCursor = -1;
                    state.CompensationFloor = 0;
                    Enter(scopes, step, owner, continuation, services);
                    return;
                }
            }
            owner.SagaState = null;
            owner.NextStepIndex++;
            if (compensationErrors.Count > 0)
                throw new InvalidOperationException(
                    $"Saga compensation failed: {string.Join("; ", compensationErrors)}", error);
            if (state.ErrorAction == SagaContinuationAction.Terminate && error != null)
                throw new SagaTerminatedException("Error policy requested termination", error);
            if (error != null)
                throw error;
            if (isCancellationCleanup)
                continuation.Status = ContinuationStatus.Cancelling;
            return;
        }

        var completed = state.CompletedSteps[state.CompensationCursor];
        state.PreviousCompensation = null;
        var prior = continuation.Previous;
        var runtimePrior = continuation.RuntimePrevious;
        continuation.SetPrevious(completed.Output?.Read(services));
        continuation.Stack.Add(new ScopePosition
        {
            ScopeId = scopes.CompensationScope(step, completed.StepIndex),
            EntryPrevious = prior,
            RuntimeEntryPrevious = runtimePrior,
            RestorePreviousOnExit = true
        });
    }

    public static void CompleteForwardActivity(WorkflowScopeCatalog scopes,
        ContinuationState continuation, WorkflowStep activity, object? output,
        IServiceProvider services)
    {
        if (!IsForwardActivity(scopes, continuation, activity))
            return;
        var owner = continuation.Stack[^2];
        owner.SagaState!.CompletedSteps.Add(new SagaCompletedStep
        {
            StepIndex = continuation.Stack[^1].NextStepIndex,
            Output = SerializedValue.From(output),
            Previous = SerializedValue.From(continuation.GetPrevious(services))
        });
    }

    public static void CompleteCompensationActivity(WorkflowScopeCatalog scopes,
        ContinuationState continuation, object? output)
    {
        if (continuation.Stack.Count < 2)
            return;
        var owner = continuation.Stack[^2];
        var state = owner.SagaState;
        if (state?.Phase != SagaPhase.Compensating || state.CompensationCursor < 0)
            return;
        var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        if (continuation.Stack[^1].ScopeId == scopes.CompensationScope(saga,
                state.CompletedSteps[state.CompensationCursor].StepIndex))
            state.PreviousCompensation = SerializedValue.From(output);
    }

    public static bool IsForwardActivity(WorkflowScopeCatalog scopes,
        ContinuationState continuation, WorkflowStep activity)
    {
        if (continuation.Stack.Count < 2 || !activity.StepMetadata.ContainsKey("IsSagaStep"))
            return false;
        var owner = continuation.Stack[^2];
        if (owner.SagaState?.Phase != SagaPhase.Forward)
            return false;
        var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        return saga.StepType == WorkflowStepType.Saga &&
            continuation.Stack[^1].ScopeId == scopes.SagaScope(saga);
    }

    public static IDictionary<string, object>? ActivityMetadata(WorkflowScopeCatalog scopes,
        ContinuationState continuation, WorkflowStep activity)
    {
        if (!IsForwardActivity(scopes, continuation, activity))
            return null;
        var state = continuation.Stack[^2].SagaState!;
        var position = continuation.Stack[^1];
        position.StepRetryCounts.TryGetValue(position.NextStepIndex, out var stepAttempt);
        var metadata = new Dictionary<string, object>
        {
            ["Saga:CurrentAttempt"] = state.RetryCount,
            ["CurrentAttempt"] = stepAttempt
        };
        if (activity.StepMetadata.TryGetValue("StepErrorHandlers", out var value) &&
            value is List<StepErrorHandlerInfo> handlers)
            metadata["MaxAttempts"] = handlers.FirstOrDefault(handler =>
                handler.HandlerAction == StepErrorAction.Retry)?.RetryPolicy?.MaximumAttempts ?? 0;
        return metadata;
    }

    public static bool HandleStepFailure(WorkflowScopeCatalog scopes,
        ContinuationState continuation, Exception error)
    {
        if (continuation.Stack.Count < 2)
            return false;
        var position = continuation.Stack[^1];
        var step = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);
        if (step?.StepType != WorkflowStepType.Activity ||
            !IsForwardActivity(scopes, continuation, step) ||
            !step.StepMetadata.TryGetValue("StepErrorHandlers", out var value) ||
            value is not List<StepErrorHandlerInfo> handlers)
            return false;
        var handler = handlers.FirstOrDefault(candidate =>
            candidate.ExceptionType.IsAssignableFrom(error.GetType()));
        if (handler == null)
            return false;
        if (handler.HandlerAction == StepErrorAction.Ignore)
        {
            position.StepRetryCounts.Remove(position.NextStepIndex);
            position.NextStepIndex++;
            continuation.SetPrevious(null);
            return true;
        }
        if (handler.HandlerAction != StepErrorAction.Retry)
            return false;
        position.StepRetryCounts.TryGetValue(position.NextStepIndex, out var attempts);
        if (attempts >= (handler.RetryPolicy?.MaximumAttempts ?? 0))
            return false;
        position.StepRetryCounts[position.NextStepIndex] = attempts + 1;
        return true;
    }

    public static void RecordAcceptedWait(ContinuationState continuation, string stepId,
        object @event)
    {
        var state = ForwardState(continuation);
        if (state == null)
            return;
        state.AcceptedWaits.Add(new SagaAcceptedWait
        {
            StepId = stepId,
            Event = SerializedValue.From(@event)!
        });
    }

    public static bool TryReuseAcceptedWait(ContinuationState continuation, string stepId,
        IServiceProvider services, out object? @event)
    {
        @event = null;
        var state = ForwardState(continuation);
        if (state is not { RetryCount: > 0 } ||
            state.AcceptedWaitCursor >= state.AcceptedWaits.Count)
            return false;
        var saved = state.AcceptedWaits[state.AcceptedWaitCursor];
        if (saved.StepId != stepId)
            return false;
        state.AcceptedWaitCursor++;
        @event = saved.Event.Read(services);
        return true;
    }

    private static SagaScopeState? ForwardState(ContinuationState continuation) =>
        continuation.Stack.Select(frame => frame.SagaState)
            .LastOrDefault(state => state?.Phase == SagaPhase.Forward);

    public static CompensationInputs? PrepareCompensationActivity(WorkflowScopeCatalog scopes,
        ContinuationState continuation, IServiceProvider services)
    {
        if (continuation.Stack.Count < 2)
            return null;
        var owner = continuation.Stack[^2];
        var state = owner.SagaState;
        if (state?.Phase != SagaPhase.Compensating || state.CompensationCursor < 0)
            return null;
        var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var completed = state.CompletedSteps[state.CompensationCursor];
        if (continuation.Stack[^1].ScopeId != scopes.CompensationScope(saga, completed.StepIndex))
            return null;
        continuation.SetPrevious(completed.Output?.Read(services));
        return new CompensationInputs(completed.Previous?.Read(services),
            state.PreviousCompensation?.Read(services));
    }

    public static bool Exit(WorkflowScopeCatalog scopes, ScopePosition owner,
        ScopePosition child, WorkflowStep step, ContinuationState continuation)
    {
        var state = owner.SagaState;
        if (step.StepType != WorkflowStepType.Saga || state == null)
            return false;
        if (state.Phase == SagaPhase.Forward && child.ScopeId == scopes.SagaScope(step))
        {
            owner.SagaState = null;
            owner.NextStepIndex++;
            return true;
        }
        if (state.Phase != SagaPhase.Compensating || state.CompensationCursor < state.CompensationFloor ||
            child.ScopeId != scopes.CompensationScope(step,
                state.CompletedSteps[state.CompensationCursor].StepIndex))
            return false;
        state.CompensationCursor--;
        return true;
    }

    public static int HandlerIndex(ContinuationState continuation)
    {
        for (var index = continuation.Stack.Count - 1; index >= 0; index--)
            if (continuation.Stack[index].SagaState?.Phase == SagaPhase.Forward)
                return index;
        return -1;
    }

    public static void Capture(WorkflowScopeCatalog scopes, ContinuationState continuation,
        int index, Exception error)
    {
        var owner = continuation.Stack[index];
        var state = owner.SagaState!;
        var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var handlerIndex = saga.CatchBlocks.FindIndex(handler =>
            handler.ExceptionType?.IsAssignableFrom(error.GetType()) == true);
        state.ErrorHandlerIndex = handlerIndex;
        var configuration = handlerIndex < 0 ? null :
            saga.CatchBlocks[handlerIndex].StepMetadata.TryGetValue("SagaErrorConfig", out var value)
                ? value as SagaErrorConfiguration
                : null;
        state.ErrorAction = configuration?.ContinuationAction;
        state.MaximumRetries = configuration?.RetryPolicy?.MaximumAttempts ?? 0;
        state.CompensationFloor = 0;
        if (configuration?.CompensationStrategy == CompensationStrategy.None)
            state.CompensationFloor = state.CompletedSteps.Count;
        else if (configuration?.CompensationStrategy == CompensationStrategy.CompensateUpTo &&
                 configuration.CompensationTargetType != null)
        {
            var target = state.CompletedSteps.FindLastIndex(completed =>
                scopes.Steps(scopes.SagaScope(saga))[completed.StepIndex].ActivityType ==
                configuration.CompensationTargetType);
            if (target >= 0)
                state.CompensationFloor = target;
        }
        continuation.Stack.RemoveRange(index + 1, continuation.Stack.Count - index - 1);
        state.SetError(error);
        state.Phase = SagaPhase.Compensating;
        state.CompensationCursor = state.CompletedSteps.Count - 1;
    }

    public static bool SkipFailedCompensation(ContinuationState continuation, Exception error)
    {
        if (continuation.Stack.Count < 2)
            return false;
        var owner = continuation.Stack[^2];
        var state = owner.SagaState;
        if (state?.Phase != SagaPhase.Compensating)
            return false;
        state.CompensationErrors.Add(error.Message);
        state.PreviousCompensation = null;
        continuation.Stack[^1].NextStepIndex++;
        return true;
    }

    public static bool IsInsideCompensation(ContinuationState continuation) =>
        continuation.Stack.Any(frame => frame.SagaState?.Phase == SagaPhase.Compensating);

    public static void PreserveCompensation(ContinuationState continuation)
    {
        var state = continuation.Stack.Select(frame => frame.SagaState)
            .FirstOrDefault(item => item?.Phase == SagaPhase.Compensating);
        if (state != null)
            state.IsCancellationCleanup = true;
    }

    public static bool BeginCancellation(ContinuationState continuation, int index)
    {
        var state = continuation.Stack[index].SagaState;
        if (state?.Phase != SagaPhase.Forward)
            return false;
        continuation.Stack.RemoveRange(index + 1, continuation.Stack.Count - index - 1);
        state.Phase = SagaPhase.Compensating;
        state.CompensationCursor = state.CompletedSteps.Count - 1;
        state.IsCancellationCleanup = true;
        continuation.Status = ContinuationStatus.Active;
        return true;
    }
}
