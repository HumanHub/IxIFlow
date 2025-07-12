using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     Interface for managing suspended workflows
/// </summary>
public interface ISuspensionManager
{
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
    ///     Processes timeouts for suspended workflows
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of timed out workflow instance IDs</returns>
    Task<IEnumerable<string>> ProcessTimeoutsAsync(CancellationToken cancellationToken = default);

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
    private readonly IWorkflowEngine _workflowEngine;

    public SuspensionManager(
        IWorkflowStateRepository stateRepository,
        IEventCorrelator eventCorrelator,
        IWorkflowEngine workflowEngine,
        ILogger<SuspensionManager> logger)
    {
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        _eventCorrelator = eventCorrelator ?? throw new ArgumentNullException(nameof(eventCorrelator));
        _workflowEngine = workflowEngine ?? throw new ArgumentNullException(nameof(workflowEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        foreach (var workflow in matchingWorkflows)
            try
            {
                _logger.LogDebug("Resuming workflow {InstanceId} with event of type {EventType}",
                    workflow.InstanceId, typeof(TEventData).Name);

                // Resume the workflow with the event
                await _workflowEngine.ResumeWorkflowAsync(workflow.InstanceId, @event, cancellationToken);

                resumedWorkflowIds.Add(workflow.InstanceId);

                _logger.LogInformation("Successfully resumed workflow {InstanceId} with event of type {EventType}",
                    workflow.InstanceId, typeof(TEventData).Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume workflow {InstanceId} with event of type {EventType}",
                    workflow.InstanceId, typeof(TEventData).Name);
            }

        _logger.LogDebug("Resumed {Count} workflows with event of type {EventType}",
            resumedWorkflowIds.Count, typeof(TEventData).Name);

        return resumedWorkflowIds;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> ProcessTimeoutsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Processing timeouts for suspended workflows");

        // Get suspended workflows that have timed out
        var suspendedWorkflows = await _stateRepository.GetSuspendedWorkflowsReadyForResumptionAsync();
        var timedOutWorkflows = suspendedWorkflows
            .Where(w => w.SuspensionInfo?.ExpiresAt != null && w.SuspensionInfo.ExpiresAt <= DateTime.UtcNow)
            .ToList();

        _logger.LogDebug("Found {Count} timed out workflows", timedOutWorkflows.Count);

        // Create timeout events and resume each timed out workflow
        var resumedWorkflowIds = new List<string>();
        foreach (var workflow in timedOutWorkflows)
            try
            {
                _logger.LogDebug("Processing timeout for workflow {InstanceId}", workflow.InstanceId);

                // Create a timeout event
                var timeoutEvent = new WorkflowTimeoutEvent
                {
                    WorkflowInstanceId = workflow.InstanceId,
                    TimeoutOccurredAt = DateTime.UtcNow,
                    SuspendedAt = workflow.SuspensionInfo?.SuspendedAt ?? DateTime.UtcNow,
                    SuspendReason = workflow.SuspensionInfo?.SuspendReason ?? "Unknown",
                    TimeoutDuration = DateTime.UtcNow - (workflow.SuspensionInfo?.SuspendedAt ?? DateTime.UtcNow)
                };

                // Resume the workflow with the timeout event
                await _workflowEngine.ResumeWorkflowAsync(workflow.InstanceId, timeoutEvent, cancellationToken);

                resumedWorkflowIds.Add(workflow.InstanceId);

                _logger.LogInformation("Successfully resumed timed out workflow {InstanceId}", workflow.InstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume timed out workflow {InstanceId}", workflow.InstanceId);
            }

        _logger.LogDebug("Resumed {Count} timed out workflows", resumedWorkflowIds.Count);

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

/// <summary>
///     Event that represents a workflow timeout
/// </summary>
public class WorkflowTimeoutEvent
{
    /// <summary>
    ///     The workflow instance ID
    /// </summary>
    public string WorkflowInstanceId { get; set; } = "";

    /// <summary>
    ///     When the timeout occurred
    /// </summary>
    public DateTime TimeoutOccurredAt { get; set; }

    /// <summary>
    ///     When the workflow was suspended
    /// </summary>
    public DateTime SuspendedAt { get; set; }

    /// <summary>
    ///     Reason for suspension
    /// </summary>
    public string SuspendReason { get; set; } = "";

    /// <summary>
    ///     Duration of the timeout
    /// </summary>
    public TimeSpan TimeoutDuration { get; set; }
}

/// <summary>
///     Interface for the workflow engine
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    ///     Executes a workflow from start to completion
    /// </summary>
    Task<WorkflowExecutionResult> ExecuteWorkflowAsync<TWorkflowData>(
        WorkflowDefinition definition,
        TWorkflowData workflowData,
        WorkflowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWorkflowData : class;

    /// <summary>
    ///     Resumes a suspended workflow with an event
    /// </summary>
    /// <typeparam name="TEventData">The type of event</typeparam>
    /// <typeparam name="TWorkflowData"></typeparam>
    /// <param name="instanceId">The workflow instance ID</param>
    /// <param name="event">The event that triggered the resumption</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the workflow execution</returns>
    Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(
        string instanceId,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class;
}