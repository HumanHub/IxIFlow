using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// One cancellation watch for every instance this host runs (B-10): it reads the cancellation
/// requests of all watched instances in one store call per interval and tells each lease whose
/// instance has a request. Instances do not poll for cancellation individually; a run also learns
/// of a request from its own commit replies. The watch needs a singleton store.
/// </summary>
internal sealed class CancellationWatch
{
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly IWorkflowStateRepository _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<Subscriber>> _watched = new(StringComparer.Ordinal);
    private Task? _loop;
    // A read past its deadline keeps running in the store; the next pass waits for it again instead
    // of queuing another call behind it.
    private (Task<IReadOnlyDictionary<string, CancellationReason>> Task, string[] InstanceIds, DateTime SentAtUtc)?
        _read;

    public CancellationWatch(IWorkflowStateRepository store, TimeProvider? time = null,
        ILogger<CancellationWatch>? logger = null)
    {
        _store = GuardedStateRepository.Wrap(store);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CancellationWatch>.Instance;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Watches an instance until the returned registration is disposed or a request is delivered.
    /// The watch completes <paramref name="cancellation"/> with the request, or reports
    /// <paramref name="lost"/> when it cannot read the instance's requests for longer than
    /// <paramref name="tolerance"/>. It reads at least every <paramref name="interval"/>, so one
    /// failed read never outlasts a lease renewal.
    /// </summary>
    public IDisposable Watch(string instanceId, TaskCompletionSource<CancellationReason> cancellation,
        Action<Exception> lost, TimeSpan tolerance, TimeSpan interval)
    {
        var subscriber = new Subscriber(this, instanceId, cancellation, lost, tolerance,
            interval < MaximumInterval ? interval : MaximumInterval, Now);
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
        while (true)
        {
            string[] instanceIds;
            TimeSpan interval;
            lock (_lock)
            {
                if (_watched.Count == 0)
                {
                    _loop = null;
                    _read = null;
                    return;
                }
                instanceIds = _watched.Keys.ToArray();
                interval = _watched.Values.SelectMany(items => items).Min(item => item.Interval);
            }
            try
            {
                await ReadAsync(instanceIds);
            }
            catch (Exception error)
            {
                // Keep watching: a fault here must not leave leases without cancellation or loss.
                _logger.LogError(error, "The cancellation watch failed");
            }
            await Task.Delay(interval, _time);
        }
    }

    private async Task ReadAsync(string[] watchedIds)
    {
        _read ??= (_store.GetCancellationRequestsAsync(watchedIds), watchedIds, Now);
        var (read, instanceIds, readAt) = _read.Value;
        IReadOnlyDictionary<string, CancellationReason> requests;
        try
        {
            requests = await read.WaitAsync(ReadTimeout, _time);
            _read = null;
        }
        catch (Exception error)
        {
            // A stalled read is waited for again until it has been out longer than any lease can
            // wait; then a fresh read may succeed where it did not.
            if (error is not TimeoutException || Now - readAt >= LongestTolerance())
                _read = null;
            _logger.LogWarning(error, "Could not read cancellation requests for {Count} workflow instances",
                instanceIds.Length);
            // Each lease has its own outage clock, counted from its last confirmed read. Every lease
            // watched now is judged, including ones added while the read was out.
            foreach (var subscriber in Subscribers(WatchedIds()).Where(item =>
                         !item.Cancellation.Task.IsCompleted && Now - item.ConfirmedAtUtc >= item.Tolerance))
                subscriber.Lost(error);
            return;
        }
        foreach (var subscriber in Subscribers(instanceIds))
        {
            if (requests.TryGetValue(subscriber.InstanceId, out var reason))
            {
                // A delivered request ends the watch; a later outage must not end ownership.
                subscriber.Cancellation.TrySetResult(reason);
                Remove(subscriber);
            }
            else if (readAt > subscriber.ConfirmedAtUtc)
                subscriber.ConfirmedAtUtc = readAt;
        }
    }

    private string[] WatchedIds()
    {
        lock (_lock)
            return _watched.Keys.ToArray();
    }

    private TimeSpan LongestTolerance()
    {
        lock (_lock)
            return _watched.Values.SelectMany(items => items).Select(item => item.Tolerance)
                .DefaultIfEmpty(TimeSpan.Zero).Max();
    }

    private Subscriber[] Subscribers(IEnumerable<string> instanceIds)
    {
        lock (_lock)
            return instanceIds
                .SelectMany(instanceId => _watched.TryGetValue(instanceId, out var items) ? items : [])
                .ToArray();
    }

    private sealed class Subscriber(CancellationWatch watch, string instanceId,
        TaskCompletionSource<CancellationReason> cancellation, Action<Exception> lost, TimeSpan tolerance,
        TimeSpan interval, DateTime subscribedAtUtc) : IDisposable
    {
        public string InstanceId { get; } = instanceId;
        public TaskCompletionSource<CancellationReason> Cancellation { get; } = cancellation;
        public Action<Exception> Lost { get; } = lost;
        public TimeSpan Tolerance { get; } = tolerance;
        public TimeSpan Interval { get; } = interval;
        public DateTime ConfirmedAtUtc { get; set; } = subscribedAtUtc;

        public void Dispose() => watch.Remove(this);
    }
}
