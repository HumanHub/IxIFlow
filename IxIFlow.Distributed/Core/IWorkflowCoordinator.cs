namespace IxIFlow.Core;

/// <summary>
/// Interface for coordinating workflow execution across distributed hosts
/// with tag-based selection and real-time health monitoring
/// </summary>
public interface IWorkflowCoordinator
{
    // ========== STANDARD WORKFLOW DISTRIBUTION ==========

    /// <summary>
    /// Select the best host for workflow execution based on options
    /// </summary>
    /// <param name="options">Workflow options including host selection criteria</param>
    /// <returns>Selected host ID</returns>
    Task<string> SelectHostForWorkflowAsync(WorkflowOptions? options = null);

    /// <summary>
    /// Select a host for resuming a suspended workflow
    /// </summary>
    /// <param name="instanceId">Workflow instance ID</param>
    /// <returns>Selected host ID for resume</returns>
    Task<string> SelectHostForResumeAsync(string instanceId);

    // ========== TAG-BASED SELECTION ==========

    /// <summary>
    /// Get hosts that match tag criteria
    /// </summary>
    /// <param name="requiredTags">Tags that hosts MUST have (all required)</param>
    /// <param name="preferredTags">Tags that are preferred (any preferred)</param>
    /// <returns>Matching host status information</returns>
    Task<IEnumerable<HostStatus>> GetHostsByTagsAsync(string[] requiredTags, string[]? preferredTags = null);

    /// <summary>
    /// Get healthy hosts matching tag criteria
    /// </summary>
    /// <param name="requiredTags">Tags that hosts MUST have</param>
    /// <returns>Array of healthy host IDs</returns>
    Task<string[]> GetHealthyHostsAsync(string[]? requiredTags = null);

    // ========== REAL-TIME OPERATIONS ==========

    /// <summary>
    /// Check the health of a specific host in real-time
    /// </summary>
    /// <param name="hostId">Host to check</param>
    /// <param name="timeout">Maximum time to wait for response</param>
    /// <returns>Health response from the host</returns>
    Task<HostHealthResponse> CheckHostHealthAsync(string hostId, TimeSpan timeout = default);

    /// <summary>
    /// Check health of multiple hosts simultaneously
    /// </summary>
    /// <param name="hostIds">Hosts to check</param>
    /// <param name="timeout">Maximum time to wait for responses</param>
    /// <returns>Health responses from all hosts</returns>
    Task<HostHealthResponse[]> BulkHealthCheckAsync(string[] hostIds, TimeSpan timeout = default);

    /// <summary>
    /// Execute a workflow immediately on a specific host, bypassing the queue
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

    // ========== HOST MANAGEMENT ==========

    /// <summary>
    /// Register a new host in the distributed cluster
    /// </summary>
    /// <param name="hostId">Unique host identifier</param>
    /// <param name="capabilities">Host capabilities and configuration</param>
    /// <returns>Task that completes when registration is done</returns>
    Task RegisterHostAsync(string hostId, HostCapabilities capabilities);

    /// <summary>
    /// Update the status of a host
    /// </summary>
    /// <param name="status">Updated host status</param>
    /// <returns>Task that completes when status is updated</returns>
    Task UpdateHostStatusAsync(HostStatus status);

    /// <summary>
    /// Get all available hosts in the cluster
    /// </summary>
    /// <returns>Current status of all hosts</returns>
    Task<IEnumerable<HostStatus>> GetAvailableHostsAsync();

    /// <summary>
    /// Discover available hosts through service discovery
    /// </summary>
    /// <returns>Array of discovered host IDs</returns>
    Task<string[]> DiscoverAvailableHostsAsync();

    /// <summary>
    /// Unregister a host from the cluster
    /// </summary>
    /// <param name="hostId">Host to unregister</param>
    /// <returns>Task that completes when unregistration is done</returns>
    Task UnregisterHostAsync(string hostId);
}
