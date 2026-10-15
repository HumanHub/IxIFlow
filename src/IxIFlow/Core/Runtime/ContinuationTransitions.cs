namespace IxIFlow.Core.Runtime;

/// <summary>
/// Moves continuations through steps, forks, joins, cancellation, deadlines and failure capture.
/// It changes only the checkpoint it is given; it never reads or writes saved state.
/// </summary>
internal sealed class ContinuationTransitions(IServiceProvider services, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // A deadline that passes after the root finishes has nothing left to interrupt.
    public bool HasDueDeadline(ExecutionCheckpoint checkpoint) =>
        (!checkpoint.CancellationRequested && !RootFinished(checkpoint) &&
         checkpoint.ExecutionDeadlineUtc <= Now) ||
        checkpoint.Waits.Any(wait => wait.DeadlineUtc <= Now);

    public static bool RootFinished(ExecutionCheckpoint checkpoint) =>
        checkpoint.Continuations[0].Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled;

    public bool ProcessExecutionDeadline(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint)
    {
        if (checkpoint.CancellationRequested || checkpoint.TimedOut ||
            checkpoint.ExecutionDeadlineUtc > Now ||
            checkpoint.ExecutionDeadlineUtc == null ||
            RootFinished(checkpoint))
            return false;
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

    public bool ProcessDueWaits(ExecutionCheckpoint checkpoint,
        WorkflowScopeCatalog scopes)
    {
        var due = checkpoint.Waits.Where(wait => wait.ChildWorkflowId == null &&
            wait.DeadlineUtc <= Now).ToArray();
        foreach (var wait in due)
        {
            var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
            var step = scopes.Step(wait.StepId);
            if (continuation.Status != ContinuationStatus.Waiting ||
                step.StepType != WorkflowStepType.Delay && step.TimeoutSteps.Count == 0)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "A due wait has no active timeout branch");
            checkpoint.Waits.Remove(wait);
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

    public void Advance(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        object workflowData)
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
            SagaScopeTransitions.RecordAcceptedWait(continuation, accepted.StepId, @event);
            WorkflowValueBinding.ApplyEventOutputs(acceptedStep, @event, workflowData);
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
                        step.PreviousStepDataType, continuation.GetPrevious(services));
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
                    continuation.GetPrevious(services));
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
                Fork(scopes, step, checkpoint, continuation);
                break;

            case WorkflowStepType.TryCatch:
                TryScopeTransitions.Enter(scopes, step, position, continuation);
                break;

            case WorkflowStepType.Saga:
                SagaScopeTransitions.Enter(scopes, step, position, continuation, services, Now);
                break;

            case WorkflowStepType.Loop:
                var shouldRun = position.LoopIterationCount == 0 && step.LoopType == LoopType.DoWhile;
                if (!shouldRun)
                {
                    var loopContext = WorkflowValueBinding.EvaluationContext(workflowData,
                        step.PreviousStepDataType, continuation.GetPrevious(services));
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

            case WorkflowStepType.SuspendResume when step.StepMetadata.TryGetValue("WaitKey", out var keyValue):
                TryScopeTransitions.EnsureCanWait(checkpoint, continuation);
                if (SagaScopeTransitions.TryReuseAcceptedWait(continuation, scopes.Id(step),
                        services, out var acceptedEvent))
                {
                    WorkflowValueBinding.ApplyEventOutputs(step, acceptedEvent!, workflowData);
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
                    DeadlineUtc = step.WaitTimeout is { } timeout
                        ? Now.Add(timeout) : null,
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
                    DeadlineUtc = Now.Add(step.DelayDuration!.Value)
                });
                continuation.Status = ContinuationStatus.Waiting;
                break;

            default:
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Structured execution does not yet support {step.StepType}");
        }

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
        checkpoint.Waits.RemoveAll(wait => wait.ChildWorkflowId == childInstanceId &&
            wait.ContinuationId == continuation.Id);
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
        checkpoint.Waits.RemoveAll(wait => wait.ChildWorkflowId == childInstanceId &&
            wait.ContinuationId == continuation.Id);
        foreach (var childWait in childCheckpoint.Waits)
            checkpoint.Waits.Add(new WaitState
            {
                RegisteredAtUtc = Now,
                ContinuationId = continuation.Id,
                StepId = pending.StepId,
                Key = childWait.Key,
                EventType = childWait.EventType,
                DeadlineUtc = childWait.DeadlineUtc,
                ChildWorkflowId = childInstanceId,
                ChildWaitId = childWait.Id
            });
        continuation.AcceptedWait = null;
        continuation.Status = ContinuationStatus.Waiting;
    }

    private static void Fork(WorkflowScopeCatalog scopes, WorkflowStep step, ExecutionCheckpoint checkpoint,
        ContinuationState parent)
    {
        if (step.ParallelBranches.Count == 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Parallel step '{step.Id}' has no branches");

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
        checkpoint.Waits.RemoveAll(wait => wait.ContinuationId == continuation.Id);
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

    public static void FinishCompletingJoins(ExecutionCheckpoint checkpoint)
    {
        foreach (var join in checkpoint.Joins.Where(item => item.IsCompleting).ToList())
        {
            var children = checkpoint.Continuations.Where(item => join.ChildContinuationIds.Contains(item.Id));
            if (!children.All(item => item.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled))
                continue;
            var parent = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
            checkpoint.Joins.Remove(join);
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
    }

    public bool CaptureFailure(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, Exception error, bool allowStepRetry = true)
    {
        if (WorkflowEngineException.IsEngineFailure(error))
            return false;
        if (allowStepRetry && !continuation.CancellationUnwind &&
            SagaScopeTransitions.HandleStepFailure(scopes, continuation, error, Now))
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
