namespace IxIFlow.Core.Runtime;

/// <summary>
/// One cancellation watch for every instance this host runs (B-10): it reads the cancellation
/// requests of all watched instances in one store call per interval and tells each lease whose
/// instance has a request. Instances do not poll for cancellation individually.
/// </summary>
internal sealed class CancellationWatch
{
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromMilliseconds(250);

    private readonly IWorkflowStateRepository _store;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Subscriber>> _watched = new(StringComparer.Ordinal);
    private Task? _loop;

    public CancellationWatch(IWorkflowStateRepository store, TimeProvider? time = null)
    {
        _store = GuardedStateRepository.Wrap(store);
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Watches an instance until the returned registration is disposed. The watch completes
    /// <paramref name="cancellation"/> with the request, or reports <paramref name="lost"/> when the
    /// store cannot be read for longer than <paramref name="tolerance"/>. It reads at least every
    /// <paramref name="interval"/>, so one failed read never outlasts a lease renewal.
    /// </summary>
    public IDisposable Watch(string instanceId, TaskCompletionSource<CancellationReason> cancellation,
        Action<Exception> lost, TimeSpan tolerance, TimeSpan interval)
    {
        var subscriber = new Subscriber(this, instanceId, cancellation, lost, tolerance,
            interval < MaximumInterval ? interval : MaximumInterval);
        lock (_lock)
        {
            if (!_watched.TryGetValue(instanceId, out var subscribers))
                _watched[instanceId] = subscribers = [];
            subscribers.Add(subscriber);
            if (_loop == null || _loop.IsCompleted)
                _loop = Task.Run(RunAsync);
        }
        return subscriber;
    }

    private void Remove(Subscriber subscriber)
    {
        lock (_lock)
        {
            if (_watched.TryGetValue(subscriber.InstanceId, out var subscribers) &&
                subscribers.Remove(subscriber) && subscribers.Count == 0)
                _watched.Remove(subscriber.InstanceId);
        }
    }

    // The loop ends when nothing is watched; the next registration starts it again.
    private async Task RunAsync()
    {
        var lastSuccessfulReadUtc = Now;
        while (true)
        {
            string[] instanceIds;
            TimeSpan interval;
            lock (_lock)
            {
                if (_watched.Count == 0)
                {
                    _loop = null;
                    return;
                }
                instanceIds = _watched.Keys.ToArray();
                interval = _watched.Values.SelectMany(items => items).Min(item => item.Interval);
            }
            try
            {
                var requests = await _store.GetCancellationRequestsAsync(instanceIds);
                lastSuccessfulReadUtc = Now;
                foreach (var (instanceId, reason) in requests)
                    foreach (var subscriber in Subscribers(instanceId))
                        subscriber.Cancellation.TrySetResult(reason);
            }
            catch (Exception error)
            {
                // A persistent store outage ends ownership, as the lease renewal would.
                foreach (var subscriber in Subscribers(null).Where(item =>
                             Now - lastSuccessfulReadUtc >= item.Tolerance))
                    subscriber.Lost(error);
            }
            await Task.Delay(interval, _time);
        }
    }

    private Subscriber[] Subscribers(string? instanceId)
    {
        lock (_lock)
            return instanceId == null
                ? _watched.Values.SelectMany(items => items).ToArray()
                : _watched.TryGetValue(instanceId, out var subscribers) ? subscribers.ToArray() : [];
    }

    private sealed class Subscriber(CancellationWatch watch, string instanceId,
        TaskCompletionSource<CancellationReason> cancellation, Action<Exception> lost, TimeSpan tolerance,
        TimeSpan interval) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;
        public string InstanceId { get; } = instanceId;
        public TaskCompletionSource<CancellationReason> Cancellation { get; } = cancellation;
        public Action<Exception> Lost { get; } = lost;
        public TimeSpan Tolerance { get; } = tolerance;

        public void Dispose() => watch.Remove(this);
    }
}
