namespace IxIFlow.Core;

/// <summary>
/// Interface for a distributed workflow host that can execute workflows locally
/// and participate in a distributed workflow execution cluster
/// </summary>
public interface IWorkflowHost
{
    /// <summary>
    /// Start the workflow host and begin accepting workflow execution requests
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when the host is started</returns>
    Task StartAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stop the workflow host and gracefully shut down
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task that completes when the host is stopped</returns>
    Task StopAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Execute a workflow using standard queued execution
    /// </summary>
    /// <typeparam name="TWorkflowData">Type of workflow data</typeparam>
    /// <param name="definition">Workflow definition</param>
    /// <param name="workflowData">Initial workflow data</param>
    /// <param name="options">Workflow execution options including host selection</param>
    /// <returns>Workflow execution result</returns>
    Task<WorkflowExecutionResult> ExecuteWorkflowAsync<TWorkflowData>(
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        WorkflowOptions? options = null)
        where TWorkflowData : class;
    
    /// <summary>
    /// Resume a suspended workflow with an event
    /// </summary>
    /// <typeparam name="TEvent">Type of resume event</typeparam>
    /// <param name="instanceId">Workflow instance ID</param>
    /// <param name="event">Resume event data</param>
    /// <returns>Workflow execution result</returns>
    Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEvent>(string instanceId, TEvent @event)
        where TEvent : class;
    
    /// <summary>
    /// Execute a workflow immediately, bypassing the normal queue
    /// </summary>
    /// <typeparam name="TWorkflowData">Type of workflow data</typeparam>
    /// <param name="definition">Workflow definition</param>
    /// <param name="workflowData">Initial workflow data</param>
    /// <param name="options">Immediate execution options</param>
    /// <returns>Workflow execution result</returns>
    Task<WorkflowExecutionResult> ExecuteImmediateAsync<TWorkflowData>(
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        ImmediateExecutionOptions options)
        where TWorkflowData : class;
    
    /// <summary>
    /// Get current health status of this host
    /// </summary>
    /// <returns>Health response with current status</returns>
    Task<HostHealthResponse> GetHealthAsync();
    
    /// <summary>
    /// Get detailed status information about this host
    /// </summary>
    /// <returns>Detailed status response</returns>
    Task<HostStatusResponse> GetDetailedStatusAsync();
    
    /// <summary>
    /// Check if this host can accept another workflow
    /// </summary>
    /// <param name="allowOverride">Whether to allow capacity override</param>
    /// <returns>True if the host can accept another workflow</returns>
    Task<bool> CanAcceptWorkflow(bool allowOverride = false);
    
    /// <summary>
    /// Current status of this host
    /// </summary>
    HostStatus Status { get; }
    
    /// <summary>
    /// Current number of workflows running on this host
    /// </summary>
    int CurrentWorkflowCount { get; }
}
