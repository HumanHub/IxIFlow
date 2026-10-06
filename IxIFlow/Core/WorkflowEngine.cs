using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>
/// Runs workflow definitions and resumes saved continuations through one execution model.
/// </summary>
public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly StructuredWorkflowRunner _runner;

    public WorkflowEngine(
        IServiceProvider services,
        IActivityExecutor activityExecutor,
        IWorkflowInvoker workflowInvoker,
        IWorkflowStateRepository repository,
        IWorkflowVersionRegistry registry)
    {
        _runner = services.GetService(typeof(StructuredWorkflowRunner)) as StructuredWorkflowRunner
            ?? new StructuredWorkflowRunner(services, activityExecutor, workflowInvoker,
                repository, registry, InProcessInstanceGate.Shared);
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
        _runner.ResumeAsync(instanceId, null, @event, cancellationToken);

    public Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(
        string instanceId,
        string key,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _runner.ResumeAsync(instanceId, key, @event, cancellationToken);
    }

    public Task<WorkflowExecutionResult> ResumeWorkflowDeliveryAsync<TEventData>(
        string instanceId, string? key, TEventData @event, string deliveryId,
        CancellationToken cancellationToken = default)
        where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        return _runner.ResumeAsync(instanceId, key, @event, cancellationToken, deliveryId);
    }

    public Task<WorkflowExecutionResult> RecoverWorkflowAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        _runner.RecoverAsync(instanceId, cancellationToken);

    public Task<WorkflowExecutionResult> CancelWorkflowAsync(
        string instanceId, CancellationReason reason,
        CancellationToken cancellationToken = default) =>
        _runner.CancelAsync(instanceId, reason, cancellationToken);

    public Task<WorkflowExecutionResult> ResolveActivityAsync(
        string instanceId, string invocationId, ActivityResolution resolution,
        CancellationToken cancellationToken = default) =>
        _runner.ResolveAsync(instanceId, invocationId, resolution, cancellationToken);

    public Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId) =>
        _runner.GetPendingActivitiesAsync(instanceId);
}
