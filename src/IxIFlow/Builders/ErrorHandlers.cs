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
    public CompensationActivityInfo? CustomCompensationActivity { get; set; }
    public List<SagaStepInfo> SagaSteps { get; set; } = new();
}

/// <summary>
///     Suspension error handler
/// </summary>
public class SuspensionErrorHandler<TEvent> : IErrorHandler
{
    public string Reason { get; set; } = string.Empty;
    public Type EventType { get; set; } = typeof(TEvent);
    public Func<object, object, bool>? ResumeCondition { get; set; }
    public IErrorHandler? PostResumeAction { get; set; }
    public CompensationErrorHandler? PreExecute { get; set; }
    public SagaErrorConfiguration? SagaErrorConfig { get; set; }
}

/// <summary>
///     Retry error handler
/// </summary>
public class RetryErrorHandler : IErrorHandler
{
    public required RetryPolicy RetryPolicy { get; set; }
    public RetryTarget RetryTarget { get; set; } = RetryTarget.FailedStep;
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

/// <summary>
///     Ignore error handler
/// </summary>
public class IgnoreErrorHandler : IErrorHandler
{
}
