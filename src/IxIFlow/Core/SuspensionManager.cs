using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     Interface for managing suspended workflows
/// </summary>
public interface ISuspensionManager
{
    /// <summary>Resumes one suspended workflow with an event.</summary>
    Task ResumeInstanceAsync<TEventData>(
        string instanceId,
        TEventData @event,
        CancellationToken cancellationToken = default) where TEventData : class;

    /// <summary>
    ///     Processes an event and resumes matching workflows
    /// </summary>
    /// <typeparam name="TEventData">The type of event</typeparam>
    /// <param name="event">The event to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of resumed workflow instances</returns>
    Task<IEnumerable<string>> ProcessEventAsync<TEventData>(
        TEventData @event,
        CancellationToken cancellationToken = default) where TEventData : class;

    /// <summary>
    ///     Gets all suspended workflows
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of suspended workflow instances</returns>
    Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a suspended workflow by ID
    /// </summary>
    /// <param name="instanceId">The workflow instance ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The suspended workflow instance, or null if not found</returns>
    Task<WorkflowInstance?> GetSuspendedWorkflowAsync(string instanceId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Implementation of ISuspensionManager that manages suspended workflows and timeouts
/// </summary>
public class SuspensionManager : ISuspensionManager
{
    private readonly IEventCorrelator _eventCorrelator;
    private readonly ILogger<SuspensionManager> _logger;
    private readonly IWorkflowStateRepository _stateRepository;
    private readonly IWorkflowClient _workflowEngine;

    public SuspensionManager(
        IWorkflowStateRepository stateRepository,
        IEventCorrelator eventCorrelator,
        IWorkflowClient workflowEngine,
        ILogger<SuspensionManager> logger)
    {
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        _eventCorrelator = eventCorrelator ?? throw new ArgumentNullException(nameof(eventCorrelator));
        _workflowEngine = workflowEngine ?? throw new ArgumentNullException(nameof(workflowEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ResumeInstanceAsync<TEventData>(
        string instanceId,
        TEventData @event,
        CancellationToken cancellationToken = default) where TEventData : class
    {
        var result = await _workflowEngine.ResumeAsync(instanceId, @event, cancellationToken);
        if (!result.EventAccepted)
            throw new InvalidOperationException(result.ErrorMessage ??
                $"Workflow {instanceId} did not accept the event; retry after it becomes idle");
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> ProcessEventAsync<TEventData>(
        TEventData @event,
        CancellationToken cancellationToken = default)
    where TEventData : class
    {
        _logger.LogDebug("Processing event of type {EventType}", typeof(TEventData).Name);

        // Find workflows that match the event
        var matchingWorkflows = await _eventCorrelator.FindMatchingWorkflowsAsync(@event, cancellationToken);

        // Resume each matching workflow
        var resumedWorkflowIds = new List<string>();
        var pendingWorkflowIds = new List<string>();
        foreach (var workflow in matchingWorkflows)
            try
            {
                _logger.LogDebug("Resuming workflow {InstanceId} with event of type {EventType}",
                    workflow.InstanceId, typeof(TEventData).Name);

                // Resume the workflow with the event
                var result = await _workflowEngine.ResumeAsync(workflow.InstanceId, @event, cancellationToken);
                if (!result.EventAccepted)
                {
                    pendingWorkflowIds.Add(workflow.InstanceId);
                    continue;
                }

                resumedWorkflowIds.Add(workflow.InstanceId);

                _logger.LogInformation("Successfully resumed workflow {InstanceId} with event of type {EventType}",
                    workflow.InstanceId, typeof(TEventData).Name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume workflow {InstanceId} with event of type {EventType}",
                    workflow.InstanceId, typeof(TEventData).Name);
                pendingWorkflowIds.Add(workflow.InstanceId);
            }

        if (pendingWorkflowIds.Count > 0)
            throw new WorkflowEventDeliveryException(resumedWorkflowIds, pendingWorkflowIds);

        _logger.LogDebug("Resumed {Count} workflows with event of type {EventType}",
            resumedWorkflowIds.Count, typeof(TEventData).Name);

        return resumedWorkflowIds;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting all suspended workflows");

        var suspendedWorkflows = await _stateRepository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Suspended);

        _logger.LogDebug("Found {Count} suspended workflows", suspendedWorkflows.Count());

        return suspendedWorkflows;
    }

    /// <inheritdoc />
    public async Task<WorkflowInstance?> GetSuspendedWorkflowAsync(string instanceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting suspended workflow {InstanceId}", instanceId);

        var workflow = await _stateRepository.GetWorkflowInstanceAsync(instanceId);

        if (workflow == null)
        {
            _logger.LogWarning("Workflow {InstanceId} not found", instanceId);
            return null;
        }

        if (workflow.Status != WorkflowStatus.Suspended)
        {
            _logger.LogWarning("Workflow {InstanceId} is not suspended (Status: {Status})",
                instanceId, workflow.Status);
            return null;
        }

        return workflow;
    }
}

