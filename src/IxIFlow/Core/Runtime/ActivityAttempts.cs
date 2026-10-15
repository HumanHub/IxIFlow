namespace IxIFlow.Core.Runtime;

/// <summary>
/// Prepares, runs and settles one activity or child invocation attempt. Commits and child
/// workflows go through the host.
/// </summary>
internal sealed class ActivityAttempts(
    IServiceProvider services,
    IActivityExecutor activityExecutor,
    ContinuationTransitions transitions,
    TimeProvider time)
{
    private readonly ActivityTransitionExecutor _activities = new(activityExecutor);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<RunningActivity?> StartActivityAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object sourceData,
        object workflowData,
        IExecutionHost host,
        CancellationTokenSource activityCancellation)
    {
        var pending = continuation.PendingActivity;
        var isRecovery = pending != null;
        if (pending != null && pending.StepId != scopes.Id(step))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "The saved activity does not match its continuation");

        var invocationId = pending?.Id ?? NewInvocationId(instance, continuation, scopes.Id(step));
        var attempt = new ActivityAttemptState
        {
            StartedAtUtc = Now,
            Kind = pending?.Resolution is { } resolution
                ? $"Operator{resolution.Kind}"
                : isRecovery ? "Recover" : "Execute"
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
                    StartedAtUtc = Now,
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
                if (pending.Resolution?.Kind == ActivityResolutionKind.Completed)
                {
                    foreach (var (name, value) in pending.Resolution.OutputProperties)
                        prepared.Activity.GetType().GetProperty(name)!
                            .SetValue(prepared.Activity, value?.Read(services));
                }
            }
        }
        catch (Exception error) when (!isRecovery && !WorkflowEngineException.IsEngineFailure(error))
        {
            return new RunningActivity(continuation, step,
                Task.FromResult(ActivityRunResult.Threw(error)), activityCancellation,
                null, continuation.Stack[^1].NextStepIndex);
        }
        catch (Exception error) when (isRecovery && error is not WorkflowEngineException { IsInterruption: true })
        {
            pending!.ResolutionReason = $"Activity recovery could not be prepared: {error.Message}";
            continuation.Status = ContinuationStatus.WaitingResolution;
            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
            return null;
        }
        catch (WorkflowEngineException error)
        {
            error.At(instance.InstanceId, scopes.Id(step), attempt.Id);
            throw;
        }

        if (isRecovery && pending!.Resolution == null && prepared.Activity is not IRecoverableActivity)
        {
            pending!.ResolutionReason = "The activity has no recovery contract";
            continuation.Status = ContinuationStatus.WaitingResolution;
            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
            return null;
        }

        pending!.ResolutionReason = null;
        pending.Attempts.Add(attempt);
        var startTrace = new ExecutionTraceEntry
        {
            Timestamp = Now,
            EntryType = TraceEntryType.ActivityStarted,
            ActivityName = step.ActivityType?.Name ?? "Activity",
            StepNumber = continuation.Stack[^1].NextStepIndex,
            Metadata = new Dictionary<string, object>
            {
                ["InvocationId"] = invocationId,
                ["AttemptId"] = attempt.Id,
                ["Kind"] = attempt.Kind
            }
        };
        if (pending.Resolution != null)
        {
            startTrace.Metadata["DecisionNote"] = pending.Resolution.Note;
            if (pending.Resolution.DecidedBy != null)
                startTrace.Metadata["DecidedBy"] = pending.Resolution.DecidedBy;
        }
        if (instance.TracingEnabled)
            instance.ExecutionHistory.Add(startTrace);
        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);

        var skipCancelledRecovery = isRecovery && continuation.CancellationUnwind &&
            !TryScopeTransitions.IsInsideFinally(continuation) &&
            !SagaScopeTransitions.IsInsideCompensation(continuation);
        var task = ObserveActivityAsync(prepared, isRecovery, recoveryState, pending.Resolution,
            skipCancelledRecovery, activityCancellation.Token);
        return new RunningActivity(continuation, step, task, activityCancellation,
            prepared, continuation.Stack[^1].NextStepIndex);
    }

    private static async Task<ActivityRunResult> ObserveActivityAsync(
        PreparedActivity prepared,
        bool isRecovery,
        object? recoveryState,
        ActivityResolutionState? operatorResolution,
        bool skipCancelledRecovery,
        CancellationToken cancellationToken)
    {
        if (operatorResolution?.Kind == ActivityResolutionKind.Completed)
            return ActivityRunResult.Returned(prepared.Activity);
        if (operatorResolution?.Kind == ActivityResolutionKind.Failed)
            return ActivityRunResult.Threw(operatorResolution.Failure?.ForPropagation()
                ?? new InvalidOperationException(operatorResolution.Note));
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
                        return ActivityRunResult.Unresolved(
                            "Cancellation interrupted the activity before its outcome was confirmed");
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

    private static async Task<ActivityRunResult> ObserveInvocationAsync(
        Task<WorkflowInvocationTransitionResult> invocation)
    {
        try
        {
            var result = await invocation;
            return result.Suspended
                ? ActivityRunResult.Suspended(result.ChildInstanceId!)
                : ActivityRunResult.Returned(result.Output);
        }
        catch (Exception error) when (!WorkflowEngineException.IsEngineFailure(error))
        {
            return ActivityRunResult.Threw(error);
        }
    }

    public async Task<RunningActivity?> StartInvocationAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object sourceData,
        object workflowData,
        IExecutionHost host,
        CancellationTokenSource cancellation)
    {
        var pending = continuation.PendingActivity;
        if (pending != null && pending.Resolution == null)
        {
            var savedChild = await host.ReadChildAsync(pending.Id);
            // The child commits its initial checkpoint before running any activity.
            // No child record means the invocation can be started with the same ID.
            if (savedChild == null && !host.ChildStartIsCheckpointed)
            {
                pending.ResolutionReason =
                    "The child workflow outcome is unknown; resolve it before continuing";
                continuation.Status = ContinuationStatus.WaitingResolution;
                await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
                return null;
            }
            if (savedChild != null)
                pending.ChildWorkflowId = savedChild.InstanceId;
        }

        pending ??= new ActivityInvocationState { StepId = scopes.Id(step), StartedAtUtc = Now };
        if (host.ChildStartIsCheckpointed)
            pending.ChildWorkflowId ??= pending.Id;
        var attempt = new ActivityAttemptState
        {
            StartedAtUtc = Now,
            Kind = pending.Resolution == null ? "InvokeWorkflow" :
                $"Operator{pending.Resolution.Kind}"
        };
        pending.Attempts.Add(attempt);
        continuation.PendingActivity = pending;
        var startTrace = new ExecutionTraceEntry
        {
            Timestamp = Now,
            EntryType = TraceEntryType.ActivityStarted,
            ActivityName = step.Name,
            StepNumber = continuation.Stack[^1].NextStepIndex,
            Metadata = new Dictionary<string, object>
            {
                ["InvocationId"] = pending.Id,
                ["AttemptId"] = attempt.Id,
                ["Kind"] = attempt.Kind
            }
        };
        if (pending.Resolution != null)
        {
            startTrace.Metadata["DecisionNote"] = pending.Resolution.Note;
            if (pending.Resolution.DecidedBy != null)
                startTrace.Metadata["DecidedBy"] = pending.Resolution.DecidedBy;
        }
        if (instance.TracingEnabled)
            instance.ExecutionHistory.Add(startTrace);
        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);

        if (ContinuationTransitions.ShouldCancelInvokedChild(continuation) &&
            pending.Resolution == null &&
            pending.ChildWorkflowId != null)
        {
            var child = await host.ReadChildAsync(pending.ChildWorkflowId);
            if (child == null && host.ChildStartIsCheckpointed)
                return new RunningActivity(continuation, step,
                    Task.FromResult(ActivityRunResult.Skipped()), cancellation, null,
                    continuation.Stack[^1].NextStepIndex);
            if (child?.Status is WorkflowStatus.Suspended or WorkflowStatus.Running or
                WorkflowStatus.NeedsResolution)
            {
                var childResult = await host.CancelChildAsync(pending.ChildWorkflowId,
                    instance.CancellationReason ?? new CancellationReason
                    {
                        ReasonCode = "parent_branch_cancelled",
                        Description = "The parent workflow branch was cancelled"
                    }, cancellation.Token);
                if (childResult.Status == WorkflowExecutionStatus.Running)
                    childResult = await host.RecoverChildAsync(pending.ChildWorkflowId,
                        cancellation.Token);
                if (childResult.Status is WorkflowExecutionStatus.Cancelled or
                    WorkflowExecutionStatus.Running or WorkflowExecutionStatus.Suspended or
                    WorkflowExecutionStatus.NeedsResolution)
                {
                    var cancellationOutcome = childResult.Status == WorkflowExecutionStatus.Cancelled
                        ? ActivityRunResult.Skipped()
                        : ActivityRunResult.Suspended(pending.ChildWorkflowId);
                    return new RunningActivity(continuation, step,
                        Task.FromResult(cancellationOutcome), cancellation, null,
                        continuation.Stack[^1].NextStepIndex);
                }
                // A terminal child failure or completion is observed through the
                // ordinary invocation path below, including its saved output or error.
            }
            if (child?.Status == WorkflowStatus.Cancelled)
                return new RunningActivity(continuation, step,
                    Task.FromResult(ActivityRunResult.Skipped()), cancellation, null,
                    continuation.Stack[^1].NextStepIndex);
        }

        Task<ActivityRunResult> task;
        if (pending.Resolution is { } decision)
            task = Task.FromResult(decision.Kind == ActivityResolutionKind.Completed
                ? ActivityRunResult.Returned(decision.InvocationResult?.Read(services))
                : ActivityRunResult.Threw(decision.Failure?.ForPropagation()
                    ?? new InvalidOperationException(decision.Note)));
        else
        {
            var accepted = continuation.AcceptedWait;
            if (accepted?.StepId == scopes.Id(step) && pending.ChildWorkflowId != null)
            {
                var childResult = await ResumeChildWaitAsync(host, pending.ChildWorkflowId,
                    accepted, cancellation.Token);
                accepted.EventAccepted = childResult.EventAccepted;
                if (childResult.EventAccepted && accepted.DeliveryHash != null)
                    checkpoint.AcceptedDeliveries.TryAdd(accepted.DeliveryId!,
                        accepted.DeliveryHash);
            }
            task = ObserveInvocationAsync(host.InvokeChildAsync(step, definition, instance,
                sourceData, continuation.GetPrevious(services), pending.Id, cancellation.Token));
        }
        return new RunningActivity(continuation, step, task,
            cancellation, null, continuation.Stack[^1].NextStepIndex);
    }

    private Task<WorkflowExecutionResult> ResumeChildWaitAsync(IExecutionHost host, string childInstanceId,
        AcceptedWaitState accepted, CancellationToken cancellationToken)
    {
        var @event = accepted.Event.Read(services)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A child resume event cannot be null");
        return host.ResumeChildAsync(childInstanceId, accepted.Key, @event, accepted.DeliveryId!,
            cancellationToken);
    }

    private async Task ProjectChildStateAsync(IExecutionHost host, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, string childInstanceId)
    {
        var child = await host.ReadChildAsync(childInstanceId)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Child workflow '{childInstanceId}' was not saved");
        if (child.Status is WorkflowStatus.Running or WorkflowStatus.NeedsResolution ||
            child.Status == WorkflowStatus.Suspended && ContinuationTransitions.ShouldCancelInvokedChild(continuation))
        {
            transitions.ProjectChildLifecycleWait(checkpoint, continuation, childInstanceId,
                child.Status == WorkflowStatus.NeedsResolution);
            return;
        }
        if (child.Status != WorkflowStatus.Suspended)
        {
            continuation.Status = ContinuationStatus.Active;
            return;
        }
        if (!ExecutionCheckpoint.IsStructured(child.ExecutionStateJson))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Child workflow '{childInstanceId}' has no saved waits to project");
        var childCheckpoint = ExecutionCheckpoint.Read(child.ExecutionStateJson);
        if (childCheckpoint.Waits.Count == 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Suspended child workflow '{childInstanceId}' has no active waits");

        transitions.ProjectChildWaits(checkpoint, continuation, childInstanceId, childCheckpoint);
    }

    public async Task SettleAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        RunningActivity activity,
        Dictionary<string, RunningActivity> inFlight,
        object workflowData,
        WorkflowInstance instance,
        IExecutionHost host)
    {
        try
        {
            var outcome = await activity.Task;
            if (outcome.Kind == ActivityRunKind.Unresolved)
            {
                MarkActivityUnresolved(activity, outcome.Reason);
                return;
            }

            if (outcome.Kind == ActivityRunKind.Suspended)
            {
                await ProjectChildStateAsync(host, checkpoint, activity.Continuation,
                    outcome.ChildWorkflowId!);
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
                if (outcome.Error is OperationCanceledException &&
                    activity.Step.StepType == WorkflowStepType.WorkflowInvocation &&
                    ContinuationTransitions.ShouldCancelInvokedChild(activity.Continuation) &&
                    activity.Continuation.PendingActivity?.ChildWorkflowId is { } childId &&
                    (await host.ReadChildAsync(childId))?.Status is
                        (WorkflowStatus.Running or WorkflowStatus.Suspended or
                            WorkflowStatus.NeedsResolution))
                {
                    // The child stopped through the parent's cancellation token.
                    // Its saved activity Start still needs reconciliation.
                    activity.Continuation.Status = ContinuationStatus.Cancelling;
                    return;
                }
                MarkActivityEnd(activity, instance, "Threw", outcome.Error);
                throw outcome.Error ?? new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "Activity failed without an exception");
            }

            activity.ActivityReturned = true;
            var output = outcome.Output;
            try
            {
                // The activity has returned successfully. Record it for saga compensation
                // before applying mappings, which can fail after an external effect.
                try
                {
                    SagaScopeTransitions.CompleteForwardActivity(scopes, activity.Continuation,
                        activity.Step, output, services);
                }
                catch (Exception error)
                {
                    MarkActivityUnresolved(activity,
                        $"Activity returned, but its saga output could not be saved: {error.Message}");
                    return;
                }
                if (activity.Prepared != null)
                    await activity.Prepared.ApplyOutputsAsync();
                else if (activity.Step.StepType == WorkflowStepType.WorkflowInvocation &&
                         activity.Continuation.PendingActivity?.Resolution?.Kind == ActivityResolutionKind.Completed)
                    WorkflowValueBinding.ApplyActivityOutputs(activity.Step, output, workflowData);
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
                if (activity.Step.StepType == WorkflowStepType.WorkflowInvocation)
                    activity.Continuation.AcceptedWait = null;
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
            !WorkflowEngineException.IsEngineFailure(error) &&
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

    private void MarkActivityUnresolved(RunningActivity activity, string? reason)
    {
        var pending = activity.Continuation.PendingActivity
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "Unresolved activity has no saved Start");
        pending.ResolutionReason = reason;
        pending.Attempts[^1].EndedAtUtc = Now;
        pending.Attempts[^1].Observation = "Unresolved";
        activity.Continuation.Status = ContinuationStatus.WaitingResolution;
    }

    private void MarkActivityEnd(RunningActivity running, WorkflowInstance instance,
        string observation,
        Exception? error = null)
    {
        var pending = running.Continuation.PendingActivity;
        if (pending == null)
            return;
        var attempt = pending.Attempts[^1];
        attempt.EndedAtUtc = Now;
        attempt.Observation = observation;
        running.Continuation.PendingActivity = null;
        if (instance.TracingEnabled)
            instance.ExecutionHistory.Add(new ExecutionTraceEntry
        {
            Timestamp = Now,
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
}

internal enum ActivityRunKind { Returned, Threw, Unresolved, Skipped, Suspended }

internal sealed record ActivityRunResult(
    ActivityRunKind Kind,
    object? Output = null,
    Exception? Error = null,
    string? Reason = null,
    string? ChildWorkflowId = null)
{
    public static ActivityRunResult Returned(object? output) =>
        new(ActivityRunKind.Returned, Output: output);
    // A thrown result is always the step's own failure; engine failures fault the activity task instead.
    public static ActivityRunResult Threw(Exception error) =>
        new(ActivityRunKind.Threw, Error: WorkflowEngineException.FromWorkflowCode(error));
    public static ActivityRunResult Unresolved(string reason) =>
        new(ActivityRunKind.Unresolved, Reason: reason);
    public static ActivityRunResult Skipped() => new(ActivityRunKind.Skipped);
    public static ActivityRunResult Suspended(string childWorkflowId) =>
        new(ActivityRunKind.Suspended, ChildWorkflowId: childWorkflowId);
}

internal sealed record RunningActivity(
    ContinuationState Continuation,
    WorkflowStep Step,
    Task<ActivityRunResult> Task,
    CancellationTokenSource Cancellation,
    PreparedActivity? Prepared,
    int StepNumber)
{
    public bool ActivityReturned { get; set; }
}
