namespace IxIFlow.Core.Runtime;

/// <summary>
/// Moves continuations through steps, forks, joins, cancellation, deadlines and failure capture.
/// It changes only the checkpoint it is given; it never reads or writes saved state.
/// </summary>
internal sealed class ContinuationTransitions(IServiceProvider services, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// True when a deadline may be due by the store's clock as the run last read it, or a child is due
    /// for a check by the host's own: the run reads the inbox, and the store says which are due (D-35).
    /// A deadline that passes after the root finishes has nothing left to interrupt.
    /// </summary>
    public bool MayBeDue(ExecutionCheckpoint checkpoint) => checkpoint.NextDeadlineLocal() <= Now;

    /// <summary>
    /// A cancellation request the host observed and the run has not applied yet; while one is pending,
    /// the run applies it before it takes events or deadlines.
    /// </summary>
    public static bool CancellationPending(ExecutionCheckpoint checkpoint, IExecutionHost host) =>
        !checkpoint.CancellationRequested && host.ObservedCancellation != null &&
        checkpoint.Continuations[0].Status != ContinuationStatus.Completed;

    // The store's time as the run last read its clock. A deadline the run sets is an estimate until it
    // reads the one the store stamped; a retry time stays the run's estimate (D-35).
    private DateTime StoreNow(ExecutionCheckpoint checkpoint) => checkpoint.Clock.ToStore(Now);

    public static bool RootFinished(ExecutionCheckpoint checkpoint) =>
        checkpoint.Continuations[0].Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled;

    /// <summary>Ends the run at its execution deadline when the store found it due at the run's last read.</summary>
    public bool ProcessExecutionDeadline(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint)
    {
        if (!checkpoint.ExecutionTimerOpen || !checkpoint.Inbox.IsDue(checkpoint.ExecutionTimerId!))
            return false;
        checkpoint.Inbox.Closed(checkpoint.ExecutionTimerId!, WaitCloseReason.TimedOut);
        checkpoint.TimedOut = true;
        checkpoint.CancellationRequested = true;
        instance.CancellationReason = new CancellationReason
        {
            ReasonCode = "execution_timeout",
            Description = "The workflow execution deadline expired",
            RequestedAt = Now
        };
        CancelTree(checkpoint, checkpoint.Continuations[0]);
        return true;
    }

    /// <summary>
    /// Times out the waits the store found due when the run last read its inbox. That read covered every
    /// event received before their deadlines, and the run decided those first (D-33, D-35).
    /// </summary>
    public bool ProcessDueWaits(ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes)
    {
        var due = checkpoint.Waits.Where(wait => wait.ChildWorkflowId == null &&
            checkpoint.Inbox.IsDue(wait.Id)).ToArray();
        foreach (var wait in due)
        {
            var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
            var step = scopes.Step(wait.StepId);
            if (continuation.Status != ContinuationStatus.Waiting ||
                step.StepType != WorkflowStepType.Delay && step.TimeoutSteps.Count == 0)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "A due wait has no active timeout branch");
            checkpoint.RemoveWaits(item => item == wait, WaitCloseReason.TimedOut);
            continuation.Status = ContinuationStatus.Active;
            if (step.StepType == WorkflowStepType.Delay)
            {
                continuation.Stack[^1].NextStepIndex++;
                continue;
            }
            continuation.Stack.Add(new ScopePosition
            {
                ScopeId = scopes.TimeoutScope(step),
                EntryPrevious = continuation.Previous,
                RuntimeEntryPrevious = continuation.RuntimePrevious,
                RestorePreviousOnExit = true,
                ExitParentScopeOnExit = true
            });
        }
        return due.Length != 0;
    }

    /// <summary>
    /// Moves a continuation past its next control-flow step. A parallel step that would make more
    /// than <paramref name="maxActiveBranches"/> continuations live throws
    /// <see cref="BranchLimitExceededException"/> before anything changes.
    /// </summary>
    public void Advance(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        object workflowData,
        int maxActiveBranches = int.MaxValue,
        int maxForEachItems = int.MaxValue)
    {
        if (continuation.AcceptedWait is { } accepted)
        {
            var acceptedPosition = continuation.Stack[^1];
            var acceptedStep = scopes.Steps(acceptedPosition.ScopeId)[acceptedPosition.NextStepIndex];
            if (scopes.Id(acceptedStep) != accepted.StepId)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "The accepted event does not match its saved wait");
            continuation.AcceptedWait = null;
            var @event = accepted.Event.Read(services)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "The accepted event could not be restored");
            // A saga retry asks again, so a request's answer is not kept for replay.
            if (acceptedStep.ReplyToRequest == null)
                SagaScopeTransitions.RecordAcceptedWait(continuation, accepted.StepId, @event);
            WorkflowValueBinding.ApplyEventOutputs(acceptedStep, @event, workflowData,
                ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
            continuation.SetPrevious(@event);
            acceptedPosition.NextStepIndex++;
            return;
        }

        var position = continuation.Stack[^1];
        var steps = scopes.Steps(position.ScopeId);
        if (position.NextStepIndex == steps.Count)
        {
            continuation.Stack.RemoveAt(continuation.Stack.Count - 1);
            if (position.RestorePreviousOnExit)
            {
                continuation.Previous = position.EntryPrevious;
                continuation.RuntimePrevious = position.RuntimeEntryPrevious;
            }
            if (continuation.Stack.Count == 0)
            {
                continuation.Status = ContinuationStatus.Completed;
                CompleteJoin(scopes, checkpoint, continuation, workflowData);
            }
            else
            {
                var parent = continuation.Stack[^1];
                if (position.ExitParentScopeOnExit)
                {
                    parent.NextStepIndex = scopes.Steps(parent.ScopeId).Count;
                    return;
                }
                var currentStep = scopes.Steps(parent.ScopeId)[parent.NextStepIndex];
                if (currentStep.StepType == WorkflowStepType.Loop &&
                    position.ScopeId == scopes.LoopScope(currentStep))
                {
                    // Revisit the loop guard with the completed iteration recorded on its parent.
                }
                else if (ForEachTransitions.ItemFinished(scopes, parent, position, currentStep))
                {
                    // The ForEach runs its next item, or completes, when it is advanced again.
                }
                else if (!TryScopeTransitions.Exit(scopes, parent, position, currentStep, continuation) &&
                         !SagaScopeTransitions.Exit(scopes, parent, position, currentStep, continuation))
                    parent.NextStepIndex++;
            }
            return;
        }

        var step = steps[position.NextStepIndex];
        switch (step.StepType)
        {
            case WorkflowStepType.Sequence:
                continuation.Stack.Add(new ScopePosition
                {
                    ScopeId = scopes.SequenceScope(step),
                    EntryPrevious = continuation.Previous,
                    RuntimeEntryPrevious = continuation.RuntimePrevious,
                    RestorePreviousOnExit = true
                });
                break;

            case WorkflowStepType.Conditional:
                if (step.OutcomeSelector != null)
                {
                    var outcomeContext = WorkflowValueBinding.EvaluationContext(workflowData,
                        step.PreviousStepDataType, continuation.GetPrevious(services),
                        ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
                    var outcome = step.OutcomeSelector(outcomeContext);
                    var selectedOutcome = step.OutcomeBranches.FindIndex(branch =>
                        !branch.IsDefault && Equals(branch.Value, outcome));
                    if (selectedOutcome < 0)
                        selectedOutcome = step.OutcomeBranches.FindIndex(branch => branch.IsDefault);
                    if (selectedOutcome < 0)
                        throw new InvalidOperationException(
                            $"Outcome step '{step.Id}' has no branch for '{outcome}'");
                    continuation.Stack.Add(new ScopePosition
                    {
                        ScopeId = scopes.OutcomeScope(step, selectedOutcome),
                        EntryPrevious = continuation.Previous,
                        RuntimeEntryPrevious = continuation.RuntimePrevious,
                        RestorePreviousOnExit = true
                    });
                    break;
                }
                var conditionContext = WorkflowValueBinding.EvaluationContext(workflowData, step.PreviousStepDataType,
                    continuation.GetPrevious(services), ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
                var selected = step.CompiledCondition?.Invoke(conditionContext)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"If step '{step.Id}' has no condition");
                continuation.Stack.Add(new ScopePosition
                {
                    ScopeId = selected
                        ? scopes.ThenScope(step)
                        : scopes.ElseScope(step),
                    EntryPrevious = continuation.Previous,
                    RuntimeEntryPrevious = continuation.RuntimePrevious,
                    RestorePreviousOnExit = true
                });
                break;

            case WorkflowStepType.Parallel:
                Fork(scopes, step, checkpoint, continuation, maxActiveBranches);
                break;

            case WorkflowStepType.TryCatch:
                TryScopeTransitions.Enter(scopes, step, position, continuation);
                break;

            case WorkflowStepType.Saga:
                SagaScopeTransitions.Enter(scopes, step, position, continuation, services, StoreNow(checkpoint));
                break;

            case WorkflowStepType.Loop:
                var shouldRun = position.LoopIterationCount == 0 && step.LoopType == LoopType.DoWhile;
                if (!shouldRun)
                {
                    var loopContext = WorkflowValueBinding.EvaluationContext(workflowData,
                        step.PreviousStepDataType, continuation.GetPrevious(services),
                        ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
                    shouldRun = step.CompiledCondition?.Invoke(loopContext)
                        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                            $"Loop step '{step.Id}' has no condition");
                }
                if (!shouldRun)
                {
                    position.LoopIterationCount = 0;
                    position.NextStepIndex++;
                    break;
                }
                position.LoopIterationCount++;
                continuation.Stack.Add(new ScopePosition
                {
                    ScopeId = scopes.LoopScope(step),
                    EntryPrevious = continuation.Previous,
                    RuntimeEntryPrevious = continuation.RuntimePrevious,
                    RestorePreviousOnExit = true
                });
                break;

            case WorkflowStepType.ForEach:
                ForEachTransitions.Advance(scopes, checkpoint, continuation, position, step, workflowData,
                    services, maxForEachItems);
                break;

            case WorkflowStepType.ForEachResult:
                ForEachTransitions.TakeResult(scopes, checkpoint, continuation, position, step, workflowData,
                    services);
                break;

            case WorkflowStepType.SuspendResume when step.ReplyToRequest != null:
                TryScopeTransitions.EnsureCanWait(checkpoint, continuation);
                AwaitReply(scopes, checkpoint, continuation, step);
                break;

            case WorkflowStepType.SuspendResume when step.StepMetadata.TryGetValue("WaitKey", out var keyValue):
                TryScopeTransitions.EnsureCanWait(checkpoint, continuation);
                if (SagaScopeTransitions.TryReuseAcceptedWait(continuation, scopes.Id(step),
                        services, out var acceptedEvent))
                {
                    WorkflowValueBinding.ApplyEventOutputs(step, acceptedEvent!, workflowData,
                        ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
                    continuation.SetPrevious(acceptedEvent);
                    position.NextStepIndex++;
                    break;
                }
                var key = keyValue as string
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"Wait step '{step.Id}' has an invalid key");
                checkpoint.Waits.Add(new WaitState
                {
                    RegisteredAtUtc = Now,
                    ContinuationId = continuation.Id,
                    StepId = scopes.Id(step),
                    Key = key,
                    Timeout = step.WaitTimeout,
                    DeadlineUtc = StoreNow(checkpoint) + step.WaitTimeout,
                    EventType = step.ResumeEventType?.AssemblyQualifiedName
                        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                            $"Wait step '{step.Id}' has no event type")
                });
                continuation.Status = ContinuationStatus.Waiting;
                break;

            case WorkflowStepType.Delay:
                TryScopeTransitions.EnsureCanWait(checkpoint, continuation);
                checkpoint.Waits.Add(new WaitState
                {
                    RegisteredAtUtc = Now,
                    ContinuationId = continuation.Id,
                    StepId = scopes.Id(step),
                    Key = "delay",
                    Timeout = step.DelayDuration!.Value,
                    DeadlineUtc = StoreNow(checkpoint) + step.DelayDuration!.Value
                });
                continuation.Status = ContinuationStatus.Waiting;
                break;

            default:
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Structured execution does not yet support {step.StepType}");
        }

    }

    // A request's WaitFor takes an answer that arrived early at once; otherwise it waits on the wait its
    // request opened in the same scope occurrence, until its own timeout from the commit that records
    // this (D-33, D-35). A saga retry sends the request again, so an answer is never replayed from an
    // earlier attempt.
    private void AwaitReply(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep step)
    {
        var stepId = scopes.Id(step);
        var activation = continuation.Stack[^1].ActivationId;
        if (checkpoint.BoundReplies.FirstOrDefault(reply =>
                reply.StepId == stepId && reply.ScopeActivationId == activation) is { } answer)
        {
            checkpoint.BoundReplies.Remove(answer);
            continuation.AcceptedWait = new AcceptedWaitState
            {
                StepId = stepId, Event = answer.Event, EventId = answer.EventId, WaitId = answer.WaitId
            };
            return;
        }
        var wait = checkpoint.Waits.FirstOrDefault(item =>
                item.IsReply && item.StepId == stepId && item.ScopeActivationId == activation)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"The request '{step.ReplyToRequest}' this wait answers has no open wait");
        if (wait.Timeout == null && step.WaitTimeout is { } timeout)
        {
            wait.Timeout = timeout;
            wait.DeadlineUtc = StoreNow(checkpoint) + timeout;
        }
        continuation.Status = ContinuationStatus.Waiting;
    }

    public static bool ShouldCancelInvokedChild(ContinuationState continuation) =>
        continuation.CancellationUnwind &&
        !TryScopeTransitions.IsInsideFinally(continuation) &&
        !SagaScopeTransitions.IsInsideCompensation(continuation);

    public void ProjectChildLifecycleWait(ExecutionCheckpoint checkpoint,
        ContinuationState continuation, string childInstanceId, bool needsResolution)
    {
        var pending = continuation.PendingActivity
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A child has no parent invocation");
        checkpoint.RemoveWaits(wait => wait.ChildWorkflowId == childInstanceId &&
            wait.ContinuationId == continuation.Id, WaitCloseReason.Replaced);
        checkpoint.Waits.Add(new WaitState
        {
            RegisteredAtUtc = Now,
            ContinuationId = continuation.Id,
            StepId = pending.StepId,
            Key = "child-workflow",
            ChildWorkflowId = childInstanceId
        });
        continuation.Status = needsResolution
            ? ContinuationStatus.WaitingResolution : ContinuationStatus.Waiting;
        pending.ResolutionReason = needsResolution
            ? $"Child workflow '{childInstanceId}' needs activity resolution" : null;
    }

    public void ProjectChildWaits(ExecutionCheckpoint checkpoint,
        ContinuationState continuation, string childInstanceId,
        ExecutionCheckpoint childCheckpoint)
    {

        var pending = continuation.PendingActivity
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "A child wait has no parent invocation");
        pending.ChildWorkflowId = childInstanceId;
        checkpoint.RemoveWaits(wait => wait.ChildWorkflowId == childInstanceId &&
            wait.ContinuationId == continuation.Id, WaitCloseReason.Replaced);
        foreach (var childWait in childCheckpoint.Waits)
            checkpoint.Waits.Add(new WaitState
            {
                RegisteredAtUtc = Now,
                ContinuationId = continuation.Id,
                StepId = pending.StepId,
                Key = childWait.Key,
                EventType = childWait.EventType,
                // The parent checks the child at the child's deadline, on the parent's own clock.
                DeadlineUtc = childWait.DeadlineUtc is { } childDeadline
                    ? checkpoint.Clock.ToLocal(childDeadline) : null,
                ChildWorkflowId = childInstanceId,
                ChildWaitId = childWait.Id
            });
        continuation.AcceptedWait = null;
        continuation.Status = ContinuationStatus.Waiting;
    }

    private static void Fork(WorkflowScopeCatalog scopes, WorkflowStep step, ExecutionCheckpoint checkpoint,
        ContinuationState parent, int maxActiveBranches)
    {
        if (step.ParallelBranches.Count == 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Parallel step '{step.Id}' has no branches");
        // Branches that finished or are cancelling do not count, so whether a nested fork fits can
        // depend on how far its siblings have got.
        var live = checkpoint.Continuations.Count(item => item.Status is not (ContinuationStatus.Completed or
            ContinuationStatus.Cancelled or ContinuationStatus.Cancelling));
        if (live + step.ParallelBranches.Count > maxActiveBranches)
            throw new BranchLimitExceededException(live, step.ParallelBranches.Count, maxActiveBranches);

        var join = new ParallelJoinState
        {
            StepId = scopes.Id(step),
            ParentContinuationId = parent.Id,
            Mode = step.ParallelJoinMode
        };
        checkpoint.Joins.Add(join);
        parent.Status = ContinuationStatus.Joining;

        for (var index = 0; index < step.ParallelBranches.Count; index++)
        {
            var child = new ContinuationState
            {
                ParentJoinId = join.Id,
                Previous = parent.Previous,
                RuntimePrevious = parent.RuntimePrevious,
                Stack = [new ScopePosition { ScopeId = scopes.BranchScope(step, index) }]
            };
            join.ChildContinuationIds.Add(child.Id);
            checkpoint.Continuations.Add(child);
        }
    }

    private static void CompleteJoin(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState child,
        object workflowData)
    {
        if (child.ParentJoinId == null)
            return;

        var join = checkpoint.Joins.Single(item => item.Id == child.ParentJoinId);
        if (join.IsCompleting)
            return;
        var children = checkpoint.Continuations.Where(item => join.ChildContinuationIds.Contains(item.Id)).ToList();
        var allCompleted = children.All(item => item.Status == ContinuationStatus.Completed);
        var shouldJoin = join.Mode switch
        {
            ParallelJoinMode.WaitAll => allCompleted,
            ParallelJoinMode.WaitAny => true,
            ParallelJoinMode.WaitConditionally =>
                (scopes.Step(join.StepId).ParallelCompletionCondition?.Invoke(workflowData)
                 ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                     "Conditional parallel join has no completion condition")) ||
                allCompleted,
            _ => throw new ArgumentOutOfRangeException(nameof(join.Mode))
        };
        if (!shouldJoin)
            return;

        join.IsCompleting = true;
        foreach (var sibling in children.Where(item => item.Status != ContinuationStatus.Completed))
            CancelTree(checkpoint, sibling);
    }

    public static void CancelTree(ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
            return;
        continuation.CancellationUnwind = true;
        continuation.RetryAfterUtc = null;
        if (continuation.PendingActivity != null &&
            continuation.Status == ContinuationStatus.WaitingResolution)
            return;
        if (TryScopeTransitions.IsInsideFinally(continuation) ||
            SagaScopeTransitions.IsInsideCompensation(continuation))
        {
            TryScopeTransitions.PreserveExistingFinally(continuation);
            SagaScopeTransitions.PreserveCompensation(continuation);
            return;
        }
        checkpoint.RemoveWaits(wait => wait.ContinuationId == continuation.Id, WaitCloseReason.Cancelled);
        continuation.AcceptedWait = null;
        foreach (var join in checkpoint.Joins.Where(item => item.ParentContinuationId == continuation.Id).ToList())
        {
            join.IsCompleting = true;
            foreach (var childId in join.ChildContinuationIds)
                CancelTree(checkpoint, checkpoint.Continuations.Single(item => item.Id == childId));
        }
        continuation.Status = checkpoint.Joins.Any(item => item.ParentContinuationId == continuation.Id)
            ? ContinuationStatus.Joining
            : ContinuationStatus.Cancelling;
    }

    /// <summary>Completes joins whose branches have all finished; returns true when one completed.</summary>
    public static bool FinishCompletingJoins(ExecutionCheckpoint checkpoint)
    {
        var completed = false;
        foreach (var join in checkpoint.Joins.Where(item => item.IsCompleting).ToList())
        {
            var children = checkpoint.Continuations.Where(item => join.ChildContinuationIds.Contains(item.Id));
            if (!children.All(item => item.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled))
                continue;
            var parent = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
            checkpoint.Joins.Remove(join);
            completed = true;
            if (checkpoint.UnhandledError != null)
            {
                var finishingCleanup = TryScopeTransitions.IsInsideFinally(parent) ||
                    SagaScopeTransitions.IsInsideCompensation(parent);
                CancelTree(checkpoint, parent);
                if (finishingCleanup)
                {
                    if (!join.ResumeParentWithoutAdvance)
                        parent.Stack[^1].NextStepIndex++;
                    parent.Status = ContinuationStatus.Active;
                }
            }
            else if (parent.CancellationUnwind && !TryScopeTransitions.IsInsideFinally(parent) &&
                     !SagaScopeTransitions.IsInsideCompensation(parent))
                parent.Status = ContinuationStatus.Cancelling;
            else
            {
                if (!join.ResumeParentWithoutAdvance)
                    parent.Stack[^1].NextStepIndex++;
                parent.Status = ContinuationStatus.Active;
            }
        }
        return completed;
    }

    public bool CaptureFailure(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, Exception error, bool allowStepRetry = true)
    {
        if (WorkflowEngineException.IsEngineFailure(error))
            return false;
        if (allowStepRetry && !continuation.CancellationUnwind &&
            SagaScopeTransitions.HandleStepFailure(scopes, continuation, error, StoreNow(checkpoint)))
            return true;
        if (continuation.CancellationUnwind)
        {
            if (SagaScopeTransitions.SkipFailedCompensation(scopes, continuation, error))
                return true;
            var cleanupBoundary = continuation.Stack.FindIndex(frame =>
                frame.TryState?.Phase == TryPhase.Finally);
            if (cleanupBoundary >= 0 &&
                CaptureScopeFailure(scopes, continuation, error, cleanupBoundary))
                return true;
            checkpoint.UnhandledError ??= SerializedException.From(error);
            checkpoint.RuntimeUnhandledError ??= error;
            TryScopeTransitions.AbortFailedFinally(continuation);
            TryScopeTransitions.ContinueCancellation(scopes, continuation);
            CancelAncestorJoins(checkpoint, continuation);
            return true;
        }
        while (true)
        {
            if (SagaScopeTransitions.SkipFailedCompensation(scopes, continuation, error) ||
                CaptureScopeFailure(scopes, continuation, error))
                return true;
            TryScopeTransitions.AbortFailedFinally(continuation);
            if (continuation.ParentJoinId == null)
            {
                if (!checkpoint.Joins.Any(join => join.IsCompleting))
                    return false;
                checkpoint.UnhandledError = SerializedException.From(error);
                checkpoint.RuntimeUnhandledError = error;
                continuation.Status = ContinuationStatus.Joining;
                CancelAncestorJoins(checkpoint, continuation);
                return true;
            }

            var join = checkpoint.Joins.Single(item => item.Id == continuation.ParentJoinId);
            join.IsCompleting = true;
            join.ResumeParentWithoutAdvance = true;
            foreach (var childId in join.ChildContinuationIds)
                CancelTree(checkpoint, checkpoint.Continuations.Single(item => item.Id == childId));
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
            if (continuation.CancellationUnwind)
                return CaptureFailure(scopes, checkpoint, continuation, error);
            if (TryScopeTransitions.Capture(scopes, continuation, error))
            {
                checkpoint.UnhandledError = null;
                checkpoint.RuntimeUnhandledError = null;
                continuation.Status = ContinuationStatus.Joining;
                return true;
            }
        }
    }

    private static bool CaptureScopeFailure(WorkflowScopeCatalog scopes,
        ContinuationState continuation, Exception error, int minimumHandlerIndex = -1)
    {
        var tryIndex = TryScopeTransitions.HandlerIndex(scopes, continuation, error);
        var sagaIndex = SagaScopeTransitions.HandlerIndex(continuation);
        if (tryIndex <= minimumHandlerIndex)
            tryIndex = -1;
        if (sagaIndex <= minimumHandlerIndex)
            sagaIndex = -1;
        if (sagaIndex > tryIndex)
        {
            SagaScopeTransitions.Capture(scopes, continuation, sagaIndex, error);
            return true;
        }
        return tryIndex >= 0 && TryScopeTransitions.Capture(scopes, continuation, error);
    }

    private static void CancelAncestorJoins(ExecutionCheckpoint checkpoint,
        ContinuationState continuation)
    {
        while (continuation.ParentJoinId != null)
        {
            var join = checkpoint.Joins.SingleOrDefault(item => item.Id == continuation.ParentJoinId);
            if (join == null)
                return;
            join.IsCompleting = true;
            foreach (var siblingId in join.ChildContinuationIds.Where(id => id != continuation.Id))
                CancelTree(checkpoint,
                    checkpoint.Continuations.Single(item => item.Id == siblingId));
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        }
    }

    public static void PrepareCheckpoint(ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes)
    {
        // A finished branch that no join or wait still refers to can never run again, so it is not
        // saved. The root stays: its status is the instance's outcome.
        var root = checkpoint.Continuations.FirstOrDefault();
        checkpoint.Continuations.RemoveAll(continuation => continuation != root &&
            continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled &&
            !checkpoint.Joins.Any(join => join.ParentContinuationId == continuation.Id ||
                                          join.ChildContinuationIds.Contains(continuation.Id)) &&
            !checkpoint.Waits.Any(wait => wait.ContinuationId == continuation.Id));
        foreach (var continuation in checkpoint.Continuations)
        {
            if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
            {
                continuation.Previous = null;
                continuation.AcceptedWait = null;
                continue;
            }

            // A completed scope restores its entry value, or ends the continuation.
            // Its last activity object will never be read again, so do not require
            // transient input properties on that object to be checkpointable.
            if (continuation.Stack.Count > 0)
            {
                var current = continuation.Stack[^1];
                if (current.NextStepIndex == scopes.Steps(current.ScopeId).Count &&
                    (current.RestorePreviousOnExit || continuation.Stack.Count == 1))
                {
                    continuation.Previous = null;
                    continuation.RuntimePrevious = null;
                }
            }

            if (continuation.RuntimePrevious != null)
                continuation.Previous = SerializedValue.From(continuation.RuntimePrevious);
            foreach (var frame in continuation.Stack)
            {
                if (frame.RuntimeEntryPrevious != null)
                    frame.EntryPrevious = SerializedValue.From(frame.RuntimeEntryPrevious);
                if (frame.TryState?.RuntimeEntryPrevious != null)
                    frame.TryState.EntryPrevious = SerializedValue.From(frame.TryState.RuntimeEntryPrevious);
            }
        }
    }
}
