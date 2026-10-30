namespace IxIFlow.Core;

/// <summary>
/// Represents the execution status of a workflow throughout its lifecycle
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(WorkflowExecutionStatusJsonConverter))]
public enum WorkflowExecutionStatus
{
    /// <summary>
    /// Workflow is queued/scheduled but not yet started
    /// </summary>
    Scheduled = 0,

    /// <summary>
    /// Workflow is currently executing
    /// </summary>
    Running = 1,

    /// <summary>
    /// Workflow completed successfully
    /// </summary>
    Success = 2,

    /// <summary>
    /// Workflow is suspended waiting for an event (can be resumed)
    /// </summary>
    Suspended = 3,

    /// <summary>
    /// Execution failed, or the request was refused before execution; consult the result's ErrorKind.
    /// </summary>
    Faulted = 4,

    /// <summary>
    /// Workflow exceeded its execution timeout
    /// </summary>
    TimedOut = 6,

    /// <summary>
    /// Workflow was cancelled by user request
    /// </summary>
    Cancelled = 7,

    /// <summary>
    /// Execution is stopped for activity resolution, a resource limit, quarantine or verification; inspect the instance stop information.
    /// </summary>
    NeedsResolution = 8,

    /// <summary>Retention removed the result; the instance ID is still reserved.</summary>
    Removed = 9,

    /// <summary>Held until an authorized continue or cancellation.</summary>
    Paused = 10,

    /// <summary>A hard stop or an unhandled saga termination ended the instance.</summary>
    Terminated = 11
}