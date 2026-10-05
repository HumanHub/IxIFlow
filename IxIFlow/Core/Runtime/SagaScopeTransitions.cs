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

        if (state.CompensationCursor < 0)
        {
            var error = state.GetError();
            var compensationErrors = state.CompensationErrors;
            var isCancellationCleanup = state.IsCancellationCleanup;
            owner.SagaState = null;
            owner.NextStepIndex++;
            if (compensationErrors.Count > 0)
                throw new InvalidOperationException(
                    $"Saga compensation failed: {string.Join("; ", compensationErrors)}", error);
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
        if (state.Phase != SagaPhase.Compensating || state.CompensationCursor < 0 ||
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

    public static void Capture(ContinuationState continuation, int index, Exception error)
    {
        var owner = continuation.Stack[index];
        var state = owner.SagaState!;
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
