using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The in-process host (B-01 to B-03): work from the application and from recovery runs in at
/// most <see cref="HostSettings.Slots"/> slots, each in its own scope. Work for one instance waits
/// in that instance's queue without holding a slot. On stop the host drains: it accepts nothing
/// new, lets running instances reach their next commit, and cancels them after the drain timeout.
/// Named LocalWorkflowHost until C-19 frees the WorkflowHost name (D-16).
/// </summary>
internal sealed class LocalWorkflowHost : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly HostSettings _settings;
    private readonly SemaphoreSlim _slots;
    private readonly object _lock = new();
    // An instance with an entry has one item running; the queue holds the items behind it.
    private readonly Dictionary<string, Queue<WorkItem>> _instances = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _running = [];
    private readonly CancellationTokenSource _draining = new();
    private readonly CancellationTokenSource _stopped = new();
    private TaskCompletionSource _settled = NewSignal();
    private bool _accepting = true;

    public LocalWorkflowHost(IServiceScopeFactory scopes, HostSettings settings)
    {
        _scopes = scopes;
        _settings = settings;
        _slots = new SemaphoreSlim(settings.Slots, settings.Slots);
    }

    /// <summary>Completes the next time any work item finishes.</summary>
    public Task Settled
    {
        get
        {
            lock (_lock)
                return _settled.Task;
        }
    }

    /// <summary>
    /// Runs an operation in a slot and its own scope. Operations for the same instance run one
    /// at a time, in order. The caller's token cancels the wait for a slot and the run itself.
    /// </summary>
    public Task<T> RunAsync<T>(string? instanceId,
        Func<DirectInstanceClient, RunControl, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var item = new WorkItem<T>(instanceId, operation, cancellationToken);
        lock (_lock)
        {
            if (!_accepting)
                throw new OperationCanceledException("The workflow host is stopping");
            if (instanceId != null && _instances.TryGetValue(instanceId, out var queue))
            {
                queue.Enqueue(item);
                return item.Completion.Task;
            }
            if (instanceId != null)
                _instances[instanceId] = new Queue<WorkItem>();
            Start(item);
        }
        return item.Completion.Task;
    }

    /// <summary>Runs an operation that needs no slot, such as a read, in its own scope.</summary>
    public async Task<T> ReadAsync<T>(Func<DirectInstanceClient, Task<T>> operation)
    {
        using var scope = _scopes.CreateScope();
        return await operation(scope.ServiceProvider.GetRequiredService<DirectInstanceClient>());
    }

    // Caller holds _lock.
    private void Start(WorkItem item)
    {
        var task = ExecuteAsync(item);
        _running.Add(task);
        _ = task.ContinueWith(finished =>
        {
            lock (_lock)
                _running.Remove(finished);
        }, TaskScheduler.Default);
    }

    private async Task ExecuteAsync(WorkItem item)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(item.CallerToken, _stopped.Token);
        var slotTaken = false;
        try
        {
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, _draining.Token))
                await _slots.WaitAsync(waiting.Token);
            slotTaken = true;
            using var scope = _scopes.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<RunControl>();
            control.Drain = _draining.Token;
            await item.RunAsync(scope.ServiceProvider.GetRequiredService<DirectInstanceClient>(), control,
                stop.Token);
        }
        catch (Exception error)
        {
            item.Fail(error);
        }
        finally
        {
            if (slotTaken)
                _slots.Release();
            Finish(item);
        }
    }

    private void Finish(WorkItem item)
    {
        TaskCompletionSource settled;
        lock (_lock)
        {
            if (item.InstanceId != null && _instances.TryGetValue(item.InstanceId, out var queue))
            {
                if (queue.Count > 0)
                    Start(queue.Dequeue());
                else
                    _instances.Remove(item.InstanceId);
            }
            settled = _settled;
            _settled = NewSignal();
        }
        settled.TrySetResult();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stops accepting work, cancels work still waiting for a slot, lets running instances reach
    /// their next commit, and cancels whatever is still running after the drain timeout.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] running;
        lock (_lock)
        {
            _accepting = false;
            foreach (var queue in _instances.Values)
                while (queue.Count > 0)
                    queue.Dequeue().Cancel();
            running = _running.ToArray();
        }
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
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // Activities that ignore cancellation are left behind; their instances are recovered
            // elsewhere once the lease expires.
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _accepting = false;
        _draining.Cancel();
        _stopped.Cancel();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private abstract class WorkItem(string? instanceId, CancellationToken callerToken)
    {
        public string? InstanceId { get; } = instanceId;
        public CancellationToken CallerToken { get; } = callerToken;
        public abstract Task RunAsync(DirectInstanceClient client, RunControl control, CancellationToken token);
        public abstract void Fail(Exception error);
        public abstract void Cancel();
    }

    private sealed class WorkItem<T>(string? instanceId,
        Func<DirectInstanceClient, RunControl, CancellationToken, Task<T>> operation,
        CancellationToken callerToken) : WorkItem(instanceId, callerToken)
    {
        public TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task RunAsync(DirectInstanceClient client, RunControl control,
            CancellationToken token) =>
            Completion.TrySetResult(await operation(client, control, token));

        public override void Fail(Exception error)
        {
            if (error is OperationCanceledException canceled)
                Completion.TrySetCanceled(canceled.CancellationToken);
            else
                Completion.TrySetException(error);
        }

        public override void Cancel() =>
            Completion.TrySetCanceled();
    }
}

/// <summary>Signals between the local host and the runs in one work item's scope.</summary>
internal sealed class RunControl
{
    private Action<string>? _accepted;

    /// <summary>Cancelled when the host drains; runs stop starting new steps.</summary>
    public CancellationToken Drain { get; set; }

    /// <summary>Called once, with the instance ID, when a start's initial checkpoint is committed.</summary>
    public void OnAccepted(Action<string> accepted) => _accepted = accepted;

    public void Accept(string instanceId) => Interlocked.Exchange(ref _accepted, null)?.Invoke(instanceId);
}

/// <summary>How many instances the host runs at once and how long it drains on stop.</summary>
internal sealed record HostSettings(int Slots, TimeSpan DrainTimeout)
{
    public const int DefaultSlots = 64;

    public static TimeSpan DefaultDrainTimeout { get; } = TimeSpan.FromSeconds(20);

    public static HostSettings Default { get; } = new(DefaultSlots, DefaultDrainTimeout);
}
