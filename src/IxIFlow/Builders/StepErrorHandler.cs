using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Implementation of an ordered step error handler
/// </summary>
public class StepErrorHandler : IStepErrorHandler
{
    internal static void Add<TException>(WorkflowStep step, Action<IStepErrorHandler<TException>> configure)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(configure);
        var policy = new StepErrorHandlerInfo { ExceptionType = typeof(TException), HandlerAction = StepErrorAction.Terminate };
        configure(new StepErrorHandler<TException>(policy));
        if (!step.StepMetadata.TryGetValue("StepErrorHandlers", out var value))
            step.StepMetadata["StepErrorHandlers"] = value = new List<StepErrorHandlerInfo>();
        ((List<StepErrorHandlerInfo>)value).Add(policy);
    }

    private readonly StepErrorHandlerInfo _handlerInfo;

    public StepErrorHandler(StepErrorHandlerInfo handlerInfo)
    {
        _handlerInfo = handlerInfo ?? throw new ArgumentNullException(nameof(handlerInfo));
    }

    public void ThenRetry(int maxRetries = 3)
    {
        ThenRetry(RetryPolicy.CreateDefault(maxRetries));
    }

    public void ThenRetry(RetryPolicy retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(retryPolicy);
        _handlerInfo.HandlerAction = StepErrorAction.Retry;
        _handlerInfo.RetryPolicy = retryPolicy;
    }

    public void ThenTerminate()
    {
        _handlerInfo.HandlerAction = StepErrorAction.Terminate;
        _handlerInfo.RetryPolicy = null;
    }

    public void ThenIgnore()
    {
        _handlerInfo.HandlerAction = StepErrorAction.Ignore;
        _handlerInfo.RetryPolicy = null;
    }
}

/// <summary>
///     Typed configuration of an ordered step error policy.
/// </summary>
public class StepErrorHandler<TException>(StepErrorHandlerInfo handlerInfo)
    : StepErrorHandler(handlerInfo), IStepErrorHandler<TException>
    where TException : Exception;
