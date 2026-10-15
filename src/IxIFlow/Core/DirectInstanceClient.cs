using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>
/// Runs instance operations inline on the instance host of the current scope. The local host
/// uses it for each work item, and a running parent uses it for its children, which run inside
/// the parent's slot until children become independent instances (D-17).
/// </summary>
internal sealed class DirectInstanceClient
{
    private readonly InstanceHost _runner;

    public DirectInstanceClient(InstanceHost runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<WorkflowExecutionResult> RunAsync<TWorkflowData>(
        WorkflowDefinition definition,
        TWorkflowData workflowData,
        WorkflowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWorkflowData : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(workflowData);
        return _runner.StartAsync(definition, workflowData, options ?? new WorkflowOptions(),
            cancellationToken);
    }

    public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class =>
        ForInstanceAsync(instanceId, _runner.ResumeAsync(instanceId, null, @event, cancellationToken));

    public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId,
        string key,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return ForInstanceAsync(instanceId, _runner.ResumeAsync(instanceId, key, @event, cancellationToken));
    }

    public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId, string? key, TEventData @event, string deliveryId,
        CancellationToken cancellationToken = default)
        where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        return ForInstanceAsync(instanceId,
            _runner.ResumeAsync(instanceId, key, @event, cancellationToken, deliveryId));
    }

    public Task<WorkflowExecutionResult> RecoverAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        ForInstanceAsync(instanceId, _runner.RecoverAsync(instanceId, cancellationToken));

    public Task<WorkflowExecutionResult> CancelAsync(
        string instanceId, CancellationReason reason,
        CancellationToken cancellationToken = default) =>
        ForInstanceAsync(instanceId, _runner.CancelAsync(instanceId, reason, cancellationToken));

    /// <summary>
    /// Saves a cancellation request. Returns the result when nothing has to run, or null when
    /// the instance is idle and must be recovered to unwind.
    /// </summary>
    public async Task<WorkflowExecutionResult?> RequestCancellationAsync(string instanceId,
        CancellationReason reason, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _runner.RequestCancellationAsync(instanceId, reason, cancellationToken);
        }
        catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
        {
            error.At(instanceId);
            throw;
        }
    }

    /// <summary>The saved state of an instance as a result, or null when it does not exist.</summary>
    public Task<WorkflowExecutionResult?> ReadAsync(string instanceId) => _runner.ReadAsync(instanceId);

    public Task<WorkflowExecutionResult> ResolveAsync(
        string instanceId, string invocationId, ActivityResolution resolution,
        CancellationToken cancellationToken = default) =>
        ForInstanceAsync(instanceId, _runner.ResolveAsync(instanceId, invocationId, resolution, cancellationToken));

    public async Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId)
    {
        try
        {
            return await _runner.GetPendingActivitiesAsync(instanceId);
        }
        catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
        {
            error.At(instanceId);
            throw;
        }
    }

    // An engine failure names the instance it belongs to. A commit that lost a race means
    // another owner is running the instance.
    private static async Task<WorkflowExecutionResult> ForInstanceAsync(string instanceId,
        Task<WorkflowExecutionResult> operation)
    {
        try
        {
            return await operation;
        }
        catch (ExecutionOwnershipLostException)
        {
            return WorkflowExecutor.Busy(instanceId);
        }
        catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
        {
            error.At(instanceId);
            throw;
        }
    }
}
