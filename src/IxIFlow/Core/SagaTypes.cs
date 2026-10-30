using IxIFlow.Builders;

namespace IxIFlow.Core;

/// <summary>
///     Error handler for workflow and saga error handling
/// </summary>
internal class ErrorHandler
{
    public Type ExceptionType { get; set; } = typeof(Exception);
    public IErrorHandler Handler { get; set; } = null!;
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
internal class SagaErrorConfiguration
{
    public CompensationStrategy CompensationStrategy { get; set; } = CompensationStrategy.CompensateAll;

    /// <summary>What follows the undo; none raises the original error again.</summary>
    public SagaContinuationAction? ContinuationAction { get; set; }

    public RetryPolicy? RetryPolicy { get; set; } = new();
    public Type? CompensationTargetType { get; set; }
    public bool IgnoreCompensationErrors { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}