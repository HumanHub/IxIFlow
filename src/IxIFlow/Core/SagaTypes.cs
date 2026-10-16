using IxIFlow.Builders;

namespace IxIFlow.Core;

/// <summary>
///     Represents a step in a saga transaction
/// </summary>
public class SagaStep
{
    public Type ActivityType { get; set; } = null!;
    public bool IsSuccess { get; set; }
    public CompensationActivityInfo? CompensationActivity { get; set; }
    public object? Result { get; set; }
    public Exception? Exception { get; set; }
    public string StepId { get; set; } = string.Empty;
}

/// <summary>
///     Saga step information used during building
/// </summary>
public class SagaStepInfo
{
    public Type ActivityType { get; set; } = null!;
    public List<PropertyMapping> InputMappings { get; set; } = new();
    public List<PropertyMapping> OutputMappings { get; set; } = new();
    public CompensationActivityInfo? CompensationActivity { get; set; }
    public string StepId { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>
///     Error handler for workflow and saga error handling
/// </summary>
public class ErrorHandler
{
    public Type ExceptionType { get; set; } = typeof(Exception);
    public IErrorHandler Handler { get; set; } = null!;
    public int Priority { get; set; } = 0;
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
/// Simple result for step-level error handling
/// </summary>
public class StepErrorHandlingResult
{
    /// <summary>
    /// Whether the error was handled successfully
    /// </summary>
    public bool IsHandled { get; set; }
    
    /// <summary>
    /// The action taken for this step error
    /// </summary>
    public StepErrorAction Action { get; set; }
    
    /// <summary>
    /// Whether the step should be considered successful after error handling
    /// </summary>
    public bool TreatAsSuccess { get; set; }
    
    /// <summary>
    /// Result data to use if treating as success
    /// </summary>
    public object? ResultData { get; set; }
    
    /// <summary>
    /// Error message if handling failed
    /// </summary>
    public string? ErrorMessage { get; set; }
}


/// <summary>
///     Retry targets for error handling
/// </summary>
public enum RetryTarget
{
    FailedStep,
    Saga
}


/// <summary>
///     Compensation strategies for saga error handling
/// </summary>
public enum CompensationStrategy
{
    None,
    CompensateAll,
    CompensateUpTo,
    CustomActivity
}

/// <summary>
///     Saga continuation actions after error handling
/// </summary>
public enum SagaContinuationAction
{
    Continue,
    Terminate,
    Retry,
    Suspend
}

/// <summary>
///     Configuration for saga error handling behavior
/// </summary>
public class SagaErrorConfiguration
{
    public CompensationStrategy CompensationStrategy { get; set; } = CompensationStrategy.CompensateAll;
    /// <summary>What follows the undo; none raises the original error again (D-36).</summary>
    public SagaContinuationAction? ContinuationAction { get; set; }
    public RetryPolicy? RetryPolicy { get; set; } = new();
    public Type? CompensationTargetType { get; set; }
    public bool IgnoreCompensationErrors { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}
