using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Saga error builder for handling errors in saga pattern with compensation strategies
/// </summary>
public class SagaErrorBuilder<TWorkflowData, TPreviousStepData> : ISagaErrorBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly List<ErrorHandler> _errorHandlers;
    private readonly List<SagaStepInfo> _sagaSteps;
    private readonly List<WorkflowStep> _steps;

    public SagaErrorBuilder(
        List<WorkflowStep> steps,
        List<SagaStepInfo> sagaSteps,
        Exception? exception = null)
    {
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _sagaSteps = sagaSteps ?? throw new ArgumentNullException(nameof(sagaSteps));
        _errorHandlers = new List<ErrorHandler>();
        if (exception != null) Exception = exception;
    }

    public Exception Exception { get; set; } = new("Default saga error");

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> Compensate()
    {
        var compensationHandler = new CompensationErrorHandler
        {
            Strategy = CompensationStrategy.CompensateAll,
            SagaSteps = _sagaSteps
        };

        return new SagaContinuationBuilder<TWorkflowData, TPreviousStepData>(
            _steps, _sagaSteps, _errorHandlers, compensationHandler);
    }

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateUpTo<TActivity>()
        where TActivity : IAsyncActivity
    {
        var compensationHandler = new CompensationErrorHandler
        {
            Strategy = CompensationStrategy.CompensateUpTo,
            CompensationTargetType = typeof(TActivity),
            SagaSteps = _sagaSteps
        };

        return new SagaContinuationBuilder<TWorkflowData, TPreviousStepData>(
            _steps, _sagaSteps, _errorHandlers, compensationHandler);
    }

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateNone()
    {
        var compensationHandler = new CompensationErrorHandler
        {
            Strategy = CompensationStrategy.None,
            SagaSteps = _sagaSteps
        };

        return new SagaContinuationBuilder<TWorkflowData, TPreviousStepData>(
            _steps, _sagaSteps, _errorHandlers, compensationHandler);
    }

    public ISagaErrorBuilder<TWorkflowData, TPreviousStepData> OnError<TException>(
        Action<ISagaErrorBuilder<TWorkflowData, TPreviousStepData>> configure)
        where TException : Exception
    {
        var nestedErrorBuilder = new SagaErrorBuilder<TWorkflowData, TPreviousStepData>(
            _steps, _sagaSteps);

        configure(nestedErrorBuilder);

        // Add handlers for specific exception type
        foreach (var handler in nestedErrorBuilder._errorHandlers)
        {
            handler.ExceptionType = typeof(TException);
            _errorHandlers.Add(handler);
        }

        return this;
    }

    public ISagaErrorBuilder<TWorkflowData, TPreviousStepData> OnError(
        Action<ISagaErrorBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        var nestedErrorBuilder = new SagaErrorBuilder<TWorkflowData, TPreviousStepData>(
            _steps, _sagaSteps);

        configure(nestedErrorBuilder);

        // Add handlers for general exceptions
        foreach (var handler in nestedErrorBuilder._errorHandlers) _errorHandlers.Add(handler);

        return this;
    }

    public ISagaErrorBuilder<TWorkflowData, TPreviousStepData> Step<TActivity>()
        where TActivity : class, IAsyncActivity
    {
        // For error recovery steps - not implemented yet
        return this;
    }

    public ISagaErrorBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity
    {
        // Create error handling step with previous step access
        var step = new WorkflowStep
        {
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            InputMappings = new List<PropertyMapping>(),
            OutputMappings = new List<PropertyMapping>()
        };

        var builder = new SagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(step);
        configure(builder);

        _steps.Add(step);
        
        // Return a new builder where TActivity becomes the new TPreviousStepData
        return new SagaErrorBuilder<TWorkflowData, TActivity>(_steps, _sagaSteps, Exception);
    }

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateWith<TActivity>(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity
    {
        // Create compensation activity builder
        var compensationInfo = new CompensationActivityInfo
        {
            ActivityType = typeof(TActivity),
            InputMappings = new List<PropertyMapping>(),
            OutputMappings = new List<PropertyMapping>()
        };

        var builder = new SagaActivityBuilder<TWorkflowData, TPreviousStepData>(
            _steps, _sagaSteps);

        configure(builder);

        var compensationHandler = new CompensationErrorHandler
        {
            Strategy = CompensationStrategy.CustomActivity,
            CustomCompensationActivity = compensationInfo,
            SagaSteps = _sagaSteps
        };

        return new SagaContinuationBuilder<TWorkflowData, TPreviousStepData>(
            _steps, _sagaSteps, _errorHandlers, compensationHandler);
    }

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateWith<TActivity>()
        where TActivity : class, IAsyncActivity
    {
        return CompensateWith<TActivity>(_ => { });
    }

    //public ISagaSuspensionBuilder<TWorkflowData, TPreviousStepData> ThenSuspend<TEvent>(string reason,
    //    Func<TEvent, WorkflowContext<TWorkflowData, TPreviousStepData>, bool> resumeCondition)
    //{
    //    var suspensionHandler = new SuspensionErrorHandler<TEvent>
    //    {
    //        Reason = reason,
    //        ResumeCondition = (evt, ctx) =>
    //            resumeCondition((TEvent)evt, (WorkflowContext<TWorkflowData, TPreviousStepData>)ctx),
    //        EventType = typeof(TEvent)
    //    };

    //    var errorHandler = new ErrorHandler
    //    {
    //        ExceptionType = typeof(Exception),
    //        Handler = suspensionHandler
    //    };

    //    _errorHandlers.Add(errorHandler);

    //    return new SagaSuspensionBuilder<TWorkflowData, TPreviousStepData>(
    //        _steps, _sagaSteps, suspensionHandler);
    //}

    //public ISagaSuspensionBuilder<TWorkflowData, TPreviousStepData> ThenSuspend(string reason)
    //{
    //    var suspensionHandler = new SuspensionErrorHandler<object>
    //    {
    //        Reason = reason,
    //        ResumeCondition = null,
    //        EventType = typeof(object)
    //    };

    //    var errorHandler = new ErrorHandler
    //    {
    //        ExceptionType = typeof(Exception),
    //        Handler = suspensionHandler
    //    };

    //    _errorHandlers.Add(errorHandler);

    //    return new SagaSuspensionBuilder<TWorkflowData, TPreviousStepData>(
    //        _steps, _sagaSteps, suspensionHandler);
    //}

    public ISagaErrorBuilder<TWorkflowData, TPreviousStepData> ThenRetry(int maxAttempts)
    {
        return ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, maxAttempts, 1));
    }

    public ISagaErrorBuilder<TWorkflowData, TPreviousStepData> ThenRetry(RetryPolicy retryPolicy)
    {
        var retryHandler = new RetryErrorHandler
        {
            RetryPolicy = retryPolicy
        };

        var errorHandler = new ErrorHandler
        {
            ExceptionType = typeof(Exception),
            Handler = retryHandler
        };

        _errorHandlers.Add(errorHandler);
        return this;
    }

    public void ThenTerminate()
    {
        var errorHandler = new ErrorHandler
        {
            ExceptionType = typeof(Exception),
            Handler = new TerminationErrorHandler()
        };

        _errorHandlers.Add(errorHandler);
    }

    public void ThenContinue()
    {
        var errorHandler = new ErrorHandler
        {
            ExceptionType = typeof(Exception),
            Handler = new ContinuationErrorHandler()
        };

        _errorHandlers.Add(errorHandler);
    }

    public void ThenIgnore()
    {
        var errorHandler = new ErrorHandler
        {
            ExceptionType = typeof(Exception),
            Handler = new IgnoreErrorHandler()
        };

        _errorHandlers.Add(errorHandler);
    }

    /// <summary>
    ///     Get all configured error handlers
    /// </summary>
    internal List<ErrorHandler> GetErrorHandlers()
    {
        return _errorHandlers;
    }
}
