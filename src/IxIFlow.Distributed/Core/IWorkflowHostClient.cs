namespace IxIFlow.Core;

/// <summary>
/// Interface for communicating directly with workflow hosts over HTTP
/// This enables the coordinator to communicate with remote hosts
/// </summary>
public interface IWorkflowHostClient
{
    /// <summary>
    /// Check the health of a specific host
    /// </summary>
    /// <param name="hostId">Host to check</param>
    /// <param name="timeout">Maximum time to wait for response</param>
    /// <returns>Health response from the host</returns>
    Task<HostHealthResponse> CheckHealthAsync(string hostId, TimeSpan timeout = default);

    /// <summary>
    /// Execute a workflow immediately on a specific host
    /// </summary>
    /// <typeparam name="TWorkflowData">Type of workflow data</typeparam>
    /// <param name="hostId">Target host ID</param>
    /// <param name="definition">Workflow definition</param>
    /// <param name="workflowData">Initial workflow data</param>
    /// <param name="options">Immediate execution options</param>
    /// <returns>Workflow execution result</returns>
    Task<WorkflowExecutionResult> ExecuteImmediateAsync<TWorkflowData>(
        string hostId, 
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        ImmediateExecutionOptions options)
        where TWorkflowData : class;

    /// <summary>
    /// Get detailed status from a specific host
    /// </summary>
    /// <param name="hostId">Host to query</param>
    /// <returns>Detailed status response</returns>
    Task<HostStatusResponse> GetStatusAsync(string hostId);

    /// <summary>
    /// Check if a host can accept another workflow
    /// </summary>
    /// <param name="hostId">Host to check</param>
    /// <param name="allowOverride">Whether to allow capacity override</param>
    /// <returns>True if the host can accept another workflow</returns>
    Task<bool> CanAcceptWorkflowAsync(string hostId, bool allowOverride = false);

    /// <summary>
    /// Resume a suspended workflow on a specific host
    /// </summary>
    /// <typeparam name="TEvent">Type of resume event</typeparam>
    /// <param name="hostId">Target host ID</param>
    /// <param name="instanceId">Workflow instance ID</param>
    /// <param name="event">Resume event data</param>
    /// <returns>Workflow execution result</returns>
    Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEvent>(
        string hostId, 
        string instanceId, 
        TEvent @event)
        where TEvent : class;

    /// <summary>
    /// Cancel a running workflow on a specific host
    /// </summary>
    /// <param name="hostId">Target host ID</param>
    /// <param name="instanceId">Workflow instance ID</param>
    /// <param name="reason">Cancellation reason</param>
    /// <returns>True if cancellation was successful</returns>
    Task<bool> CancelWorkflowAsync(string hostId, string instanceId, CancellationReason reason);

    /// <summary>
    /// Get workflow instance status from a specific host
    /// </summary>
    /// <param name="hostId">Host to query</param>
    /// <param name="instanceId">Workflow instance ID</param>
    /// <returns>Workflow instance information</returns>
    Task<WorkflowInstance?> GetWorkflowInstanceAsync(string hostId, string instanceId);

    /// <summary>
    /// Send a queued workflow request to a specific host
    /// </summary>
    /// <typeparam name="TWorkflowData">Type of workflow data</typeparam>
    /// <param name="hostId">Target host ID</param>
    /// <param name="definition">Workflow definition</param>
    /// <param name="workflowData">Initial workflow data</param>
    /// <param name="options">Workflow execution options</param>
    /// <returns>Workflow instance ID for tracking</returns>
    Task<string> QueueWorkflowAsync<TWorkflowData>(
        string hostId, 
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        WorkflowOptions? options = null)
        where TWorkflowData : class;
}
