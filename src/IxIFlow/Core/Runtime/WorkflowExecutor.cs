namespace IxIFlow.Core.Runtime;

/// <summary>
/// Runs one instance from its checkpoint until it completes, fails, waits or loses ownership.
/// Everything outside the instance (commits, cancellation requests, ownership and child
/// workflows) goes through <see cref="IExecutionHost"/>.
/// </summary>
internal sealed class WorkflowExecutor
{
    private readonly TimeProvider _time;
    private readonly ContinuationTransitions _transitions;
    private readonly ActivityAttempts _attempts;

    public WorkflowExecutor(IServiceProvider services, IActivityExecutor activityExecutor, TimeProvider time)
    {
        _time = time;
        _transitions = new ContinuationTransitions(services, time);
        _attempts = new ActivityAttempts(services, activityExecutor, _transitions, time);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<WorkflowExecutionResult> RunAsync(
        WorkflowDefinition definition,
        WorkflowInstance instance,
        ExecutionCheckpoint checkpoint,
        object workflowData,
        IExecutionHost host,
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
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    "The registered workflow definition differs from the saved version");
            if (await ReconcileChildWaitsAsync(checkpoint, host))
                await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                host.ThrowIfOwnershipLost();
                var deadlineAdvanced = _transitions.ProcessExecutionDeadline(instance, checkpoint);
                deadlineAdvanced |= await ProcessDueChildWaitsAsync(checkpoint, host);
                deadlineAdvanced |= _transitions.ProcessDueWaits(checkpoint, scopes);
                if (deadlineAdvanced)
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
                await ApplyCancellationRequestAsync(instance, checkpoint, workflowData,
                    scopes, host, host.ObservedCancellation);
                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelling))
                    activity.Cancellation.Cancel();

                var unwinding = checkpoint.Continuations.FirstOrDefault(item =>
                    item.Status == ContinuationStatus.Cancelling && !inFlight.ContainsKey(item.Id));
                if (unwinding != null)
                {
                    if (unwinding.PendingActivity != null)
                    {
                        // A committed Start must be resolved before cancellation can discard
                        // the branch or run its compensation and Finally steps.
                        unwinding.Status = ContinuationStatus.Active;
                        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
                        continue;
                    }
                    TryScopeTransitions.ContinueCancellation(scopes, unwinding);
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
                    continue;
                }

                ContinuationTransitions.FinishCompletingJoins(checkpoint);

                // Advance every runnable branch to its next asynchronous boundary.
                // This lets a sibling start even when another activity is awaiting it.
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    host.ThrowIfOwnershipLost();
                    if (_transitions.HasDueDeadline(checkpoint))
                        break;
                    await ApplyCancellationRequestAsync(instance, checkpoint, workflowData,
                        scopes, host, host.ObservedCancellation);
                    if (!checkpoint.CancellationRequested &&
                        host.CancellationSignal?.IsCompleted == true &&
                        checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                        break;
                    var runnable = checkpoint.Continuations.FirstOrDefault(item =>
                        item.Status == ContinuationStatus.Active &&
                        !inFlight.ContainsKey(item.Id) &&
                        (item.RetryAfterUtc == null || item.RetryAfterUtc <= Now));
                    if (runnable == null)
                        break;
                    runnable.RetryAfterUtc = null;

                    var position = runnable.Stack[^1];
                    var next = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);
                    if (next?.StepType is WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation)
                    {
                        var sourceData = workflowData;
                        var activityCancellation = new CancellationTokenSource();
                        RunningActivity? running;
                        try
                        {
                            if (next.StepType == WorkflowStepType.Activity)
                            {
                                running = await _attempts.StartActivityAsync(scopes, checkpoint, runnable, next,
                                    definition, instance, sourceData, workflowData, host,
                                    activityCancellation);
                            }
                            else
                            {
                                running = await _attempts.StartInvocationAsync(scopes, checkpoint, runnable, next,
                                    definition, instance, sourceData, workflowData, host,
                                    activityCancellation);
                            }
                        }
                        catch (Exception error)
                        {
                            activityCancellation.Dispose();
                            AttachStep(error, instance, scopes.Id(next), runnable.PendingActivity);
                            throw;
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
                            _transitions.Advance(scopes, checkpoint, runnable, workflowData);
                        }
                        catch (Exception error)
                        {
                            if (next != null)
                                AttachStep(error, instance, scopes.Id(next), null);
                            if (!_transitions.CaptureFailure(scopes, checkpoint, runnable, error))
                                throw;
                        }
                        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
                        foreach (var activity in inFlight.Values.Where(item =>
                                     item.Continuation.Status == ContinuationStatus.Cancelling))
                            activity.Cancellation.Cancel();
                    }
                }

                if (!checkpoint.CancellationRequested && host.CancellationSignal?.IsCompleted == true &&
                    checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                    continue;
                if (checkpoint.Continuations.Any(item =>
                        item.Status == ContinuationStatus.Cancelling && !inFlight.ContainsKey(item.Id)) ||
                    checkpoint.Joins.Any(item => item.IsCompleting &&
                        checkpoint.Continuations.Where(child => item.ChildContinuationIds.Contains(child.Id))
                            .All(child => child.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)))
                    continue;
                if (_transitions.HasDueDeadline(checkpoint))
                    continue;
                var nextRetry = checkpoint.Continuations
                    .Where(item => item.Status == ContinuationStatus.Active &&
                                   item.RetryAfterUtc > Now)
                    .Min(item => item.RetryAfterUtc);
                var nextWaitDeadline = checkpoint.Waits
                    .Where(wait => wait.DeadlineUtc > Now)
                    .Min(wait => wait.DeadlineUtc);
                if (inFlight.Count == 0 && nextRetry == null)
                {
                    if (await ApplyCancellationRequestAsync(instance, checkpoint, workflowData,
                            scopes, host, host.ObservedCancellation))
                        continue;
                    break;
                }

                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelling))
                    activity.Cancellation.Cancel();

                var runningTasks = inFlight.Values.Select(item => (Task)item.Task).ToList();
                using var retryTimerCancellation = new CancellationTokenSource();
                Task? retryTimer = null;
                var nextWake = new[] { nextRetry,
                        inFlight.Count > 0 ? nextWaitDeadline : null,
                        checkpoint.CancellationRequested ? null : checkpoint.ExecutionDeadlineUtc }
                    .Where(due => due != null).Min();
                if (nextWake is { } retryAt)
                {
                    var delay = retryAt - Now;
                    if (delay < TimeSpan.Zero)
                        delay = TimeSpan.Zero;
                    retryTimer = Task.Delay(delay > TimeSpan.FromDays(1)
                        ? TimeSpan.FromDays(1) : delay, _time, retryTimerCancellation.Token);
                    runningTasks.Add(retryTimer);
                }
                if (!checkpoint.CancellationRequested && callerCancellation != null)
                    runningTasks.Add(callerCancellation);
                if (host.OwnershipLoss is { } ownershipLoss)
                    runningTasks.Add(ownershipLoss);
                if (host.CancellationSignal is { } cancellationSignal &&
                    !checkpoint.CancellationRequested &&
                    checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                    runningTasks.Add(cancellationSignal);
                var finished = await Task.WhenAny(runningTasks);
                if (finished != retryTimer)
                    retryTimerCancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (finished == retryTimer || finished == callerCancellation ||
                    finished == host.CancellationSignal)
                    continue;
                host.ThrowIfOwnershipLost();
                var completedActivity = inFlight.Values.First(item => item.Task == finished);
                try
                {
                    await _attempts.SettleAsync(scopes, checkpoint, completedActivity, inFlight, workflowData, instance, host);
                }
                catch (Exception error)
                {
                    AttachStep(error, instance, scopes.Id(completedActivity.Step),
                        completedActivity.Continuation.PendingActivity);
                    if (!_transitions.CaptureFailure(scopes, checkpoint, completedActivity.Continuation,
                            error, !completedActivity.ActivityReturned))
                        throw;
                }
                await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (checkpoint.UnhandledError != null && checkpoint.Waits.Count == 0 && checkpoint.Joins.Count == 0)
                throw checkpoint.RuntimeUnhandledError
                      ?? (checkpoint.UnhandledError.IsRestorable
                          ? checkpoint.UnhandledError.Restore()
                          : new InvalidOperationException(checkpoint.UnhandledError.Message));

            if (checkpoint.CancellationRequested &&
                checkpoint.Continuations[0].Status == ContinuationStatus.Cancelled)
            {
                instance.Status = checkpoint.TimedOut
                    ? WorkflowStatus.TimedOut : WorkflowStatus.Cancelled;
                instance.CompletedAt = Now;
            }
            else if (checkpoint.Continuations[0].Status == ContinuationStatus.Completed)
            {
                instance.Status = WorkflowStatus.Completed;
                instance.CompletedAt = Now;
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
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "Execution stopped without a runnable continuation or wait");
            }

            instance.SuspensionInfo = checkpoint.Waits.Count == 1
                ? new SuspensionInfo
                {
                    SuspendReason = checkpoint.Waits[0].Key,
                    ResumeEventType = checkpoint.Waits[0].EventType,
                    SuspendedAt = checkpoint.Waits[0].RegisteredAtUtc
                }
                : null;
            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);

            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = instance.Status switch
                {
                    WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
                    WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
                    WorkflowStatus.TimedOut => WorkflowExecutionStatus.TimedOut,
                    WorkflowStatus.NeedsResolution => WorkflowExecutionStatus.NeedsResolution,
                    _ => WorkflowExecutionStatus.Suspended
                },
                WorkflowData = workflowData,
                TraceEntries = instance.ExecutionHistory.ToList(),
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
                ExecutionTime = Now - instance.StartedAt!.Value
            };
        }
        catch (ExecutionOwnershipLostException)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await Task.WhenAll(inFlight.Values.Select(item => item.Task));
            }
            catch
            {
                // The former owner cannot commit these activity outcomes.
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }
            return Busy(instance.InstanceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await Task.WhenAll(inFlight.Values.Select(item => item.Task));
                var scopes = new WorkflowScopeCatalog(definition);
                foreach (var activity in inFlight.Values.ToArray())
                {
                    if (activity.Task.Result is { Kind: ActivityRunKind.Threw,
                            Error: OperationCanceledException })
                        continue;
                    try
                    {
                        await _attempts.SettleAsync(scopes, checkpoint, activity, inFlight, workflowData, instance, host);
                    }
                    catch (Exception error) when (!WorkflowEngineException.IsEngineFailure(error))
                    {
                        if (!_transitions.CaptureFailure(scopes, checkpoint, activity.Continuation,
                                error, !activity.ActivityReturned))
                        {
                            checkpoint.UnhandledError = SerializedException.From(error);
                            checkpoint.RuntimeUnhandledError = error;
                            activity.Continuation.Status = ContinuationStatus.Completed;
                        }
                    }
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
                }
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }
            throw;
        }
        catch (WorkflowEngineException ex) when (WorkflowEngineException.IsEngineFailure(ex) && ex.IsInterruption)
        {
            // The last committed checkpoint stays recoverable; nothing is written here.
            ex.At(instance.InstanceId);
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
            if (WorkflowEngineException.IsEngineFailure(ex))
                ((WorkflowEngineException)ex).At(instance.InstanceId);

            var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!cancelled)
                checkpoint.UnhandledError ??= SerializedException.From(ex);
            instance.Status = cancelled ? WorkflowStatus.Cancelled : WorkflowStatus.Failed;
            instance.SuspensionInfo = null;
            checkpoint.Waits.Clear();
            checkpoint.Joins.Clear();
            foreach (var continuation in checkpoint.Continuations.Where(item =>
                         item.Status != ContinuationStatus.Completed))
                continuation.Status = ContinuationStatus.Cancelled;
            instance.LastError = ex.Message;
            instance.LastErrorStackTrace = ex.StackTrace;
            instance.CompletedAt = Now;
            if (instance.ExecutionSnapshot != null)
            {
                instance.ExecutionSnapshot.Status = instance.Status;
                instance.ExecutionSnapshot.Pointers.Clear();
                instance.ExecutionSnapshot.Frames.Clear();
            }
            if (host.PersistsState)
            {
                var scopes = new WorkflowScopeCatalog(definition);
                try
                {
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
                }
                catch (ExecutionCheckpointWriter.CheckpointDataException)
                {
                    // A terminal failure cannot resume. Retain the last committed data
                    // and the execution history even if live state cannot be serialized.
                    checkpoint.Continuations.Clear();
                    checkpoint.UnhandledError = null;
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now,
                        preserveSavedWorkflowData: true);
                }
            }
            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = cancelled ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Faulted,
                WorkflowData = workflowData,
                TraceEntries = instance.ExecutionHistory.ToList(),
                ErrorMessage = ex.Message,
                ErrorType = SerializedException.CatchType(ex).FullName,
                ErrorStackTrace = ex.StackTrace
            };
        }
    }

    private async Task<bool> ApplyCancellationRequestAsync(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, WorkflowScopeCatalog scopes,
        IExecutionHost host, CancellationReason? observedReason = null)
    {
        if (!host.PersistsState || checkpoint.CancellationRequested)
            return false;
        var requested = observedReason;
        if (requested == null)
        {
            try
            {
                requested = await host.ReadCancellationRequestAsync();
            }
            catch (Exception)
            {
                // The lease watcher detects a persistent store outage.
                // Keep committed activity attempts in this owner.
                return false;
            }
        }
        if (requested == null)
            return false;
        var root = checkpoint.Continuations[0];
        if (root.Status == ContinuationStatus.Completed)
            return false;
        instance.CancellationReason = requested;
        checkpoint.CancellationRequested = true;
        checkpoint.UnhandledError = null;
        checkpoint.RuntimeUnhandledError = null;
        ContinuationTransitions.CancelTree(checkpoint, root);
        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
        return true;
    }

    private async Task<bool> ProcessDueChildWaitsAsync(ExecutionCheckpoint checkpoint, IExecutionHost host)
    {
        var due = checkpoint.Waits.Where(wait => wait.ChildWorkflowId != null &&
            wait.DeadlineUtc <= Now)
            .GroupBy(wait => (wait.ContinuationId, wait.ChildWorkflowId))
            .ToArray();
        if (due.Length == 0)
            return false;
        foreach (var childWaits in due)
        {
            var childId = childWaits.Key.ChildWorkflowId!;
            var result = await host.RecoverChildAsync(childId);
            if (result.Status == WorkflowExecutionStatus.Running)
            {
                foreach (var wait in childWaits)
                    wait.DeadlineUtc = Now.AddSeconds(5);
                continue;
            }
            if (result.Status == WorkflowExecutionStatus.NeedsResolution)
            {
                foreach (var wait in childWaits)
                    wait.DeadlineUtc = null;
                var unresolved = checkpoint.Continuations.Single(item =>
                    item.Id == childWaits.Key.ContinuationId);
                unresolved.Status = ContinuationStatus.WaitingResolution;
                unresolved.PendingActivity!.ResolutionReason =
                    $"Child workflow '{childId}' needs activity resolution";
                continue;
            }
            checkpoint.Waits.RemoveAll(wait => wait.ChildWorkflowId == childId &&
                wait.ContinuationId == childWaits.Key.ContinuationId);
            var continuation = checkpoint.Continuations.Single(item =>
                item.Id == childWaits.Key.ContinuationId);
            continuation.Status = ContinuationStatus.Active;
        }
        return true;
    }

    public async Task<bool> ReconcileChildWaitsAsync(ExecutionCheckpoint checkpoint, IExecutionHost host)
    {
        var changed = false;
        var childWaits = checkpoint.Waits.Where(wait => wait.ChildWorkflowId != null)
            .GroupBy(wait => (wait.ContinuationId, wait.ChildWorkflowId)).ToArray();
        foreach (var group in childWaits)
        {
            var child = await host.ReadChildAsync(group.Key.ChildWorkflowId!);
            if (child == null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"Saved child workflow '{group.Key.ChildWorkflowId}' is missing");
            var continuation = checkpoint.Continuations.Single(item =>
                item.Id == group.Key.ContinuationId);
            if (child.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or
                WorkflowStatus.Cancelled or WorkflowStatus.TimedOut or WorkflowStatus.Terminated)
            {
                checkpoint.Waits.RemoveAll(wait => wait.ChildWorkflowId == child.InstanceId &&
                    wait.ContinuationId == continuation.Id);
                continuation.Status = ContinuationStatus.Active;
                changed = true;
            }
            else if (child.Status == WorkflowStatus.Running &&
                     (continuation.Status != ContinuationStatus.Waiting ||
                      group.Any(wait => !string.IsNullOrEmpty(wait.EventType) ||
                                        wait.ChildWaitId != null)))
            {
                _transitions.ProjectChildLifecycleWait(checkpoint, continuation, child.InstanceId,
                    needsResolution: false);
                changed = true;
            }
            else if (child.Status == WorkflowStatus.NeedsResolution &&
                     (continuation.Status != ContinuationStatus.WaitingResolution ||
                      group.Any(wait => !string.IsNullOrEmpty(wait.EventType) ||
                                        wait.ChildWaitId != null)))
            {
                _transitions.ProjectChildLifecycleWait(checkpoint, continuation, child.InstanceId,
                    needsResolution: true);
                changed = true;
            }
            else if (child.Status == WorkflowStatus.Suspended &&
                     ContinuationTransitions.ShouldCancelInvokedChild(continuation))
            {
                // A cancelled parent polls the child's cancellation rather than
                // exposing a child event wait that it no longer intends to accept.
                if (continuation.Status != ContinuationStatus.Waiting ||
                    group.Any(wait => !string.IsNullOrEmpty(wait.EventType) ||
                                      wait.ChildWaitId != null))
                {
                    _transitions.ProjectChildLifecycleWait(checkpoint, continuation, child.InstanceId,
                        needsResolution: false);
                    changed = true;
                }
            }
            else if (child.Status == WorkflowStatus.Suspended)
            {
                var childCheckpoint = ExecutionCheckpoint.Read(child.ExecutionStateJson);
                var savedWaitIds = group.Select(wait => wait.ChildWaitId)
                    .OrderBy(id => id, StringComparer.Ordinal);
                var currentWaitIds = childCheckpoint.Waits.Select(wait => wait.Id)
                    .OrderBy(id => id, StringComparer.Ordinal);
                if (continuation.Status != ContinuationStatus.Waiting ||
                    !savedWaitIds.SequenceEqual(currentWaitIds))
                {
                    _transitions.ProjectChildWaits(checkpoint, continuation, child.InstanceId, childCheckpoint);
                    continuation.PendingActivity!.ResolutionReason = null;
                    changed = true;
                }
            }
        }
        return changed;
    }

    // Records where an engine failure happened. Failures raised by workflow code keep their own message.
    private static void AttachStep(Exception error, WorkflowInstance instance, string stepId,
        ActivityInvocationState? pending)
    {
        if (WorkflowEngineException.IsEngineFailure(error))
            ((WorkflowEngineException)error).At(instance.InstanceId, stepId, pending?.Attempts.LastOrDefault()?.Id);
    }

    public static WorkflowExecutionResult Busy(string instanceId) => new()
    {
        InstanceId = instanceId,
        Status = WorkflowExecutionStatus.Running,
        ErrorMessage = "The workflow instance is executing on another host"
    };
}
