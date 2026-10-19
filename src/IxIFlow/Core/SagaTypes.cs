using IxIFlow.Builders;

namespace IxIFlow.Core;

/// <summary>
///     Error handler for workflow and saga error handling
/// </summary>
public class ErrorHandler
{
    public Type ExceptionType { get; set; } = typeof(Exception);
    public IErrorHandler Handler { get; set; } = null!;
}

/// <summary>
/// Step-level error handling actions
/// </summary>
public enum StepErrorAction
{
    /// <summary>
    /// Terminate the step and propagate to saga level
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

/// <summary>
///     Compensation strategies for saga error handling
/// </summary>
public enum CompensationStrategy
{
    None,
    CompensateAll,
    CompensateUpTo
}

/// <summary>
///     Saga continuation actions after error handling
/// </summary>
public enum SagaContinuationAction
{
    Continue,
    Terminate,
    Retry
}

/// <summary>
///     Configuration for saga error handling behavior
/// </summary>
public class SagaErrorConfiguration
{
    public CompensationStrategy CompensationStrategy { get; set; } = CompensationStrategy.CompensateAll;
    /// <summary>What follows the undo; none raises the original error again.</summary>
    public SagaContinuationAction? ContinuationAction { get; set; }
    public RetryPolicy? RetryPolicy { get; set; } = new();
    public Type? CompensationTargetType { get; set; }
    public bool IgnoreCompensationErrors { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}
