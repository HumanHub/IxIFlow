using Microsoft.Extensions.Logging;

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
    private readonly ILogger _logger = LogScope.Logger(services);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<RunningActivity?> StartActivityAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        IExecutionHost host,
        CancellationTokenSource activityCancellation)
    {
        checkpoint.Touch(continuation);
        var pending = continuation.PendingActivity;
        var isRecovery = pending != null;
        if (pending != null && pending.StepId != scopes.Id(step))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "The saved activity does not match its continuation");

        var invocationId = pending?.Id ?? NewInvocationId(instance, continuation, scopes.Id(step));
        // The attempt, its activity's own logging included, logs with its step and invocation (G-03).
        using var logScope = _logger.BeginScope(LogScope.ForStep(scopes.Id(step), invocationId));
        // A step retry continues the invocation its failed attempt began, from the inputs that attempt saved (D-14).
        var retried = pending == null && continuation.RetryInputs?.InvocationId == invocationId
            ? continuation.RetryInputs
            : null;
        var retryInputs = retried?.Inputs;
        continuation.RetryInputs = null;
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
            prepared = await _activities.PrepareAsync(step, definition, instance, checkpoint.DataOf(continuation),
                continuation.GetPrevious(services),
                TryScopeTransitions.CatchFault(checkpoint, continuation, services),
                activityCancellation.Token, invocationId, attempt.Id,
                continuation.Stack[^1].NextStepIndex, !isRecovery && retryInputs == null,
                (pending?.Inputs ?? retryInputs)?.CodeInputs,
                compensationInputs, SagaScopeTransitions.ActivityMetadata(scopes, continuation, step),
                ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
            if (pending == null)
            {
                retryInputs?.Restore(prepared.Activity, services);
                recoveryState = (prepared.Activity as IRecoverableActivity)?
                    .CaptureRecoveryStateObject(prepared.Context);
                if (prepared.Activity is IRecoverableActivity && recoveryState == null)
                    throw new InvalidOperationException(
                        $"Activity '{prepared.Activity.GetType().Name}' returned a null recovery state");
                prepared.SetRecoveryState(recoveryState);
                pending = new ActivityInvocationState
                {
                    StartedAtUtc = retried?.StartedAtUtc ?? Now,
                    Id = invocationId,
                    StepId = scopes.Id(step),
                    Inputs = retryInputs ?? prepared.Inputs,
                    RecoveryState = SerializedValue.From(recoveryState),
                    Attempts = retried?.Attempts ?? []
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
            if (step.RequestName != null)
                prepared.SetReply(ReplyAddressFor(scopes, checkpoint, continuation, step, instance, pending));
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
            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);
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
            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);
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
            instance.Trace.Add(startTrace);
        // Claiming the attempt is not progress: an activity that kills its owner keeps counting.
        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now,
            clearsCrashCount: false);

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

    // A request's answer gets its wait before the activity runs, so no answer can come too early for it
    // (D-33). A retry of the step keeps the wait and its token, even once the answer has arrived.
    private ReplyAddress ReplyAddressFor(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep step, WorkflowInstance instance,
        ActivityInvocationState pending)
    {
        if (checkpoint.Waits.FirstOrDefault(wait => wait.IsReply && wait.InvocationId == pending.Id) is { } open)
            return new ReplyAddress(ReplyToken.Create(instance.InstanceId, open.Id, open.Subject));
        if (checkpoint.BoundReplies.FirstOrDefault(reply => reply.InvocationId == pending.Id) is { } answered)
            return new ReplyAddress(ReplyToken.Create(instance.InstanceId, answered.WaitId, answered.Subject));
        var position = continuation.Stack[^1];
        var waitFor = scopes.Steps(position.ScopeId).Skip(position.NextStepIndex + 1)
            .First(candidate => candidate.ReplyToRequest == step.RequestName);
        var subject = step.RequestSubject is { } describe
            ? ReplyToken.Digest(describe(WorkflowValueBinding.EvaluationContext(checkpoint.DataOf(continuation),
                step.PreviousStepDataType, continuation.GetPrevious(services),
                ForEachTransitions.ItemsOf(scopes, checkpoint, continuation))))
            : null;
        var wait = new WaitState
        {
            RegisteredAtUtc = Now,
            ContinuationId = continuation.Id,
            StepId = scopes.Id(waitFor),
            Key = step.RequestName!,
            EventType = step.ReplyType!.AssemblyQualifiedName!,
            IsReply = true,
            InvocationId = pending.Id,
            ScopeActivationId = position.ActivationId,
            Subject = subject
        };
        checkpoint.AddWait(wait);
        return new ReplyAddress(ReplyToken.Create(instance.InstanceId, wait.Id, subject));
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

    /// <summary>
    /// Runs a child invocation step (D-17). With nothing pending it starts the child: it resolves the
    /// child's definition, maps the inputs into new child data and waits for the child, whose start the
    /// next commit carries with the wait; nothing runs here. Once the child's completion has been taken,
    /// it gives the child's outcome to settle like an activity's. An operator's decision about the
    /// invocation is settled the same way.
    /// </summary>
    public async Task<RunningActivity?> StartInvocationAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        IExecutionHost host,
        CancellationTokenSource cancellation)
    {
        using var logScope = _logger.BeginScope(LogScope.ForStep(scopes.Id(step), continuation.PendingActivity?.Id));
        checkpoint.Touch(continuation);
        var stepNumber = continuation.Stack[^1].NextStepIndex;
        RunningActivity Settles(ActivityRunResult outcome) =>
            new(continuation, step, Task.FromResult(outcome), cancellation, null, stepNumber);
        var pending = continuation.PendingActivity;
        if (pending?.Resolution is { } decision)
        {
            Attempt(instance, continuation, step, pending, $"Operator{decision.Kind}");
            return Settles(decision.Kind == ActivityResolutionKind.Completed
                ? ActivityRunResult.Returned(decision.InvocationResult?.Read(services))
                : ActivityRunResult.Threw(decision.Failure?.ForPropagation()
                    ?? new InvalidOperationException(decision.Note)));
        }
        if (continuation.AcceptedWait is { } accepted && accepted.StepId == scopes.Id(step) && pending != null)
        {
            continuation.AcceptedWait = null;
            var completed = accepted.Event.Read(services) as ChildCompleted
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"Child invocation step '{step.Name}' took an event that is not its child's completion");
            var dataType = (await host.ResolveChildAsync(step, starting: false)).WorkflowDataType!;
            return Settles(completed.Outcome(dataType));
        }
        if (pending != null)
        {
            // The child is still running: the branch waits for its completion.
            if (checkpoint.Waits.Any(wait => wait.ContinuationId == continuation.Id &&
                                             wait.ChildWorkflowId == pending.ChildWorkflowId))
            {
                continuation.Status = ContinuationStatus.Waiting;
                return null;
            }
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Child invocation step '{step.Name}' has neither its child's wait nor its completion");
        }

        pending = new ActivityInvocationState { StepId = scopes.Id(step), StartedAtUtc = Now };
        pending.ChildWorkflowId = pending.Id;
        using var startScope = _logger.BeginScope(LogScope.ForStep(scopes.Id(step), pending.Id));
        var childDefinition = await host.ResolveChildAsync(step, starting: true);
        ChildStart start;
        try
        {
            var context = WorkflowValueBinding.EvaluationContext(checkpoint.DataOf(continuation),
                step.PreviousStepDataType, continuation.GetPrevious(services),
                ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
            var data = ChildInputs.NewData(childDefinition, step, context);
            start = host.StartChild(childDefinition, data, instance, pending.Id, Guid.NewGuid().ToString("N"));
        }
        catch (CheckpointLimitExceededException error)
        {
            // The same child would be refused again, so an operator decides (A-08, D-28).
            continuation.PendingActivity = pending;
            Attempt(instance, continuation, step, pending, "InvokeWorkflow");
            return Settles(ActivityRunResult.Unresolved($"The child workflow could not start: {error.Message}"));
        }
        catch (Exception error) when (!WorkflowEngineException.IsEngineFailure(error))
        {
            return Settles(ActivityRunResult.Threw(error));
        }
        continuation.PendingActivity = pending;
        Attempt(instance, continuation, step, pending, "InvokeWorkflow");
        transitions.AwaitChild(checkpoint, continuation, scopes.Id(step), start, detached: !step.CancelWithParent);
        return null;
    }

    // Records an invocation attempt and its start in the trace.
    private void Attempt(WorkflowInstance instance, ContinuationState continuation, WorkflowStep step,
        ActivityInvocationState pending, string kind)
    {
        var attempt = new ActivityAttemptState { StartedAtUtc = Now, Kind = kind };
        pending.Attempts.Add(attempt);
        if (!instance.TracingEnabled)
            return;
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
        instance.Trace.Add(startTrace);
    }

    public async Task SettleAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        RunningActivity activity,
        Dictionary<string, RunningActivity> inFlight,
        WorkflowInstance instance,
        IExecutionHost host)
    {
        using var logScope = _logger.BeginScope(LogScope.ForStep(scopes.Id(activity.Step),
            activity.Continuation.PendingActivity?.Id));
        checkpoint.Touch(activity.Continuation);
        // The branch's copy may have changed while its activity ran; it is recorded at the next commit.
        activity.Continuation.DataChanged = activity.Continuation.ParentJoinId != null;
        try
        {
            var outcome = await activity.Task;
            if (outcome.Kind == ActivityRunKind.Unresolved)
            {
                MarkActivityUnresolved(activity, outcome.Reason);
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
                // Kept until the failure is handled, for a retry of the step (D-14).
                if (activity.Continuation.PendingActivity is { } failed)
                    activity.Continuation.RetryInputs = new KeptActivityInputs
                    {
                        InvocationId = failed.Id,
                        Inputs = failed.Inputs,
                        StartedAtUtc = failed.StartedAtUtc,
                        Attempts = failed.Attempts
                    };
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
                    SagaScopeTransitions.CompleteForwardActivity(scopes, checkpoint, activity.Continuation,
                        activity.Step, output, services, activity.Continuation.PendingActivity?.Id);
                }
                catch (Exception error)
                {
                    MarkActivityUnresolved(activity,
                        $"Activity returned, but its saga output could not be saved: {error.Message}");
                    return;
                }
                if (activity.Prepared != null)
                    await activity.Prepared.ApplyOutputsAsync();
                // A child's outcome is discarded while its branch unwinds outside cleanup (D-17).
                else if (activity.Step.StepType == WorkflowStepType.WorkflowInvocation && !Unwinding(activity.Continuation))
                    WorkflowValueBinding.ApplyActivityOutputs(activity.Step, output, checkpoint.DataOf(activity.Continuation),
                        ForEachTransitions.ItemsOf(scopes, checkpoint, activity.Continuation));
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

    private static bool Unwinding(ContinuationState continuation) =>
        continuation.Status is ContinuationStatus.Cancelling or ContinuationStatus.Cancelled ||
        continuation.CancellationUnwind && !TryScopeTransitions.IsInsideFinally(continuation) &&
        !SagaScopeTransitions.IsInsideCompensation(continuation);

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
            instance.Trace.Add(new ExecutionTraceEntry
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

internal enum ActivityRunKind { Returned, Threw, Unresolved, Skipped }

internal sealed record ActivityRunResult(
    ActivityRunKind Kind,
    object? Output = null,
    Exception? Error = null,
    string? Reason = null)
{
    public static ActivityRunResult Returned(object? output) =>
        new(ActivityRunKind.Returned, Output: output);
    // A thrown result is always the step's own failure; engine failures fault the activity task instead.
    public static ActivityRunResult Threw(Exception error) =>
        new(ActivityRunKind.Threw, Error: WorkflowEngineException.FromWorkflowCode(error));
    public static ActivityRunResult Unresolved(string reason) =>
        new(ActivityRunKind.Unresolved, Reason: reason);
    public static ActivityRunResult Skipped() => new(ActivityRunKind.Skipped);
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
