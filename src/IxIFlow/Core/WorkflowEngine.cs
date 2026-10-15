using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>
/// Runs workflow definitions and resumes saved continuations through one execution model.
/// </summary>
internal sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly InstanceHost _runner;

    public WorkflowEngine(InstanceHost runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<WorkflowExecutionResult> ExecuteWorkflowAsync<TWorkflowData>(
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

    public Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(
        string instanceId,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class =>
        ForInstanceAsync(instanceId, _runner.ResumeAsync(instanceId, null, @event, cancellationToken));

    public Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(
        string instanceId,
        string key,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return ForInstanceAsync(instanceId, _runner.ResumeAsync(instanceId, key, @event, cancellationToken));
    }

    public Task<WorkflowExecutionResult> ResumeWorkflowDeliveryAsync<TEventData>(
        string instanceId, string? key, TEventData @event, string deliveryId,
        CancellationToken cancellationToken = default)
        where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        return ForInstanceAsync(instanceId,
            _runner.ResumeAsync(instanceId, key, @event, cancellationToken, deliveryId));
    }

    public Task<WorkflowExecutionResult> RecoverWorkflowAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        ForInstanceAsync(instanceId, _runner.RecoverAsync(instanceId, cancellationToken));

    public Task<WorkflowExecutionResult> CancelWorkflowAsync(
        string instanceId, CancellationReason reason,
        CancellationToken cancellationToken = default) =>
        ForInstanceAsync(instanceId, _runner.CancelAsync(instanceId, reason, cancellationToken));

    public Task<WorkflowExecutionResult> ResolveActivityAsync(
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
