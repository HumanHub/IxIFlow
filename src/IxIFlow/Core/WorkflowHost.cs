using System.Diagnostics.CodeAnalysis;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Core;

/// <summary>
/// The host every workflow execution runs in (B-01 to B-03, D-02, D-16). <c>AddIxIFlow</c> registers it as a hosted
/// service, and <see cref="IWorkflowClient"/> calls become its work. Work from the application and from recovery runs
/// in at most <c>IxIFlowOptions.Slots</c> slots, each in its own scope. Work for one instance waits
/// in that instance's queue without holding a slot. A call made from inside a slot, for example by
/// an activity, runs inline in that slot instead of waiting for another. On stop the host drains:
/// it accepts nothing new, cancels queued work, lets running instances reach their next commit,
/// and cancels them after the drain timeout.
/// </summary>
public sealed partial class WorkflowHost : IHostedService, IDisposable
{
    private static readonly AsyncLocal<RunContext?> CurrentRun = new();

    private readonly IServiceScopeFactory _scopes;
    private readonly HostSettings _settings;
    private readonly SemaphoreSlim _slots;
    private readonly object _lock = new();
    // An instance with an entry has one item admitted; the queue holds the items behind it.
    private readonly Dictionary<string, Queue<WorkItem>> _instances = new(StringComparer.Ordinal);
    // Every admitted item until it finishes, added under the lock that admits it, so a stopping
    // host waits for all of them.
    private readonly HashSet<WorkItem> _admitted = [];
    // Runs that ended while inline calls were still using their scope; each holds its slot until
    // the last of those calls ends.
    private readonly HashSet<Task> _lingering = [];
    // Callers waiting for work on an instance to finish, by instance.
    private readonly Dictionary<string, SettledSignal> _settled = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _draining = new();
    private readonly CancellationTokenSource _stopped = new();
    private bool _accepting = true;
    // Instances this host saved with a later runnable time, earliest first, and one timer for the
    // earliest (C-12). Work saved by other hosts, and anything lost here, is found by the poll.
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SortedSet<(DateTime Due, string InstanceId)> _due = new(Comparer<(DateTime Due, string InstanceId)>
        .Create((left, right) => left.Due != right.Due
            ? left.Due.CompareTo(right.Due)
            : string.CompareOrdinal(left.InstanceId, right.InstanceId)));
    private readonly Dictionary<string, DateTime> _dueOf = new(StringComparer.Ordinal);
    // Resumes that found the instance owned or failed, by instance, for their backoff.
    private readonly Dictionary<string, int> _resumeRetries = new(StringComparer.Ordinal);
    private const int MaxResumeRetries = 6;
    private ITimer? _timer;
    private DateTime? _armedFor;

    internal WorkflowHost(IServiceScopeFactory scopes, HostSettings settings, TimeProvider? time = null,
        ILogger<WorkflowHost>? logger = null)
    {
        _scopes = scopes;
        _settings = settings;
        _slots = new SemaphoreSlim(settings.Slots, settings.Slots);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<WorkflowHost>.Instance;
        // The timer must not carry the context of whoever first scheduled something: its callbacks
        // would then look like calls from inside that run.
        using (ExecutionContext.SuppressFlow())
            _timer = _time.CreateTimer(_ => ResumeDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Slots not running anything now; the poll claims at most this many instances.</summary>
    internal int FreeSlots => _slots.CurrentCount;

    /// <summary>
    /// Resumes an instance at <paramref name="dueUtc"/>, the runnable time a run just saved for it.
    /// The latest saved time replaces the one before, which a later step may have made obsolete.
    /// </summary>
    internal void Schedule(string instanceId, DateTime dueUtc)
    {
        lock (_lock)
        {
            if (!_accepting)
                return;
            SetDueLocked(instanceId, dueUtc);
        }
    }

    /// <summary>
    /// Resumes an instance now, for example after a cancellation request, without touching the
    /// time saved for it.
    /// </summary>
    internal void WakeNow(string instanceId)
    {
        lock (_lock)
        {
            if (!_accepting)
                return;
        }
        DispatchResume(instanceId);
    }

    // A resume starts on the thread pool with no caller context: whoever asked, a run, an activity
    // or a timer firing on the caller's thread, it is never taken for a call from inside that run.
    private void DispatchResume(string instanceId) =>
        ThreadPool.UnsafeQueueUserWorkItem(state => _ = ResumeAsync(state), instanceId, preferLocal: false);

    // Caller holds _lock.
    private void SetDueLocked(string instanceId, DateTime dueUtc)
    {
        if (_dueOf.Remove(instanceId, out var previous))
            _due.Remove((previous, instanceId));
        _dueOf[instanceId] = dueUtc;
        _due.Add((dueUtc, instanceId));
        ArmLocked();
    }

    /// <summary>Forgets an instance that has finished; nothing will be due for it.</summary>
    internal void Unschedule(string instanceId)
    {
        lock (_lock)
        {
            _resumeRetries.Remove(instanceId);
            if (_dueOf.Remove(instanceId, out var previous))
            {
                _due.Remove((previous, instanceId));
                ArmLocked();
            }
        }
    }

    // Caller holds _lock.
    private void ArmLocked()
    {
        if (_timer == null)
            return;
        if (_due.Count == 0)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _armedFor = null;
            return;
        }
        var next = _due.Min.Due;
        if (_armedFor == next)
            return;
        _armedFor = next;
        var delay = next - _time.GetUtcNow().UtcDateTime;
        // A timer cannot wait arbitrarily long; a far time is re-armed when the timer fires.
        delay = delay < TimeSpan.Zero ? TimeSpan.Zero
            : delay > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : delay;
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void ResumeDue()
    {
        var due = new List<string>();
        lock (_lock)
        {
            _armedFor = null;
            var now = _time.GetUtcNow().UtcDateTime;
            while (_due.Count > 0 && _due.Min.Due <= now)
            {
                var entry = _due.Min;
                _due.Remove(entry);
                _dueOf.Remove(entry.InstanceId);
                due.Add(entry.InstanceId);
            }
            if (_accepting)
                ArmLocked();
        }
        foreach (var instanceId in due)
            DispatchResume(instanceId);
    }

    // Goes through the same queue and slots as every other call.
    private async Task ResumeAsync(string instanceId)
    {
        WorkflowExecutionResult? result = null;
        try
        {
            result = await RunAsync(instanceId, (client, _, token) => client.RecoverAsync(instanceId, token),
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The host is stopping; the instance stays runnable in the store.
            return;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Could not resume workflow {InstanceId} at its runnable time", instanceId);
        }
        lock (_lock)
        {
            // An owner held it, or the resume failed: look again after a backoff, a few times. An
            // owner that finishes reschedules the instance itself; after that the poll covers it.
            if ((result == null || result.Status == WorkflowExecutionStatus.Running) &&
                _resumeRetries.GetValueOrDefault(instanceId) < MaxResumeRetries)
            {
                var attempt = _resumeRetries.GetValueOrDefault(instanceId);
                _resumeRetries[instanceId] = attempt + 1;
                var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                // A time saved since this resume was dispatched is newer; it stands.
                if (_accepting && !_dueOf.ContainsKey(instanceId))
                    SetDueLocked(instanceId, _time.GetUtcNow().UtcDateTime + backoff);
            }
            else
                _resumeRetries.Remove(instanceId);
        }
    }

    /// <summary>True inside a slot, where client calls run inline.</summary>
    internal bool IsInsideRun => CurrentRun.Value?.IsOpen == true;

    /// <summary>
    /// Runs an operation inline when called from inside a run that is still open, and returns false
    /// otherwise. The run's scope stays alive until the operation ends, even if the run ends first.
    /// </summary>
    internal bool TryRunInline<T>(Func<SlotClient, RunControl, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, [NotNullWhen(true)] out Task<T>? run)
    {
        if (CurrentRun.Value is { } current && current.TryEnter())
        {
            run = RunInlineAsync(current, operation, cancellationToken);
            return true;
        }
        run = null;
        return false;
    }

    // An inline call stops with its caller or with the host, whichever comes first.
    private async Task<T> RunInlineAsync<T>(RunContext current,
        Func<SlotClient, RunControl, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopped.Token);
            return await operation(current.Client, current.Control, stop.Token);
        }
        finally
        {
            await current.ExitAsync();
        }
    }

    /// <summary>
    /// Watches for the next work item on <paramref name="instanceId"/> to finish on this host.
    /// Dispose the watch when done waiting.
    /// </summary>
    internal IDisposable WatchSettled(string instanceId, out Task settled)
    {
        lock (_lock)
        {
            if (!_settled.TryGetValue(instanceId, out var signal))
                _settled[instanceId] = signal = new SettledSignal();
            signal.Watchers++;
            settled = signal.Source.Task;
            return new SettledWatch(this, instanceId, signal);
        }
    }

    private void StopWatching(string instanceId, SettledSignal signal)
    {
        lock (_lock)
        {
            if (--signal.Watchers == 0 && _settled.TryGetValue(instanceId, out var current) && current == signal)
                _settled.Remove(instanceId);
        }
    }

    /// <summary>
    /// Runs an operation in a slot and its own scope. Operations for the same instance run one at a
    /// time, in order. The caller's token cancels the wait and the run itself. Called from inside a
    /// slot, the operation runs inline in that slot and scope.
    /// </summary>
    internal Task<T> RunAsync<T>(string? instanceId,
        Func<SlotClient, RunControl, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        // A run's context stays with activities that outlive it; once it is closed their calls queue.
        if (TryRunInline(operation, cancellationToken, out var inline))
            return inline;

        var item = new WorkItem<T>(instanceId, operation, cancellationToken);
        var start = false;
        lock (_lock)
        {
            if (!_accepting)
                return Task.FromCanceled<T>(new CancellationToken(true));
            if (instanceId != null && _instances.TryGetValue(instanceId, out var queue))
                queue.Enqueue(item);
            else
            {
                if (instanceId != null)
                    _instances[instanceId] = new Queue<WorkItem>();
                _admitted.Add(item);
                start = true;
            }
        }
        item.WatchCaller();
        if (start)
            Dispatch(item);
        return item.Completion.Task;
    }

    /// <summary>Runs an operation that needs no slot, such as a read, in its own scope.</summary>
    internal async Task<T> ReadAsync<T>(Func<SlotClient, Task<T>> operation)
    {
        if (TryRunInline((client, _, _) => operation(client), CancellationToken.None, out var inline))
            return await inline;
        await using var scope = _scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<SlotClient>());
    }

    // Runs the item on the thread pool, so no caller thread or lock holder executes instance code.
    private void Dispatch(WorkItem item) => _ = Task.Run(() => ExecuteAsync(item));

    private async Task ExecuteAsync(WorkItem item)
    {
        var slotTaken = false;
        try
        {
            if (item.IsSettled)
                return;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(item.CallerToken, _stopped.Token);
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, _draining.Token))
                await _slots.WaitAsync(waiting.Token);
            slotTaken = true;
            item.MarkStarted();
            var scope = _scopes.CreateAsyncScope();
            RunContext? context = null;
            try
            {
                var control = scope.ServiceProvider.GetRequiredService<RunControl>();
                control.Drain = _draining.Token;
                control.OnInstanceKnown(instanceId => Register(item, instanceId));
                control.OnScheduled(Schedule);
                control.OnUnscheduled(Unschedule);
                control.OnRunnable(WakeNow);
                var client = scope.ServiceProvider.GetRequiredService<SlotClient>();
                context = new RunContext(client, control);
                CurrentRun.Value = context;
                await item.RunAsync(client, control, stop.Token);
            }
            finally
            {
                CurrentRun.Value = null;
                if (context == null)
                    await scope.DisposeAsync();
                else
                {
                    // Inline calls still running in the scope, from work the run left behind, keep the
                    // scope and the slot until the last of them ends; a stopping host waits for them.
                    var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_lock)
                        _lingering.Add(released.Task);
                    slotTaken = false;
                    await context.CloseAsync(async () =>
                    {
                        try
                        {
                            await scope.DisposeAsync();
                        }
                        finally
                        {
                            _slots.Release();
                            lock (_lock)
                                _lingering.Remove(released.Task);
                            released.TrySetResult();
                        }
                    });
                }
            }
        }
        catch (Exception error)
        {
            item.Fail(error);
        }
        finally
        {
            if (slotTaken)
                _slots.Release();
            item.Complete();
            Finish(item);
        }
    }

    // A start without an explicit ID learns its ID when it is saved; later work for that instance
    // queues behind it.
    private void Register(WorkItem item, string instanceId)
    {
        lock (_lock)
        {
            if (item.InstanceId == null && !_instances.ContainsKey(instanceId))
            {
                _instances[instanceId] = new Queue<WorkItem>();
                item.InstanceId = instanceId;
            }
        }
    }

    internal void NotifyCompletion(string instanceId)
    {
        SettledSignal? signal;
        lock (_lock)
            _settled.Remove(instanceId, out signal);
        signal?.Source.TrySetResult();
    }

    private void Finish(WorkItem item)
    {
        WorkItem? next = null;
        SettledSignal? settled = null;
        lock (_lock)
        {
            _admitted.Remove(item);
            if (item.InstanceId != null && _instances.TryGetValue(item.InstanceId, out var queue))
            {
                // Items whose callers gave up are skipped.
                while (queue.Count > 0 && next == null)
                {
                    var candidate = queue.Dequeue();
                    if (!candidate.IsSettled)
                        next = candidate;
                }
                if (next == null)
                    _instances.Remove(item.InstanceId);
                else
                    _admitted.Add(next);
            }
            if (item.InstanceId != null && _settled.Remove(item.InstanceId, out var signal))
                settled = signal;
        }
        item.Done.TrySetResult();
        settled?.Source.TrySetResult();
        if (next != null)
            Dispatch(next);
    }

    /// <summary>Does nothing: the host takes work as soon as it is created.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stops accepting work, cancels work that has not started, lets running instances reach their
    /// next commit, and cancels whatever is still running after the drain timeout.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var running = StopAccepting();
        _draining.Cancel();
        try
        {
            await Task.WhenAll(running).WaitAsync(_settings.DrainTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        _stopped.Cancel();
        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // The executor stops waiting for activities that ignore cancellation and releases the
            // lease; their instances continue on recovery.
        }
    }

    /// <summary>
    /// Stops at once, cancelling running instances without letting them reach their next commit. Call
    /// <see cref="StopAsync"/> first to drain; a generic host does.
    /// </summary>
    public void Dispose()
    {
        StopAccepting();
        _draining.Cancel();
        _stopped.Cancel();
    }

    private Task[] StopAccepting()
    {
        var cancelled = new List<WorkItem>();
        Task[] running;
        lock (_lock)
        {
            _accepting = false;
            foreach (var queue in _instances.Values)
                while (queue.Count > 0)
                    cancelled.Add(queue.Dequeue());
            running = _admitted.Select(item => item.Done.Task).Concat(_lingering).ToArray();
            // Due instances stay runnable in the store for the next host.
            _due.Clear();
            _dueOf.Clear();
            _resumeRetries.Clear();
            _timer?.Dispose();
            _timer = null;
        }
        foreach (var item in cancelled)
            item.Cancel();
        return running;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The run a call is inside of. Calls enter it while it is open and keep its scope alive; once it
    /// is closed, new calls queue like any other.
    /// </summary>
    private sealed class RunContext(SlotClient client, RunControl control)
    {
        private readonly object _lock = new();
        private int _inFlight;
        private bool _closed;
        private Func<ValueTask>? _release;

        public SlotClient Client { get; } = client;
        public RunControl Control { get; } = control;

        public bool IsOpen
        {
            get
            {
                lock (_lock)
                    return !_closed;
            }
        }

        public bool TryEnter()
        {
            lock (_lock)
            {
                if (_closed)
                    return false;
                _inFlight++;
                return true;
            }
        }

        public ValueTask ExitAsync()
        {
            Func<ValueTask>? release = null;
            lock (_lock)
            {
                if (--_inFlight == 0 && _closed)
                    (release, _release) = (_release, null);
            }
            return release?.Invoke() ?? ValueTask.CompletedTask;
        }

        // Closes the run; its scope is released now, or by the last inline call still in it.
        public ValueTask CloseAsync(Func<ValueTask> release)
        {
            lock (_lock)
            {
                _closed = true;
                if (_inFlight > 0)
                {
                    _release = release;
                    return ValueTask.CompletedTask;
                }
            }
            return release();
        }
    }

    private sealed class SettledSignal
    {
        public TaskCompletionSource Source { get; } = NewSignal();
        public int Watchers { get; set; }
    }

    private sealed class SettledWatch(WorkflowHost host, string instanceId, SettledSignal signal) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                host.StopWatching(instanceId, signal);
        }
    }

    private abstract class WorkItem(string? instanceId, CancellationToken callerToken)
    {
        private CancellationTokenRegistration _callerRegistration;
        private int _started;

        // Once running, the run observes the caller's token and the caller hears the outcome after
        // the run has cleaned up and committed what it could.
        public void MarkStarted() => Interlocked.Exchange(ref _started, 1);

        protected bool Started => Volatile.Read(ref _started) == 1;

        public string? InstanceId { get; set; } = instanceId;
        public CancellationToken CallerToken { get; } = callerToken;

        /// <summary>Completes when the host has finished with the item, run or not.</summary>
        public TaskCompletionSource Done { get; } = NewSignal();
        public abstract bool IsSettled { get; }
        public abstract Task RunAsync(SlotClient client, RunControl control, CancellationToken token);
        public abstract void Fail(Exception error);
        public abstract void Cancel();
        public abstract void Complete();

        // A caller who gives up while the item waits stops waiting at once.
        public void WatchCaller()
        {
            if (CallerToken.CanBeCanceled)
                _callerRegistration = CallerToken.Register(static state => ((WorkItem)state!).CancelWaiting(), this);
        }

        private void CancelWaiting()
        {
            if (!Started)
                Cancel();
        }

        protected void StopWatchingCaller() => _callerRegistration.Dispose();
    }

    private sealed class WorkItem<T>(string? instanceId,
        Func<SlotClient, RunControl, CancellationToken, Task<T>> operation,
        CancellationToken callerToken) : WorkItem(instanceId, callerToken)
    {
        private T? _result;
        private Exception? _error;
        private bool _hasResult;

        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool IsSettled => Completion.Task.IsCompleted;

        public override async Task RunAsync(SlotClient client, RunControl control,
            CancellationToken token)
        {
            _result = await operation(client, control, token);
            _hasResult = true;
        }

        public override void Fail(Exception error) => _error = error;

        public override void Cancel()
        {
            StopWatchingCaller();
            Completion.TrySetCanceled();
        }

        // The caller hears the outcome only after the scope is disposed and the slot released.
        public override void Complete()
        {
            StopWatchingCaller();
            if (_error is OperationCanceledException canceled)
                Completion.TrySetCanceled(canceled.CancellationToken);
            else if (_error != null)
                Completion.TrySetException(_error);
            else if (_hasResult)
                Completion.TrySetResult(_result!);
            else
                Completion.TrySetCanceled();
        }
    }
}
