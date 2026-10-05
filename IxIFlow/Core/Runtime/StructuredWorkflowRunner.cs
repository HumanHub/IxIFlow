using System.Text.Json;
using System.Runtime.ExceptionServices;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Drives serializable continuations over a structured workflow definition.
/// A wait parks one continuation; the instance becomes idle only when no work can run.
/// </summary>
internal sealed class StructuredWorkflowRunner(
    IServiceProvider services,
    IActivityExecutor activityExecutor,
    IWorkflowInvoker workflowInvoker,
    IWorkflowStateRepository repository,
    IWorkflowVersionRegistry registry,
    InProcessInstanceGate gate)
{
    private readonly ActivityTransitionExecutor _activities = new(activityExecutor);
    private readonly WorkflowInvocationTransitionExecutor _invocations = new(workflowInvoker);

    public static bool HasStructuredCheckpoint(WorkflowInstance? instance) =>
        instance != null && ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson);

    public async Task<WorkflowExecutionResult> StartAsync<TData>(
        WorkflowDefinition definition,
        TData workflowData,
        WorkflowOptions options,
        CancellationToken cancellationToken)
        where TData : class
    {
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = workflowData.GetType().AssemblyQualifiedName!,
            WorkflowDataJson = JsonSerializer.Serialize(workflowData),
            Status = WorkflowStatus.Running,
            CorrelationId = options.CorrelationId ?? Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            TotalSteps = definition.Steps.Count,
            Properties = new Dictionary<string, object>(options.Properties)
        };
        var scopes = new WorkflowScopeCatalog(definition);
        if (!options.PersistState && scopes.ContainsWait)
            throw new InvalidOperationException("A workflow with waits requires state persistence");
        var checkpoint = new ExecutionCheckpoint
        {
            DefinitionFingerprint = scopes.Fingerprint,
            Continuations =
            [
                new ContinuationState
                {
                    Stack = [new ScopePosition { ScopeId = "root" }]
                }
            ]
        };

        using var lease = await gate.EnterAsync(instance.InstanceId, cancellationToken);
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        instance.ExecutionSnapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes);
        if (options.PersistState)
        {
            await registry.RegisterWorkflowAsync(definition);
            await PersistAsync(instance, checkpoint, workflowData, scopes, true);
        }
        return await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
            options.PersistState, cancellationToken);
    }

    public async Task<WorkflowExecutionResult> ResumeAsync<TEvent>(
        string instanceId,
        string? key,
        TEvent @event,
        CancellationToken cancellationToken)
        where TEvent : class
    {
        using var lease = await gate.EnterAsync(instanceId, cancellationToken);
        var instance = await repository.GetWorkflowInstanceAsync(instanceId);
        if (instance?.Status == WorkflowStatus.Running && HasStructuredCheckpoint(instance))
            return new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = WorkflowExecutionStatus.Running,
                ErrorMessage = "The workflow instance is executing; retry the event after it becomes idle"
            };
        if (instance?.Status is not (WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution) ||
            !HasStructuredCheckpoint(instance))
            return Rejected(instanceId, "The workflow instance is not waiting");

        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        var candidates = checkpoint.Waits.Where(wait =>
            (key == null || wait.Key == key) &&
            WorkflowTypeIdentity.Resolve(wait.EventType)?.IsAssignableFrom(@event.GetType()) == true).ToList();
        if (candidates.Count == 0)
            return Rejected(instanceId, "No active wait matches this event and key");

        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
        if (definition == null)
            return Rejected(instanceId, "The registered workflow definition is unavailable");

        var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
            ?? throw new InvalidOperationException($"Workflow data type '{instance.WorkflowDataType}' is unavailable");
        var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
            ?? throw new InvalidOperationException("Saved workflow data cannot be read");
        var scopes = new WorkflowScopeCatalog(definition);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            return Rejected(instanceId, "The registered workflow definition differs from the saved version");
        var matching = StructuredWaitMatcher.FindMatches(checkpoint, scopes, workflowData,
            @event, services, key);
        if (matching.Count == 0)
            return new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = WorkflowExecutionStatus.Suspended,
                WorkflowData = workflowData,
                ErrorMessage = "The event did not satisfy any active wait"
            };
        if (matching.Count > 1)
            return Rejected(instanceId, "The event satisfies more than one wait; provide a unique key");

        var wait = matching[0];
        var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);

        continuation.AcceptedWait = new AcceptedWaitState
        {
            StepId = wait.StepId,
            Event = SerializedValue.From(@event)!
        };
        checkpoint.Waits.Remove(wait);
        continuation.Status = ContinuationStatus.Active;

        instance.Status = WorkflowStatus.Running;
        await PersistAsync(instance, checkpoint, workflowData, scopes, true);

        var resumed = await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
            true, cancellationToken);
        resumed.EventAccepted = true;
        return resumed;
    }

    public async Task<WorkflowExecutionResult> RecoverAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var lease = await gate.EnterAsync(instanceId, cancellationToken);
        var instance = await repository.GetWorkflowInstanceAsync(instanceId);
        if (instance?.Status is not (WorkflowStatus.Running or WorkflowStatus.NeedsResolution) ||
            !HasStructuredCheckpoint(instance))
            return Rejected(instanceId, "The workflow instance has no interrupted execution to recover");

        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
        if (definition == null)
            return Rejected(instanceId, "The registered workflow definition is unavailable");
        var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
            ?? throw new InvalidOperationException($"Workflow data type '{instance.WorkflowDataType}' is unavailable");
        var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
            ?? throw new InvalidOperationException("Saved workflow data cannot be read");
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        var scopes = new WorkflowScopeCatalog(definition);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            return Rejected(instanceId, "The registered workflow definition differs from the saved version");
        foreach (var continuation in checkpoint.Continuations.Where(item =>
                     item.PendingActivity != null && item.Status is
                         ContinuationStatus.WaitingResolution or ContinuationStatus.Cancelling))
            continuation.Status = ContinuationStatus.Active;
        instance.Status = WorkflowStatus.Running;
        return await RunAndSaveAsync(definition, instance, checkpoint,
            workflowData, true, cancellationToken);
    }

    private async Task<WorkflowExecutionResult> RunAndSaveAsync(
        WorkflowDefinition definition,
        WorkflowInstance instance,
        ExecutionCheckpoint checkpoint,
        object workflowData,
        bool persistState,
        CancellationToken cancellationToken)
    {
        var inFlight = new Dictionary<string, RunningActivity>();
        var cancellationWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => cancellationWake.TrySetResult());
        var callerCancellation = cancellationToken.CanBeCanceled
            ? cancellationWake.Task
            : null;
        try
        {
            var scopes = new WorkflowScopeCatalog(definition);
            if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
                throw new InvalidOperationException("The registered workflow definition differs from the saved version");
            while (true)
            {
                if (cancellationToken.IsCancellationRequested && !checkpoint.CancellationRequested)
                {
                    checkpoint.CancellationRequested = true;
                    CancelTree(checkpoint, checkpoint.Continuations[0]);
                    await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                }
                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelling))
                    activity.Cancellation.Cancel();

                // Let cancelled activities settle before running their Finally blocks.
                var cancelled = inFlight.Values.FirstOrDefault(item =>
                    item.Continuation.Status == ContinuationStatus.Cancelling);
                if (cancelled != null)
                {
                    await SettleAsync(scopes, cancelled, inFlight, workflowData, instance);
                    await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                    continue;
                }

                var unwinding = checkpoint.Continuations.FirstOrDefault(item =>
                    item.Status == ContinuationStatus.Cancelling && !inFlight.ContainsKey(item.Id));
                if (unwinding != null)
                {
                    if (unwinding.PendingActivity != null)
                    {
                        // A committed Start must be resolved before cancellation can discard
                        // the branch or run its compensation and Finally steps.
                        unwinding.Status = ContinuationStatus.Active;
                        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                        continue;
                    }
                    TryScopeTransitions.ContinueCancellation(scopes, unwinding);
                    await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                    continue;
                }

                FinishCompletingJoins(checkpoint);

                // Advance every runnable branch to its next asynchronous boundary.
                // This lets a sibling start even when another activity is awaiting it.
                while (true)
                {
                    var runnable = checkpoint.Continuations.FirstOrDefault(item =>
                        item.Status == ContinuationStatus.Active && !inFlight.ContainsKey(item.Id));
                    if (runnable == null)
                        break;

                    var position = runnable.Stack[^1];
                    var next = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);
                    if (next?.StepType is WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation)
                    {
                        var sourceData = workflowData;
                        var activityCancellation = new CancellationTokenSource();
                        RunningActivity? running;
                        if (next.StepType == WorkflowStepType.Activity)
                        {
                            running = await StartActivityAsync(scopes, checkpoint, runnable, next,
                                definition, instance, sourceData, workflowData, persistState,
                                activityCancellation);
                        }
                        else
                        {
                            running = await StartInvocationAsync(scopes, checkpoint, runnable, next,
                                definition, instance, sourceData, workflowData, persistState,
                                activityCancellation);
                        }
                        if (running != null)
                            inFlight.Add(runnable.Id, running);
                        else
                            activityCancellation.Dispose();
                    }
                    else
                    {
                        try
                        {
                            Advance(scopes, checkpoint, runnable, workflowData);
                        }
                        catch (Exception error)
                        {
                            if (!CaptureFailure(scopes, checkpoint, runnable, error))
                                throw;
                        }
                        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                        if (inFlight.Values.Any(item => item.Continuation.Status == ContinuationStatus.Cancelling))
                            break;
                    }
                }

                if (inFlight.Count == 0)
                {
                    if (checkpoint.Continuations.Any(item => item.Status == ContinuationStatus.Cancelling) ||
                        checkpoint.Joins.Any(item => item.IsCompleting &&
                            checkpoint.Continuations.Where(child => item.ChildContinuationIds.Contains(child.Id))
                                .All(child => child.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)))
                        continue;
                    break;
                }

                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelling))
                    activity.Cancellation.Cancel();

                var runningTasks = inFlight.Values.Select(item => (Task)item.Task).ToList();
                if (!checkpoint.CancellationRequested && callerCancellation != null)
                    runningTasks.Add(callerCancellation);
                var finished = await Task.WhenAny(runningTasks);
                if (finished == callerCancellation)
                    continue;
                var completedActivity = inFlight.Values.First(item => item.Task == finished);
                try
                {
                    await SettleAsync(scopes, completedActivity, inFlight, workflowData, instance);
                }
                catch (CheckpointPersistenceException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    if (!CaptureFailure(scopes, checkpoint, completedActivity.Continuation, error))
                        throw;
                }
                await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
            }

            if (checkpoint.UnhandledError != null && checkpoint.Waits.Count == 0 && checkpoint.Joins.Count == 0)
                throw checkpoint.RuntimeUnhandledError
                      ?? (checkpoint.UnhandledError.IsRestorable
                          ? checkpoint.UnhandledError.Restore()
                          : new InvalidOperationException(checkpoint.UnhandledError.Message));

            if (checkpoint.CancellationRequested &&
                checkpoint.Continuations[0].Status == ContinuationStatus.Cancelled)
            {
                instance.Status = WorkflowStatus.Cancelled;
                instance.CompletedAt = DateTime.UtcNow;
            }
            else if (checkpoint.Continuations[0].Status == ContinuationStatus.Completed)
            {
                instance.Status = WorkflowStatus.Completed;
                instance.CompletedAt = DateTime.UtcNow;
            }
            else if (checkpoint.Continuations.Any(item =>
                         item.Status == ContinuationStatus.WaitingResolution))
            {
                instance.Status = WorkflowStatus.NeedsResolution;
            }
            else if (checkpoint.Waits.Count > 0)
            {
                instance.Status = WorkflowStatus.Suspended;
            }
            else
            {
                throw new InvalidOperationException("Execution stopped without a runnable continuation or wait");
            }

            instance.SuspensionInfo = checkpoint.Waits.Count == 1
                ? new SuspensionInfo
                {
                    SuspendReason = checkpoint.Waits[0].Key,
                    ResumeEventType = checkpoint.Waits[0].EventType,
                    SuspendedAt = checkpoint.Waits[0].RegisteredAtUtc
                }
                : null;
            await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);

            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = instance.Status switch
                {
                    WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
                    WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
                    WorkflowStatus.NeedsResolution => WorkflowExecutionStatus.NeedsResolution,
                    _ => WorkflowExecutionStatus.Suspended
                },
                WorkflowData = workflowData,
                ErrorMessage = instance.Status switch
                {
                    WorkflowStatus.NeedsResolution => checkpoint.Continuations
                        .First(item => item.Status == ContinuationStatus.WaitingResolution)
                        .PendingActivity?.ResolutionReason,
                    WorkflowStatus.Suspended when checkpoint.Waits.Count == 1 =>
                        $"Workflow suspended: {checkpoint.Waits[0].Key}",
                    WorkflowStatus.Suspended =>
                        $"Workflow suspended with {checkpoint.Waits.Count} active waits",
                    _ => null
                },
                ExecutionTime = DateTime.UtcNow - instance.StartedAt!.Value
            };
        }
        catch (CheckpointPersistenceException ex)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await Task.WhenAll(inFlight.Values.Select(item => item.Task));
            }
            catch
            {
                // Activity outcomes are ignored after the checkpoint write failed.
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }

            ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            throw;
        }
        catch (Exception ex)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await Task.WhenAll(inFlight.Values.Select(item => item.Task));
            }
            catch
            {
                // The original execution error is reported below.
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }

            var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            instance.Status = cancelled ? WorkflowStatus.Cancelled : WorkflowStatus.Failed;
            instance.SuspensionInfo = null;
            checkpoint.Waits.Clear();
            checkpoint.Joins.Clear();
            foreach (var continuation in checkpoint.Continuations.Where(item =>
                         item.Status != ContinuationStatus.Completed))
                continuation.Status = ContinuationStatus.Cancelled;
            instance.LastError = ex.Message;
            instance.LastErrorStackTrace = ex.StackTrace;
            instance.CompletedAt = DateTime.UtcNow;
            instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData, workflowData.GetType());
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            if (instance.ExecutionSnapshot != null)
            {
                instance.ExecutionSnapshot.Status = instance.Status;
                instance.ExecutionSnapshot.Pointers.Clear();
                instance.ExecutionSnapshot.Frames.Clear();
            }
            if (persistState)
                await PersistAsync(instance, checkpoint, workflowData,
                    new WorkflowScopeCatalog(definition), true);
            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = cancelled ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Faulted,
                WorkflowData = workflowData,
                ErrorMessage = ex.Message,
                ErrorType = ex.GetType().FullName,
                ErrorStackTrace = ex.StackTrace
            };
        }
    }

    private void Advance(
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
                throw new InvalidOperationException("The accepted event does not match its saved wait");
            continuation.AcceptedWait = null;
            var @event = accepted.Event.Read(services)
                ?? throw new InvalidOperationException("The accepted event could not be restored");
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
                    ?? throw new InvalidOperationException($"If step '{step.Id}' has no condition");
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
                SagaScopeTransitions.Enter(scopes, step, position, continuation, services);
                break;

            case WorkflowStepType.Loop:
                var shouldRun = position.LoopIterationCount == 0 && step.LoopType == LoopType.DoWhile;
                if (!shouldRun)
                {
                    var loopContext = WorkflowValueBinding.EvaluationContext(workflowData,
                        step.PreviousStepDataType, continuation.GetPrevious(services));
                    shouldRun = step.CompiledCondition?.Invoke(loopContext)
                        ?? throw new InvalidOperationException($"Loop step '{step.Id}' has no condition");
                }
                if (!shouldRun)
                {
                    position.LoopIterationCount = 0;
                    position.NextStepIndex++;
                    break;
                }
                if (position.LoopIterationCount >= 1000)
                    throw new InvalidOperationException("Loop exceeded maximum iterations (1000)");
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
                    ?? throw new InvalidOperationException($"Wait step '{step.Id}' has an invalid key");
                checkpoint.Waits.Add(new WaitState
                {
                    ContinuationId = continuation.Id,
                    StepId = scopes.Id(step),
                    Key = key,
                    EventType = step.ResumeEventType?.AssemblyQualifiedName
                        ?? throw new InvalidOperationException($"Wait step '{step.Id}' has no event type")
                });
                continuation.Status = ContinuationStatus.Waiting;
                break;

            default:
                throw new NotSupportedException($"Structured execution does not yet support {step.StepType}");
        }

    }

    private async Task<RunningActivity?> StartActivityAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object sourceData,
        object workflowData,
        bool persistState,
        CancellationTokenSource activityCancellation)
    {
        var pending = continuation.PendingActivity;
        var isRecovery = pending != null;
        if (pending != null && pending.StepId != scopes.Id(step))
            throw new InvalidOperationException("The saved activity does not match its continuation");

        var invocationId = pending?.Id ?? NewInvocationId(instance, continuation, scopes.Id(step));
        var attempt = new ActivityAttemptState
        {
            Kind = isRecovery ? "Recover" : "Execute"
        };
        PreparedActivity prepared;
        object? recoveryState = null;
        try
        {
            var compensationInputs = SagaScopeTransitions.PrepareCompensationActivity(
                scopes, continuation, services);
            prepared = await _activities.PrepareAsync(step, definition, instance, sourceData,
                continuation.GetPrevious(services),
                TryScopeTransitions.CatchFault(checkpoint, continuation, services),
                activityCancellation.Token, invocationId, attempt.Id,
                continuation.Stack[^1].NextStepIndex, !isRecovery,
                pending?.Inputs.CodeInputs,
                compensationInputs, SagaScopeTransitions.ActivityMetadata(scopes, continuation, step));
            if (pending == null)
            {
                recoveryState = (prepared.Activity as IRecoverableActivity)?
                    .CaptureRecoveryStateObject(prepared.Context);
                if (prepared.Activity is IRecoverableActivity && recoveryState == null)
                    throw new InvalidOperationException(
                        $"Activity '{prepared.Activity.GetType().Name}' returned a null recovery state");
                prepared.SetRecoveryState(recoveryState);
                pending = new ActivityInvocationState
                {
                    Id = invocationId,
                    StepId = scopes.Id(step),
                    Inputs = ActivityInputSnapshot.Capture(prepared.Activity, step),
                    RecoveryState = SerializedValue.From(recoveryState)
                };
                continuation.PendingActivity = pending;
            }
            else
            {
                pending.Inputs.Restore(prepared.Activity, services);
                recoveryState = pending.RecoveryState?.Read(services);
                prepared.SetRecoveryState(recoveryState);
            }
        }
        catch (Exception error) when (!isRecovery)
        {
            return new RunningActivity(continuation, step,
                Task.FromResult(ActivityRunResult.Threw(error)), activityCancellation,
                null, continuation.Stack[^1].NextStepIndex);
        }
        catch (Exception error)
        {
            pending!.ResolutionReason = $"Activity recovery could not be prepared: {error.Message}";
            continuation.Status = ContinuationStatus.WaitingResolution;
            await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
            return null;
        }

        if (isRecovery && prepared.Activity is not IRecoverableActivity)
        {
            pending!.ResolutionReason = "The activity has no recovery contract";
            continuation.Status = ContinuationStatus.WaitingResolution;
            await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
            return null;
        }

        pending!.ResolutionReason = null;
        pending.Attempts.Add(attempt);
        instance.ExecutionHistory.Add(new ExecutionTraceEntry
        {
            EntryType = TraceEntryType.ActivityStarted,
            ActivityName = step.ActivityType?.Name ?? "Activity",
            StepNumber = continuation.Stack[^1].NextStepIndex,
            Metadata = new Dictionary<string, object>
            {
                ["InvocationId"] = invocationId,
                ["AttemptId"] = attempt.Id,
                ["Kind"] = attempt.Kind
            }
        });
        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);

        var skipCancelledRecovery = isRecovery && continuation.CancellationUnwind &&
            !TryScopeTransitions.IsInsideFinally(continuation) &&
            !SagaScopeTransitions.IsInsideCompensation(continuation);
        var task = ObserveActivityAsync(prepared, isRecovery, recoveryState,
            skipCancelledRecovery, activityCancellation.Token);
        return new RunningActivity(continuation, step, task, activityCancellation,
            prepared, continuation.Stack[^1].NextStepIndex);
    }

    private static async Task<ActivityRunResult> ObserveActivityAsync(
        PreparedActivity prepared,
        bool isRecovery,
        object? recoveryState,
        bool skipCancelledRecovery,
        CancellationToken cancellationToken)
    {
        if (isRecovery)
        {
            ActivityRecoveryResolution resolution;
            try
            {
                resolution = await ((IRecoverableActivity)prepared.Activity)
                    .RecoverObjectAsync(recoveryState, prepared.Context, cancellationToken);
            }
            catch (Exception error)
            {
                return ActivityRunResult.Unresolved(
                    $"Recovery check failed: {error.Message}");
            }

            switch (resolution.Disposition)
            {
                case ActivityRecoveryDisposition.Completed:
                    return ActivityRunResult.Returned(prepared.Activity);
                case ActivityRecoveryDisposition.Unresolved:
                    return ActivityRunResult.Unresolved(resolution.Reason ??
                        "The activity outcome is not known");
                case ActivityRecoveryDisposition.Faulted:
                    return ActivityRunResult.Threw(resolution.Error ??
                        new InvalidOperationException("Recovery did not provide an exception"));
                case ActivityRecoveryDisposition.Execute:
                    if (skipCancelledRecovery)
                        return ActivityRunResult.Skipped();
                    break;
                default:
                    return ActivityRunResult.Unresolved("Unknown recovery decision");
            }
        }

        try
        {
            await prepared.Activity.ExecuteAsync(prepared.Context, cancellationToken);
            return ActivityRunResult.Returned(prepared.Activity);
        }
        catch (Exception error)
        {
            return ActivityRunResult.Threw(error);
        }
    }

    private static string NewInvocationId(WorkflowInstance instance,
        ContinuationState continuation, string stepId)
    {
        var position = continuation.Stack[^1];
        var identity = $"{instance.InstanceId}|{position.ActivationId}|{stepId}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static async Task<ActivityRunResult> ObserveInvocationAsync(Task<object?> invocation)
    {
        try
        {
            return ActivityRunResult.Returned(await invocation);
        }
        catch (Exception error)
        {
            return ActivityRunResult.Threw(error);
        }
    }

    private async Task<RunningActivity?> StartInvocationAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object sourceData,
        object workflowData,
        bool persistState,
        CancellationTokenSource cancellation)
    {
        if (continuation.PendingActivity != null)
        {
            continuation.PendingActivity.ResolutionReason =
                "The child workflow outcome is unknown; resolve it before continuing";
            continuation.Status = ContinuationStatus.WaitingResolution;
            await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
            return null;
        }

        var pending = new ActivityInvocationState { StepId = scopes.Id(step) };
        var attempt = new ActivityAttemptState { Kind = "InvokeWorkflow" };
        pending.Attempts.Add(attempt);
        continuation.PendingActivity = pending;
        instance.ExecutionHistory.Add(new ExecutionTraceEntry
        {
            EntryType = TraceEntryType.ActivityStarted,
            ActivityName = step.Name,
            StepNumber = continuation.Stack[^1].NextStepIndex,
            Metadata = new Dictionary<string, object>
            {
                ["InvocationId"] = pending.Id,
                ["AttemptId"] = attempt.Id,
                ["Kind"] = attempt.Kind
            }
        });
        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);

        var task = _invocations.ExecuteAsync(step, definition, instance, sourceData,
            continuation.GetPrevious(services), cancellation.Token);
        return new RunningActivity(continuation, step, ObserveInvocationAsync(task),
            cancellation, null, continuation.Stack[^1].NextStepIndex);
    }

    private async Task SettleAsync(
        WorkflowScopeCatalog scopes,
        RunningActivity activity,
        Dictionary<string, RunningActivity> inFlight,
        object workflowData,
        WorkflowInstance instance)
    {
        try
        {
            var outcome = await activity.Task;
            if (outcome.Kind == ActivityRunKind.Unresolved)
            {
                var pending = activity.Continuation.PendingActivity
                    ?? throw new InvalidOperationException("Unresolved activity has no saved Start");
                pending.ResolutionReason = outcome.Reason;
                pending.Attempts[^1].EndedAtUtc = DateTime.UtcNow;
                pending.Attempts[^1].Observation = "Unresolved";
                activity.Continuation.Status = ContinuationStatus.WaitingResolution;
                return;
            }

            if (outcome.Kind == ActivityRunKind.Skipped)
            {
                MarkActivityEnd(activity, instance, "NotRun");
                activity.Continuation.Status = ContinuationStatus.Cancelling;
                return;
            }

            if (outcome.Kind == ActivityRunKind.Threw)
            {
                MarkActivityEnd(activity, instance, "Threw", outcome.Error);
                throw outcome.Error ?? new InvalidOperationException("Activity failed without an exception");
            }

            var output = outcome.Output;
            try
            {
                // The activity has returned successfully. Record it for saga compensation
                // before applying mappings, which can fail after an external effect.
                SagaScopeTransitions.CompleteForwardActivity(scopes, activity.Continuation,
                    activity.Step, output, services);
                if (activity.Prepared != null)
                    await activity.Prepared.ApplyOutputsAsync();
                if (activity.Continuation.Status is ContinuationStatus.Cancelling or ContinuationStatus.Cancelled ||
                    activity.Continuation.CancellationUnwind &&
                    !TryScopeTransitions.IsInsideFinally(activity.Continuation) &&
                    !SagaScopeTransitions.IsInsideCompensation(activity.Continuation))
                {
                    MarkActivityEnd(activity, instance, "Returned");
                    activity.Continuation.Status = ContinuationStatus.Cancelling;
                    return;
                }

                SagaScopeTransitions.CompleteCompensationActivity(scopes, activity.Continuation, output);
                activity.Continuation.SetPrevious(output);
                activity.Continuation.Stack[^1].StepRetryCounts.Remove(
                    activity.Continuation.Stack[^1].NextStepIndex);
                activity.Continuation.Stack[^1].NextStepIndex++;
                MarkActivityEnd(activity, instance, "Returned");
            }
            catch (Exception error)
            {
                MarkActivityEnd(activity, instance, "OutputFailed", error);
                throw;
            }
        }
        catch (Exception error) when (
            error is not CheckpointPersistenceException &&
            (activity.Continuation.Status is (ContinuationStatus.Cancelling or ContinuationStatus.Cancelled) ||
             activity.Continuation.CancellationUnwind &&
             !TryScopeTransitions.IsInsideFinally(activity.Continuation) &&
             !SagaScopeTransitions.IsInsideCompensation(activity.Continuation)))
        {
            // The parent join has cancelled this branch.
            activity.Continuation.Status = ContinuationStatus.Cancelling;
        }
        finally
        {
            inFlight.Remove(activity.Continuation.Id);
            activity.Cancellation.Dispose();
        }
    }

    private static void MarkActivityEnd(RunningActivity running, WorkflowInstance instance,
        string observation,
        Exception? error = null)
    {
        var pending = running.Continuation.PendingActivity;
        if (pending == null)
            return;
        var attempt = pending.Attempts[^1];
        attempt.EndedAtUtc = DateTime.UtcNow;
        attempt.Observation = observation;
        running.Continuation.PendingActivity = null;
        instance.ExecutionHistory.Add(new ExecutionTraceEntry
        {
            EntryType = error == null ? TraceEntryType.ActivityCompleted : TraceEntryType.ActivityFailed,
            ActivityName = running.Step.ActivityType?.Name ?? running.Step.Name,
            StepNumber = running.StepNumber,
            Duration = attempt.EndedAtUtc - attempt.StartedAtUtc,
            ErrorMessage = error?.Message,
            StackTrace = error?.StackTrace,
            Metadata = new Dictionary<string, object>
            {
                ["InvocationId"] = pending.Id,
                ["AttemptId"] = attempt.Id,
                ["Observation"] = observation
            }
        });
    }

    private enum ActivityRunKind { Returned, Threw, Unresolved, Skipped }

    private sealed record ActivityRunResult(
        ActivityRunKind Kind,
        object? Output = null,
        Exception? Error = null,
        string? Reason = null)
    {
        public static ActivityRunResult Returned(object? output) =>
            new(ActivityRunKind.Returned, Output: output);
        public static ActivityRunResult Threw(Exception error) =>
            new(ActivityRunKind.Threw, Error: error);
        public static ActivityRunResult Unresolved(string reason) =>
            new(ActivityRunKind.Unresolved, Reason: reason);
        public static ActivityRunResult Skipped() => new(ActivityRunKind.Skipped);
    }

    private sealed record RunningActivity(
        ContinuationState Continuation,
        WorkflowStep Step,
        Task<ActivityRunResult> Task,
        CancellationTokenSource Cancellation,
        PreparedActivity? Prepared,
        int StepNumber);

    private static void Fork(WorkflowScopeCatalog scopes, WorkflowStep step, ExecutionCheckpoint checkpoint,
        ContinuationState parent)
    {
        if (step.ParallelBranches.Count == 0)
            throw new InvalidOperationException($"Parallel step '{step.Id}' has no branches");

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
                 ?? throw new InvalidOperationException("Conditional parallel join has no completion condition")) ||
                allCompleted,
            _ => throw new ArgumentOutOfRangeException(nameof(join.Mode))
        };
        if (!shouldJoin)
            return;

        join.IsCompleting = true;
        foreach (var sibling in children.Where(item => item.Status != ContinuationStatus.Completed))
            CancelTree(checkpoint, sibling);
    }

    private static void CancelTree(ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
            return;
        continuation.CancellationUnwind = true;
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

    private static void FinishCompletingJoins(ExecutionCheckpoint checkpoint)
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

    private static bool CaptureFailure(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, Exception error)
    {
        if (!continuation.CancellationUnwind &&
            SagaScopeTransitions.HandleStepFailure(scopes, continuation, error))
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
            join.ResumeParentWithoutAdvance = true;
            foreach (var siblingId in join.ChildContinuationIds.Where(id => id != continuation.Id))
                CancelTree(checkpoint,
                    checkpoint.Continuations.Single(item => item.Id == siblingId));
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        }
    }

    private static WorkflowExecutionResult Rejected(string instanceId, string reason) => new()
    {
        InstanceId = instanceId,
        Status = WorkflowExecutionStatus.Faulted,
        ErrorMessage = reason
    };

    private async Task PersistAsync(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        object workflowData, WorkflowScopeCatalog scopes, bool persistState)
    {
        if (!persistState)
            return;
        try
        {
            PrepareCheckpoint(checkpoint, scopes);
            instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData, workflowData.GetType());
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            instance.ExecutionSnapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes);
            var expectedRevision = instance.Revision;
            var commitId = Guid.NewGuid().ToString("N");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                WorkflowCommitResult result;
                try
                {
                    result = await repository.CommitWorkflowInstanceAsync(
                        instance, expectedRevision, commitId);
                }
                catch (Exception) when (attempt < 2)
                {
                    continue;
                }

                if (result.Status == WorkflowCommitStatus.Conflict)
                    throw new InvalidOperationException(
                        $"Workflow instance '{instance.InstanceId}' changed at revision {result.Revision}");
                instance.Revision = result.Revision;
                return;
            }
        }
        catch (Exception ex)
        {
            throw new CheckpointPersistenceException(ex);
        }
    }

    private sealed class CheckpointPersistenceException(Exception inner) : Exception(
        "The workflow checkpoint could not be saved", inner);

    private static void PrepareCheckpoint(ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes)
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
