using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The in-process host (B-01 to B-03): work from the application and from recovery runs in at
/// most <see cref="HostSettings.Slots"/> slots, each in its own scope. Work for one instance waits
/// in that instance's queue without holding a slot. A call made from inside a slot, for example by
/// an activity, runs inline in that slot instead of waiting for another. On stop the host drains:
/// it accepts nothing new, cancels queued work, lets running instances reach their next commit,
/// and cancels them after the drain timeout.
/// Named LocalWorkflowHost until C-19 frees the WorkflowHost name (D-16).
/// </summary>
internal sealed class LocalWorkflowHost : IHostedService, IDisposable
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
    // Callers waiting for work on an instance to finish, by instance.
    private readonly Dictionary<string, SettledSignal> _settled = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _draining = new();
    private readonly CancellationTokenSource _stopped = new();
    private bool _accepting = true;

    public LocalWorkflowHost(IServiceScopeFactory scopes, HostSettings settings)
    {
        _scopes = scopes;
        _settings = settings;
        _slots = new SemaphoreSlim(settings.Slots, settings.Slots);
    }

    /// <summary>True inside a slot, where client calls run inline.</summary>
    public bool IsInsideRun => CurrentRun.Value is { IsOpen: true };

    /// <summary>
    /// Watches for the next work item on <paramref name="instanceId"/> to finish on this host.
    /// Dispose the watch when done waiting.
    /// </summary>
    public IDisposable WatchSettled(string instanceId, out Task settled)
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
    public Task<T> RunAsync<T>(string? instanceId,
        Func<DirectInstanceClient, RunControl, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        // A run's context stays with activities that outlive it; once it is closed their calls queue.
        if (CurrentRun.Value is { IsOpen: true } current)
            return operation(current.Client, current.Control, cancellationToken);

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
    public async Task<T> ReadAsync<T>(Func<DirectInstanceClient, Task<T>> operation)
    {
        if (CurrentRun.Value is { IsOpen: true } current)
            return await operation(current.Client);
        await using var scope = _scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<DirectInstanceClient>());
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
            await using var scope = _scopes.CreateAsyncScope();
            var control = scope.ServiceProvider.GetRequiredService<RunControl>();
            control.Drain = _draining.Token;
            control.OnInstanceKnown(instanceId => Register(item, instanceId));
            var client = scope.ServiceProvider.GetRequiredService<DirectInstanceClient>();
            var context = new RunContext(client, control);
            CurrentRun.Value = context;
            try
            {
                await item.RunAsync(client, control, stop.Token);
            }
            finally
            {
                context.Close();
                CurrentRun.Value = null;
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
            running = _admitted.Select(item => item.Done.Task).ToArray();
        }
        foreach (var item in cancelled)
            item.Cancel();
        return running;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RunContext(DirectInstanceClient client, RunControl control)
    {
        private int _closed;

        public DirectInstanceClient Client { get; } = client;
        public RunControl Control { get; } = control;
        public bool IsOpen => Volatile.Read(ref _closed) == 0;

        public void Close() => Interlocked.Exchange(ref _closed, 1);
    }

    private sealed class SettledSignal
    {
        public TaskCompletionSource Source { get; } = NewSignal();
        public int Watchers { get; set; }
    }

    private sealed class SettledWatch(LocalWorkflowHost host, string instanceId, SettledSignal signal) : IDisposable
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
        public abstract Task RunAsync(DirectInstanceClient client, RunControl control, CancellationToken token);
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
        Func<DirectInstanceClient, RunControl, CancellationToken, Task<T>> operation,
        CancellationToken callerToken) : WorkItem(instanceId, callerToken)
    {
        private T? _result;
        private Exception? _error;
        private bool _hasResult;

        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool IsSettled => Completion.Task.IsCompleted;

        public override async Task RunAsync(DirectInstanceClient client, RunControl control,
            CancellationToken token)
        {
            _result = await operation(client, control, token);
            _hasResult = true;
        }

        public override void Fail(Exception error) => _error = error;

        public override void Cancel() => Completion.TrySetCanceled();

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

/// <summary>Signals between the local host and the runs in one work item's scope.</summary>
internal sealed class RunControl
{
    private Action<string>? _accepted;
    private Action<string>? _instanceKnown;

    /// <summary>Cancelled when the host drains; runs stop starting new steps.</summary>
    public CancellationToken Drain { get; set; }

    /// <summary>Called once, with the instance ID, when a start's initial checkpoint is committed.</summary>
    public void OnAccepted(Action<string> accepted) => _accepted = accepted;

    public void OnInstanceKnown(Action<string> instanceKnown) => _instanceKnown = instanceKnown;

    // The first start in a scope is the work item's own; children start later in the same scope.
    public void Accept(string instanceId)
    {
        Interlocked.Exchange(ref _instanceKnown, null)?.Invoke(instanceId);
        Interlocked.Exchange(ref _accepted, null)?.Invoke(instanceId);
    }
}

/// <summary>How many instances the host runs at once and how long it drains on stop.</summary>
internal sealed record HostSettings(int Slots, TimeSpan DrainTimeout)
{
    public const int DefaultSlots = 64;

    public static TimeSpan DefaultDrainTimeout { get; } = TimeSpan.FromSeconds(20);

    public static HostSettings Default { get; } = new(DefaultSlots, DefaultDrainTimeout);
}
