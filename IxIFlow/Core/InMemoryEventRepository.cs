using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     In-memory implementation of IEventRepository
/// </summary>
public class InMemoryEventRepository : IEventRepository
{
    private readonly Dictionary<string, object> _eventTemplates = new();
    private readonly ILogger<InMemoryEventRepository> _logger;
    private readonly ISuspensionManager _suspensionManager;

    public InMemoryEventRepository(
        ISuspensionManager suspensionManager,
        ILogger<InMemoryEventRepository> logger)
    {
        _suspensionManager = suspensionManager ?? throw new ArgumentNullException(nameof(suspensionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<EventTemplate<TEvent>> CreateEventTemplateAsync<TEvent>(
        string workflowInstanceId,
        EventTemplate<TEvent> eventTemplate,
        CancellationToken cancellationToken = default) where TEvent : class
    {
        _logger.LogDebug("Creating event template for workflow {WorkflowInstanceId} of type {EventType}",
            workflowInstanceId, typeof(TEvent).Name);

        // Store the event template
        _eventTemplates[workflowInstanceId] = eventTemplate;

        return Task.FromResult(eventTemplate);
    }

    /// <inheritdoc />
    public Task<EventTemplate<TEvent>> GetEventTemplateAsync<TEvent>(
        string workflowInstanceId,
        CancellationToken cancellationToken = default) where TEvent : class
    {
        _logger.LogDebug("Getting event template for workflow {WorkflowInstanceId} of type {EventType}",
            workflowInstanceId, typeof(TEvent).Name);

        // Get the event template
        if (_eventTemplates.TryGetValue(workflowInstanceId, out var template) &&
            template is EventTemplate<TEvent> typedTemplate) return Task.FromResult(typedTemplate);

        throw new InvalidOperationException($"Event template not found for workflow {workflowInstanceId}");
    }

    /// <inheritdoc />
    public async Task<EventTemplate<TEvent>> UpdateEventTemplateAsync<TEvent>(
        string workflowInstanceId,
        TEvent eventData,
        bool triggerResume = true,
        CancellationToken cancellationToken = default) where TEvent : class
    {
        _logger.LogDebug("Updating event template for workflow {WorkflowInstanceId} of type {EventType}",
            workflowInstanceId, typeof(TEvent).Name);

        // Get the event template
        var template = await GetEventTemplateAsync<TEvent>(workflowInstanceId, cancellationToken);

        // Update the event data
        template.EventData = eventData;

        // Store the updated template
        _eventTemplates[workflowInstanceId] = template;

        // Trigger workflow resumption if requested
        if (triggerResume)
        {
            _logger.LogDebug("Triggering workflow resumption for {WorkflowInstanceId}", workflowInstanceId);
            await _suspensionManager.ProcessEventAsync(eventData, cancellationToken);
        }

        return template;
    }

    /// <inheritdoc />
    public Task<IEnumerable<EventTemplate<TEvent>>> GetEventTemplatesByTypeAsync<TEvent>(
        CancellationToken cancellationToken = default) where TEvent : class
    {
        _logger.LogDebug("Getting all event templates of type {EventType}", typeof(TEvent).Name);

        // Filter templates by event type
        var templates = _eventTemplates.Values
            .OfType<EventTemplate<TEvent>>()
            .ToList();

        return Task.FromResult<IEnumerable<EventTemplate<TEvent>>>(templates);
    }
}