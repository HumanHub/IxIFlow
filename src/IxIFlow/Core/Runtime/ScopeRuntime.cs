using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core.Runtime;

/// <summary>Owns a value from the instant open returns, through settlement and eventual local release.</summary>
internal sealed class ScopeRelease(object value, ScopeValues values)
{
    private object? _value = value;
    public object? Value => _value;
    private readonly object _gate = new();
    private Task? _release;

    public Task ReleaseAsync(IEnumerable<Task> dependencies, bool dispose)
    {
        lock (_gate)
            return _release ??= ReleaseCoreAsync(dependencies.ToArray(), dispose);
    }

    private async Task ReleaseCoreAsync(Task[] dependencies, bool dispose)
    {
        try
        {
            await Task.WhenAll(dependencies).ConfigureAwait(false);
        }
        catch
        {
        }

        await values.Fresh().RunAsync(async () =>
        {
            try
            {
                if (dispose)
                {
                    if (_value is IAsyncDisposable asynchronous)
                        await asynchronous.DisposeAsync();
                    else if (_value is IDisposable synchronous)
                        synchronous.Dispose();
                }
            }
            finally
            {
                _value = null;
            }

            return true;
        }, cleanup: true).ConfigureAwait(false);
    }
}

internal sealed class ScopeContext(IActivityContext activity, ScopeValues values) : IScopeContext
{
    public IServiceProvider Services => activity.Services;
    public string WorkflowInstanceId => activity.WorkflowInstanceId;
    public string CorrelationId => activity.CorrelationId;
    public string WorkflowName => activity.WorkflowName;
    public int WorkflowVersion => activity.WorkflowVersion;
    public string InvocationId => activity.InvocationId;
    public ILogger Logger => activity.Logger;

    public TValue Scope<TValue>() where TValue : class => ScopeOrDefault<TValue>() ??
        throw new InvalidOperationException($"No enclosing scope provides {typeof(TValue)}");

    public TValue? ScopeOrDefault<TValue>() where TValue : class => values.Find<TValue>();
}

internal sealed class ScopeRuntime(
    IServiceProvider services,
    IActivityExecutor executor,
    ContinuationTransitions transitions,
    TimeProvider time,
    WorkflowScopeCatalog catalog,
    ExecutionCheckpoint checkpoint,
    WorkflowDefinition definition,
    WorkflowInstance instance,
    ExecutionSession host,
    WorkflowDefinitionLease definitionLease)
{
    private readonly ActivityTransitionExecutor _activities = new(executor);
    private readonly List<ScopeFrameState> _owned = [];
    private readonly List<ScopeThreadScheduler> _threads = [];
    private readonly HashSet<Task> _admitted = [];
    private readonly List<Task> _releases = [];
    private readonly Dictionary<Task, Task> _extensions = [];
    private readonly Dictionary<ScopeFrameState, Task> _cleanupAdmissions = [];
    private bool IsActive(Task task) => !task.IsCompleted || _extensions.GetValueOrDefault(task)?.IsCompleted == false;
    public int ActiveAuthorCount => _admitted.Count(IsActive);

    public int ActiveCount(IEnumerable<RunningActivity> running) => _admitted.Where(IsActive)
        .Concat(running.Where(item => item.Step.StepType is WorkflowStepType.Activity or WorkflowStepType.Scope)
            .Select(item => item.ActualTask ?? item.Task)).Distinct().Count();

    private readonly Dictionary<string, List<Task>> _users = new(StringComparer.Ordinal);
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public static List<string> IdsOf(ContinuationState continuation) => (continuation.InheritedScopes ?? [])
        .Concat(continuation.Stack.Where(frame => frame.Kind == FrameKind.Scope).Select(frame => frame.ActivationId))
        .Distinct(StringComparer.Ordinal).ToList();

    public ScopeFrameState[] Entries(IEnumerable<string> ids) => ids.Select(id =>
        checkpoint.Scopes.FirstOrDefault(entry => entry.Id == id) ?? _owned.Single(entry => entry.Id == id)).ToArray();

    public ScopeValues ValuesOf(ContinuationState continuation) => new(Entries(IdsOf(continuation)));

    public bool Available(ContinuationState continuation)
    {
        if (continuation.PendingActivity?.Resolution != null)
            return true;

        var entries = Entries(IdsOf(continuation));

        return entries.All(entry => !entry.Quarantined && entry.InFlight == null &&
            (entry.OpenAfterUtc == null || entry.OpenAfterUtc <= checkpoint.Clock.ToStore(Now)) &&
            (catalog.Step(entry.StepId).ScopeConcurrency != ScopeConcurrency.Serialized || entry.Permit?.IsCompleted != false));
    }

    public bool HasQuarantine => _owned.Any(entry => entry.Quarantined);

    public static void RecordClose(ExecutionCheckpoint checkpoint, ContinuationState continuation, string id,
        ScopeCloseReason reason, PendingFailure? error = null)
    {
        var entry = checkpoint.Scopes.Single(item => item.Id == id);

        if (entry.Phase == ScopePhase.Closing)
            return;

        entry.Phase = ScopePhase.Closing;
        entry.CloseReason = reason;
        entry.CloseError = error;
        (continuation.PendingScopeCloses ??= []).Add(id);
        checkpoint.TouchOwner(entry.ContinuationId);
    }

    public bool CanClose(ContinuationState continuation)
    {
        if (continuation.PendingScopeCloses is not { Count: > 0 } pending)
            return false;

        var entry = checkpoint.Scopes.Single(item => item.Id == pending[0]);

        return entry.InFlight == null && !entry.Quarantined &&
            Entries(entry.EnclosingScopeIds).All(outer => !outer.Quarantined && outer.InFlight == null &&
                (catalog.Step(outer.StepId).ScopeConcurrency != ScopeConcurrency.Serialized || outer.Permit?.IsCompleted != false)) &&
            !checkpoint.Continuations.Any(branch => branch.Id != continuation.Id &&
                (branch.Status is not (ContinuationStatus.Completed or ContinuationStatus.Cancelled) ||
                    branch.PendingScopeCloses is { Count: > 0 }) &&
                branch.InheritedScopes?.Contains(entry.Id) == true) &&
            !_users.GetValueOrDefault(entry.Id, []).Any(task => !task.IsCompleted);
    }

    public async Task<(bool Required, RunningActivity? Operation)> PrerequisiteAsync(ContinuationState continuation, WorkflowStep step,
        CancellationTokenSource cancellation)
    {
        if (continuation.PendingActivity?.Resolution != null)
            return (false, null);

        foreach (var entry in Entries(IdsOf(continuation)))
            if (entry.Value == null)
                return (true, await OpenAsync(continuation, catalog.Step(entry.StepId), cancellation, entry, reopen: true));

        return step.StepType == WorkflowStepType.Scope
            ? (true, await OpenAsync(continuation, step, cancellation, null, reopen: false))
            : (false, null);
    }

    private async Task<PreparedActivity> PrepareAsync(ContinuationState continuation, WorkflowStep step, string invocation,
        CancellationToken cancellation, bool inputs, ScopeValues values) =>
        await _activities.PrepareAsync(step, definition, instance,
            inputs
                ? checkpoint.DataOf(continuation)
                : (step.WorkflowDataType == typeof(string)
                    ? string.Empty
                    : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(step.WorkflowDataType)),
            inputs ? continuation.GetPrevious(services) : null,
            inputs ? TryScopeTransitions.CatchFault(checkpoint, continuation, services, step, catalog) : null,
            cancellation, invocation, "", continuation.Stack.LastOrDefault()?.NextStepIndex ?? 0, inputs,
            items: inputs ? ForEachTransitions.ItemsOf(catalog, checkpoint, continuation) : ItemValues.None, scopeValues: values);

    private async Task<RunningActivity?> OpenAsync(ContinuationState needing, WorkflowStep step, CancellationTokenSource cancellation,
        ScopeFrameState? entry, bool reopen)
    {
        using var templateDispatch = WorkflowDefinitionScope.Dispatch(definition, catalog.Id(step));

        entry ??= checkpoint.Scopes.SingleOrDefault(item => item.Phase is ScopePhase.Preparing or ScopePhase.Opening &&
            item.ContinuationId == needing.Id && item.StepId == catalog.Id(step));

        var fresh = entry == null || entry.Phase == ScopePhase.Preparing;

        entry ??= new ScopeFrameState
            { ContinuationId = needing.Id, StepId = catalog.Id(step), EnclosingScopeIds = IdsOf(needing), Phase = ScopePhase.Preparing };

        var enclosing = Entries(entry.EnclosingScopeIds);
        entry.Scheduler ??= enclosing.Select(item => item.Scheduler).FirstOrDefault(item => item != null);
        var callbackValues = new ScopeValues([.. enclosing, entry]);
        var contextValues = new ScopeValues(enclosing, callbackValues.Operation);
        PreparedActivity prepared;

        try
        {
            prepared = await PrepareAsync(needing, step, entry.InvocationId, cancellation.Token, fresh, contextValues);

            if (!fresh)
                entry.Inputs.Restore(prepared.Activity, services);

            ActivityContract.ValidateRequired(prepared.Activity, fresh ? prepared.Inputs : entry.Inputs, services);
        }
        catch (Exception error)
        {
            templateDispatch.ThrowIfFailed();

            if (!WorkflowEngineException.IsFromWorkflowCode(error))
                throw;

            if (!checkpoint.Scopes.Contains(entry))
                checkpoint.Scopes.Add(entry);

            await OpenFailedAsync(needing, entry, step, error, reopen);

            return null;
        }

        // Saved pins are engine decisions, outside the authored preparation boundary.
        templateDispatch.ThrowIfFailed();
        templateDispatch.ValidateInputs(fresh ? prepared.Inputs : entry.Inputs, prepared.Activity);

        if (fresh)
        {
            entry.Inputs = prepared.Inputs;
            entry.Phase = ScopePhase.Opening;

            if (!checkpoint.Scopes.Contains(entry))
                checkpoint.Scopes.Add(entry);

            checkpoint.TouchOwner(entry.ContinuationId);

            await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, catalog, Now,
                clearsCrashCount: false);
        }

        if (!_owned.Contains(entry))
            _owned.Add(entry);

        if (entry.Scheduler == null && step.ScopeThreading == ScopeThreading.ThreadAffine)
        {
            entry.Scheduler = ((ScopeThreadRegistry)services.GetService(typeof(ScopeThreadRegistry))!).Create();
            _threads.Add(entry.Scheduler);
        }

        var captured = entry;
        var method = typeof(IScopeActivity<>).MakeGenericType(step.ScopeValueType!).GetMethod(nameof(IScopeActivity<object>.OpenAsync))!;

        var actual = ObserveOpenDispatchAsync(callbackValues.RunAsync(async token =>
        {
            Task? task;

            try
            {
                task = (Task?)Invoke(method, prepared.Activity,
                    [new ScopeOpening(reopen, captured.RecordedOpens), new ScopeContext(prepared.Context, contextValues), token]);
            }
            catch (Exception error) when (WorkflowEngineException.IsFromWorkflowCode(error))
            {
                templateDispatch.ThrowIfFailed();

                return ActivityRunResult.Threw(error);
            }

            if (task == null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "A scope open returned a null task");

            try
            {
                await task;
            }
            catch (Exception error)
            {
                templateDispatch.ThrowIfFailed();

                return ActivityRunResult.Threw(error);
            }

            var value = task.GetType().GetProperty("Result")!.GetValue(task)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "A scope open returned a null value");

            // Transfer ownership before returning to the executor, including late/abandoned results.
            captured.Release = new ScopeRelease(value, callbackValues);

            // Keep ownership of a returned value even when author code swallowed a refusal.
            try
            {
                templateDispatch.ThrowIfFailed();
            }
            catch (Exception error)
            {
                return new(ActivityRunKind.Threw, Error: error);
            }

            return ActivityRunResult.Returned(null);
        }, cancellation.Token), cancellation.Token);

        var running = new RunningActivity(needing, step, actual, cancellation, prepared, needing.Stack.LastOrDefault()?.NextStepIndex ?? 0)
        {
            CommitSettlement = false,
            LifecycleSettlement = async () =>
            {
                using var templateFrame = templateDispatch.Resume();
                captured.InFlight = null;
                ActivityRunResult result;

                try
                {
                    result = await actual;
                    templateDispatch.ThrowIfFailed();
                }
                catch (Exception error)
                {
                    result = Failed(error);
                }

                if (result.Kind == ActivityRunKind.Skipped)
                {
                    if (!reopen)
                    {
                        checkpoint.Scopes.Remove(captured);
                        await ReclaimThreadAsync(captured);
                    }

                    checkpoint.TouchOwner(captured.ContinuationId);
                    needing.AuthorizedDispatch = null;

                    return;
                }

                if (!reopen && (result.Error == null || WorkflowEngineException.IsFromWorkflowCode(result.Error)) &&
                    (needing.Status is ContinuationStatus.Cancelling or ContinuationStatus.Cancelled ||
                        needing.CancellationUnwind && !CancellationScopeTransitions.IsInsideHandler(needing) &&
                        !TryScopeTransitions.IsInsideFinally(needing) && !SagaScopeTransitions.IsInsideCompensation(needing)))
                {
                    if (captured.Release?.Value is { } cancelledValue)
                    {
                        captured.Value = cancelledValue;
                        captured.LiveActivity = prepared.Activity;
                        using var cleanupCancellation = new CancellationTokenSource();
                        var close = CloseValueAsync(captured, ScopeCloseReason.Cancelled, null, cleanupCancellation.Token);
                        AdmitCleanup(captured, close);
                        var closed = await ObserveCloseAsync(close, cleanupCancellation);

                        if (closed.Error != null)
                            Trace(captured, TraceEntryType.ScopeCloseFailed, error: closed.Error, reason: ScopeCloseReason.Cancelled);

                        if (!close.IsCompleted)
                            Quarantine(captured);

                        await BoundReleaseAsync(captured, dispose: closed.Error != null);

                        if (closed.Error != null && !WorkflowEngineException.IsFromWorkflowCode(closed.Error))
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closed.Error).Throw();

                        Trace(captured, TraceEntryType.ScopeClosed, reason: ScopeCloseReason.Cancelled);
                    }

                    checkpoint.Scopes.Remove(captured);
                    await ReclaimThreadAsync(captured);
                    checkpoint.TouchOwner(captured.ContinuationId);
                    needing.AuthorizedDispatch = null;

                    return;
                }

                if (result.Error == null)
                {
                    try
                    {
                        foreach (var property in prepared.Activity.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                                     .Where(property => property.GetMethod != null && property.GetIndexParameters().Length == 0))
                            if (ReferenceEquals(ActivityPropertyAccess.GetValue(property, prepared.Activity), captured.Release!.Value))
                                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                                    $"Scope property '{property.Name}' holds its live value");

                        SerializedValue saved;

                        try
                        {
                            saved = SerializedValue.From(prepared.Activity)!;
                        }
                        catch (Exception error)
                        {
                            templateDispatch.ThrowIfFailed();

                            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                                "Scope configuration and outputs cannot be saved", error);
                        }

                        templateDispatch.ThrowIfFailed();

                        if (!reopen)
                        {
                            var copy = saved.Read(services)!;
                            checkpoint.MarkDataChanged(needing);

                            WorkflowValueBinding.ApplyActivityOutputs(step, copy, checkpoint.DataOf(needing),
                                ForEachTransitions.ItemsOf(catalog, checkpoint, needing));

                            templateDispatch.ThrowIfFailed();
                            captured.Activity = saved;
                            needing.SetPrevious(copy);
                            templateDispatch.ThrowIfFailed();

                            needing.Stack.Add(new ScopePosition
                            {
                                Kind = FrameKind.Scope, ActivationId = captured.Id,
                                ScopeId = catalog.ScopeBodyScope(step), EntryPrevious = saved
                            });

                            captured.Phase = ScopePhase.Open;
                            needing.AuthorizedDispatch = null;
                        }

                        captured.Value = captured.Release!.Value;
                        captured.LiveActivity = prepared.Activity;
                        captured.RecordedOpens++;
                        captured.OpenFailures = 0;
                        captured.OpenAfterUtc = null;
                        checkpoint.TouchOwner(captured.ContinuationId);
                        Trace(captured, TraceEntryType.ScopeOpened, reopen: reopen);

                        return;
                    }
                    catch (Exception error)
                    {
                        result = Failed(error);
                    }
                }

                if (captured.Release?.Value is { } returned)
                {
                    captured.Value = returned;
                    captured.LiveActivity = prepared.Activity;
                    using var cleanupCancellation = new CancellationTokenSource();
                    var cleanup = CloseValueAsync(captured, ScopeCloseReason.Failed, result.Error, cleanupCancellation.Token);
                    AdmitCleanup(captured, cleanup);
                    var cleanupResult = await ObserveCloseAsync(cleanup, cleanupCancellation);

                    if (cleanupResult.Error != null)
                        Trace(captured, TraceEntryType.ScopeCloseFailed, error: cleanupResult.Error);

                    if (!cleanup.IsCompleted)
                        Quarantine(captured);

                    await BoundReleaseAsync(captured, dispose: cleanupResult.Error != null);

                    if (cleanupResult.Error != null && !WorkflowEngineException.IsFromWorkflowCode(cleanupResult.Error))
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupResult.Error).Throw();
                }

                if (!WorkflowEngineException.IsFromWorkflowCode(result.Error!))
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Error!).Throw();

                await OpenFailedAsync(needing, captured, step, result.Error!, reopen);
            }
        };

        captured.InFlight = running;
        Retain(actual);
        Reserve(running, enclosing);

        return running;

        ActivityRunResult Failed(Exception error)
        {
            try
            {
                templateDispatch.ThrowIfFailed();
            }
            catch (Exception guard)
            {
                error = guard;
            }

            return new(ActivityRunKind.Threw, Error: error);
        }
    }

    private static async Task<ActivityRunResult> ObserveOpenDispatchAsync(Task<ActivityRunResult> dispatch, CancellationToken cancellation)
    {
        try
        {
            return await dispatch;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The queued open was rejected before author entry; it has no fault or value to close.
            return ActivityRunResult.Skipped();
        }
    }

    private async Task OpenFailedAsync(ContinuationState needing, ScopeFrameState entry, WorkflowStep step, Exception error, bool reopen)
    {
        checkpoint.TouchOwner(entry.ContinuationId);
        entry.OpenFailures++;

        if (reopen && needing.PendingActivity is { Resolution: null } pending)
        {
            pending.ResolutionReason = $"The scope '{step.Name}' could not be re-opened for recovery: {error.Message}";
            pending.ResolutionDiagnostic = checkpoint.RecordDiagnostic(error);
            needing.Status = ContinuationStatus.WaitingResolution;
        }
        else if (step.ScopeRetryPolicy is { } retry && entry.OpenFailures <= retry.MaximumAttempts)
        {
            entry.OpenAfterUtc = RetrySchedule.NextAttemptUtc(retry, entry.OpenFailures, checkpoint.Clock.ToStore(Now));
            needing.RetryAfterUtc = entry.OpenAfterUtc;
        }
        else
        {
            var failure = PendingFailure.Capture(error, catalog, checkpoint, needing, newOccurrence: true);

            if (reopen)
                failure = new PendingFailure
                {
                    Error = failure.Error,
                    ScopeBoundary = new() { OwnerContinuationId = entry.ContinuationId, BodyActivationId = entry.Id }
                };
            else
            {
                checkpoint.Scopes.Remove(entry);
                await ReclaimThreadAsync(entry);
            }

            transitions.CaptureFailure(catalog, checkpoint, needing, failure, allowStepRetry: false);
        }

        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, catalog, Now);
    }

    public async Task<RunningActivity?> StartCloseAsync(ContinuationState continuation)
    {
        var entry = checkpoint.Scopes.Single(item => item.Id == continuation.PendingScopeCloses![0]);

        if (entry.Value == null)
        {
            CompleteClose(continuation, entry, interrupted: true);

            return null;
        }

        foreach (var enclosing in Entries(entry.EnclosingScopeIds))
            if (enclosing.Value == null)
                return await OpenAsync(continuation, catalog.Step(enclosing.StepId), new(), enclosing, true);

        var cancellation = new CancellationTokenSource();
        var actual = CloseValueAsync(entry, entry.CloseReason!.Value, entry.CloseError?.ForPropagation(checkpoint), cancellation.Token);
        _cleanupAdmissions[entry] = actual;
        var timed = ObserveCloseAsync(actual, cancellation);

        var running = new RunningActivity(continuation, catalog.Step(entry.StepId), timed, cancellation, null, 0)
        {
            CommitSettlement = false, ActualTask = actual,
            LifecycleSettlement = async () =>
            {
                entry.InFlight = null;
                var result = await timed;

                if (result.Error != null)
                {
                    Trace(entry, TraceEntryType.ScopeCloseFailed, error: result.Error);

                    if (!actual.IsCompleted)
                        Quarantine(entry);

                    await BoundReleaseAsync(entry, dispose: true);
                }
                else
                    await BoundReleaseAsync(entry, dispose: false);

                if (result.Error != null && !WorkflowEngineException.IsFromWorkflowCode(result.Error))
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Error).Throw();

                var reason = entry.CloseReason;

                CompleteClose(continuation, entry, interrupted: false,
                    advance: result.Error == null || reason != ScopeCloseReason.Completed);

                if (result.Error != null && reason == ScopeCloseReason.Completed)
                {
                    transitions.CaptureFailure(catalog, checkpoint, continuation,
                        PendingFailure.Capture(result.Error, catalog, checkpoint, continuation, newOccurrence: true),
                        allowStepRetry: false);

                    await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, checkpoint.RootData, catalog, Now);
                }
            }
        };

        entry.InFlight = running;
        Reserve(running, [.. Entries(entry.EnclosingScopeIds), entry]);
        Retain(actual);

        return running;
    }

    private async Task<ActivityRunResult> ObserveCloseAsync(Task actual, CancellationTokenSource cancellation)
    {
        using var timeout = new CancellationTokenSource();
        var deadline = Task.Delay(host.Limits.ScopeCloseTimeout, time, timeout.Token);

        try
        {
            while (!actual.IsCompleted)
            {
                var wake = checkpoint.Signals?.Changed;
                var completed = await Task.WhenAny(wake == null ? [actual, deadline] : new[] { actual, deadline, wake });

                if (checkpoint.Signals?.OwnershipLost == true)
                {
                    ScopeOperation.RequestCancellation(cancellation);

                    throw new ExecutionOwnershipLostException(new InvalidOperationException("Ownership was lost during scope cleanup"));
                }

                if (completed == deadline)
                    throw new TimeoutException("Scope close exceeded its configured timeout");
            }

            await actual;

            return ActivityRunResult.Returned(null);
        }
        catch (ExecutionOwnershipLostException)
        {
            throw;
        }
        catch (Exception error)
        {
            ScopeOperation.RequestCancellation(cancellation);

            return new(ActivityRunKind.Threw, Error: error);
        }
        finally
        {
            timeout.Cancel();
        }
    }

    private Task CloseValueAsync(ScopeFrameState entry, ScopeCloseReason reason, Exception? error, CancellationToken cancellation = default)
    {
        checkpoint.Signals?.ThrowIfOwnershipLost();
        using var templateDispatch = WorkflowDefinitionScope.Dispatch(definition, entry.StepId);
        templateDispatch.ValidateInputs(entry.Inputs, entry.LiveActivity!);
        var values = new ScopeValues([.. Entries(entry.EnclosingScopeIds), entry]);
        var prepared = entry.InFlight?.Prepared;

        return values.RunAsync(async token =>
        {
            var step = catalog.Step(entry.StepId);

            var method =
                typeof(IScopeActivity<>).MakeGenericType(step.ScopeValueType!).GetMethod(nameof(IScopeActivity<object>.CloseAsync))!;

            var context = new ScopeContext(prepared?.Context ?? new LifecycleIdentity(services, instance, entry.InvocationId),
                new ScopeValues(Entries(entry.EnclosingScopeIds), values.Operation));

            var task = (Task?)Invoke(method, entry.LiveActivity!, [entry.Value, new ScopeClosing(reason, error), context, token])
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "A scope close returned a null task");

            try
            {
                await task;
            }
            catch (Exception failure)
            {
                templateDispatch.ThrowIfFailed();
                WorkflowEngineException.FromWorkflowCode(failure);

                throw;
            }

            templateDispatch.ThrowIfFailed();

            return true;
        }, cancellation, cleanup: true);
    }

    private void CompleteClose(ContinuationState continuation, ScopeFrameState entry, bool interrupted, bool advance = true)
    {
        Trace(entry, TraceEntryType.ScopeClosed, interrupted: interrupted);
        checkpoint.Scopes.Remove(entry);
        continuation.PendingScopeCloses!.Remove(entry.Id);
        checkpoint.TouchOwner(entry.ContinuationId);

        if (entry.CloseReason == ScopeCloseReason.Completed && advance && continuation.Stack.Count > 0)
        {
            continuation.Previous = entry.Activity;
            continuation.RuntimePrevious = null;
            continuation.Stack[^1].NextStepIndex++;
            continuation.AuthorizedDispatch = null;
        }

        if (continuation.PendingScopeCloses.Count == 0 && continuation.Stack.Count == 0 && continuation.Status == ContinuationStatus.Active)
            continuation.Status = ContinuationStatus.Completed;
    }

    public void Reserve(RunningActivity running, IEnumerable<ScopeFrameState>? entries = null)
    {
        var actual = running.ActualTask ?? running.Task;
        Retain(actual);
        _admitted.RemoveWhere(task => task.IsCompleted && _extensions.GetValueOrDefault(task)?.IsCompleted != false);
        _admitted.Add(actual);

        foreach (var entry in entries ?? Entries(IdsOf(running.Continuation)))
        {
            if (catalog.Step(entry.StepId).ScopeConcurrency == ScopeConcurrency.Serialized)
                entry.Permit = actual;

            if (!_users.TryGetValue(entry.Id, out var tasks))
                _users.Add(entry.Id, tasks = []);

            tasks.RemoveAll(task => task.IsCompleted);
            tasks.Add(actual);
        }
    }

    private void AdmitCleanup(ScopeFrameState entry, Task actual)
    {
        _cleanupAdmissions[entry] = actual;
        _admitted.Add(actual);

        foreach (var member in Entries(entry.EnclosingScopeIds).Append(entry))
        {
            if (catalog.Step(member.StepId).ScopeConcurrency == ScopeConcurrency.Serialized)
                member.Permit = actual;

            if (!_users.TryGetValue(member.Id, out var tasks))
                _users.Add(member.Id, tasks = []);

            tasks.Add(actual);
        }

        Retain(actual);
    }

    private void Quarantine(ScopeFrameState entry)
    {
        foreach (var member in Entries(entry.EnclosingScopeIds).Append(entry))
        {
            member.Quarantined = true;

            if (member.Scheduler != null)
                member.Scheduler.Sealed = true;
        }
    }

    private Task ReleaseAsync(ScopeFrameState entry, bool dispose)
    {
        if (entry.Release == null)
            return Task.CompletedTask;

        var task = ClearReleasedEntryAsync(entry, entry.Release.ReleaseAsync(_users.GetValueOrDefault(entry.Id, []), dispose));

        if (!_releases.Contains(task))
            _releases.Add(task);

        if (_cleanupAdmissions.TryGetValue(entry, out var admission))
            _extensions[admission] = task;

        foreach (var outer in Entries(entry.EnclosingScopeIds))
        {
            if (!_users.TryGetValue(outer.Id, out var tasks))
                _users.Add(outer.Id, tasks = []);

            tasks.Add(task);
        }

        // The operation context still resolves through this entry until its users and
        // cancellation posts finish. Drop the binding on the owning thread afterward.
        Retain(task);

        return task;
    }

    private async Task ClearReleasedEntryAsync(ScopeFrameState entry, Task release)
    {
        try
        {
            await release.ConfigureAwait(false);
        }
        finally
        {
            await new ScopeValues([.. Entries(entry.EnclosingScopeIds), entry]).RunAsync(() =>
            {
                entry.Value = null;
                entry.LiveActivity = null;

                return Task.FromResult(true);
            }, cleanup: true).ConfigureAwait(false);
        }
    }

    private async Task ReclaimThreadAsync(ScopeFrameState entry)
    {
        var scheduler = entry.Scheduler;

        if (scheduler == null || scheduler.Sealed || _owned.Any(other => other != entry &&
                other.Scheduler == scheduler && (other.Value != null || other.InFlight != null ||
                    checkpoint.Scopes.Contains(other) && other.Phase != ScopePhase.Closing)))
            return;

        await scheduler.StopAsync();

        foreach (var owned in _owned.Where(owned => owned.Scheduler == scheduler))
            owned.Scheduler = null;

        _threads.Remove(scheduler);
    }

    private async Task BoundReleaseAsync(ScopeFrameState entry, bool dispose)
    {
        var release = ReleaseAsync(entry, dispose);

        try
        {
            await release.WaitAsync(host.Limits.ReleaseGrace, time);
            await ReclaimThreadAsync(entry);
        }
        catch (TimeoutException)
        {
            Quarantine(entry);
        }
        catch (Exception error)
        {
            LogScope.Logger(services).LogWarning(error, "Local scope release failed");
        }
    }

    public async Task FinishPassAsync(bool abandon)
    {
        bool HardStop() => checkpoint.Signals is { OwnershipLost: true } or { RunCancelled: true } or
            { Cancellation.Mode: WorkflowControlMode.Terminate };

        abandon |= HardStop();

        if (!abandon)
        {
            while (!HardStop() && ActiveAuthorCount < host.Limits.MaxActivitiesPerRun &&
                   checkpoint.Continuations.FirstOrDefault(CanClose) is { } owner)
            {
                var operation = await StartCloseAsync(owner);

                if (operation == null)
                    continue;

                try
                {
                    await operation.Task;
                    await operation.LifecycleSettlement!();
                }
                finally
                {
                    operation.Cancellation.Dispose();
                }
            }
        }

        var releases = new List<Task>();
        Exception? engineFailure = null;

        foreach (var entry in _owned.OrderByDescending(item => item.EnclosingScopeIds.Count))
        {
            abandon |= HardStop();

            if (entry.InFlight is { } running)
            {
                if (!running.Cancellation.IsCancellationRequested)
                    ScopeOperation.RequestCancellation(running.Cancellation);

                var late = ReleaseLateAsync(entry, running.ActualTask ?? running.Task);

                foreach (var outer in Entries(entry.EnclosingScopeIds))
                {
                    if (!_users.TryGetValue(outer.Id, out var tasks))
                        _users.Add(outer.Id, tasks = []);

                    tasks.Add(late);
                }

                Retain(late);
                releases.Add(late);

                continue;
            }

            if (entry.Value == null)
                continue;

            if (!abandon && ActiveAuthorCount < host.Limits.MaxActivitiesPerRun && !entry.Quarantined &&
                !_users.GetValueOrDefault(entry.Id, []).Any(task => !task.IsCompleted))
            {
                var cancellation = new CancellationTokenSource();
                Task actual;

                try
                {
                    actual = CloseValueAsync(entry, ScopeCloseReason.Suspended, null, cancellation.Token);
                }
                catch
                {
                    cancellation.Dispose();

                    throw;
                }

                AdmitCleanup(entry, actual);
                ActivityRunResult outcome;

                try
                {
                    outcome = await ObserveCloseAsync(actual, cancellation);
                }
                finally
                {
                    // CancelAsync can still be dispatching the close's cleanup callbacks when
                    // its timeout is observed. Disposing their source now can remove the marker
                    // ScopeValues waits for, preventing the actual task and release from ending.
                    if (actual.IsCompleted)
                        cancellation.Dispose();
                    else
                        Retain(actual.ContinueWith(completed =>
                        {
                            _ = completed.Exception;
                            cancellation.Dispose();
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
                }

                if (outcome.Error == null)
                {
                    Trace(entry, TraceEntryType.ScopeClosed, reason: ScopeCloseReason.Suspended);
                    await BoundReleaseAsync(entry, dispose: false);

                    continue;
                }

                Trace(entry, TraceEntryType.ScopeCloseFailed, error: outcome.Error, reason: ScopeCloseReason.Suspended);

                if (outcome.Error is not TimeoutException && !WorkflowEngineException.IsFromWorkflowCode(outcome.Error))
                    engineFailure ??= outcome.Error;

                if (!actual.IsCompleted)
                    Quarantine(entry);
            }

            releases.Add(ReleaseAsync(entry, true));
        }

        releases.AddRange(_releases.Where(task => !task.IsCompleted));
        releases.AddRange(_users.Values.SelectMany(tasks => tasks).Where(task => !task.IsCompleted));
        var cleanup = StopThreadsAsync(releases);
        Retain(cleanup);

        try
        {
            await cleanup.WaitAsync(host.Limits.ReleaseGrace, time);

            foreach (var entry in _owned)
            {
                entry.Scheduler = null;
                entry.Release = null;
            }

            _threads.Clear();
        }
        catch (TimeoutException)
        {
            LogScope.Logger(services).LogWarning("Scope release is still running; {Count} scope threads remain retained", _threads.Count);
        }
        catch (Exception error)
        {
            LogScope.Logger(services).LogWarning(error, "Local scope release failed");
        }

        if (engineFailure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(engineFailure).Throw();
    }

    private async Task ReleaseLateAsync(ScopeFrameState entry, Task actual)
    {
        var dependencies = _users.GetValueOrDefault(entry.Id, []).ToArray();

        try
        {
            await actual;
        }
        catch
        {
        }

        if (entry.Release != null)
            await ClearReleasedEntryAsync(entry, entry.Release.ReleaseAsync(dependencies, true));
    }

    private async Task StopThreadsAsync(List<Task> releases)
    {
        try
        {
            await Task.WhenAll(releases);
        }
        finally
        {
            await Task.WhenAll(_threads.Select(thread => thread.StopAsync()));
        }
    }

    private void Retain(Task task)
    {
        host.RetainWork(task);
        var lease = definitionLease.Retain();
        ExecutionWork.Retain(task);

        _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                lease.Dispose();
            }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Trace(ScopeFrameState entry, TraceEntryType kind, bool reopen = false, bool interrupted = false,
        Exception? error = null, ScopeCloseReason? reason = null)
    {
        if (checkpoint.Trace.Accepts(kind, Now))
            checkpoint.Trace.Add(new ExecutionTraceEntry
            {
                Timestamp = Now, EntryType = kind, StepId = entry.StepId, ContinuationId = entry.ContinuationId,
                FrameActivationId = entry.Id, Error = error == null ? null : checkpoint.RecordDiagnostic(error),
                Payload = new ScopeTrace(entry.StepId, WorkflowStepType.Scope, entry.ContinuationId, entry.Id, reopen,
                    entry.RecordedOpens, reason ?? entry.CloseReason, interrupted
                        ? ScopeTraceCause.Interrupted
                        : error is TimeoutException
                            ? ScopeTraceCause.Timeout
                            : kind == TraceEntryType.ScopeOpened
                                ? null
                                : ScopeTraceCause.Closed) { InvocationId = entry.InvocationId }
            });
    }

    private static object? Invoke(MethodInfo method, object target, object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            WorkflowDefinitionScope.ThrowIfDispatchFailed();
            WorkflowEngineException.FromWorkflowCode(error.InnerException);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();

            throw;
        }
    }

    private sealed class LifecycleIdentity(IServiceProvider services, WorkflowInstance instance, string invocation) : IActivityContext
    {
        public IServiceProvider Services => services;
        public string WorkflowInstanceId => instance.InstanceId;
        public string CorrelationId => instance.CorrelationId;
        public string WorkflowName => instance.WorkflowName;
        public int WorkflowVersion => instance.WorkflowVersion;
        public string InvocationId => invocation;
        public string AttemptId => "";
        public ActivityAttempt Attempt { get; } = ActivityAttempt.First;
        public object? RecoveryState => null;
        public string ActivityName => "Scope";
        public int StepNumber => 0;
        public DateTime ActivityStartedAt => default;
        public ILogger Logger => LogScope.Logger(services);
        public object WorkflowData => throw new InvalidOperationException("Lifecycle code has no workflow data");
        public object? PreviousStepData => null;
        public IDictionary<string, object> Metadata { get; } = new Dictionary<string, object>();
        public ReplyAddress? Reply => null;
    }
}