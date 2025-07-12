namespace IxIFlow.Core;

/// <summary>
///     Interface for managing event templates
/// </summary>
public interface IEventRepository
{
    /// <summary>
    ///     Creates an event template for a suspended workflow
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="workflowInstanceId">The workflow instance ID</param>
    /// <param name="eventTemplate">The event template</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created event template</returns>
    Task<EventTemplate<TEvent>> CreateEventTemplateAsync<TEvent>(
        string workflowInstanceId,
        EventTemplate<TEvent> eventTemplate,
        CancellationToken cancellationToken = default) where TEvent : class;

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
    ///     Updates an event template and optionally triggers workflow resumption
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="workflowInstanceId">The workflow instance ID</param>
    /// <param name="eventData">The updated event data</param>
    /// <param name="triggerResume">Whether to trigger workflow resumption</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated event template</returns>
    Task<EventTemplate<TEvent>> UpdateEventTemplateAsync<TEvent>(
        string workflowInstanceId,
        TEvent eventData,
        bool triggerResume = true,
        CancellationToken cancellationToken = default) where TEvent : class;

    /// <summary>
    ///     Gets all event templates for a specific event type
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of event templates</returns>
    Task<IEnumerable<EventTemplate<TEvent>>> GetEventTemplatesByTypeAsync<TEvent>(
        CancellationToken cancellationToken = default) where TEvent : class;
}