namespace IxIFlow.Core;

/// <summary>
///     Interface for correlating events with suspended workflows
/// </summary>
public interface IEventCorrelator
{
    /// <summary>
    ///     Finds suspended workflows that match the given event
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="event">The event to match</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of matching workflow instances</returns>
    Task<IEnumerable<WorkflowInstance>> FindMatchingWorkflowsAsync<TEvent>(
        TEvent @event,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Evaluates if an event matches the resume conditions for a suspended workflow
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="event">The event to evaluate</param>
    /// <param name="workflowInstance">The suspended workflow instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the event matches the resume conditions</returns>
    Task<bool> EvaluateResumeConditionAsync<TEvent>(
        TEvent @event,
        WorkflowInstance workflowInstance,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Checks if an event matches the resume condition for a specific workflow
    /// </summary>
    /// <typeparam name="TEvent">The type of event</typeparam>
    /// <param name="workflowInstanceId">The workflow instance ID</param>
    /// <param name="event">The event to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the event matches the resume condition</returns>
    Task<bool> CheckResumeConditionAsync<TEvent>(
        string workflowInstanceId,
        TEvent @event,
        CancellationToken cancellationToken = default);
}