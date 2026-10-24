namespace IxIFlow.Core.Runtime;

internal sealed record CompensationInputs(object? PreviousStep, object? PreviousCompensation, object? Reply,
    bool Answered);

/// <summary>
/// Tracks committed saga activities and drives their compensations in reverse order.
/// </summary>
internal static class SagaScopeTransitions
{
    public static PendingFailure? Enter(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, WorkflowStep step,
        ScopePosition owner, ContinuationState continuation, IServiceProvider services, DateTime now)
    {
        var state = owner.SagaState ??= new SagaScopeState();
        if (state.Phase == SagaPhase.Forward)
        {
            checkpoint.Trace.Record(TraceEntryType.StepEntered, step, continuation);
            continuation.Stack.Add(new ScopePosition
            {
                ScopeId = scopes.SagaScope(step),
                EntryPrevious = continuation.Previous,
                RuntimeEntryPrevious = continuation.RuntimePrevious,
                RestorePreviousOnExit = true
            });
            return null;
        }

        if (state.CompensationCursor < state.CompensationFloor)
        {
            if (!state.CancellationRequested && state.ErrorHandlerIndex >= 0 &&
                !state.ErrorHandlerStarted)
            {
                checkpoint.Trace.Record(TraceEntryType.CatchEntered, step, continuation, state.ErrorHandlerIndex,
                    scopes.CatchBodyScope(step.CatchBlocks[state.ErrorHandlerIndex]));
                state.ErrorHandlerStarted = true;
                continuation.Stack.Add(new ScopePosition
                {
                    ScopeId = scopes.CatchBodyScope(step.CatchBlocks[state.ErrorHandlerIndex]),
                    EntryPrevious = continuation.Previous,
                    RuntimeEntryPrevious = continuation.RuntimePrevious,
                    RestorePreviousOnExit = true
                });
                return null;
            }

            // Steps kept for a retry are only its restart point: a saga that cannot retry after all undoes
            // them too before it ends (D-36).
            if (state.ErrorAction == SagaContinuationAction.Retry && state.CompensationFloor > 0 &&
                (state.CompensationErrors.Count > 0 || state.CancellationRequested ||
                 state.RetryCount >= state.MaximumRetries))
            {
                GiveUp(state);
                return null;
            }
            // A retry handler whose own steps failed cannot retry: the saga ends as terminated.
            if (state.HandlerError is { } handlerError)
            {
                owner.SagaState = null;
                owner.NextStepIndex++;
                throw Terminated($"the error handler failed: {handlerError.Message}", handlerError.ForPropagation(checkpoint));
            }

            var mayContinue = state.ErrorAction == SagaContinuationAction.Continue &&
                (state.CompensationErrors.Count == 0 || state.IgnoreCompensationErrors);
            var error = state.CancellationRequested || mayContinue ? null : state.GetTerminalError(checkpoint);
            var compensationErrors = state.CompensationErrors;
            var isCancellationCleanup = state.IsCancellationCleanup;
            if ((compensationErrors.Count == 0 || mayContinue) &&
                !state.CancellationRequested && state.ErrorAction is { } action)
            {
                if (action == SagaContinuationAction.Continue)
                {
                    owner.SagaState = null;
                    owner.NextStepIndex++;
                    return null;
                }
                if (action == SagaContinuationAction.Retry)
                {
                    if (state.RetryCount >= state.MaximumRetries)
                    {
                        owner.SagaState = null;
                        throw Terminated("retry exhausted", error);
                    }
                    state.RetryCount++;
                    var retryConfiguration = step.CatchBlocks[state.ErrorHandlerIndex]
                        .StepMetadata.TryGetValue("SagaErrorConfig", out var retryValue)
                        ? retryValue as SagaErrorConfiguration
                        : null;
                    continuation.RetryAfterUtc = RetrySchedule.NextAttemptUtc(
                        retryConfiguration?.RetryPolicy, state.RetryCount, now);
                    Restart(scopes, checkpoint, step, owner, continuation, services, now);
                    return null;
                }
            }
            owner.SagaState = null;
            owner.NextStepIndex++;
            if (state.Error?.ScopeBoundary != null) return state.Error;
            if (compensationErrors.Count > 0)
                throw Terminated($"compensation failed: {string.Join("; ", compensationErrors)}", error);
            if (state.ErrorAction == SagaContinuationAction.Terminate && error != null)
                throw new SagaTerminatedException("Error policy requested termination", error, true);
            if (error != null)
            {
                if (state.ErrorHandlerIndex >= 0 && state.ErrorAction != null && state.Error != null)
                    throw error; // The saga created a new failure occurrence.
                return state.Error;
            }
            if (isCancellationCleanup)
                continuation.Status = ContinuationStatus.Cancelling;
            return null;
        }

        var completed = state.CompletedSteps[state.CompensationCursor];
        checkpoint.Trace.Record(TraceEntryType.CompensationStarted, scopes.Step(completed.StepId), continuation,
            scopeId: scopes.CompensationScope(step, scopes.Step(completed.StepId)));
        state.PreviousCompensation = null;
        var prior = continuation.Previous;
        var runtimePrior = continuation.RuntimePrevious;
        continuation.SetPrevious(completed.Output?.Read(services));
        continuation.Stack.Add(new ScopePosition
        {
            ScopeId = scopes.CompensationScope(step, scopes.Step(completed.StepId)),
            EntryPrevious = prior,
            RuntimeEntryPrevious = runtimePrior,
            RestorePreviousOnExit = true
        });
        return null;
    }

    // A saga that cannot recover ends as the saga's own failure, which a surrounding Catch can handle.
    private static SagaTerminatedException Terminated(string reason, Exception? error) =>
        error == null ? new SagaTerminatedException(reason) : new SagaTerminatedException(reason, error);

    /// <summary>
    /// Records a forward activity that returned, where it ran, and for a request its invocation and any answer
    /// already kept for it.
    /// </summary>
    public static void CompleteForwardActivity(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep activity, object? output, IServiceProvider services,
        string? invocationId)
    {
        var owner = ForwardOwner(scopes, continuation, activity);
        if (owner == null)
            return;
        var state = owner.SagaState!;
        RecordFrames(scopes, continuation, continuation.Stack.IndexOf(owner), state);
        var position = continuation.Stack[^1];
        var request = activity.RequestName != null ? invocationId : null;
        var kept = request == null ? null : checkpoint.BoundReplies.FirstOrDefault(reply => reply.InvocationId == request);
        state.CompletedSteps.Add(new SagaCompletedStep
        {
            StepId = scopes.Id(activity),
            Output = SerializedValue.From(output),
            Previous = SerializedValue.From(continuation.GetPrevious(services)),
            FrameId = position.ActivationId,
            StepIndex = position.NextStepIndex,
            InvocationId = request,
            Reply = kept == null ? null : new SagaReply { Event = kept.Event, EventId = kept.EventId, WaitId = kept.WaitId }
        });
    }

    /// <summary>
    /// Records an answer a saga request received, kept for its <c>WaitFor</c> or taken by it, so the request's
    /// compensation can read it and a retry that kept the request can give it to the <c>WaitFor</c> again.
    /// </summary>
    public static void NoteReply(ContinuationState continuation, string? invocationId, SerializedValue reply,
        string eventId, string waitId)
    {
        if (invocationId == null)
            return;
        foreach (var completed in continuation.Stack.Select(frame => frame.SagaState)
                     .SelectMany(state => state?.CompletedSteps ?? []))
            if (completed.InvocationId == invocationId)
                completed.Reply = new SagaReply { Event = reply, EventId = eventId, WaitId = waitId };
    }

    /// <summary>
    /// The request's record when the retried pass reaches its <c>WaitFor</c>: for a request the retry kept, with
    /// the answer it took in an earlier attempt or its timeout; for one that ran again, a record with neither.
    /// </summary>
    public static SagaCompletedStep? KeptRequest(WorkflowScopeCatalog scopes, ContinuationState continuation, WorkflowStep wait)
    {
        var state = ForwardState(continuation);
        if (state == null)
            return null;
        var position = continuation.Stack[^1];
        var request = scopes.Steps(position.ScopeId).Take(position.NextStepIndex)
            .LastOrDefault(step => step.RequestName == wait.ReplyToRequest);
        if (request == null)
            return null;
        var requestId = scopes.Id(request);
        return state.CompletedSteps.LastOrDefault(completed => completed.StepId == requestId);
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
                scopes.Step(state.CompletedSteps[state.CompensationCursor].StepId)))
            state.PreviousCompensation = SerializedValue.From(output);
    }

    internal static ScopePosition? ForwardOwner(WorkflowScopeCatalog scopes,
        ContinuationState continuation, WorkflowStep activity)
    {
        if (!activity.StepMetadata.ContainsKey("IsSagaStep"))
            return null;
        var activityId = scopes.Id(activity);
        for (var index = continuation.Stack.Count - 2; index >= 0; index--)
        {
            var owner = continuation.Stack[index];
            if (owner.SagaState?.Phase != SagaPhase.Forward)
                continue;
            var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            if (saga.StepType == WorkflowStepType.Saga &&
                activityId.StartsWith(scopes.SagaScope(saga) + "/", StringComparison.Ordinal))
                return owner;
        }
        return null;
    }

    public static IDictionary<string, object>? ActivityMetadata(WorkflowScopeCatalog scopes,
        ContinuationState continuation, WorkflowStep activity)
    {
        var owner = ForwardOwner(scopes, continuation, activity);
        if (owner == null)
            return null;
        // The step's own attempts are in IActivityContext.Attempt (D-37); a saga retry is a new invocation.
        return new Dictionary<string, object> { ["Saga:CurrentAttempt"] = owner.SagaState!.RetryCount };
    }

    public static void RecordAcceptedWait(ContinuationState continuation, string stepId,
        object @event)
    {
        var state = ForwardState(continuation);
        if (state == null)
            return;
        // A fresh acceptance is past anything this attempt could replay: what earlier attempts accepted from
        // here on belonged to a path this one left.
        state.AcceptedWaits.RemoveRange(state.AcceptedWaitCursor,
            state.AcceptedWaits.Count - state.AcceptedWaitCursor);
        state.AcceptedWaits.Add(new SagaAcceptedWait
        {
            StepId = stepId,
            Event = SerializedValue.From(@event)!,
            Completed = state.CompletedSteps.Count
        });
        state.AcceptedWaitCursor = state.AcceptedWaits.Count;
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
        // A wait accepted in this wait's own timeout path stays: should the wait time out again, the path
        // replays it.
        if (saved.StepId.StartsWith(stepId + "/", StringComparison.Ordinal))
            return false;
        if (saved.StepId != stepId)
        {
            // The retry took another path; what the earlier attempt accepted from here on belongs to that path.
            state.AcceptedWaits.RemoveRange(state.AcceptedWaitCursor,
                state.AcceptedWaits.Count - state.AcceptedWaitCursor);
            return false;
        }
        state.AcceptedWaitCursor++;
        // Replayed, it belongs where this attempt reached it, for any later restart.
        saved.Completed = state.CompletedSteps.Count;
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
        if (continuation.Stack[^1].ScopeId != scopes.CompensationScope(saga,
                scopes.Step(completed.StepId)))
            return null;
        continuation.SetPrevious(completed.Output?.Read(services));
        return new CompensationInputs(completed.Previous?.Read(services),
            state.PreviousCompensation?.Read(services), completed.Reply?.Event.Read(services),
            completed.Reply != null);
    }

    public static bool Exit(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ScopePosition owner,
        ScopePosition child, WorkflowStep step, ContinuationState continuation)
    {
        var state = owner.SagaState;
        if (step.StepType != WorkflowStepType.Saga || state == null)
            return false;
        if (state.Phase == SagaPhase.Compensating && state.ErrorHandlerStarted &&
            state.ErrorHandlerIndex >= 0 &&
            child.ScopeId == scopes.CatchBodyScope(step.CatchBlocks[state.ErrorHandlerIndex]))
            return true;
        if (state.Phase == SagaPhase.Forward && child.ScopeId == scopes.SagaScope(step))
        {
            owner.SagaState = null;
            owner.NextStepIndex++;
            return true;
        }
        if (state.Phase != SagaPhase.Compensating || state.CompensationCursor < state.CompensationFloor ||
            child.ScopeId != scopes.CompensationScope(step,
                scopes.Step(state.CompletedSteps[state.CompensationCursor].StepId)))
            return false;
        if (!child.CompensationFailed)
            checkpoint.Trace.Record(TraceEntryType.CompensationCompleted, scopes.Step(state.CompletedSteps[state.CompensationCursor].StepId),
            continuation, scopeId: child.ScopeId);
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

    public static void Capture(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, int index, PendingFailure failure)
    {
        var error = failure.ForPropagation(checkpoint);
        var owner = continuation.Stack[index];
        var state = owner.SagaState!;
        if (failure.ScopeBoundary?.Contains(continuation, index) == true)
        { CaptureBounded(scopes, checkpoint, continuation, index, failure); return; }
        var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var handlerIndex = saga.CatchBlocks.FindIndex(handler =>
            handler.ExceptionType?.IsAssignableFrom(SerializedException.CatchType(error)) == true);
        SerializedException savedError;
        if (handlerIndex < 0)
            savedError = SerializedException.From(error, checkpoint);
        else
        {
            try
            {
                savedError = SerializedException.FromForCatch(error,
                    saga.CatchBlocks[handlerIndex].ExceptionType!,
                    saga.CatchBlocks[handlerIndex].FaultType!, checkpoint);
            }
            catch (Exception projectionError)
            {
                error = FaultCapture.ProjectionFailure(error, projectionError);
                failure = PendingFailure.Capture(error, scopes, checkpoint, continuation, newOccurrence: true);
                savedError = failure.Error;
                handlerIndex = -1;
            }
        }
        state.ErrorHandlerIndex = handlerIndex;
        state.ErrorHandlerStarted = false;
        var configuration = handlerIndex < 0 ? null :
            saga.CatchBlocks[handlerIndex].StepMetadata.TryGetValue("SagaErrorConfig", out var value)
                ? value as SagaErrorConfiguration
                : null;
        state.ErrorAction = configuration?.ContinuationAction;
        state.IgnoreCompensationErrors = configuration?.IgnoreCompensationErrors ?? false;
        state.MaximumRetries = configuration?.RetryPolicy?.MaximumAttempts ?? 0;
        // Cleanup retains the original failing pick even after its wrapper's body frames leave.
        var failedAt = failure.Route is { SagaRestart: { } savedPoint } route && route.ActivationId == owner.ActivationId
            ? savedPoint : RecordFailurePoint(scopes, continuation, index, state);
        // A step whose activity returned but whose outputs failed has acted: it is undone, and a retry runs it
        // again (D-36).
        var count = state.CompletedSteps.Count;
        var failingCompleted = count > 0 && state.CompletedSteps[^1].FrameId == failedAt.FrameId &&
                               state.CompletedSteps[^1].StepIndex == failedAt.StepIndex;
        var strategy = configuration?.CompensationStrategy ?? CompensationStrategy.CompensateAll;
        state.CompensationFloor = 0;
        if (strategy == CompensationStrategy.None)
            state.CompensationFloor = failingCompleted ? count - 1 : count;
        var target = -1;
        if (strategy == CompensationStrategy.CompensateUpTo && configuration!.CompensationTargetType != null)
        {
            target = state.CompletedSteps.FindIndex(completed =>
                scopes.Step(completed.StepId).ActivityType == configuration.CompensationTargetType);
            if (target >= 0)
                state.CompensationFloor = target;
        }
        // A retry that is not to come keeps nothing; one that is keeps its requests' waits and answers open.
        // It resumes at the first step undone, T itself when T completed, even as the saga's first step: the
        // saga's start would choose its outcome branches again from data the undo changed.
        var retrying = state.ErrorAction == SagaContinuationAction.Retry && state.RetryCount < state.MaximumRetries;
        if (state.ErrorAction == SagaContinuationAction.Retry && !retrying)
            state.CompensationFloor = 0;
        state.RestartAt = !retrying ? null
            : strategy == CompensationStrategy.None
                ? state.CompensationFloor < count ? PointOf(state.CompletedSteps[state.CompensationFloor]) : failedAt
            : target >= 0 ? PointOf(state.CompletedSteps[target])
            : null;
        state.KeptWaitIds.Clear();
        if (retrying)
            foreach (var request in state.CompletedSteps.Take(state.CompensationFloor)
                         .Select(completed => completed.InvocationId).OfType<string>())
                state.KeptWaitIds.AddRange(checkpoint.Waits.Where(wait => wait.IsReply && wait.InvocationId == request)
                    .Select(wait => wait.Id)
                    .Concat(checkpoint.BoundReplies.Where(reply => reply.InvocationId == request)
                        .Select(reply => reply.WaitId)));
        var forward = continuation.Stack[index + 1];
        checkpoint.Trace.DropFrames(continuation, index + 1, continuation.CancellationUnwind
            ? continuation.ItemUnwindCause ?? ItemEndCause.Cancelled : ItemEndCause.UnwoundByOuterHandler, continuation.UnwindFailure);
        continuation.Previous = forward.EntryPrevious;
        continuation.RuntimePrevious = forward.RuntimeEntryPrevious;
        state.Error = new PendingFailure { Error = savedError, ScopeBoundary = failure.ScopeBoundary, Route = failure.Route, OriginatingFailure = failure.OriginatingFailure };

        state.Phase = SagaPhase.Compensating;
        state.CompensationCursor = state.CompletedSteps.Count - 1;
    }

    public static void CaptureBounded(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState continuation, int index, PendingFailure failure)
    {
        var state = continuation.Stack[index].SagaState!;
        // A scope that still cannot reopen must not restart the compensation that needed it.
        // Keep the first bounded failure and finish the remaining cleanup in reverse order.
        if (state.Phase == SagaPhase.Compensating && state.CompensationCursor >= 0 &&
            index + 1 < continuation.Stack.Count)
        {
            var owner = continuation.Stack[index];
            var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            var completed = scopes.Step(state.CompletedSteps[state.CompensationCursor].StepId);
            var compensationScope = scopes.CompensationScope(saga, completed);
            if (continuation.Stack[index + 1].ScopeId == compensationScope)
            {
                state.CompensationErrors.Add(failure.Error.Message);
                state.PreviousCompensation = null;
                checkpoint.Trace.Record(TraceEntryType.CompensationFailed, completed, continuation,
                    scopeId: compensationScope);
                state.CompensationCursor--;
            }
        }
        if (index + 1 < continuation.Stack.Count)
            checkpoint.Trace.DropFrames(continuation, index + 1, ItemEndCause.UnwoundByOuterHandler, failure);
        if (state.Error?.ScopeBoundary == null) state.Error = failure;
        state.HandlerError = null;
        state.ErrorHandlerIndex = -1;
        state.ErrorAction = null;
        state.ErrorHandlerStarted = false;
        state.CompensationFloor = 0;
        state.RestartAt = null;
        state.KeptWaitIds.Clear();
        if (state.Phase == SagaPhase.Forward) state.CompensationCursor = state.CompletedSteps.Count - 1;
        state.Phase = SagaPhase.Compensating;
    }

    public static bool SkipFailedCompensation(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, Exception error)
    {
        if (continuation.Stack.Count < 2)
            return false;
        var owner = continuation.Stack[^2];
        var state = owner.SagaState;
        if (state?.Phase != SagaPhase.Compensating || state.CompensationCursor < 0)
            return false;
        var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        if (continuation.Stack[^1].ScopeId != scopes.CompensationScope(saga,
                scopes.Step(state.CompletedSteps[state.CompensationCursor].StepId)))
            return false;
        state.CompensationErrors.Add(error.Message);
        state.PreviousCompensation = null;
        checkpoint.Trace.Record(TraceEntryType.CompensationFailed,
            scopes.Step(state.CompletedSteps[state.CompensationCursor].StepId), continuation,
            scopeId: continuation.Stack[^1].ScopeId);
        continuation.Stack[^1].CompensationFailed = true;
        continuation.Stack[^1].NextStepIndex++;
        return true;
    }

    /// <summary>
    /// Whether the continuation runs a saga's cleanup: its compensations, or the steps of a handler that does not
    /// retry. A retry handler's own steps are not cleanup: no retry follows a cancellation, so they are cancelled
    /// like the forward pass, and the saga then undoes what it kept.
    /// </summary>
    public static bool IsInsideCompensation(ContinuationState continuation) =>
        continuation.Stack.Any(frame => IsCompensating(frame.SagaState));

    public static bool IsCompensating(SagaScopeState? state) =>
        state is { Phase: SagaPhase.Compensating } && !RunsRetryHandler(state);

    internal static bool RunsRetryHandler(SagaScopeState state) =>
        state is { Phase: SagaPhase.Compensating, ErrorAction: SagaContinuationAction.Retry, ErrorHandlerStarted: true, HandlerError: null,
            ErrorHandlerIndex: >= 0 } &&
        state.CompensationCursor < state.CompensationFloor;

    /// <summary>
    /// Cancellation unwinding reaches a saga whose retry handler ran, or still runs, its own steps: the
    /// handler's frames go, no retry follows, and the saga undoes the steps it kept before the cancellation goes
    /// on.
    /// </summary>
    public static bool CancelRetryHandler(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, int index)
    {
        var owner = continuation.Stack[index];
        if (owner.SagaState is not { Phase: SagaPhase.Compensating } state || !RunsRetryHandler(state) ||
            state.ErrorHandlerIndex < 0)
            return false;
        // Its handler's frame is above it while the handler runs; once the handler is done, nothing is.
        if (index + 1 < continuation.Stack.Count)
        {
            var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            var body = continuation.Stack[index + 1];
            if (body.ScopeId != scopes.CatchBodyScope(saga.CatchBlocks[state.ErrorHandlerIndex]))
                return false;
            checkpoint.Trace.DropFrames(continuation, index + 1, continuation.CancellationUnwind
            ? continuation.ItemUnwindCause ?? ItemEndCause.Cancelled : ItemEndCause.UnwoundByOuterHandler, continuation.UnwindFailure);
            continuation.Previous = body.EntryPrevious;
            continuation.RuntimePrevious = body.RuntimeEntryPrevious;
        }
        // A retry handler is abandoned work even inside preserved cleanup. Return to that
        // cleanup after undo; only a saga with no enclosing owner resumes the unwind itself.
        state.IsCancellationCleanup = !continuation.Stack.Take(index).Any(frame =>
            frame.CancellationState?.IsCancellationCleanup == true || frame.TryState?.IsCancellationCleanup == true ||
            frame.SagaState?.IsCancellationCleanup == true);
        state.CancellationRequested = true;
        state.ErrorHandlerStarted = false;
        GiveUp(state);
        continuation.Status = ContinuationStatus.Active;
        return true;
    }

    /// <summary>
    /// A cancellation reaching a saga that compensates finishes the compensation and then cancels; a saga
    /// compensating for a retry undoes the steps it kept for it too.
    /// </summary>
    public static void PreserveCompensation(SagaScopeState state, bool ownsUnwind)
    {
        // Cancellation suppresses saga retry even when an enclosing cleanup owns the unwind.
        state.CancellationRequested = true;
        state.IsCancellationCleanup = ownsUnwind;
        if (state.ErrorAction == SagaContinuationAction.Retry)
            GiveUp(state);
    }

    /// <summary>
    /// The frame index of a saga whose retry handler's own steps are running; a failure there ends the saga as
    /// terminated, once the steps kept for the retry are undone.
    /// </summary>
    public static int RetryHandlerIndex(WorkflowScopeCatalog scopes, ContinuationState continuation)
    {
        // A nested saga's handler that does not retry passes its failure on, so the search goes past it.
        for (var index = continuation.Stack.Count - 2; index >= 0; index--)
        {
            var owner = continuation.Stack[index];
            if (owner.SagaState is not { Phase: SagaPhase.Compensating, ErrorHandlerStarted: true } state ||
                state.ErrorAction != SagaContinuationAction.Retry || state.HandlerError != null ||
                state.ErrorHandlerIndex < 0)
                continue;
            var saga = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            if (continuation.Stack[index + 1].ScopeId ==
                scopes.CatchBodyScope(saga.CatchBlocks[state.ErrorHandlerIndex]))
                return index;
        }
        return -1;
    }

    /// <summary>
    /// Ends a retry handler whose own steps failed: its frames go, the kept steps are undone, and then the saga
    /// raises <see cref="SagaTerminatedException"/> with the handler's failure.
    /// </summary>
    public static void GiveUpAfterHandlerFailure(ExecutionCheckpoint checkpoint, ContinuationState continuation, int index, PendingFailure failure)
    {
        var state = continuation.Stack[index].SagaState!;
        var body = continuation.Stack[index + 1];
        checkpoint.Trace.DropFrames(continuation, index + 1, continuation.CancellationUnwind
            ? continuation.ItemUnwindCause ?? ItemEndCause.Cancelled : ItemEndCause.UnwoundByOuterHandler, failure);
        continuation.Previous = body.EntryPrevious;
        continuation.RuntimePrevious = body.RuntimeEntryPrevious;
        state.HandlerError = failure;
        GiveUp(state);
    }

    /// <summary>
    /// Records that a saga request's answer timed out, so a retry that keeps the request takes its
    /// <c>WaitFor</c>'s timeout path again instead of waiting on a closed wait.
    /// </summary>
    public static void NoteTimeout(ContinuationState continuation, string? invocationId, string waitId)
    {
        if (invocationId == null)
            return;
        foreach (var completed in continuation.Stack.Select(frame => frame.SagaState)
                     .SelectMany(state => state?.CompletedSteps ?? []))
            if (completed.InvocationId == invocationId)
            {
                completed.ReplyTimedOut = true;
                completed.TimedOutWaitId = waitId;
            }
    }

    private static void GiveUp(SagaScopeState state)
    {
        state.CompensationFloor = 0;
        state.KeptWaitIds.Clear();
        state.RestartAt = null;
    }

    // Starts the saga again where its undo stopped (D-36): the kept steps stay, their frames are pushed again
    // with new activations, so requests that run again get new invocations, and the kept requests' waits and
    // answers move to the restored frames. With no restart point it starts at the saga's first step.
    private static void Restart(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, WorkflowStep step,
        ScopePosition owner, ContinuationState continuation, IServiceProvider services, DateTime now)
    {
        var state = owner.SagaState!;
        var point = state.RestartAt;
        checkpoint.Trace.Retry(TraceEntryType.SagaRetryStarted, step, continuation, state.RetryCount,
            point?.StepId, point == null ? scopes.SagaScope(step) : state.Frames[point.FrameId].ScopeId);
        var kept = point == null ? 0 : state.CompensationFloor;
        state.CompletedSteps.RemoveRange(kept, state.CompletedSteps.Count - kept);
        // Waits accepted after the restart point are replayed as the retried pass reaches them.
        // So are those the restarted step itself accepted: one whose outputs then failed, and those in a
        // timeout path it took.
        var replayFrom = state.AcceptedWaits.FindIndex(accepted => accepted.Completed > kept ||
            accepted.Completed == kept && point?.StepId is { } restarted &&
            (accepted.StepId == restarted || accepted.StepId.StartsWith(restarted + "/", StringComparison.Ordinal)));
        state.AcceptedWaitCursor = point == null ? 0 : replayFrom < 0 ? state.AcceptedWaits.Count : replayFrom;
        state.CompensationErrors.Clear();
        state.Error = null;

        state.Phase = SagaPhase.Forward;
        state.CompensationCursor = -1;
        state.CompensationFloor = 0;
        state.RestartAt = null;
        if (point == null)
        {
            state.Frames.Clear();
            state.KeptWaitIds.Clear();
            Enter(scopes, checkpoint, step, owner, continuation, services, now);
            return;
        }
        var chain = new List<(string Id, SagaFrameRecord Record)>();
        for (string? id = point.FrameId; id != null; id = state.Frames[id].ParentFrameId)
            chain.Insert(0, (id, state.Frames[id]));
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var depth = 0; depth < chain.Count; depth++)
        {
            var record = chain[depth].Record;
            var frame = new ScopePosition
            {
                ScopeId = record.ScopeId,
                Kind = record.Kind,
                NextStepIndex = depth + 1 < chain.Count ? chain[depth + 1].Record.IndexInParent : point.StepIndex,
                EntryPrevious = record.EntryPrevious,
                RestorePreviousOnExit = record.RestorePreviousOnExit,
                ExitParentScopeOnExit = record.ExitParentScopeOnExit
            };
            if (depth + 1 < chain.Count && scopes.Steps(record.ScopeId)[frame.NextStepIndex].StepType == WorkflowStepType.CancellationScope)
                frame.CancellationState = new CancellationScopeState { EntryPrevious = chain[depth + 1].Record.EntryPrevious };
            renamed[chain[depth].Id] = frame.ActivationId;
            continuation.Stack.Add(frame);
        }
        foreach (var wait in checkpoint.Waits.Where(wait => state.KeptWaitIds.Contains(wait.Id)))
            if (wait.ScopeActivationId != null && renamed.TryGetValue(wait.ScopeActivationId, out var to))
                wait.ScopeActivationId = to;
        foreach (var reply in checkpoint.BoundReplies.Where(reply => state.KeptWaitIds.Contains(reply.WaitId)))
            if (renamed.TryGetValue(reply.ScopeActivationId, out var to))
                reply.ScopeActivationId = to;
        state.KeptWaitIds.Clear();
        // The restored frames take the place of those they restore, for the kept steps and any later restart;
        // records nothing can restart from any more go.
        foreach (var (from, to) in renamed)
        {
            state.Frames[to] = state.Frames[from];
            state.Frames.Remove(from);
        }
        foreach (var record in state.Frames.Values)
            if (record.ParentFrameId != null && renamed.TryGetValue(record.ParentFrameId, out var parent))
                record.ParentFrameId = parent;
        foreach (var completed in state.CompletedSteps)
            if (renamed.TryGetValue(completed.FrameId, out var frameId))
                completed.FrameId = frameId;
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in state.CompletedSteps.Select(completed => completed.FrameId).Concat(renamed.Values))
            for (string? id = start; id != null && reachable.Add(id); id = state.Frames.GetValueOrDefault(id)?.ParentFrameId)
            {
            }
        foreach (var unreachable in state.Frames.Keys.Where(id => !reachable.Contains(id)).ToArray())
            state.Frames.Remove(unreachable);
        continuation.Previous = point.Previous;
        continuation.RuntimePrevious = null;
    }

    // Records the saga's own frames above its owner, the body and the outcome branches within it, once each,
    // and returns the index of the innermost; frames above it, such as a WaitFor's timeout path, are not the
    // saga's, so a failure there restarts at the step that holds them.
    private static int RecordFrames(WorkflowScopeCatalog scopes, ContinuationState continuation, int ownerIndex,
        SagaScopeState state)
    {
        var innermost = ownerIndex + 1;
        for (var index = ownerIndex + 1; index < continuation.Stack.Count; index++)
        {
            var frame = continuation.Stack[index];
            var parent = index > ownerIndex + 1 ? continuation.Stack[index - 1] : null;
            if (parent != null &&
                scopes.Steps(parent.ScopeId).ElementAtOrDefault(parent.NextStepIndex) is not
                    { StepType: WorkflowStepType.Switch or WorkflowStepType.CancellationScope })
                break;
            if (parent != null && !scopes.Steps(parent.ScopeId)[parent.NextStepIndex].StepMetadata
                    .ContainsKey("IsSagaStep"))
                break;
            innermost = index;
            if (!state.Frames.ContainsKey(frame.ActivationId))
                state.Frames.Add(frame.ActivationId, new SagaFrameRecord
            {
                ScopeId = frame.ScopeId,
                Kind = frame.Kind,
                ParentFrameId = parent?.ActivationId,
                IndexInParent = parent?.NextStepIndex ?? 0,
                EntryPrevious = Saved(frame.RuntimeEntryPrevious, frame.EntryPrevious),
                RestorePreviousOnExit = frame.RestorePreviousOnExit,
                ExitParentScopeOnExit = frame.ExitParentScopeOnExit
            });
        }
        return innermost;
    }

    internal static SagaRestartPoint RecordFailurePoint(WorkflowScopeCatalog scopes, ContinuationState continuation,
        int index, SagaScopeState state)
    {
        var innermost = RecordFrames(scopes, continuation, index, state);
        var at = continuation.Stack[innermost];
        return new SagaRestartPoint
        {
            FrameId = at.ActivationId,
            StepIndex = at.NextStepIndex,
            StepId = scopes.Steps(at.ScopeId).ElementAtOrDefault(at.NextStepIndex) is { } failing
                ? scopes.Id(failing)
                : null,
            Previous = innermost == continuation.Stack.Count - 1
                ? Saved(continuation.RuntimePrevious, continuation.Previous)
                : Saved(continuation.Stack[innermost + 1].RuntimeEntryPrevious,
                    continuation.Stack[innermost + 1].EntryPrevious)
        };
    }

    private static SagaRestartPoint PointOf(SagaCompletedStep completed) => new()
    {
        FrameId = completed.FrameId,
        StepIndex = completed.StepIndex,
        Previous = completed.Previous
    };

    private static SerializedValue? Saved(object? runtime, SerializedValue? saved) =>
        runtime != null ? SerializedValue.From(runtime) : saved;

    public static bool BeginCancellation(ExecutionCheckpoint checkpoint, ContinuationState continuation, int index)
    {
        var state = continuation.Stack[index].SagaState;
        if (state?.Phase != SagaPhase.Forward)
            return false;
        var forward = continuation.Stack[index + 1];
        checkpoint.Trace.DropFrames(continuation, index + 1, continuation.CancellationUnwind
            ? continuation.ItemUnwindCause ?? ItemEndCause.Cancelled : ItemEndCause.UnwoundByOuterHandler, continuation.UnwindFailure);
        continuation.Previous = forward.EntryPrevious;
        continuation.RuntimePrevious = forward.RuntimeEntryPrevious;
        state.Phase = SagaPhase.Compensating;
        state.CompensationCursor = state.CompletedSteps.Count - 1;
        state.IsCancellationCleanup = true;
        state.CancellationRequested = true;
        continuation.Status = ContinuationStatus.Active;
        return true;
    }
}
