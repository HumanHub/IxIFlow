namespace IxIFlow.Core;

/// <summary>
/// Represents the execution state for propagation-based exception handling
/// </summary>
public class ExecutionState
{
    /// <summary>
    /// The current pending exception that needs to be handled
    /// </summary>
    public Exception? PendingException { get; set; }

    /// <summary>
    /// Stack of active try blocks for nested exception handling
    /// </summary>
    public Stack<WorkflowStep> TryStack { get; set; } = new();

    /// <summary>
    /// The result of the last executed step
    /// </summary>
    public object? LastStepResult { get; set; }

    /// <summary>
    /// Whether we are currently inside a try block
    /// </summary>
    public bool IsInTryBlock => TryStack.Count > 0;

    /// <summary>
    /// Gets the current try block (if any)
    /// </summary>
    public WorkflowStep? CurrentTryBlock => TryStack.Count > 0 ? TryStack.Peek() : null;

    /// <summary>
    /// Step-level metadata for execution context (e.g., retry attempts)
    /// </summary>
    public Dictionary<string, object> StepMetadata { get; set; } = new();
}
