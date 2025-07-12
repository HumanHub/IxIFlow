using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Saga continuation builder - decides what to do after compensation strategy
/// </summary>
public class SagaContinuationBuilder<TWorkflowData, TPreviousStepData> : ISagaContinuationBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly CompensationErrorHandler _compensationHandler;
    private readonly List<ErrorHandler> _errorHandlers;
    private readonly List<SagaStepInfo> _sagaSteps;
    private readonly List<WorkflowStep> _steps;

    public SagaContinuationBuilder(
        List<WorkflowStep> steps,
        List<SagaStepInfo> sagaSteps,
        List<ErrorHandler> errorHandlers,
        CompensationErrorHandler compensationHandler)
    {
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _sagaSteps = sagaSteps ?? throw new ArgumentNullException(nameof(sagaSteps));
        _errorHandlers = errorHandlers ?? throw new ArgumentNullException(nameof(errorHandlers));
        _compensationHandler = compensationHandler ?? throw new ArgumentNullException(nameof(compensationHandler));
    }

    public void ThenContinue(bool ignoreCompensationErrors = false)
    {
        // Create the saga error configuration
        var sagaErrorConfig = new SagaErrorConfiguration
        {
            CompensationStrategy = _compensationHandler.Strategy,
            ContinuationAction = SagaContinuationAction.Continue,
            RetryPolicy = null,
            CompensationTargetType = _compensationHandler.CompensationTargetType
        };

        var errorHandler = new ErrorHandler
        {
            ExceptionType = typeof(Exception),
            Handler = new ContinuationErrorHandler
            {
                IgnoreCompensationErrors = ignoreCompensationErrors,
                PreExecute = _compensationHandler,
                SagaErrorConfig = sagaErrorConfig
            }
        };

        _errorHandlers.Add(errorHandler);
    }

    public void ThenTerminate()
    {
        // Create the saga error configuration
        var sagaErrorConfig = new SagaErrorConfiguration
        {
            CompensationStrategy = _compensationHandler.Strategy,
            ContinuationAction = SagaContinuationAction.Terminate,
            RetryPolicy = null,
            CompensationTargetType = _compensationHandler.CompensationTargetType
        };

        var errorHandler = new ErrorHandler
        {
            ExceptionType = typeof(Exception),
            Handler = new TerminationErrorHandler
            {
                PreExecute = _compensationHandler,
                SagaErrorConfig = sagaErrorConfig
            }
        };

        _errorHandlers.Add(errorHandler);
    }

    public void ThenRetry(int maxAttempts)
    {
        ThenRetry(new RetryPolicy(maxAttempts));
    }

    public void ThenRetry(RetryPolicy retryPolicy)
    {
        var errorHandler = new ErrorHandler
        {
            ExceptionType = typeof(Exception),
            Handler = new RetryErrorHandler
            {
                RetryPolicy = retryPolicy,
                PreExecute = _compensationHandler
            }
        };

        _errorHandlers.Add(errorHandler);
    }
}