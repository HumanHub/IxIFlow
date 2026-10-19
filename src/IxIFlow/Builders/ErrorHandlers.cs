using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Compensation error handler
/// </summary>
public class CompensationErrorHandler : IErrorHandler
{
    public CompensationStrategy Strategy { get; set; }
    public Type? CompensationTargetType { get; set; }
    public List<SagaStepInfo> SagaSteps { get; set; } = new();
}

/// <summary>
///     Retry error handler
/// </summary>
public class RetryErrorHandler : IErrorHandler
{
    public required RetryPolicy RetryPolicy { get; set; }
    public CompensationErrorHandler? PreExecute { get; set; }
}

/// <summary>
///     Termination error handler
/// </summary>
public class TerminationErrorHandler : IErrorHandler
{
    public CompensationErrorHandler? PreExecute { get; set; }
    public SagaErrorConfiguration? SagaErrorConfig { get; set; }
}

/// <summary>
///     Continuation error handler
/// </summary>
public class ContinuationErrorHandler : IErrorHandler
{
    public bool IgnoreCompensationErrors { get; set; }
    public CompensationErrorHandler? PreExecute { get; set; }
    public SagaErrorConfiguration? SagaErrorConfig { get; set; }
}
