using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Runs one instance from its checkpoint until it completes, fails, waits or loses ownership.
/// Control arrives through delivered messages; commits, inbox reads and child workflows
/// are requested through <see cref="IExecutionHost"/>.
/// </summary>
internal sealed class WorkflowExecutor
{
    private readonly TimeProvider _time;
    private readonly ContinuationTransitions _transitions;
    private readonly ActivityAttempts _attempts;
    private readonly EventIntake _intake;
    private readonly ILogger _logger;

    private readonly IExecutionHost _host;
    private readonly IWorkflowDefinitionSource _definitions;

    public WorkflowExecutor(IServiceProvider services, IExecutionHost host)
    {
        _host = host;
        _definitions = (IWorkflowDefinitionSource)services.GetService(typeof(IWorkflowDefinitionSource))!;
        var activityExecutor = (IActivityExecutor)services.GetService(typeof(IActivityExecutor))!;
        var time = (TimeProvider)services.GetService(typeof(TimeProvider))!;
        _time = time;
        _logger = LogScope.Logger(services);
        _transitions = new ContinuationTransitions(services, time);
        _attempts = new ActivityAttempts(services, activityExecutor, _transitions, time, _definitions);
        _intake = new EventIntake(services, time);
    }

    private readonly ConcurrentDictionary<Guid, ExecutionSignals> _runs = new();

    internal ExecutionSignals PrepareAssignment(Guid identity) =>
        _runs.GetOrAdd(identity, id => new ExecutionSignals(id, CloseRun));

    private void CloseRun(Guid runId) => _runs.TryRemove(runId, out _);
    public void Deliver(Cancel message)
    {
        if (_runs.TryGetValue(message.AssignmentId, out var signals)) signals.Deliver(message);
    }
    public void Deliver(Arrivals message)
    {
        if (_runs.TryGetValue(message.AssignmentId, out var signals)) signals.Deliver(message);
    }
    public void Deliver(Recycle message)
    {
        if (_runs.TryGetValue(message.AssignmentId, out var signals)) signals.Deliver(message);
    }

    public async Task<Ready> ExecuteAsync(Assign assign, CancellationToken stop)
    {
        var signals = PrepareAssignment(assign.AssignmentId);
        var session = new ExecutionSession(_host, assign.AssignmentId, assign.Instance.InstanceId,
            assign.Epoch, assign.Limits, assign.CanPark);
        using var stopping = stop.Register(() => Deliver(new Cancel(assign.AssignmentId, CancelKind.Run)));
        try
        {
            if (assign.Breakpoints.Count != 0)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    "Breakpoints are not supported until G-04");
            signals.ThrowIfOwnershipLost();
            var definition = await _definitions.GetWorkflowDefinitionAsync(assign.Instance.WorkflowName,
                assign.Instance.WorkflowVersion) ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    "The assigned workflow definition is unavailable");
            _ = WorkflowDefinitionValidator.ForExecution(definition);
            if (definition.Name != assign.Instance.WorkflowName || definition.Version != assign.Instance.WorkflowVersion)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    "The definition source returned a different workflow identity");
            // Assignment input is saved state. Never execute against a host-owned instance or data object.
            var instance = System.Text.Json.JsonSerializer.Deserialize<WorkflowInstance>(
                System.Text.Json.JsonSerializer.Serialize(assign.Instance))!;
            instance.Incarnation = assign.Instance.Incarnation;
            if (assign.Parts != null) instance.Saved(new Dictionary<string, string>(assign.Parts), instance.Revision);
            instance.ExecutionStateJson = StoredCheckpoint.WholeState(instance);
            var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            checkpoint.Signals = signals;
            if (checkpoint.StructureHash != assign.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "The assignment structure hash differs from its checkpoint");
            var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Workflow data type '{instance.WorkflowDataType}' is unavailable");
            var data = System.Text.Json.JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Saved workflow data cannot be read");
            signals.Deliver(new Arrivals(assign.AssignmentId, assign.InboxSequence));
            if (checkpoint.StructureHash == WorkflowDefinitionValidator.ForExecution(definition).StructureHash &&
                assign.Recovery is { } preparation)
            {
                var scopes = WorkflowDefinitionValidator.ForExecution(definition);
                checkpoint.Trace.Configure(instance, checkpoint, assign.Limits, _time, scopes);
                if (preparation.ClearOperatorStop)
                {
                    checkpoint.OperatorStop = null;
                    instance.LastError = null;
                    instance.LastErrorStackTrace = null;
                    if (preparation.Released) OperatorTrace.Released(checkpoint, preparation.Actor, Now);
                }
                foreach (var continuation in checkpoint.Continuations.Where(item =>
                             preparation.ReactivatedContinuations.Contains(item.Id)))
                {
                    checkpoint.Touch(continuation);
                    continuation.Status = ContinuationStatus.Active;
                }
                instance.Status = WorkflowStatus.Running;
                if (preparation.PendingCancellation is { } reason && !checkpoint.CancellationRequested &&
                    checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                    OperatorTrace.ApplyCancellation(instance, checkpoint, reason, Now);
                if (preparation.ClearOperatorStop)
                    await ExecutionCheckpointWriter.CommitAsync(session, instance, checkpoint, null, scopes, Now,
                        preserveSavedWorkflowData: true, enforceLimit: false);
            }
            var outcome = await RunCoreAsync(definition, instance, checkpoint, data, session, signals);
            return new(assign.AssignmentId, outcome, session.LastAcknowledgedCommit);
        }
        catch (ExecutionOwnershipLostException)
        {
            return new(assign.AssignmentId, new(AssignmentOutcomeKind.OwnershipLost), session.LastAcknowledgedCommit);
        }
        catch (CheckpointInterruptedException error)
        {
            return new(assign.AssignmentId, new(AssignmentOutcomeKind.Interrupted, error.Message), session.LastAcknowledgedCommit);
        }
        finally
        {
            CloseRun(assign.AssignmentId);
        }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task<AssignmentOutcome> RunCoreAsync(
        WorkflowDefinition definition,
        WorkflowInstance instance,
        ExecutionCheckpoint checkpoint,
        object workflowData,
        ExecutionSession host,
        ExecutionSignals signals)
    {
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        if (checkpoint.StructureHash != scopes.StructureHash)
            return await StopForOperatorAsync(definition, instance, checkpoint, workflowData, host,
                OperatorStopKind.Verification, scopes.Mismatch(definition, checkpoint.StructureHash));
        checkpoint.Trace.Configure(instance, checkpoint, host.Limits, _time, scopes);
        // Everything logged during the run carries its instance (G-03).
        using var logScope = _logger.BeginScope(LogScope.ForInstance(instance));
        // A span for the run pass, and the host's count of active runs (D-39).
        using var span = Telemetry.Source.StartActivity($"run {instance.WorkflowName}");
        Telemetry.Tag(span, instance);
        using var activeRun = Telemetry.Running(instance);
        // Expressions read time from the engine's clock (D-42).
        using var clock = RunClock.Use(_time);
        checkpoint.RootData = workflowData;
        var inFlight = new Dictionary<string, RunningActivity>();
        // Set when a step hit a resource limit: the run starts nothing new and stops once its
        // running activities are committed (D-28).
        string? limitStop = null;
        var limitHitWhileCancelling = false;
        // Continuations that found the activity cap full, in the order they first waited, so a
        // freed permit goes to the one that has waited longest rather than the first in the list.
        var waitingForActivity = new Dictionary<string, long>(StringComparer.Ordinal);
        long waitOrder = 0;
        // A saved wait, a completed join or a branch stopped for resolution must be committed
        // before the run blocks or ends; the next commit of any kind covers it (A-04).
        var uncommitted = new UncommittedBoundary();
        long arrivalsSeen = 0;
        try
        {
            while (true)
            {
                signals.ThrowIfStopped();
                signals.ThrowIfOwnershipLost();
                var messageWake = signals.Changed;
                var progressed = false;
                // The store says which deadlines are due when the run reads its inbox, and that read
                // covers every event received before them, so events are taken before deadlines and one
                // received in time beats its deadline (D-33, D-35). A cancellation request the run has
                // not applied yet goes first: its unwind closes the waits, and the events are then
                // refused as cancelled. The execution deadline ends the run before any event is taken.
                if (signals.InboxSequence > arrivalsSeen || _transitions.MayBeDue(checkpoint))
                {
                    arrivalsSeen = signals.InboxSequence;
                    checkpoint.Inbox.ExpectArrivals();
                }
                if (!CancellationPending(checkpoint))
                    progressed |= await _intake.TakeAsync(scopes, checkpoint, host, instance);
                // A request the host learned of while the run read its inbox also goes first.
                if (!CancellationPending(checkpoint) &&
                    _transitions.ProcessExecutionDeadline(instance, checkpoint))
                {
                    progressed = true;
                    await _intake.TakeAsync(scopes, checkpoint, host, instance);
                }
                if (!CancellationPending(checkpoint))
                    progressed |= _transitions.ProcessDueWaits(checkpoint, scopes);
                if (progressed)
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);
                await ApplyCancellationRequestAsync(instance, checkpoint, checkpoint.RootData,
                    scopes, host, signals.Cancellation);
                // A tree cancelled after the limit was hit never runs the step that hit it; it unwinds
                // instead. A limit hit by cleanup code during cancellation still stops.
                if (limitStop != null && checkpoint.CancellationRequested && !limitHitWhileCancelling)
                    limitStop = null;
                await SignalCancellationsAsync(inFlight, uncommitted, host, instance, checkpoint, checkpoint.RootData,
                    scopes);

                var unwinding = checkpoint.Continuations.FirstOrDefault(item =>
                    item.Status == ContinuationStatus.Cancelling && !inFlight.ContainsKey(item.Id));
                if (unwinding != null)
                {
                    checkpoint.Touch(unwinding);
                    var unwindBoundary = EffectBoundary.Of(checkpoint);
                    if (unwinding.PendingActivity != null)
                    {
                        // A committed Start must be resolved before cancellation can discard
                        // the branch or run its compensation and Finally steps.
                        unwinding.Status = ContinuationStatus.Active;
                    }
                    else
                        TryScopeTransitions.ContinueCancellation(scopes, checkpoint, unwinding);
                    uncommitted.NoteIf(unwindBoundary.CrossedBy(checkpoint), instance);
                    continue;
                }

                var conflicts = new List<(ContinuationState Parent, JoinConflictException Conflict)>();
                uncommitted.NoteIf(ContinuationTransitions.FinishCompletingJoins(scopes, checkpoint, conflicts), instance);
                // A join whose branches conflict fails at its parallel step, where a Catch can handle it.
                foreach (var (parent, conflict) in conflicts)
                {
                    var parallel = NextStep(scopes, parent);
                    if (parallel != null)
                        AttachStep(conflict, instance, scopes.Id(parallel), null);
                    if (!_transitions.CaptureFailure(scopes, checkpoint, parent, conflict))
                        throw conflict;
                }

                // Advance every runnable branch to its next asynchronous boundary.
                // This lets a sibling start even when another activity is awaiting it.
                while (true)
                {
                    signals.ThrowIfStopped();
                    signals.ThrowIfOwnershipLost();
                    // A draining host starts no new steps (B-12), nor does a run stopping at a limit.
                    if (_transitions.MayBeDue(checkpoint) || signals.Recycle ||
                        limitStop != null)
                        break;
                    await ApplyCancellationRequestAsync(instance, checkpoint, checkpoint.RootData,
                        scopes, host, signals.Cancellation);
                    if (!checkpoint.CancellationRequested &&
                        signals.Cancellation != null &&
                        checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                        break;
                    // A parallel ForEach starts its queued items as room frees, in a transition of its own.
                    var admission = EffectBoundary.Of(checkpoint);
                    try
                    {
                        ForEachTransitions.Admit(scopes, checkpoint, host.Limits.MaxActiveBranches);
                    }
                    catch (BranchLimitExceededException limit)
                    {
                        limitStop = limit.Message;
                        limitHitWhileCancelling = checkpoint.CancellationRequested;
                        break;
                    }
                    uncommitted.NoteIf(admission.CrossedBy(checkpoint), instance);
                    // At the activity cap, continuations about to start an activity wait their turn;
                    // the rest keep advancing (B-09).
                    var activitiesFull = inFlight.Values.Count(item =>
                        item.Step.StepType == WorkflowStepType.Activity) >= host.Limits.MaxActivitiesPerRun;
                    var ready = checkpoint.Continuations.Where(item =>
                        item.Status == ContinuationStatus.Active &&
                        !inFlight.ContainsKey(item.Id) &&
                        (item.RetryAfterUtc == null || item.RetryAfterUtc <= checkpoint.Clock.ToStore(Now))).ToList();
                    var runnable = activitiesFull
                        ? ready.FirstOrDefault(item => NextStep(scopes, item)?.StepType != WorkflowStepType.Activity)
                        : ready.Where(item => waitingForActivity.ContainsKey(item.Id))
                              .MinBy(item => waitingForActivity[item.Id]) ?? ready.FirstOrDefault();
                    if (activitiesFull)
                        foreach (var waiting in ready.Where(item =>
                                     NextStep(scopes, item)?.StepType == WorkflowStepType.Activity))
                            waitingForActivity.TryAdd(waiting.Id, waitOrder++);
                    if (runnable != null)
                        waitingForActivity.Remove(runnable.Id);
                    if (runnable == null)
                        break;
                    checkpoint.Touch(runnable);
                    runnable.RetryAfterUtc = null;

                    var position = runnable.Stack[^1];
                    var next = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);
                    if (next?.StepType is WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation)
                    {
                                var activityCancellation = new CancellationTokenSource();
                        RunningActivity? running;
                        try
                        {
                            if (next.StepType == WorkflowStepType.Activity)
                            {
                                running = await _attempts.StartActivityAsync(scopes, checkpoint, runnable, next,
                                    definition, instance, host,
                                    activityCancellation);
                            }
                            else
                            {
                                running = await _attempts.StartInvocationAsync(scopes, checkpoint, runnable, next,
                                    definition, instance, host,
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
                        var boundary = EffectBoundary.Of(checkpoint);
                        try
                        {
                            _transitions.Advance(scopes, checkpoint, runnable,
                                host.Limits.MaxActiveBranches, host.Limits.MaxForEachItems);
                        }
                        catch (ResourceLimitExceededException limit) when
                            (limit is BranchLimitExceededException or ForEachLimitExceededException)
                        {
                            // Nothing was forked or saved, the list included; the step runs after a release.
                            limitStop = limit.Message;
                            limitHitWhileCancelling = checkpoint.CancellationRequested;
                            break;
                        }
                        catch (Exception error)
                        {
                            if (next != null)
                                AttachStep(error, instance, scopes.Id(next), null);
                            if (!_transitions.CaptureFailure(scopes, checkpoint, runnable, error))
                                throw;
                        }
                        uncommitted.NoteIf(boundary.CrossedBy(checkpoint), instance);
                        await SignalCancellationsAsync(inFlight, uncommitted, host, instance, checkpoint,
                            checkpoint.RootData, scopes);
                    }
                }

                if (!checkpoint.CancellationRequested && signals.Cancellation != null &&
                    checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                    continue;
                if (checkpoint.Continuations.Any(item =>
                        item.Status == ContinuationStatus.Cancelling && !inFlight.ContainsKey(item.Id)) ||
                    checkpoint.Joins.Any(item => item.IsCompleting &&
                        checkpoint.BranchesOf(item)
                            .All(child => child.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)))
                    continue;
                if (_transitions.MayBeDue(checkpoint))
                    continue;
                // Retry times are the store's, as the run last read its clock, so a host whose clock
                // differs retries when the one that scheduled the retry would have (D-35). A retry that
                // fell due since the branches were advanced wakes the run at once.
                var nextRetry = checkpoint.Continuations
                    .Where(item => item.Status == ContinuationStatus.Active && item.RetryAfterUtc != null)
                    .Min(item => item.RetryAfterUtc) is { } retryTime
                    ? checkpoint.Clock.ToLocal(retryTime) : (DateTime?)null;
                if (inFlight.Count == 0 &&
                    (nextRetry == null || signals.Recycle || limitStop != null))
                {
                    if (await ApplyCancellationRequestAsync(instance, checkpoint, checkpoint.RootData,
                            scopes, host, signals.Cancellation))
                        continue;
                    // Events that arrived since the last read are taken before the run goes idle.
                    if (checkpoint.Inbox.MayHaveUnread && limitStop == null &&
                        !signals.Recycle && !CancellationPending(checkpoint))
                        continue;
                    break;
                }

                await SignalCancellationsAsync(inFlight, uncommitted, host, instance, checkpoint, checkpoint.RootData,
                    scopes);
                // Children start, or are asked to cancel, in the run's next commit (D-17). A run about to wait,
                // for its activities or a timer, makes that commit now rather than when the wait ends.
                if (checkpoint.ChildStarts.Count > 0 || checkpoint.ChildCancellations.Count > 0)
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);

                var runningTasks = inFlight.Values.Select(item => (Task)item.Task).ToList();
                using var retryTimerCancellation = new CancellationTokenSource();
                Task? retryTimer = null;
                // A sibling's wait deadline may come before a retry; the run wakes for whichever is first.
                // Deadlines are the store's, seen on the host's clock (D-35).
                var nextWake = new[] { nextRetry, checkpoint.NextDeadlineLocal() }.Min();
                // With nothing in flight, a long wait for a retry gives the slot and lease back; the
                // host resumes the run on time (C-03). A request that arrived meanwhile keeps it here.
                if (inFlight.Count == 0 && host.CanPark && nextWake is { } parkUntil &&
                    parkUntil - Now > host.Limits.ParkRetriesLongerThan)
                {
                    if (await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData,
                            scopes, Now, parkFor: parkUntil - Now))
                        return new(AssignmentOutcomeKind.Parked, $"The workflow instance waits for a retry at {parkUntil:O}; it continues then");
                    continue;
                }
                if (nextWake is { } retryAt)
                {
                    var delay = retryAt - Now;
                    if (delay < TimeSpan.Zero)
                        delay = TimeSpan.Zero;
                    retryTimer = Task.Delay(delay > TimeSpan.FromDays(1)
                        ? TimeSpan.FromDays(1) : delay, _time, retryTimerCancellation.Token);
                    runningTasks.Add(retryTimer);
                }
                runningTasks.Add(messageWake);
                if (uncommitted.IsPending(instance))
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);
                var finished = await Task.WhenAny(runningTasks);
                if (finished != retryTimer)
                    retryTimerCancellation.Cancel();
                signals.ThrowIfStopped();
                if (finished == retryTimer || finished == messageWake)
                    continue;
                signals.ThrowIfOwnershipLost();
                var completedActivity = inFlight.Values.First(item => item.Task == finished);
                try
                {
                    await _attempts.SettleAsync(scopes, checkpoint, completedActivity, inFlight, instance, host);
                }
                catch (Exception error)
                {
                    AttachStep(error, instance, scopes.Id(completedActivity.Step),
                        completedActivity.Continuation.PendingActivity);
                    if (!_transitions.CaptureFailure(scopes, checkpoint, completedActivity.Continuation,
                            error, !completedActivity.ActivityReturned))
                        throw;
                }
                await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);
            }

            signals.ThrowIfStopped();
            if (checkpoint.UnhandledError != null && checkpoint.Waits.Count == 0 && checkpoint.Joins.Count == 0)
                throw checkpoint.RuntimeUnhandledError
                      ?? (checkpoint.UnhandledError.IsRestorable
                          ? checkpoint.UnhandledError.Restore()
                          : new InvalidOperationException(checkpoint.UnhandledError.Message));

            if (limitStop != null)
                return await StopForLimitAsync(instance, checkpoint, checkpoint.RootData, host, scopes, limitStop);

            // A drained run stops before the instance is idle, with branches still to advance or items still
            // to admit; it keeps its status and continues on recovery from what was committed.
            if (signals.Recycle &&
                (checkpoint.Continuations.Any(item => item.Status == ContinuationStatus.Active) ||
                 ForEachTransitions.MayAdmit(scopes, checkpoint)))
            {
                if (uncommitted.IsPending(instance))
                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);
                return new(AssignmentOutcomeKind.Drained,
                    "The host stopped before the instance became idle; it continues when recovered");
            }

            if (checkpoint.CancellationRequested &&
                checkpoint.Continuations[0].Status == ContinuationStatus.Cancelled)
            {
                instance.Status = checkpoint.TimedOut
                    ? WorkflowStatus.TimedOut : WorkflowStatus.Cancelled;
                instance.CompletedAt = Now;
                OperatorTrace.CompletedCancellation(instance, checkpoint, Now);
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

            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);

            return new(AssignmentOutcomeKind.Idle, instance.Status switch
            {
                WorkflowStatus.NeedsResolution => checkpoint.Continuations
                    .First(item => item.Status == ContinuationStatus.WaitingResolution)
                    .PendingActivity?.ResolutionReason,
                WorkflowStatus.Suspended when checkpoint.Waits.Count == 1 =>
                    $"Workflow suspended: {checkpoint.Waits[0].Key}",
                WorkflowStatus.Suspended => $"Workflow suspended with {checkpoint.Waits.Count} active waits",
                _ => null
            });
        }
        catch (ExecutionOwnershipLostException)
        {
            // The former owner cannot commit these activity outcomes.
            await AbandonInFlightAsync(inFlight);
            return new(AssignmentOutcomeKind.OwnershipLost);
        }
        catch (CheckpointInterruptedException error)
        {
            await AbandonInFlightAsync(inFlight);
            return new(AssignmentOutcomeKind.Interrupted, error.Message);
        }
        catch (RunCancelledException)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await WaitForActivitiesAsync(inFlight);
                await SettleCancelledRunAsync(definition, instance, checkpoint, checkpoint.RootData, host, inFlight,
                    uncommitted);
            }
            catch (CheckpointLimitExceededException)
            {
                // The instance keeps its last checkpoint; recovery stops it at the limit.
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }
            signals.ThrowIfOwnershipLost();
            return new(AssignmentOutcomeKind.RunCancelled);
        }
        catch (CheckpointLimitExceededException limit)
        {
            // Activity outcomes after the last checkpoint are not kept; recovery re-derives them.
            await AbandonInFlightAsync(inFlight);
            return await StopForOperatorAsync(definition, instance, checkpoint, checkpoint.RootData, host,
                OperatorStopKind.ResourceLimit, limit.Message);
        }
        catch (CheckpointVerificationException unverified)
        {
            // An engine fault, not the workflow's: the last checkpoint is kept, as at a limit.
            await AbandonInFlightAsync(inFlight);
            return await StopForOperatorAsync(definition, instance, checkpoint, checkpoint.RootData, host,
                OperatorStopKind.Verification, unverified.Message);
        }
        catch (WorkflowEngineException ex) when (WorkflowEngineException.IsEngineFailure(ex) && ex.IsInterruption)
        {
            // The last committed checkpoint stays recoverable; nothing is written here.
            ex.At(instance.InstanceId);
            // Activity outcomes are ignored after the checkpoint write failed.
            await AbandonInFlightAsync(inFlight);

            throw;
        }
        catch (Exception ex)
        {
            // The original execution error is reported below.
            await AbandonInFlightAsync(inFlight);
            if (WorkflowEngineException.IsEngineFailure(ex))
                ((WorkflowEngineException)ex).At(instance.InstanceId);

            signals.ThrowIfOwnershipLost();
            checkpoint.UnhandledError ??= SerializedException.From(ex);
            instance.Status = WorkflowStatus.Failed;
            // A reply wait whose scope already ended, such as a saga abandoned in this pass, closed as cancelled.
            checkpoint.CloseEndedRequests();
            // A run that fails ends every branch.
            checkpoint.TouchAll();
            ContinuationTransitions.AbandonChildren(checkpoint);
            checkpoint.RemoveWaits(_ => true, WaitCloseReason.InstanceFinished);
            checkpoint.Joins.Clear();
            foreach (var continuation in checkpoint.Continuations.Where(item =>
                         item.Status != ContinuationStatus.Completed))
            {
                checkpoint.Trace.DropFrames(continuation, 0, continuation.ItemUnwindCause ?? ItemEndCause.Failed);
                continuation.Status = ContinuationStatus.Cancelled;
            }
            instance.LastError = ex.Message;
            instance.LastErrorStackTrace = ex.StackTrace;
            instance.CompletedAt = Now;
            try
            {
                await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now);
            }
            catch (Exception error) when (error is ExecutionCheckpointWriter.CheckpointDataException or
                                              CheckpointLimitExceededException)
            {
                // A terminal failure cannot resume. Retain the last committed data and the
                // execution history even if live state cannot be serialized or is over the limit.
                checkpoint.RemoveContinuations(_ => true);
                checkpoint.UnhandledError = null;
                // The failure record is the last committed data without continuations; it is
                // not held to the limit, so a failure is never lost.
                await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, scopes, Now,
                    preserveSavedWorkflowData: true, enforceLimit: false);
            }
            return new(AssignmentOutcomeKind.Idle, ex.Message,
                SerializedException.CatchType(ex).FullName, ex.StackTrace);
        }
    }

    private static bool CancellationPending(ExecutionCheckpoint checkpoint) =>
        ContinuationTransitions.CancellationPending(checkpoint);

    private async Task<bool> ApplyCancellationRequestAsync(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, WorkflowScopeCatalog scopes,
        ExecutionSession host, CancellationReason? observedReason = null)
    {
        // A run learns of requests from the host's cancellation watch and from its own commit
        // replies (B-10); it never reads them itself.
        var requested = observedReason;
        if (checkpoint.CancellationRequested || requested == null)
            return false;
        var root = checkpoint.Continuations[0];
        if (root.Status == ContinuationStatus.Completed)
            return false;
        OperatorTrace.ApplyCancellation(instance, checkpoint, requested, Now);
        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
        return true;
    }

    // Records where an engine failure happened. Failures raised by workflow code keep their own message.
    private static void AttachStep(Exception error, WorkflowInstance instance, string stepId,
        ActivityInvocationState? pending)
    {
        if (error is CheckpointInterruptedException { InnerException: { } cause }) error = cause;
        if (WorkflowEngineException.IsEngineFailure(error))
            ((WorkflowEngineException)error).At(instance.InstanceId, stepId, pending?.Attempts.LastOrDefault()?.Id);
    }

    // The caller cancelled the run: activities that finished are settled and saved before it stops.
    private async Task SettleCancelledRunAsync(WorkflowDefinition definition, WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, ExecutionSession host,
        Dictionary<string, RunningActivity> inFlight, UncommittedBoundary uncommitted)
    {
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        if (uncommitted.IsPending(instance))
            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
        // Activities that ignored cancellation are not settled; their starts are resolved on recovery.
        foreach (var activity in inFlight.Values.Where(item => item.Task.IsCompletedSuccessfully).ToArray())
        {
            if (activity.Task.Result is { Kind: ActivityRunKind.Threw,
                    Error: OperationCanceledException })
                continue;
            try
            {
                await _attempts.SettleAsync(scopes, checkpoint, activity, inFlight, instance, host);
            }
            catch (Exception error) when (!WorkflowEngineException.IsEngineFailure(error))
            {
                if (!_transitions.CaptureFailure(scopes, checkpoint, activity.Continuation,
                        error, !activity.ActivityReturned))
                {
                    checkpoint.UnhandledError = SerializedException.From(error);
                    checkpoint.RuntimeUnhandledError = error;
                    checkpoint.Touch(activity.Continuation);
                    activity.Continuation.Status = ContinuationStatus.Completed;
                }
            }
            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
        }
    }

    /// <summary>
    /// What a control-flow step can change that must be saved before anything outside the instance
    /// depends on it: the saved waits, which accept events, and their timeouts, from which the store
    /// stamps deadlines that must not move;
    /// branches stopped for resolution; scheduled retries, whose deadline must not move; and branches
    /// being cancelled, whose running activities are told to stop. Other control flow is not committed
    /// and is re-derived from the last checkpoint after a crash.
    /// </summary>
    private readonly record struct EffectBoundary(string Key)
    {
        public static EffectBoundary Of(ExecutionCheckpoint checkpoint) => new(string.Join("|",
            string.Join(",", checkpoint.Waits.Select(wait => $"{wait.Id}@{wait.Timeout}")),
            string.Join(",", checkpoint.Continuations
                .Where(item => item.Status is ContinuationStatus.WaitingResolution or ContinuationStatus.Cancelling)
                .Select(item => $"{item.Id}:{item.Status}")),
            string.Join(",", checkpoint.Continuations
                .Where(item => item.RetryAfterUtc != null)
                .Select(item => $"{item.Id}@{item.RetryAfterUtc:O}"))));

        public bool CrossedBy(ExecutionCheckpoint checkpoint) => Of(checkpoint) != this;
    }

    /// <summary>Remembers a boundary crossed since the last commit, identified by the revision it followed.</summary>
    private sealed class UncommittedBoundary
    {
        private long? _afterRevision;

        public void NoteIf(bool crossed, WorkflowInstance instance)
        {
            if (crossed)
                _afterRevision = instance.Revision;
        }

        public bool IsPending(WorkflowInstance instance) => _afterRevision == instance.Revision;
    }

    // Telling a running activity to stop is visible outside the instance, so the decision that
    // cancels its branch is committed first.
    private async Task SignalCancellationsAsync(Dictionary<string, RunningActivity> inFlight,
        UncommittedBoundary uncommitted, ExecutionSession host, WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, WorkflowScopeCatalog scopes)
    {
        var cancelled = inFlight.Values.Where(item => item.Continuation.Status == ContinuationStatus.Cancelling &&
            !item.Cancellation.IsCancellationRequested).ToList();
        if (cancelled.Count == 0)
            return;
        if (uncommitted.IsPending(instance))
            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
        foreach (var activity in cancelled)
            activity.Cancellation.Cancel();
    }

    private async Task AbandonInFlightAsync(Dictionary<string, RunningActivity> inFlight)
    {
        foreach (var activity in inFlight.Values)
            activity.Cancellation.Cancel();
        try
        {
            await WaitForActivitiesAsync(inFlight);
        }
        catch
        {
            // The caller decides what the run reports; these outcomes are not committed.
        }
        finally
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Dispose();
        }
    }

    // How long a stopping run waits for cancelled activities before it lets go of the instance.
    // An activity that ignores cancellation keeps running detached; the epoch fences its outcome.
    private static readonly TimeSpan ActivityStopGrace = TimeSpan.FromSeconds(5);

    private async Task WaitForActivitiesAsync(Dictionary<string, RunningActivity> inFlight)
    {
        try
        {
            await Task.WhenAll(inFlight.Values.Select(item => item.Task)).WaitAsync(ActivityStopGrace, _time);
        }
        catch (TimeoutException)
        {
        }
    }

    // A checkpoint over the limit, or one that fails verification, is never saved. The instance keeps its last
    // checkpoint and stops at NeedsResolution until an operator releases it, or cancels it (D-28, A-08).
    private async Task<AssignmentOutcome> StopForOperatorAsync(WorkflowDefinition definition,
        WorkflowInstance instance, ExecutionCheckpoint abandoned, object workflowData, ExecutionSession host, OperatorStopKind kind, string reason)
    {
        var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance));
        abandoned.Trace.DiscardPending();
        checkpoint.Trace = abandoned.Trace;
        checkpoint.Signals = abandoned.Signals;
        checkpoint.OperatorStop = new OperatorStopState
        {
            Kind = kind, Reason = reason, StoppedAtUtc = Now
        };
        instance.Status = WorkflowStatus.NeedsResolution;
        instance.LastError = reason;
        instance.CompletedAt = null;
        // The stop saves the last committed checkpoint plus a small marker, so it is not held to the
        // limit; otherwise a checkpoint just under the limit could never stop.
        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData,
            WorkflowDefinitionValidator.ForExecution(definition), Now, preserveSavedWorkflowData: true, enforceLimit: false,
            preserveContinuationState: true);
        return new(AssignmentOutcomeKind.Idle, reason);
    }

    private static WorkflowStep? NextStep(WorkflowScopeCatalog scopes, ContinuationState continuation) =>
        scopes.Steps(continuation.Stack[^1].ScopeId).ElementAtOrDefault(continuation.Stack[^1].NextStepIndex);

    // A step hit a limit after the running activities were committed: the current checkpoint is
    // consistent and within limits, so it is saved with the stop. If it no longer fits, the commit
    // throws and the byte limit's stop keeps the last checkpoint instead.
    private async Task<AssignmentOutcome> StopForLimitAsync(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, ExecutionSession host, WorkflowScopeCatalog scopes,
        string reason)
    {
        checkpoint.OperatorStop = new OperatorStopState
        {
            Kind = OperatorStopKind.ResourceLimit, Reason = reason, StoppedAtUtc = Now
        };
        instance.Status = WorkflowStatus.NeedsResolution;
        instance.LastError = reason;
        instance.LastErrorStackTrace = null;
        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, workflowData, scopes, Now);
        return new(AssignmentOutcomeKind.Idle, reason);
    }
}
