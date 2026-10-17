namespace IxIFlow.Core.Runtime;

/// <summary>Owns and renews one persisted workflow execution.</summary>
internal sealed class WorkflowExecutionLease : IAsyncDisposable
{
    private readonly IWorkflowStateRepository _repository;
    private readonly string _instanceId;
    private readonly string _token;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Exception> _loss =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CancellationReason> _cancellation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _renewal;
    private readonly IDisposable _cancellationWatch;

    private WorkflowExecutionLease(IWorkflowStateRepository repository, string instanceId,
        string token, ExecutionLeaseGrant grant, DateTime requestedAtUtc, ExecutionLeaseSettings settings,
        TimeProvider time, CancellationWatch watch)
    {
        Epoch = grant.Epoch;
        CrashCount = grant.CrashCount;
        _repository = repository;
        _time = time;
        _instanceId = instanceId;
        _token = token;
        _renewal = RenewUntilStoppedAsync(settings, requestedAtUtc.Add(settings.Duration));
        _cancellationWatch = watch.Watch(instanceId, _cancellation,
            Lose, settings.Duration, settings.RenewalInterval, ObserveInbox);
    }

    private readonly object _inboxLock = new();
    private long _inboxSequence;
    private TaskCompletionSource _inboxChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The highest inbox sequence the host's watch has seen for this instance.</summary>
    public long InboxSequence
    {
        get { lock (_inboxLock) return _inboxSequence; }
    }

    /// <summary>Completes when the watch sees the inbox sequence rise; a new task follows each rise.</summary>
    public Task InboxChanged
    {
        get { lock (_inboxLock) return _inboxChanged.Task; }
    }

    private void ObserveInbox(long sequence)
    {
        TaskCompletionSource? risen = null;
        lock (_inboxLock)
        {
            if (sequence <= _inboxSequence)
                return;
            _inboxSequence = sequence;
            risen = _inboxChanged;
            _inboxChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        risen.TrySetResult();
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>The execution epoch the store granted with this lease; commits are fenced by it.</summary>
    public long Epoch { get; }

    /// <summary>How many owners in a row lost the instance while it was running, before this one (B-08).</summary>
    public int CrashCount { get; }

    public Task Loss => _loss.Task;

    // The lease is lost once; the host counts it (D-39).
    private void Lose(Exception error)
    {
        if (_loss.TrySetResult(error))
            Telemetry.LeasesLost.Add(1);
    }
    public Task<CancellationReason> Cancellation => _cancellation.Task;

    public static async Task<WorkflowExecutionLease?> TryAcquireAsync(
        IWorkflowStateRepository repository, WorkflowInstance instance,
        ExecutionLeaseSettings settings, CancellationWatch watch, TimeProvider? time = null)
    {
        if (settings.Duration <= TimeSpan.Zero || settings.RenewalInterval <= TimeSpan.Zero ||
            settings.RenewalInterval >= settings.Duration)
            throw new ArgumentOutOfRangeException(nameof(settings),
                "The renewal interval must be positive and shorter than the lease duration");

        var token = Guid.NewGuid().ToString("N");
        time ??= TimeProvider.System;
        // The store may have granted the lease at any point after it was asked.
        var requestedAtUtc = time.GetUtcNow().UtcDateTime;
        if (await repository.TryAcquireExecutionLeaseAsync(instance.InstanceId, token,
                settings.Duration) is not { } grant)
            return null;
        return new WorkflowExecutionLease(repository, instance.InstanceId, token, grant, requestedAtUtc, settings,
            time, watch);
    }

    /// <summary>Records a cancellation request learned outside the watch, such as from a commit.</summary>
    public void ObserveCancellation(CancellationReason reason)
    {
        _cancellation.TrySetResult(reason);
        // The request is known; a later outage of the watch's reads must not end this ownership.
        _cancellationWatch.Dispose();
    }

    public void ThrowIfLost()
    {
        if (_loss.Task.IsCompleted)
            throw new ExecutionOwnershipLostException(_loss.Task.Result);
    }

    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    // Loss is decided by the clock, not by when a store call returns: a renewal that has not
    // succeeded by the time the lease may have expired ends ownership (B-09). The expiry counts
    // from when the renewal was sent, since the store may have applied it at any point after.
    private async Task RenewUntilStoppedAsync(ExecutionLeaseSettings settings, DateTime expiresAtUtc)
    {
        var delay = NextRenewal(settings, expiresAtUtc);
        try
        {
            while (true)
            {
                await Task.Delay(delay, _time, _stop.Token);
                var sentAtUtc = Now;
                if (sentAtUtc >= expiresAtUtc)
                {
                    Lose(new TimeoutException(
                        $"Execution lease for workflow instance '{_instanceId}' expired before it could be renewed"));
                    return;
                }
                var renewal = _repository.RenewExecutionLeaseAsync(_instanceId, _token, settings.Duration);
                var remaining = expiresAtUtc - Now;
                try
                {
                    var renewed = await renewal.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                        _time, _stop.Token);
                    if (!renewed)
                    {
                        Lose(new InvalidOperationException(
                            $"Execution lease for workflow instance '{_instanceId}' was lost"));
                        return;
                    }
                    expiresAtUtc = sentAtUtc.Add(settings.Duration);
                    delay = NextRenewal(settings, expiresAtUtc);
                }
                catch (TimeoutException)
                {
                    Lose(new TimeoutException(
                        $"Execution lease for workflow instance '{_instanceId}' could not be renewed before it expired"));
                    return;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    if (Now >= expiresAtUtc)
                    {
                        Lose(error);
                        return;
                    }
                    // Try again soon, while the lease still holds.
                    var left = (expiresAtUtc - Now) / 4;
                    delay = left < settings.RenewalInterval ? left : settings.RenewalInterval;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Lose(error);
        }
    }

    // Normally one interval; sooner when a slow renewal left less time than that before expiry.
    private TimeSpan NextRenewal(ExecutionLeaseSettings settings, DateTime expiresAtUtc)
    {
        var half = (expiresAtUtc - Now) / 2;
        return half < settings.RenewalInterval ? (half > TimeSpan.Zero ? half : TimeSpan.Zero) : settings.RenewalInterval;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _cancellationWatch.Dispose();
        await _renewal;
        _stop.Dispose();
        try
        {
            await _repository.ReleaseExecutionLeaseAsync(_instanceId, _token).WaitAsync(ReleaseTimeout, _time);
        }
        catch
        {
            // A lease release is best effort. Committed terminal and idle states
            // release it atomically; an interrupted run can be reclaimed at expiry.
        }
    }
}
