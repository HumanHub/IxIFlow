namespace IxIFlow.Core;

/// <summary>
/// Represents the execution status of a workflow throughout its lifecycle
/// </summary>
public enum WorkflowExecutionStatus
{
    /// <summary>
    /// Workflow is queued/scheduled but not yet started
    /// </summary>
    Scheduled,
    
    /// <summary>
    /// Workflow is currently executing
    /// </summary>
    Running,
    
    /// <summary>
    /// Workflow completed successfully
    /// </summary>
    Success,
    
    /// <summary>
    /// Workflow is suspended waiting for an event (can be resumed)
    /// </summary>
    Suspended,
    
    /// <summary>
    /// Workflow failed during execution due to an exception (can potentially be compensated/retried)
    /// </summary>
    Faulted,
    
    /// <summary>
    /// Workflow failed to start or definitively failed (cannot be recovered)
    /// </summary>
    Failed,
    
    /// <summary>
    /// Workflow exceeded its execution timeout
    /// </summary>
    TimedOut,
    
    /// <summary>
    /// Workflow was cancelled by user request
    /// </summary>
    Cancelled
}
