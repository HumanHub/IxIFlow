using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Core.Runtime;

/// <summary>One timer and one renewal batch for all execution leases held by a host.</summary>
internal sealed class ExecutionLeaseRenewer : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private readonly IWorkflowStateRepository _store;
    private readonly TimeProvider _time;
    private readonly ILogger<ExecutionLeaseRenewer> _logger;
    private readonly ILogger<SensitiveDiagnostics>? _sensitiveDiagnostics;
    private readonly InspectionRedactionPolicy _inspectionPolicy;
    private readonly object _lock = new();
    private readonly HashSet<Registration> _leases = [];
    private readonly HashSet<Task> _batches = [];
    private readonly ITimer _timer;
    private bool _disposed;

    public ExecutionLeaseRenewer(IWorkflowStateRepository store, TimeProvider? time = null,
        ILogger<ExecutionLeaseRenewer>? logger = null, ILogger<SensitiveDiagnostics>? sensitiveDiagnostics = null,
        InspectionRedactionPolicy? inspectionPolicy = null)
    {
        _store = GuardedStateRepository.Wrap(store);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ExecutionLeaseRenewer>.Instance;
        _sensitiveDiagnostics = sensitiveDiagnostics;
        _inspectionPolicy = inspectionPolicy ?? InspectionRedactionPolicy.Default;
        _timer = _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal Registration Register(ExecutionLeaseRenewal request, DateTime dueAtUtc)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var registration = new Registration(this, request, dueAtUtc);
            _leases.Add(registration);
            Arm();
            return registration;
        }
    }

    // Called under _lock. New leases can bring the next pass forward; a lease acquired between
    // passes joins the next batch even when its own first renewal is not due yet.
    private void Arm()
    {
        var due = _leases.Where(lease => !lease.InFlight).Select(lease => lease.DueAtUtc)
            .DefaultIfEmpty(DateTime.MaxValue).Min();
        var delay = due - _time.GetUtcNow().UtcDateTime;
        _timer.Change(due == DateTime.MaxValue ? Timeout.InfiniteTimeSpan :
            delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
    }

    private void Tick()
    {
        Registration[] batch;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_disposed) return;
            var now = _time.GetUtcNow().UtcDateTime;
            if (!_leases.Any(lease => !lease.InFlight && lease.DueAtUtc <= now))
            {
                Arm();
                return;
            }
            // A stale registration and its replacement can briefly share an ID. Send the other
            // token on the next pass; duplicate IDs would fail the store's entire batch.
            batch = _leases.Where(lease => !lease.InFlight)
                .DistinctBy(lease => lease.Request.InstanceId, StringComparer.Ordinal).ToArray();
            foreach (var lease in batch) lease.InFlight = true;
            _batches.Add(completed.Task);
            Arm();
        }
        // A hung call never blocks the timer or another lease's expiry clock. Its registrations
        // stay in flight, so the host never queues another renewal for the same hung leases.
        _ = RenewAsync(batch, completed);
    }

    private async Task RenewAsync(Registration[] batch, TaskCompletionSource completed)
    {
        var sentAtUtc = _time.GetUtcNow().UtcDateTime;
        try
        {
            var results = await _store.RenewExecutionLeasesAsync(batch.Select(lease => lease.Request).ToArray());
            if (results.Count != batch.Length)
                throw new InvalidOperationException("A renewal batch must return one result per lease");
            for (var index = 0; index < batch.Length; index++)
                batch[index].Reply.TrySetResult(new Attempt(sentAtUtc, results[index]));
        }
        catch (Exception error)
        {
            _inspectionPolicy.Log(_logger, _sensitiveDiagnostics, LogLevel.Warning, error,
                "Could not renew execution leases for {Count} workflow instances", batch.Length);
            foreach (var lease in batch) lease.Reply.TrySetException(error);
        }
        finally
        {
            completed.TrySetResult();
            lock (_lock) _batches.Remove(completed.Task);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            foreach (var lease in _leases) lease.Reply.TrySetCanceled();
            _leases.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] batches;
        lock (_lock) batches = _batches.ToArray();
        try
        {
            // Shutdown must also finish when an injected workflow clock is no longer advancing.
            await Task.WhenAll(batches).WaitAsync(DrainTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Abandoned {Count} outstanding execution lease renewal batches after {Timeout}",
                batches.Count(batch => !batch.IsCompleted), DrainTimeout);
        }
    }

    internal sealed record Attempt(DateTime SentAtUtc, ExecutionLeaseRenewalResult Result);

    internal sealed class Registration(ExecutionLeaseRenewer owner, ExecutionLeaseRenewal request,
        DateTime dueAtUtc) : IDisposable
    {
        internal ExecutionLeaseRenewal Request { get; } = request;
        internal DateTime DueAtUtc = dueAtUtc;
        internal bool InFlight;
        internal TaskCompletionSource<Attempt> Reply = NewReply();
        private static TaskCompletionSource<Attempt> NewReply() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Attempt> Next => Reply.Task;

        public void Schedule(DateTime due)
        {
            lock (owner._lock)
            {
                if (!owner._leases.Contains(this)) return;
                Reply = NewReply();
                DueAtUtc = due;
                InFlight = false;
                owner.Arm();
            }
        }

        public void Dispose()
        {
            lock (owner._lock)
            {
                owner._leases.Remove(this);
                // Observe a late failure of an abandoned store call as well as a completed one.
                _ = Reply.Task.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                if (!owner._disposed) owner.Arm();
            }
        }
    }
}
