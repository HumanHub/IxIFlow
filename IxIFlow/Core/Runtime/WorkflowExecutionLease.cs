namespace IxIFlow.Core.Runtime;

/// <summary>Owns and renews one persisted workflow execution.</summary>
internal sealed class WorkflowExecutionLease : IAsyncDisposable
{
    private readonly IWorkflowStateRepository _repository;
    private readonly string _instanceId;
    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<Exception> _loss =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _renewal;

    private WorkflowExecutionLease(IWorkflowStateRepository repository, string instanceId,
        string token, ExecutionLeaseSettings settings)
    {
        _repository = repository;
        _instanceId = instanceId;
        _token = token;
        _renewal = RenewUntilStoppedAsync(settings);
    }

    public Task Loss => _loss.Task;

    public static async Task<WorkflowExecutionLease?> TryAcquireAsync(
        IWorkflowStateRepository repository, WorkflowInstance instance,
        ExecutionLeaseSettings settings)
    {
        if (settings.Duration <= TimeSpan.Zero || settings.RenewalInterval <= TimeSpan.Zero ||
            settings.RenewalInterval >= settings.Duration)
            throw new ArgumentOutOfRangeException(nameof(settings),
                "The renewal interval must be positive and shorter than the lease duration");

        var token = Guid.NewGuid().ToString("N");
        if (!await repository.TryAcquireExecutionLeaseAsync(instance.InstanceId, token,
                settings.Duration))
            return null;
        instance.ExecutionLeaseToken = token;
        return new WorkflowExecutionLease(repository, instance.InstanceId, token, settings);
    }

    public void ThrowIfLost()
    {
        if (_loss.Task.IsCompleted)
            throw new ExecutionLeaseLostException(_loss.Task.Result);
    }

    private async Task RenewUntilStoppedAsync(ExecutionLeaseSettings settings)
    {
        try
        {
            while (true)
            {
                await Task.Delay(settings.RenewalInterval, _stop.Token);
                if (!await _repository.RenewExecutionLeaseAsync(_instanceId, _token,
                        settings.Duration))
                {
                    _loss.TrySetResult(new InvalidOperationException(
                        $"Execution lease for workflow instance '{_instanceId}' was lost"));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _loss.TrySetResult(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _renewal;
        _stop.Dispose();
        try
        {
            await _repository.ReleaseExecutionLeaseAsync(_instanceId, _token);
        }
        catch
        {
            // A lease release is best effort. Committed terminal and idle states
            // release it atomically; an interrupted run can be reclaimed at expiry.
        }
    }
}

internal sealed class ExecutionLeaseLostException(Exception cause) : Exception(
    "Workflow execution ownership was lost", cause);
