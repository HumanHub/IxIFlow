using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     Interface for managing workflow events
/// </summary>
public interface IWorkflowEventManager
{
    /// <summary>
    ///     Gets an event template for a suspended workflow
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="workflowInstanceId">The workflow instance ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The event template</returns>
    Task<EventTemplate<TEvent>> GetEventTemplateAsync<TEvent>(
        string workflowInstanceId,
        CancellationToken cancellationToken = default) where TEvent : class;

    /// <summary>
    ///     Updates an event template and triggers workflow resumption
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="workflowInstanceId">The workflow instance ID</param>
    /// <param name="eventData">The updated event data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated event template</returns>
    Task<EventTemplate<TEvent>> UpdateEventAndResumeAsync<TEvent>(
        string workflowInstanceId,
        TEvent eventData,
        CancellationToken cancellationToken = default) where TEvent : class;

    /// <summary>
    ///     Gets all event templates for suspended workflows waiting for a specific event type
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of event templates</returns>
    Task<IEnumerable<EventTemplate<TEvent>>> GetSuspendedWorkflowEventTemplatesAsync<TEvent>(
        CancellationToken cancellationToken = default) where TEvent : class;
}

/// <summary>
///     Implementation of IWorkflowEventManager
/// </summary>
public class WorkflowEventManager : IWorkflowEventManager
{
    private readonly IEventRepository _eventRepository;
    private readonly ILogger<WorkflowEventManager> _logger;
    private readonly ISuspensionManager _suspensionManager;

    public WorkflowEventManager(
        IEventRepository eventRepository,
        ISuspensionManager suspensionManager,
        ILogger<WorkflowEventManager> logger)
    {
        _eventRepository = eventRepository ?? throw new ArgumentNullException(nameof(eventRepository));
        _suspensionManager = suspensionManager ?? throw new ArgumentNullException(nameof(suspensionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<EventTemplate<TEvent>> GetEventTemplateAsync<TEvent>(
        string workflowInstanceId,
        CancellationToken cancellationToken = default) where TEvent : class
    {
        _logger.LogDebug("Getting event template for workflow {WorkflowInstanceId} of type {EventType}",
            workflowInstanceId, typeof(TEvent).Name);

        // Verify the workflow is suspended
        var workflow = await _suspensionManager.GetSuspendedWorkflowAsync(workflowInstanceId, cancellationToken);
        if (workflow == null)
            throw new InvalidOperationException($"Workflow {workflowInstanceId} is not suspended or does not exist");

        // Get the event template
        return await _eventRepository.GetEventTemplateAsync<TEvent>(workflowInstanceId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EventTemplate<TEvent>> UpdateEventAndResumeAsync<TEvent>(
        string workflowInstanceId,
        TEvent eventData,
        CancellationToken cancellationToken = default) where TEvent : class
    {
        _logger.LogDebug("Updating event and resuming workflow {WorkflowInstanceId} with event of type {EventType}",
            workflowInstanceId, typeof(TEvent).Name);

        // Verify the workflow is suspended
        var workflow = await _suspensionManager.GetSuspendedWorkflowAsync(workflowInstanceId, cancellationToken);
        if (workflow == null)
            throw new InvalidOperationException($"Workflow {workflowInstanceId} is not suspended or does not exist");

        // Update the event template and trigger resumption
        return await _eventRepository.UpdateEventTemplateAsync(workflowInstanceId, eventData, true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<EventTemplate<TEvent>>> GetSuspendedWorkflowEventTemplatesAsync<TEvent>(
        CancellationToken cancellationToken = default) where TEvent : class
    {
        _logger.LogDebug("Getting all event templates for suspended workflows waiting for event type {EventType}",
            typeof(TEvent).Name);

        // Get all event templates for the specified event type
        return await _eventRepository.GetEventTemplatesByTypeAsync<TEvent>(cancellationToken);
    }
}