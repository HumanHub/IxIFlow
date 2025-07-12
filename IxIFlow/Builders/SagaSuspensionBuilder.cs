using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Builders;

///// <summary>
/////     Saga suspension builder for advanced suspend/resume scenarios
///// </summary>
//public class SagaSuspensionBuilder<TWorkflowData, TPreviousStepData> : ISagaSuspensionBuilder<TWorkflowData, TPreviousStepData>
//    where TWorkflowData : class
//    where TPreviousStepData : class
//{
//    private readonly List<SagaStepInfo> _sagaSteps;
//    private readonly List<WorkflowStep> _steps;
//    private readonly IErrorHandler _suspensionHandler;
//    private readonly ILogger<SagaSuspensionBuilder<TWorkflowData, TPreviousStepData>>? _logger;

//    public SagaSuspensionBuilder(
//        List<WorkflowStep> steps,
//        List<SagaStepInfo> sagaSteps,
//        IErrorHandler suspensionHandler,
//        ILogger<SagaSuspensionBuilder<TWorkflowData, TPreviousStepData>>? logger = null)
//    {
//        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
//        _sagaSteps = sagaSteps ?? throw new ArgumentNullException(nameof(sagaSteps));
//        _suspensionHandler = suspensionHandler ?? throw new ArgumentNullException(nameof(suspensionHandler));
//        _logger = logger;
//    }

//    public void ThenRetrySaga(int maxRetries = 1)
//    {
//        ThenRetrySaga(new RetryPolicy(maxRetries));
//    }
//    public void ThenRetrySaga(RetryPolicy retryPolicy)
//    {
//        // Handle any generic SuspensionErrorHandler<TEvent>
//        var suspensionHandlerType = _suspensionHandler.GetType();
//        if (suspensionHandlerType.IsGenericType && 
//            suspensionHandlerType.GetGenericTypeDefinition() == typeof(SuspensionErrorHandler<>))
//        {
//            _logger?.LogTrace("Found SuspensionErrorHandler: {HandlerType}", suspensionHandlerType.Name);
            
//            // Use reflection to set PostResumeAction and SagaErrorConfig
//            var postResumeActionProp = suspensionHandlerType.GetProperty("PostResumeAction");
//            var sagaErrorConfigProp = suspensionHandlerType.GetProperty("SagaErrorConfig");
//            var preExecuteProp = suspensionHandlerType.GetProperty("PreExecute");
            
//            if (postResumeActionProp != null && sagaErrorConfigProp != null)
//            {
//                var preExecute = preExecuteProp?.GetValue(_suspensionHandler) as CompensationErrorHandler;
                
//                var postResumeAction = new RetryErrorHandler
//                {
//                    RetryPolicy = retryPolicy,
//                    RetryTarget = RetryTarget.Saga
//                };

//                // For suspend/resume, use Suspend action during error handling
//                // The post-resume action will handle the retry after resumption
//                var sagaErrorConfig = new SagaErrorConfiguration
//                {
//                    CompensationStrategy = preExecute?.Strategy ?? CompensationStrategy.CompensateAll,
//                    ContinuationAction = SagaContinuationAction.Suspend,
//                    RetryPolicy = retryPolicy,
//                    CompensationTargetType = preExecute?.CompensationTargetType
//                };

//                postResumeActionProp.SetValue(_suspensionHandler, postResumeAction);
//                sagaErrorConfigProp.SetValue(_suspensionHandler, sagaErrorConfig);
                
//                _logger?.LogDebug("Configured PostResumeAction and SagaErrorConfig on {HandlerType}: PostResumeAction={PostResumeActionType}, ContinuationAction={ContinuationAction}, MaxRetries={MaxRetries}",
//                    suspensionHandlerType.Name, postResumeAction.GetType().Name, sagaErrorConfig.ContinuationAction, sagaErrorConfig.RetryPolicy?.MaximumAttempts);
//            }
//            else
//            {
//                _logger?.LogWarning("Missing required properties on {HandlerType}: PostResumeAction={HasPostResumeAction}, SagaErrorConfig={HasSagaErrorConfig}",
//                    suspensionHandlerType.Name, postResumeActionProp != null, sagaErrorConfigProp != null);
//            }
//        }
//        else
//        {
//            _logger?.LogWarning("Suspension handler is not SuspensionErrorHandler<TEvent>: {HandlerType}", suspensionHandlerType.Name);
//        }
//    }
//    public void ThenRetryFailedStep(int maxRetries = 1)
//    {
//        if (_suspensionHandler is SuspensionErrorHandler<object> suspensionHandler)
//            suspensionHandler.PostResumeAction = new RetryErrorHandler
//            {
//                RetryPolicy = new RetryPolicy(maxRetries),
//                RetryTarget = RetryTarget.FailedStep
//            };
//    }

//    public void ThenContinue()
//    {
//        if (_suspensionHandler is SuspensionErrorHandler<object> suspensionHandler)
//            suspensionHandler.PostResumeAction = new ContinuationErrorHandler();
//    }

//    public void ThenExecute(Action<ISagaBuilder<TWorkflowData, TPreviousStepData>> configure)
//    {
//        var sagaBuilder = new SagaBuilder<TWorkflowData, TPreviousStepData>(_steps);

//        configure(sagaBuilder);

//        if (_suspensionHandler is SuspensionErrorHandler<object> suspensionHandler)
//            suspensionHandler.PostResumeAction = new CustomSagaErrorHandler
//            {
//                SagaBuilder = (ISagaBuilder<object, object>)sagaBuilder
//            };
//    }
//}
