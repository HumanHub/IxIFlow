using IxIFlow.Builders;

namespace IxIFlow.Core;

/// <summary>
/// Step-level error handling actions
/// </summary>
public enum StepErrorAction
{
    /// <summary>
    /// Propagate the step failure to enclosing handlers
    /// </summary>
    Terminate,

    /// <summary>
    /// Retry the step with configured retry policy
    /// </summary>
    Retry,

    /// <summary>
    /// Ignore the error and continue as if step succeeded
    /// </summary>
    Ignore
}

/// <summary>
/// Information about step-level error handlers
/// </summary>
public class StepErrorHandlerInfo
{
    /// <summary>
    /// The exception type this handler can handle
    /// </summary>
    public Type ExceptionType { get; set; } = null!;

    /// <summary>
    /// The action to take when this exception occurs
    /// </summary>
    public StepErrorAction HandlerAction { get; set; }

    /// <summary>
    /// Retry policy for retry action
    /// </summary>
    public RetryPolicy? RetryPolicy { get; set; }
}
