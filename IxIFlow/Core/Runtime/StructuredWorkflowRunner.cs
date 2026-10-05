using System.Text.Json;

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
            WorkflowDataType = typeof(TData).AssemblyQualifiedName!,
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
            Type.GetType(wait.EventType)?.IsAssignableFrom(@event.GetType()) == true).ToList();
        if (candidates.Count != 1)
            return Rejected(instanceId, candidates.Count == 0
                ? "No active wait matches this event and key"
                : "The event matches more than one wait; provide a unique key");

        var wait = candidates[0];
        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
        if (definition == null)
            return Rejected(instanceId, "The registered workflow definition is unavailable");

        var dataType = Type.GetType(instance.WorkflowDataType)
            ?? throw new InvalidOperationException($"Workflow data type '{instance.WorkflowDataType}' is unavailable");
        var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
            ?? throw new InvalidOperationException("Saved workflow data cannot be read");
        var scopes = new WorkflowScopeCatalog(definition);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            return Rejected(instanceId, "The registered workflow definition differs from the saved version");
        var step = scopes.Step(wait.StepId);
        if (!WorkflowValueBinding.Matches(step, @event, workflowData))
            return new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = WorkflowExecutionStatus.Suspended,
                WorkflowData = workflowData
            };

        WorkflowValueBinding.ApplyEventOutputs(step, @event, workflowData);
        checkpoint.Waits.Remove(wait);
        var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
        continuation.SetPrevious(@event);
        continuation.Stack[^1].NextStepIndex++;
        continuation.Status = ContinuationStatus.Active;

        instance.Status = WorkflowStatus.Running;
        await PersistAsync(instance, checkpoint, workflowData, scopes);

        return await RunAndSaveAsync(definition, instance, checkpoint, workflowData, cancellationToken);
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
        var dataType = Type.GetType(instance.WorkflowDataType)
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
        try
        {
            var scopes = new WorkflowScopeCatalog(definition);
            if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
                throw new InvalidOperationException("The registered workflow definition differs from the saved version");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelled))
                    activity.Cancellation.Cancel();

                // A join cannot leave an activity running in a cancelled branch.
                var cancelled = inFlight.Values.FirstOrDefault(item =>
                    item.Continuation.Status == ContinuationStatus.Cancelled);
                if (cancelled != null)
                {
                    await SettleAsync(cancelled, inFlight, workflowData);
                    await PersistAsync(instance, checkpoint, workflowData, scopes);
                    continue;
                }

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
                        var sourceData = runnable.ParentJoinId == null
                            ? workflowData
                            : JsonSerializer.Deserialize(
                                JsonSerializer.Serialize(workflowData, workflowData.GetType()),
                                workflowData.GetType())!;
                        var sourceIsIsolated = !ReferenceEquals(sourceData, workflowData);
                        var activityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var task = _activities.ExecuteAsync(next, definition, instance, sourceData,
                            runnable.GetPrevious(services), activityCancellation.Token);
                        inFlight.Add(runnable.Id, new RunningActivity(runnable, next, task,
                            activityCancellation, sourceIsIsolated));
                    }
                    else
                    {
                        Advance(scopes, checkpoint, runnable, workflowData);
                        await PersistAsync(instance, checkpoint, workflowData, scopes);
                        if (inFlight.Values.Any(item => item.Continuation.Status == ContinuationStatus.Cancelled))
                            break;
                    }
                }

                if (inFlight.Count == 0)
                    break;

                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelled))
                    activity.Cancellation.Cancel();

                var finished = await Task.WhenAny(inFlight.Values.Select(item => item.Task));
                var completedActivity = inFlight.Values.First(item => item.Task == finished);
                await SettleAsync(completedActivity, inFlight, workflowData);
                await PersistAsync(instance, checkpoint, workflowData, scopes);
            }

            if (checkpoint.Continuations[0].Status == ContinuationStatus.Completed)
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
                Status = instance.Status == WorkflowStatus.Completed
                    ? WorkflowExecutionStatus.Success
                    : WorkflowExecutionStatus.Suspended,
                WorkflowData = workflowData,
                ExecutionTime = DateTime.UtcNow - instance.StartedAt!.Value
            };
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
                continuation.Stack[^1].NextStepIndex++;
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

            case WorkflowStepType.SuspendResume when step.StepMetadata.TryGetValue("WaitKey", out var keyValue):
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

    private static async Task SettleAsync(
        RunningActivity activity,
        Dictionary<string, RunningActivity> inFlight,
        object workflowData)
    {
        try
        {
            var output = await activity.Task;
            if (activity.Continuation.Status == ContinuationStatus.Cancelled)
                return;

            if (activity.SourceIsIsolated)
                WorkflowValueBinding.ApplyActivityOutputs(activity.Step, output, workflowData);
            activity.Continuation.SetPrevious(output);
            activity.Continuation.Stack[^1].NextStepIndex++;
        }
        catch (OperationCanceledException) when (activity.Continuation.Status == ContinuationStatus.Cancelled)
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

        foreach (var sibling in children.Where(item => item.Status != ContinuationStatus.Completed))
            CancelTree(checkpoint, sibling);

        var parent = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        parent.Stack[^1].NextStepIndex++;
        parent.Status = ContinuationStatus.Active;
        checkpoint.Joins.Remove(join);
    }

    private static void CancelTree(ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        continuation.Status = ContinuationStatus.Cancelled;
        checkpoint.Waits.RemoveAll(wait => wait.ContinuationId == continuation.Id);
        foreach (var join in checkpoint.Joins.Where(item => item.ParentContinuationId == continuation.Id).ToList())
        {
            foreach (var childId in join.ChildContinuationIds)
                CancelTree(checkpoint, checkpoint.Continuations.Single(item => item.Id == childId));
            checkpoint.Joins.Remove(join);
        }
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
        await repository.SaveWorkflowInstanceAsync(instance);
    }

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
            }
        }
    }
}
