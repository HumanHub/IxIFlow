using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Implementation of saga step error handler (generic)
/// </summary>
public class SagaStepErrorHandler : ISagaStepErrorHandler
{
    private readonly StepErrorHandlerInfo _handlerInfo;

    public SagaStepErrorHandler(StepErrorHandlerInfo handlerInfo)
    {
        _handlerInfo = handlerInfo ?? throw new ArgumentNullException(nameof(handlerInfo));
    }

    public void ThenRetry(int maxRetries = 3)
    {
        _handlerInfo.HandlerAction = StepErrorAction.Retry;
        _handlerInfo.RetryPolicy = new RetryPolicy { MaximumAttempts = maxRetries };
    }

    public void ThenRetry(RetryPolicy retryPolicy)
    {
        _handlerInfo.HandlerAction = StepErrorAction.Retry;
        _handlerInfo.RetryPolicy = retryPolicy;
    }

    public void ThenTerminate()
    {
        _handlerInfo.HandlerAction = StepErrorAction.Terminate;
    }

    public void ThenIgnore()
    {
        _handlerInfo.HandlerAction = StepErrorAction.Ignore;
    }
}

/// <summary>
///     Implementation of saga step error handler
/// </summary>
public class SagaStepErrorHandler<TException> : ISagaStepErrorHandler<TException>
    where TException : Exception
{
    private readonly StepErrorHandlerInfo _handlerInfo;

    public SagaStepErrorHandler(StepErrorHandlerInfo handlerInfo)
    {
        _handlerInfo = handlerInfo ?? throw new ArgumentNullException(nameof(handlerInfo));
    }

    public void ThenRetry(int maxRetries = 3)
    {
        _handlerInfo.HandlerAction = StepErrorAction.Retry;
        _handlerInfo.RetryPolicy = new RetryPolicy { MaximumAttempts = maxRetries };
    }

    public void ThenRetry(RetryPolicy retryPolicy)
    {
        _handlerInfo.HandlerAction = StepErrorAction.Retry;
        _handlerInfo.RetryPolicy = retryPolicy;
    }

    public void ThenTerminate()
    {
        _handlerInfo.HandlerAction = StepErrorAction.Terminate;
    }

    public void ThenIgnore()
    {
        _handlerInfo.HandlerAction = StepErrorAction.Ignore;
    }
}