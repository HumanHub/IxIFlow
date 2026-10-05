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
    IWorkflowStateRepository repository,
    IWorkflowVersionRegistry registry,
    InProcessInstanceGate gate)
{
    private readonly ActivityTransitionExecutor _activities = new(activityExecutor);

    public static bool UsesStructuredExecution(WorkflowDefinition definition) =>
        WorkflowScopeCatalog.RequiresStructuredExecution(definition.Steps);

    public static bool HasStructuredCheckpoint(WorkflowInstance? instance) =>
        instance != null && ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson);

    public async Task<WorkflowExecutionResult> StartAsync<TData>(
        WorkflowDefinition definition,
        TData workflowData,
        WorkflowOptions options,
        CancellationToken cancellationToken)
        where TData : class
    {
        if (!options.PersistState)
            throw new InvalidOperationException("WaitFor requires workflow state persistence");

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
        await registry.RegisterWorkflowAsync(definition);
        await repository.SaveWorkflowInstanceAsync(instance);
        return await RunAndSaveAsync(definition, instance, checkpoint, workflowData, cancellationToken);
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
        if (instance?.Status != WorkflowStatus.Suspended || !HasStructuredCheckpoint(instance))
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
        var step = scopes.Step(wait.StepId);
        var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);

        WorkflowValueBinding.ApplyEventOutputs(step, @event, workflowData);
        checkpoint.Waits.Remove(wait);
        continuation.SetPrevious(@event);
        continuation.Stack[^1].NextStepIndex++;
        continuation.Status = ContinuationStatus.Active;

        instance.Status = WorkflowStatus.Running;
        await PersistAsync(instance, checkpoint, workflowData, scopes);

        var resumed = await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
            cancellationToken);
        resumed.EventAccepted = true;
        return resumed;
    }

    public async Task<WorkflowExecutionResult> RecoverAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var lease = await gate.EnterAsync(instanceId, cancellationToken);
        var instance = await repository.GetWorkflowInstanceAsync(instanceId);
        if (instance?.Status != WorkflowStatus.Running || !HasStructuredCheckpoint(instance))
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
        return await RunAndSaveAsync(definition, instance, checkpoint,
            workflowData, cancellationToken);
    }

    private async Task<WorkflowExecutionResult> RunAndSaveAsync(
        WorkflowDefinition definition,
        WorkflowInstance instance,
        ExecutionCheckpoint checkpoint,
        object workflowData,
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
                    await PersistAsync(instance, checkpoint, workflowData, scopes);
                }
                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelling))
                    activity.Cancellation.Cancel();

                // Let cancelled activities settle before running their Finally blocks.
                var cancelled = inFlight.Values.FirstOrDefault(item =>
                    item.Continuation.Status == ContinuationStatus.Cancelling);
                if (cancelled != null)
                {
                    await SettleAsync(scopes, cancelled, inFlight, workflowData);
                    await PersistAsync(instance, checkpoint, workflowData, scopes);
                    continue;
                }

                var unwinding = checkpoint.Continuations.FirstOrDefault(item =>
                    item.Status == ContinuationStatus.Cancelling && !inFlight.ContainsKey(item.Id));
                if (unwinding != null)
                {
                    TryScopeTransitions.ContinueCancellation(scopes, unwinding);
                    await PersistAsync(instance, checkpoint, workflowData, scopes);
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
                    if (next?.StepType == WorkflowStepType.Activity)
                    {
                        var compensationInputs = SagaScopeTransitions.PrepareCompensationActivity(
                            scopes, runnable, services);
                        var sourceData = runnable.ParentJoinId == null
                            ? workflowData
                            : JsonSerializer.Deserialize(
                                JsonSerializer.Serialize(workflowData, workflowData.GetType()),
                                workflowData.GetType())!;
                        var sourceIsIsolated = !ReferenceEquals(sourceData, workflowData);
                        var activityCancellation = new CancellationTokenSource();
                        var task = _activities.ExecuteAsync(next, definition, instance, sourceData,
                            runnable.GetPrevious(services), TryScopeTransitions.CatchException(checkpoint, runnable),
                            activityCancellation.Token, compensationInputs);
                        inFlight.Add(runnable.Id, new RunningActivity(runnable, next, task,
                            activityCancellation, sourceIsIsolated));
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
                        await PersistAsync(instance, checkpoint, workflowData, scopes);
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
                    await SettleAsync(scopes, completedActivity, inFlight, workflowData);
                }
                catch (Exception error)
                {
                    if (!CaptureFailure(scopes, checkpoint, completedActivity.Continuation, error))
                        throw;
                }
                await PersistAsync(instance, checkpoint, workflowData, scopes);
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
            await PersistAsync(instance, checkpoint, workflowData, scopes);

            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = instance.Status switch
                {
                    WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
                    WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
                    _ => WorkflowExecutionStatus.Suspended
                },
                WorkflowData = workflowData,
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
            await repository.SaveWorkflowInstanceAsync(instance);
            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = cancelled ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Faulted,
                WorkflowData = workflowData,
                ErrorMessage = ex.Message,
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

    private async Task SettleAsync(
        WorkflowScopeCatalog scopes,
        RunningActivity activity,
        Dictionary<string, RunningActivity> inFlight,
        object workflowData)
    {
        try
        {
            var output = await activity.Task;
            if (activity.Continuation.Status is ContinuationStatus.Cancelling or ContinuationStatus.Cancelled)
            {
                if (activity.Continuation.Status == ContinuationStatus.Cancelling &&
                    SagaScopeTransitions.IsForwardActivity(scopes, activity.Continuation, activity.Step))
                {
                    if (activity.SourceIsIsolated)
                        WorkflowValueBinding.ApplyActivityOutputs(activity.Step, output, workflowData);
                    SagaScopeTransitions.CompleteForwardActivity(scopes, activity.Continuation,
                        activity.Step, output, services);
                }
                return;
            }

            if (activity.SourceIsIsolated)
                WorkflowValueBinding.ApplyActivityOutputs(activity.Step, output, workflowData);
            SagaScopeTransitions.CompleteForwardActivity(scopes, activity.Continuation, activity.Step,
                output, services);
            SagaScopeTransitions.CompleteCompensationActivity(scopes, activity.Continuation, output);
            activity.Continuation.SetPrevious(output);
            activity.Continuation.Stack[^1].NextStepIndex++;
        }
        catch (Exception) when (activity.Continuation.Status is
            ContinuationStatus.Cancelling or ContinuationStatus.Cancelled)
        {
            // The parent join has cancelled this branch.
        }
        finally
        {
            inFlight.Remove(activity.Continuation.Id);
            activity.Cancellation.Dispose();
        }
    }

    private sealed record RunningActivity(
        ContinuationState Continuation,
        WorkflowStep Step,
        Task<object?> Task,
        CancellationTokenSource Cancellation,
        bool SourceIsIsolated);

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
        if (TryScopeTransitions.IsInsideFinally(continuation) ||
            SagaScopeTransitions.IsInsideCompensation(continuation))
        {
            TryScopeTransitions.PreserveExistingFinally(continuation);
            SagaScopeTransitions.PreserveCompensation(continuation);
            return;
        }
        checkpoint.Waits.RemoveAll(wait => wait.ContinuationId == continuation.Id);
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
                CancelTree(checkpoint, parent);
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
        if (continuation.CancellationUnwind)
        {
            if (SagaScopeTransitions.SkipFailedCompensation(continuation, error))
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
            return true;
        }
        while (true)
        {
            if (SagaScopeTransitions.SkipFailedCompensation(continuation, error) ||
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
            SagaScopeTransitions.Capture(continuation, sagaIndex, error);
            return true;
        }
        return tryIndex >= 0 && TryScopeTransitions.Capture(scopes, continuation, error);
    }

    private static WorkflowExecutionResult Rejected(string instanceId, string reason) => new()
    {
        InstanceId = instanceId,
        Status = WorkflowExecutionStatus.Faulted,
        ErrorMessage = reason
    };

    private async Task PersistAsync(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        object workflowData, WorkflowScopeCatalog scopes)
    {
        PrepareCheckpoint(checkpoint);
        instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData, workflowData.GetType());
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        instance.ExecutionSnapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes);
        try
        {
            await repository.SaveWorkflowInstanceAsync(instance);
        }
        catch (Exception ex)
        {
            throw new CheckpointPersistenceException(ex);
        }
    }

    private sealed class CheckpointPersistenceException(Exception inner) : Exception(
        "The workflow checkpoint could not be saved", inner);

    private static void PrepareCheckpoint(ExecutionCheckpoint checkpoint)
    {
        foreach (var continuation in checkpoint.Continuations)
        {
            if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
            {
                continuation.Previous = null;
                continue;
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
