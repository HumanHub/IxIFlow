namespace IxIFlow.Core;

/// <summary>
/// Starts, resumes, cancels, recovers and resolves workflow instances. A call that runs an
/// instance returns when the instance is idle: suspended at a wait, stopped for resolution,
/// completed, cancelled, timed out or failed.
/// </summary>
public interface IWorkflowClient
{
    /// <summary>Starts an instance of the definition and runs it until it is idle.</summary>
    Task<WorkflowExecutionResult> RunAsync<TWorkflowData>(
        WorkflowDefinition definition,
        TWorkflowData workflowData,
        WorkflowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWorkflowData : class;

    /// <summary>Delivers an event to the instance's only matching wait and runs it until it is idle.</summary>
    Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class;

    /// <summary>Delivers an event to the wait with this correlation key and runs the instance until it is idle.</summary>
    Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId,
        string key,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class;

    /// <summary>Delivers an event at most once for a durable message delivery ID.</summary>
    Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId,
        string? key,
        TEventData @event,
        string deliveryId,
        CancellationToken cancellationToken = default)
        where TEventData : class;

    /// <summary>Continues an interrupted or due instance without an external event.</summary>
    Task<WorkflowExecutionResult> RecoverAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Requests durable cancellation and unwinds an idle instance immediately.</summary>
    Task<WorkflowExecutionResult> CancelAsync(
        string instanceId,
        CancellationReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one committed activity start whose external outcome is unknown. The decision is
    /// saved before execution continues.
    /// </summary>
    Task<WorkflowExecutionResult> ResolveAsync(
        string instanceId,
        string invocationId,
        ActivityResolution resolution,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the saved activity attempts awaiting an operator decision.</summary>
    Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId);
}
