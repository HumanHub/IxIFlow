using IxIFlow.Builders;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
/// Handles saga execution with compensation and error handling using propagation-based approach
/// </summary>
public class SagaExecutor : ISagaExecutor
{
    private readonly IActivityExecutor _activityExecutor;
    private readonly ISuspendResumeExecutor _suspendResumeExecutor;
    private readonly ILogger<SagaExecutor> _logger;

    public SagaExecutor(
        IActivityExecutor activityExecutor,
        ISuspendResumeExecutor suspendResumeExecutor,
        ILogger<SagaExecutor> logger)
    {
        _activityExecutor = activityExecutor ?? throw new ArgumentNullException(nameof(activityExecutor));
        _suspendResumeExecutor = suspendResumeExecutor ?? throw new ArgumentNullException(nameof(suspendResumeExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes a saga step with enhanced suspend/resume support including mid-saga resume
    /// </summary>
    public async Task<StepExecutionResult> ExecuteSagaStepAsync<TWorkflowData>(
        WorkflowStep sagaStep,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken,
        SagaResumeInfo? resumeInfo = null)
        where TWorkflowData : class
    {
        _logger.LogDebug("Starting saga execution for step {StepId} with {StepCount} steps", sagaStep.Id, sagaStep.SequenceSteps.Count);

        // Get saga retry configuration (0-based)
        var sagaCurrentAttempt = executionState.StepMetadata.TryGetValue("CurrentAttempt", out var currentAttemptObj) 
            ? (int)currentAttemptObj : 0; // 0-based: first execution = attempt 0
        var sagaMaxAttempts = executionState.StepMetadata.TryGetValue("MaxAttempts", out var maxAttemptsObj) 
            ? (int)maxAttemptsObj : 0; // 0-based: 0 retries allowed = 0 max

        _logger.LogTrace("Saga execution attempt {CurrentAttempt}/{MaxAttempts}", sagaCurrentAttempt, sagaMaxAttempts);

        // Implement saga retry loop (like step-level retries)
        var sagaRetryAttempt = 0;
        var maxSagaRetries = 0; // Will be set if retry is requested

        while (true) // Loop until success, termination, or retry exhausted
        {
            _logger.LogTrace("Saga retry loop iteration: attempt {RetryAttempt}", sagaRetryAttempt);

            // Handle mid-saga resume with resumeInfo
            var startIndex = resumeInfo?.ResumeFromStepIndex ?? 0;
            var successfulSagaSteps = resumeInfo?.CompletedSteps ?? new List<SagaExecutionStepInfo>();
            var sagaStepIndex = startIndex;

            _logger.LogDebug("Saga resume: starting from step {StartIndex} with {CompletedStepsCount} completed steps", 
                startIndex, successfulSagaSteps.Count);

            try
            {
                // Handle PostResumeAction before starting execution
                if (resumeInfo?.PostResumeAction != null)
                {
                    _logger.LogDebug("Handling post resume action: {ActionType}", resumeInfo.PostResumeAction.GetType().Name);
                    await HandlePostResumeActionAsync(resumeInfo.PostResumeAction, resumeInfo);
                }

                // Execute saga steps sequentially starting from resume point
                for (int i = startIndex; i < sagaStep.SequenceSteps.Count; i++)
                {
                    var step = sagaStep.SequenceSteps[i];
                    sagaStepIndex = i;

                    _logger.LogTrace("Executing saga step {Index}: {StepId} ({ActivityType})", 
                        sagaStepIndex, step.Id, step.ActivityType?.Name);

                    // Execute step with step-level error handling
                    var stepResult = await ExecuteSagaStepWithStepLevelErrorHandlingAsync(
                        step, context, executionState, sagaStepIndex, cancellationToken);

                    _logger.LogTrace("Saga step {Index} result: IsSuccess={IsSuccess}, PendingException={PendingException}", 
                        sagaStepIndex, stepResult.IsSuccess, executionState.PendingException?.GetType().Name ?? "null");

                    if (!stepResult.IsSuccess)
                    {
                        // Check if this is a successful suspension (special case - not a real failure)
                        if (stepResult.ErrorMessage != null && stepResult.ErrorMessage.Contains("suspended") && executionState.PendingException == null)
                        {
                            _logger.LogDebug("Saga step suspended successfully at saga level: {StepId}", step.Id);
                            return stepResult; // Return the suspension result directly - workflow should be suspended
                        }

                        _logger.LogWarning("Saga step {Index} failed: {StepId} - {ErrorMessage}", 
                            sagaStepIndex, step.Id, stepResult.ErrorMessage);
                        _logger.LogDebug("Calling HandleSagaFailureAsync with {SuccessfulStepsCount} successful steps", 
                            successfulSagaSteps.Count);
                        
                        // Handle saga-level failure with compensation
                        await HandleSagaFailureAsync(sagaStep, successfulSagaSteps, 
                            stepResult.Exception ?? new Exception(stepResult.ErrorMessage ?? "Saga step failed"), 
                            context, executionState, cancellationToken);
                        
                        _logger.LogDebug("HandleSagaFailureAsync completed for step {Index}", sagaStepIndex);
                        
                        // Clear pending exception after successful saga handling
                        // If HandleSagaFailureAsync returns normally (Continue), clear the exception
                        // If it throws (Terminate), the exception propagates anyway
                        executionState.PendingException = null;
                        
                        return new StepExecutionResult
                        {
                            IsSuccess = true, // Continue execution to allow exception handling
                            OutputData = executionState.LastStepResult
                        };
                    }

                    // Check if the step set a pending exception (from activity failure)
                    if (executionState.PendingException != null)
                    {
                        _logger.LogWarning("Saga step {Index} set pending exception: {ExceptionType} - {Message}", 
                            sagaStepIndex, executionState.PendingException.GetType().Name, executionState.PendingException.Message);
                        _logger.LogDebug("Calling HandleSagaFailureAsync with {SuccessfulStepsCount} successful steps", 
                            successfulSagaSteps.Count);
                        
                        // Handle saga-level failure with compensation
                        await HandleSagaFailureAsync(sagaStep, successfulSagaSteps, executionState.PendingException, context, executionState, cancellationToken);
                        
                        // Clear the pending exception since we handled it
                        executionState.PendingException = null;
                        
                        return new StepExecutionResult
                        {
                            IsSuccess = true,
                            OutputData = executionState.LastStepResult
                        };
                    }

                    // Track successful saga step for potential compensation
                    var sagaStepInfo = new SagaExecutionStepInfo
                    {
                        StepIndex = sagaStepIndex,
                        Step = step,
                        Result = stepResult.OutputData,
                        CompletedAt = DateTime.UtcNow
                    };
                    successfulSagaSteps.Add(sagaStepInfo);

                    _logger.LogTrace("Saga step {Index} completed: {StepId}", sagaStepIndex, step.Id);
                    sagaStepIndex++;
                }

                _logger.LogDebug("All saga steps completed successfully: {StepId}", sagaStep.Id);
                return new StepExecutionResult
                {
                    IsSuccess = true,
                    OutputData = executionState.LastStepResult
                };
            }
            catch (SagaRetryRequestedException retryRequest)
            {
                _logger.LogInformation("Saga retry requested: {OriginalException}", 
                    retryRequest.OriginalException.GetType().Name);

                // Set up retry parameters on first retry request
                if (sagaRetryAttempt == 0)
                {
                    maxSagaRetries = retryRequest.RetryPolicy.MaximumAttempts;
                    _logger.LogTrace("Saga retry setup: maxRetries={MaxRetries}", maxSagaRetries);
                }

                // Check if we can retry
                if (sagaRetryAttempt < maxSagaRetries)
                {
                    sagaRetryAttempt++;
                    _logger.LogInformation("Retrying saga: attempt {Attempt}/{MaxAttempts}", 
                        sagaRetryAttempt, maxSagaRetries);

                    // Update execution state for next attempt
                    executionState.PendingException = null;
                    executionState.LastStepResult = context.PreviousStepData;
                    // Use separate keys for saga-level retry metadata
                    executionState.StepMetadata["Saga:CurrentAttempt"] = sagaRetryAttempt;
                    executionState.StepMetadata["Saga:MaxAttempts"] = maxSagaRetries;

                    // Continue the loop (retry the entire saga)
                    continue;
                }
                else
                {
                    _logger.LogError("Saga retry exhausted: {Attempts}/{MaxAttempts}", 
                        sagaRetryAttempt, maxSagaRetries);
                    
                    // Retry exhausted, throw termination exception
                    throw new SagaTerminatedException(
                        $"Saga retry exhausted after {sagaRetryAttempt} attempts. Original: {retryRequest.OriginalException.GetType().Name}: {retryRequest.OriginalException.Message}", 
                        retryRequest.OriginalException);
                }
            }
            catch (SagaSuspendRequestedException suspendRequest)
            {
                _logger.LogInformation("Saga suspend requested: {OriginalException}", 
                    suspendRequest.OriginalException.GetType().Name);

                // Execute saga suspension with correct method signature
                var suspendResult = await SuspendSagaAsync(sagaStep, context, executionState, suspendRequest.SagaErrorConfig, suspendRequest.OriginalException, cancellationToken);
                
                // Successful suspension returns IsSuccess=false with "suspended" message
                if (!suspendResult.IsSuccess && suspendResult.ErrorMessage != null && suspendResult.ErrorMessage.Contains("suspended"))
                {
                    _logger.LogInformation("Saga suspended successfully: {Message}", suspendResult.ErrorMessage);
                    
                    // Return suspended result - workflow should be suspended (pass through the SuspendResumeExecutor result)
                    return suspendResult;
                }
                else
                {
                    _logger.LogError("Saga suspend failed: {ErrorMessage}", suspendResult.ErrorMessage);
                    
                    // Suspend failed, throw termination exception
                    throw new SagaTerminatedException(
                        $"Saga suspend failed: {suspendResult.ErrorMessage}. Original: {suspendRequest.OriginalException.GetType().Name}: {suspendRequest.OriginalException.Message}", 
                        suspendRequest.OriginalException);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saga execution failed with direct exception: {ExceptionType} - {Message}", 
                    ex.GetType().Name, ex.Message);
                
                // Handle saga failure with compensation
                await HandleSagaFailureAsync(sagaStep, successfulSagaSteps, ex, context, executionState, cancellationToken);
                
                return new StepExecutionResult
                {
                    IsSuccess = true, // Continue execution to allow exception handling
                    OutputData = executionState.LastStepResult
                };
            }
        }
    }

    /// <summary>
    /// Executes a single saga step with step-level error handling (retry, ignore, terminate)
    /// </summary>
    private async Task<StepExecutionResult> ExecuteSagaStepWithStepLevelErrorHandlingAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        int stepIndex,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        var maxRetries = GetStepMaxRetries(step);
        var retryAttempt = 0;

        _logger.LogTrace("Starting step-level retry loop: maxRetries={MaxRetries}, step={StepId}", maxRetries, step.Id);

        while (retryAttempt <= maxRetries)
        {
            _logger.LogTrace("Retry loop iteration: attempt={RetryAttempt}, maxRetries={MaxRetries}", retryAttempt, maxRetries);
            
            try
            {
                _logger.LogDebug("Executing saga step attempt {Attempt}/{MaxAttempts}: {StepId}", 
                    retryAttempt + 1, maxRetries + 1, step.Id);

                // Set retry context for the activity to use (0-based)
                executionState.StepMetadata["CurrentAttempt"] = retryAttempt; // 0-based: 0, 1, 2...
                executionState.StepMetadata["MaxAttempts"] = maxRetries; // 0-based: 0 = no retries, 1 = one retry

                // Execute the step normally using WorkflowEngine's step execution
                var stepResult = await ExecuteStepDirectlyAsync(step, context, executionState, cancellationToken);

                _logger.LogTrace("Step result: IsSuccess={IsSuccess}, PendingException={PendingException}", 
                    stepResult.IsSuccess, executionState.PendingException?.GetType().Name ?? "null");

                if (stepResult.IsSuccess && executionState.PendingException == null)
                {
                    _logger.LogDebug("Saga step succeeded on attempt {Attempt}: {StepId}", retryAttempt + 1, step.Id);
                    return stepResult;
                }

                // Check if this is a successful suspension (special case - not a real failure)
                if (!stepResult.IsSuccess && stepResult.ErrorMessage != null && 
                    stepResult.ErrorMessage.Contains("suspended") && executionState.PendingException == null)
                {
                    _logger.LogDebug("Saga step suspended successfully on attempt {Attempt}: {StepId}", retryAttempt + 1, step.Id);
                    return stepResult; // Return the suspension result directly - don't treat as failure
                }

                // Step failed or has pending exception - check step-level error handling
                var exception = executionState.PendingException ?? 
                    stepResult.Exception ?? 
                    new Exception(stepResult.ErrorMessage ?? "Step failed");

                _logger.LogWarning("Saga step failed on attempt {Attempt}: {StepId} - {ExceptionType}: {Message}", 
                    retryAttempt + 1, step.Id, exception.GetType().Name, exception.Message);

                // Check step-level error handling
                var stepErrorAction = GetStepErrorAction(step, exception);
                _logger.LogTrace("Error action: {StepErrorAction} for {ExceptionType}", stepErrorAction, exception.GetType().Name);

                switch (stepErrorAction)
                {
                    case StepErrorAction.Retry:
                        if (retryAttempt < maxRetries)
                        {
                            retryAttempt++;
                            _logger.LogInformation("Retrying saga step {StepId}: Attempt {Attempt}/{MaxAttempts}", 
                                step.Id, retryAttempt + 1, maxRetries + 1);
                            
                            // Clear pending exception for retry
                            executionState.PendingException = null;
                            continue; // Retry the step
                        }
                        else
                        {
                            _logger.LogError("Saga step retry exhausted: {StepId} - Max retries ({MaxRetries}) exceeded", 
                                step.Id, maxRetries);
                            // Fall through to terminate (propagate to saga level)
                            goto case StepErrorAction.Terminate;
                        }

                    case StepErrorAction.Ignore:
                        _logger.LogInformation("Ignoring saga step error: {StepId} - Continuing as if step succeeded", step.Id);
                        // Clear pending exception and return success
                        executionState.PendingException = null;
                        return new StepExecutionResult
                        {
                            IsSuccess = true,
                            OutputData = executionState.LastStepResult
                        };

                    case StepErrorAction.Terminate:
                    default:
                        _logger.LogWarning("Terminating saga step: {StepId} - Propagating to saga level", step.Id);
                        // Propagate exception to saga level (don't clear pending exception)
                        if (executionState.PendingException == null)
                        {
                            executionState.PendingException = exception;
                        }
                        return new StepExecutionResult
                        {
                            IsSuccess = false,
                            ErrorMessage = exception.Message,
                            ErrorStackTrace = exception.StackTrace,
                            Exception = exception
                        };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saga step threw direct exception on attempt {Attempt}: {StepId}", 
                    retryAttempt + 1, step.Id);

                // Check step-level error handling for direct exceptions
                var stepErrorAction = GetStepErrorAction(step, ex);

                switch (stepErrorAction)
                {
                    case StepErrorAction.Retry:
                        if (retryAttempt < maxRetries)
                        {
                            retryAttempt++;
                            _logger.LogInformation("Retrying saga step after exception {StepId}: Attempt {Attempt}/{MaxAttempts}", 
                                step.Id, retryAttempt + 1, maxRetries + 1);
                            continue; // Retry the step
                        }
                        else
                        {
                            _logger.LogError("Saga step retry exhausted after exception: {StepId} - Max retries ({MaxRetries}) exceeded", 
                                step.Id, maxRetries);
                            // Fall through to terminate
                            goto case StepErrorAction.Terminate;
                        }

                    case StepErrorAction.Ignore:
                        _logger.LogInformation("Ignoring saga step exception: {StepId} - Continuing as if step succeeded", step.Id);
                        return new StepExecutionResult
                        {
                            IsSuccess = true,
                            OutputData = executionState.LastStepResult
                        };

                    case StepErrorAction.Terminate:
                    default:
                        _logger.LogError("Terminating saga step after exception: {StepId} - Propagating to saga level", step.Id);
                        return new StepExecutionResult
                        {
                            IsSuccess = false,
                            ErrorMessage = ex.Message,
                            ErrorStackTrace = ex.StackTrace,
                            Exception = ex
                        };
                }
            }
        }

        // Should never reach here, but just in case
        return new StepExecutionResult
        {
            IsSuccess = false,
            ErrorMessage = "Unexpected end of retry loop",
            Exception = new InvalidOperationException("Unexpected end of retry loop")
        };
    }

    /// <summary>
    /// Executes a step directly (delegates to appropriate executor based on step type)
    /// </summary>
    private async Task<StepExecutionResult> ExecuteStepDirectlyAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Executing saga step: {StepType} - {StepId}", step.StepType, step.Id);

        // Handle different step types in sagas
        switch (step.StepType)
        {
            case WorkflowStepType.Activity:
                return await ExecuteActivityStepInSagaAsync(step, context, executionState, cancellationToken);
                
            case WorkflowStepType.SuspendResume:
                return await ExecuteSuspendResumeStepInSagaAsync(step, context, executionState, cancellationToken);
                
            case WorkflowStepType.Conditional:
                return await ExecuteConditionalStepInSagaAsync(step, context, executionState, cancellationToken);
                
            default:
                throw new NotSupportedException($"Step type {step.StepType} is not supported in sagas yet.");
        }
    }

    /// <summary>
    /// Executes an activity step within a saga
    /// </summary>
    private async Task<StepExecutionResult> ExecuteActivityStepInSagaAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        if (step.ActivityType == null)
            throw new InvalidOperationException($"Activity type is null for saga step {step.Id}");

        _logger.LogDebug("Executing saga activity: {ActivityType}", step.ActivityType.Name);

        try
        {
            // Use reflection to create the correct generic type based on step.PreviousStepDataType
            var workflowDataType = typeof(TWorkflowData);
            var previousStepDataType = step.PreviousStepDataType ?? typeof(object);

        // Get the generic method ExecuteTypedActivityAsync<TWorkflowData, TPreviousStepData>
        var executeMethod = typeof(IActivityExecutor).GetMethod(nameof(IActivityExecutor.ExecuteTypedActivityAsync));
        if (executeMethod == null)
            throw new InvalidOperationException("ExecuteTypedActivityAsync method not found");

        var genericExecuteMethod = executeMethod.MakeGenericMethod(workflowDataType, previousStepDataType);

        // Create the request using reflection
        var requestType = typeof(TypedActivityExecutionRequest<,>).MakeGenericType(workflowDataType, previousStepDataType);
            var request = Activator.CreateInstance(requestType);

            // Convert the actual runtime result to the expected previous step data type
            object? convertedPreviousStepData;
            if (executionState.LastStepResult != null && previousStepDataType != typeof(object))
            {
                if (previousStepDataType.IsAssignableFrom(executionState.LastStepResult.GetType()))
                {
                    convertedPreviousStepData = executionState.LastStepResult;
                }
                else
                {
                    try
                    {
                        convertedPreviousStepData = Activator.CreateInstance(previousStepDataType);
                    }
                    catch
                    {
                        convertedPreviousStepData = null;
                    }
                }
            }
            else if (previousStepDataType != typeof(object))
            {
                try
                {
                    convertedPreviousStepData = Activator.CreateInstance(previousStepDataType);
                }
                catch
                {
                    convertedPreviousStepData = null;
                }
            }
            else
            {
                convertedPreviousStepData = executionState.LastStepResult;
            }

            // Set properties using reflection
            requestType.GetProperty("ActivityType")?.SetValue(request, step.ActivityType);
            requestType.GetProperty("WorkflowData")?.SetValue(request, context.WorkflowData);
            requestType.GetProperty("PreviousStepData")?.SetValue(request, convertedPreviousStepData);
            requestType.GetProperty("InputMappings")?.SetValue(request, step.InputMappings);
            requestType.GetProperty("OutputMappings")?.SetValue(request, step.OutputMappings);
            requestType.GetProperty("Step")?.SetValue(request, step);
            requestType.GetProperty("ExecutionContext")?.SetValue(request, context);
            requestType.GetProperty("CancellationToken")?.SetValue(request, cancellationToken);
            requestType.GetProperty("CatchException")?.SetValue(request, null);
            requestType.GetProperty("StepMetadata")?.SetValue(request, executionState.StepMetadata);

            // Execute using activity executor
            var resultTask = (Task)genericExecuteMethod.Invoke(_activityExecutor, [request])!;
            await resultTask;

            // Get the result using reflection
            var resultProperty = resultTask.GetType().GetProperty("Result");
            var result = resultProperty?.GetValue(resultTask) as TypedActivityExecutionResult<object>;

            if (result == null)
                throw new InvalidOperationException("Failed to get activity execution result");

            if (result.IsSuccess)
            {
                _logger.LogDebug("Saga activity completed: {ActivityType}", step.ActivityType.Name);

                // Update execution state
                executionState.LastStepResult = result.OutputData;

                return new StepExecutionResult
                {
                    IsSuccess = true,
                    OutputData = result.OutputData
                };
            }
            else
            {
                _logger.LogError("Saga activity failed: {ActivityType} - {ErrorMessage}", 
                    step.ActivityType.Name, result.ErrorMessage);

                // Set pending exception for step-level error handling
                executionState.PendingException = result.Exception ?? new Exception(result.ErrorMessage ?? "Activity failed");

                return new StepExecutionResult
                {
                    IsSuccess = false,
                    ErrorMessage = result.ErrorMessage,
                    Exception = result.Exception
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saga activity threw exception: {ActivityType}", step.ActivityType?.Name);

            // Set pending exception for step-level error handling
            executionState.PendingException = ex;

            return new StepExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.StackTrace,
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Executes a suspend/resume step within a saga (same behavior as error handler suspend)
    /// </summary>
    private async Task<StepExecutionResult> ExecuteSuspendResumeStepInSagaAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Executing saga suspend/resume step: {StepId}", step.Id);

        try
        {
            // Delegate to SuspendResumeExecutor for unified suspend/resume logic
            // This ensures direct suspend steps and error handler suspends behave identically
            var result = await _suspendResumeExecutor.ExecuteSuspendResumeStepAsync(
                step, context, executionState, cancellationToken);

            _logger.LogDebug("Saga suspend/resume step result: IsSuccess={IsSuccess}, ErrorMessage={ErrorMessage}", 
                result.IsSuccess, result.ErrorMessage);

            // For direct suspend steps in saga, just pass through the result
            // The SuspendResumeExecutor handles the actual suspension logic properly
            // Don't treat suspension as an error - just let it flow through naturally
            if (!result.IsSuccess && result.ErrorMessage != null && result.ErrorMessage.Contains("suspended"))
            {
                _logger.LogInformation("Saga suspend successful: {Message}", result.ErrorMessage);
                // Just return the suspend result - don't throw an exception
                return result;
            }

            // Update execution state if needed (for resume data continuity)
            if (result.IsSuccess && result.OutputData != null)
            {
                executionState.LastStepResult = result.OutputData;
            }

            return result;
        }
        catch (SagaSuspendRequestedException)
        {
            // Re-throw saga suspend requests (expected behavior)
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saga suspend/resume step threw exception: {StepId}", step.Id);

            // Set pending exception for step-level error handling
            executionState.PendingException = ex;

            return new StepExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.StackTrace,
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Executes a conditional step (OutcomeOn pattern) within a saga
    /// </summary>
    private async Task<StepExecutionResult> ExecuteConditionalStepInSagaAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Executing saga conditional step (OutcomeOn): {StepId}", step.Id);

        try
        {
            // Extract outcome configuration from step metadata
            if (!step.StepMetadata.TryGetValue("PropertySelector", out var propertySelectorObj) ||
                propertySelectorObj is not Func<object, object?> propertySelector)
            {
                throw new InvalidOperationException($"PropertySelector not found in conditional step metadata: {step.Id}");
            }

            if (!step.StepMetadata.TryGetValue("OutcomeBranches", out var outcomeBranchesObj))
            {
                throw new InvalidOperationException($"OutcomeBranches not found in conditional step metadata: {step.Id}");
            }

            // Get the property value to evaluate
            object? propertyValue;
            try
            {
                // Create context for property evaluation
                var evaluationContext = CreateEvaluationContext(context, executionState, step);
                propertyValue = propertySelector(evaluationContext);
                _logger.LogTrace("Conditional property evaluated to: {PropertyValue}", propertyValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate conditional property in saga step: {StepId}", step.Id);
                throw new InvalidOperationException($"Failed to evaluate conditional property: {ex.Message}", ex);
            }

            // Find matching outcome branch using reflection (since OutcomeBranch is internal to SagaBuilder)
            var outcomeBranchesType = outcomeBranchesObj.GetType();
            if (!outcomeBranchesType.IsGenericType || !outcomeBranchesType.GetGenericTypeDefinition().IsAssignableFrom(typeof(List<>)))
            {
                throw new InvalidOperationException($"OutcomeBranches is not a List: {outcomeBranchesType}");
            }

            var outcomeBranches = (System.Collections.IList)outcomeBranchesObj;
            object? selectedBranch = null;

            // Find the matching branch
            foreach (var branch in outcomeBranches)
            {
                var branchValueProperty = branch.GetType().GetProperty("Value");
                var isDefaultProperty = branch.GetType().GetProperty("IsDefault");
                
                if (branchValueProperty != null && isDefaultProperty != null)
                {
                    var branchValue = branchValueProperty.GetValue(branch);
                    var isDefault = (bool)(isDefaultProperty.GetValue(branch) ?? false);

                    if (!isDefault && Equals(propertyValue, branchValue))
                    {
                        selectedBranch = branch;
                        _logger.LogTrace("Found matching outcome branch for value: {PropertyValue}", propertyValue);
                        break;
                    }
                }
            }

            // If no match found, try to find default branch
            if (selectedBranch == null)
            {
                step.StepMetadata.TryGetValue("DefaultBranch", out var defaultBranchObj);
                if (defaultBranchObj != null)
                {
                    selectedBranch = defaultBranchObj;
                    _logger.LogTrace("Using default outcome branch");
                }
            }

            if (selectedBranch == null)
            {
                _logger.LogWarning("No matching outcome branch found for value: {PropertyValue}", propertyValue);
                // Return success with no action taken
                return new StepExecutionResult
                {
                    IsSuccess = true,
                    OutputData = executionState.LastStepResult
                };
            }

            // Execute the selected branch steps
            var branchStepsProperty = selectedBranch.GetType().GetProperty("Steps");
            if (branchStepsProperty?.GetValue(selectedBranch) is List<WorkflowStep> branchSteps)
            {
                _logger.LogTrace("Executing {StepCount} steps in selected outcome branch", branchSteps.Count);

                // Execute each step in the branch sequentially
                foreach (var branchStep in branchSteps)
                {
                    var stepResult = await ExecuteStepDirectlyAsync(branchStep, context, executionState, cancellationToken);
                    
                    if (!stepResult.IsSuccess)
                    {
                        _logger.LogError("Outcome branch step failed: {StepId} - {ErrorMessage}", 
                            branchStep.Id, stepResult.ErrorMessage);
                        return stepResult;
                    }

                    // Update execution state for next step
                    if (stepResult.OutputData != null)
                    {
                        executionState.LastStepResult = stepResult.OutputData;
                    }
                }

                _logger.LogTrace("All outcome branch steps completed successfully");
                return new StepExecutionResult
                {
                    IsSuccess = true,
                    OutputData = executionState.LastStepResult
                };
            }
            else
            {
                _logger.LogWarning("No steps found in selected outcome branch");
                return new StepExecutionResult
                {
                    IsSuccess = true,
                    OutputData = executionState.LastStepResult
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saga conditional step failed: {StepId}", step.Id);

            // Set pending exception for step-level error handling
            executionState.PendingException = ex;

            return new StepExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.StackTrace,
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Creates evaluation context for conditional property evaluation
    /// </summary>
    private object CreateEvaluationContext<TWorkflowData>(
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        WorkflowStep conditionalStep)
        where TWorkflowData : class
    {
        // Get the previous step data type from the conditional step metadata
        var previousStepDataType = conditionalStep.PreviousStepDataType ?? typeof(object);
        
        _logger.LogTrace("Creating evaluation context: WorkflowData={WorkflowDataType}, PreviousStep={PreviousStepType}", 
            typeof(TWorkflowData).Name, previousStepDataType.Name);

        // Create a dynamic context that matches the expected generic types
        var contextType = typeof(WorkflowContext<,>).MakeGenericType(typeof(TWorkflowData), previousStepDataType);
        var evaluationContext = Activator.CreateInstance(contextType);

        // Set WorkflowData property
        var workflowDataProperty = contextType.GetProperty("WorkflowData");
        workflowDataProperty?.SetValue(evaluationContext, context.WorkflowData);

        // Set PreviousStep property with current execution state
        var previousStepProperty = contextType.GetProperty("PreviousStep");
        
        // Convert the last step result to the expected previous step type if needed
        object? convertedPreviousStepData;
        if (executionState.LastStepResult != null && previousStepDataType != typeof(object))
        {
            if (previousStepDataType.IsAssignableFrom(executionState.LastStepResult.GetType()))
            {
                convertedPreviousStepData = executionState.LastStepResult;
            }
            else
            {
                try
                {
                    // Create an instance of the expected type and try to populate it
                    convertedPreviousStepData = Activator.CreateInstance(previousStepDataType);
                    
                    // If it's an activity result, try to copy properties
                    if (executionState.LastStepResult != null)
                    {
                        var sourceType = executionState.LastStepResult.GetType();
                        var targetType = previousStepDataType;
                        
                        foreach (var sourceProperty in sourceType.GetProperties())
                        {
                            var targetProperty = targetType.GetProperty(sourceProperty.Name);
                            if (targetProperty != null && targetProperty.CanWrite && 
                                targetProperty.PropertyType.IsAssignableFrom(sourceProperty.PropertyType))
                            {
                                var value = sourceProperty.GetValue(executionState.LastStepResult);
                                targetProperty.SetValue(convertedPreviousStepData, value);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to convert previous step data from {SourceType} to {TargetType}, using source object", 
                        executionState.LastStepResult?.GetType().Name, previousStepDataType.Name);
                    convertedPreviousStepData = executionState.LastStepResult;
                }
            }
        }
        else
        {
            convertedPreviousStepData = executionState.LastStepResult;
        }
        
        previousStepProperty?.SetValue(evaluationContext, convertedPreviousStepData);

        _logger.LogTrace("Evaluation context created successfully with PreviousStep of type: {ActualType}", 
            convertedPreviousStepData?.GetType().Name ?? "null");

        return evaluationContext!;
    }

    /// <summary>
    /// Gets the maximum number of retries for a step from its error handling configuration
    /// </summary>
    private int GetStepMaxRetries(WorkflowStep step)
    {
        _logger.LogTrace("Getting maximum retries for step: {StepId}", step.Id);
        
        // Check step metadata for step-level error handling configuration
        if (step.StepMetadata.TryGetValue("StepErrorHandlers", out var handlersObj))
        {
            if (handlersObj is List<StepErrorHandlerInfo> handlers)
            {
                // Return the maximum retry count from all handlers
                var maxRetries = handlers.Max(h => h.RetryPolicy?.MaximumAttempts ?? 0);
                _logger.LogTrace("Maximum retries for step {StepId}: {MaxRetries}", step.Id, maxRetries);
                return maxRetries;
            }
        }

        // Default: no retries
        return 0;
    }

    /// <summary>
    /// Gets the retry policy for a step and exception type
    /// </summary>
    private RetryPolicy? GetStepRetryPolicy(WorkflowStep step, Exception exception)
    {
        // Check step metadata for step-level error handling configuration
        if (step.StepMetadata.TryGetValue("StepErrorHandlers", out var handlersObj) &&
            handlersObj is List<StepErrorHandlerInfo> handlers)
        {
            // Find the most specific handler for this exception type
            var matchingHandler = handlers
                .Where(h => h.ExceptionType.IsAssignableFrom(exception.GetType()))
                .OrderBy(h => GetTypeHierarchyDepth(h.ExceptionType, exception.GetType()))
                .FirstOrDefault();

            return matchingHandler?.RetryPolicy;
        }

        return null;
    }

    /// <summary>
    /// Gets the step-level error action for the given exception
    /// </summary>
    private StepErrorAction GetStepErrorAction(WorkflowStep step, Exception exception)
    {
        // Check step metadata for step-level error handling configuration
        if (step.StepMetadata.TryGetValue("StepErrorHandlers", out var handlersObj) &&
            handlersObj is List<StepErrorHandlerInfo> handlers)
        {
            // Find the most specific handler for this exception type
            var matchingHandler = handlers
                .Where(h => h.ExceptionType.IsAssignableFrom(exception.GetType()))
                .OrderBy(h => GetTypeHierarchyDepth(h.ExceptionType, exception.GetType()))
                .FirstOrDefault();

            if (matchingHandler != null)
            {
                _logger.LogDebug("Found step-level error handler: {HandlerAction} for {ExceptionType}", 
                    matchingHandler.HandlerAction, exception.GetType().Name);
                return matchingHandler.HandlerAction;
            }
        }

        // Default: terminate (propagate to saga level)
        _logger.LogDebug("No step-level error handler found for {ExceptionType}, defaulting to Terminate", 
            exception.GetType().Name);
        return StepErrorAction.Terminate;
    }

    /// <summary>
    /// Gets the depth of type hierarchy to find the most specific exception handler
    /// </summary>
    private int GetTypeHierarchyDepth(Type handlerType, Type exceptionType)
    {
        var depth = 0;
        var currentType = exceptionType;

        while (currentType != null && currentType != handlerType)
        {
            depth++;
            currentType = currentType.BaseType;
        }

        return currentType == handlerType ? depth : int.MaxValue;
    }

    /// <summary>
    /// Handles saga failure by executing compensation and applying error handling strategy
    /// </summary>
    private async Task HandleSagaFailureAsync<TWorkflowData>(
        WorkflowStep sagaStep,
        List<SagaExecutionStepInfo> successfulSteps,
        Exception exception,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogInformation("Handling saga failure: {ExceptionType} - {Message}, SuccessfulSteps: {StepCount}", 
            exception.GetType().Name, exception.Message, successfulSteps.Count);

        try
        {
            // Find the appropriate saga-level error handler for this exception type
            var errorHandler = FindSagaErrorHandler(sagaStep, exception);
            
            if (errorHandler != null)
            {
                _logger.LogInformation("Found saga error handler: {HandlerType} for {ExceptionType}", 
                    errorHandler.Handler.GetType().Name, exception.GetType().Name);
                
                // Get saga error configuration from the handler
                var sagaErrorConfig = GetSagaErrorConfiguration(errorHandler);
                
                if (sagaErrorConfig != null)
                {
                    _logger.LogInformation("Applying saga error config: Strategy={Strategy}, Action={Action}", 
                        sagaErrorConfig.CompensationStrategy, sagaErrorConfig.ContinuationAction);
                    
                    // Execute compensation based on strategy
                    await ExecuteCompensationAsync(sagaErrorConfig, successfulSteps, context, cancellationToken);
                    
                    // Apply continuation action
                    await ApplySagaContinuationActionAsync(sagaErrorConfig, exception, sagaStep, context, executionState, cancellationToken);
                    return;
                }
                else
                {
                    _logger.LogWarning("No saga error config found, applying default behavior");
                    await ApplyDefaultSagaBehaviorAsync(successfulSteps, exception, context, cancellationToken);
                }
            }
            else
            {
                _logger.LogWarning("No saga error handler found for {ExceptionType}, applying default behavior", 
                    exception.GetType().Name);
                await ApplyDefaultSagaBehaviorAsync(successfulSteps, exception, context, cancellationToken);
            }
        }
        catch (SagaTerminatedException sagaTerminatedException)
        {
            _logger.LogInformation("Saga terminated by explicit handler: {Message}", sagaTerminatedException.Message);
            throw;
        }
        catch (Exception handlerEx)
        {
            _logger.LogError(handlerEx, "Exception in HandleSagaFailureAsync: {ErrorMessage}", handlerEx.Message);
            throw;
        }
    }

    /// <summary>
    /// Finds the appropriate saga-level error handler for the given exception type
    /// </summary>
    private ErrorHandler? FindSagaErrorHandler(WorkflowStep sagaStep, Exception exception)
    {
        // Look for error handlers in catch blocks
        foreach (var catchBlock in sagaStep.CatchBlocks)
        {
            if (catchBlock.StepMetadata.TryGetValue("SagaErrorConfig", out var configObj))
            {
                // Check if this catch block can handle the exception type
                if (catchBlock.ExceptionType == null || 
                    catchBlock.ExceptionType.IsAssignableFrom(exception.GetType()))
                {
                    return new ErrorHandler
                    {
                        ExceptionType = catchBlock.ExceptionType ?? typeof(Exception),
                        Handler = new SagaErrorConfigHandler { SagaErrorConfig = (SagaErrorConfiguration)configObj }
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets saga error configuration from error handler
    /// </summary>
    private SagaErrorConfiguration? GetSagaErrorConfiguration(ErrorHandler errorHandler)
    {
        if (errorHandler.Handler is SagaErrorConfigHandler configHandler)
        {
            return configHandler.SagaErrorConfig;
        }

        // Handle RetryErrorHandler from SagaContinuationBuilder.ThenRetry()
        if (errorHandler.Handler is RetryErrorHandler retryHandler)
        {
            // Create saga error configuration from retry handler
            return new SagaErrorConfiguration
            {
                CompensationStrategy = retryHandler.PreExecute?.Strategy ?? CompensationStrategy.CompensateAll,
                ContinuationAction = SagaContinuationAction.Retry,
                RetryPolicy = retryHandler.RetryPolicy,
                CompensationTargetType = retryHandler.PreExecute?.CompensationTargetType
            };
        }

        // Handle SuspensionErrorHandler for suspend/resume scenarios
        var handlerType = errorHandler.Handler.GetType();
        if (handlerType.IsGenericType && handlerType.GetGenericTypeDefinition() == typeof(SuspensionErrorHandler<>))
        {
            _logger.LogTrace("Found SuspensionErrorHandler: {HandlerTypeName}", handlerType.Name);
            
            // Get SagaErrorConfig property using reflection
            var suspensionSagaErrorConfigProp = handlerType.GetProperty("SagaErrorConfig");
            if (suspensionSagaErrorConfigProp != null)
            {
                var suspensionSagaErrorConfig = suspensionSagaErrorConfigProp.GetValue(errorHandler.Handler) as SagaErrorConfiguration;
                if (suspensionSagaErrorConfig != null)
                {
                    _logger.LogTrace("Found SagaErrorConfig from SuspensionErrorHandler: Action={Action}", suspensionSagaErrorConfig.ContinuationAction);
                    return suspensionSagaErrorConfig;
                }
            }
            
            // Extract suspend configuration from SuspensionErrorHandler properties
            var eventTypeProperty = handlerType.GetProperty("EventType");
            var resumeConditionProperty = handlerType.GetProperty("ResumeCondition");
            var postResumeActionProperty = handlerType.GetProperty("PostResumeAction");
            var preExecuteProperty = handlerType.GetProperty("PreExecute");
            
            var eventType = eventTypeProperty?.GetValue(errorHandler.Handler) as Type;
            var resumeCondition = resumeConditionProperty?.GetValue(errorHandler.Handler);
            var postResumeAction = postResumeActionProperty?.GetValue(errorHandler.Handler);
            var preExecute = preExecuteProperty?.GetValue(errorHandler.Handler) as CompensationErrorHandler;
            
            _logger.LogTrace("Extracted from SuspensionErrorHandler: EventType={EventType}, PostResumeAction={PostResumeAction}", 
                eventType?.Name, postResumeAction?.GetType().Name);
            
            // Create SagaErrorConfiguration with suspend metadata
            var sagaErrorConfig = new SagaErrorConfiguration
            {
                CompensationStrategy = preExecute?.Strategy ?? CompensationStrategy.CompensateAll,
                ContinuationAction = SagaContinuationAction.Suspend,
                RetryPolicy = null,
                CompensationTargetType = preExecute?.CompensationTargetType,
                Metadata = new Dictionary<string, object>()
            };
            
            // Store suspend configuration in metadata
            if (eventType != null)
            {
                sagaErrorConfig.Metadata["ResumeEventType"] = eventType;
            }
            
            if (postResumeAction != null)
            {
                sagaErrorConfig.Metadata["PostResumeAction"] = postResumeAction;
            }
            
            if (resumeCondition != null)
            {
                sagaErrorConfig.Metadata["ResumeCondition"] = resumeCondition;
            }
            
            _logger.LogTrace("Created SagaErrorConfig with suspend metadata: {MetadataCount} entries", sagaErrorConfig.Metadata.Count);
            return sagaErrorConfig;
        }

        // Handle other continuation handlers
        if (errorHandler.Handler is TerminationErrorHandler terminationHandler)
        {
            return terminationHandler.SagaErrorConfig ?? new SagaErrorConfiguration
            {
                CompensationStrategy = terminationHandler.PreExecute?.Strategy ?? CompensationStrategy.CompensateAll,
                ContinuationAction = SagaContinuationAction.Terminate,
                CompensationTargetType = terminationHandler.PreExecute?.CompensationTargetType
            };
        }

        if (errorHandler.Handler is ContinuationErrorHandler continuationHandler)
        {
            return continuationHandler.SagaErrorConfig ?? new SagaErrorConfiguration
            {
                CompensationStrategy = continuationHandler.PreExecute?.Strategy ?? CompensationStrategy.CompensateAll,
                ContinuationAction = SagaContinuationAction.Continue,
                CompensationTargetType = continuationHandler.PreExecute?.CompensationTargetType
            };
        }

        // Try to extract from other handler types using reflection
        var sagaErrorConfigProperty = handlerType.GetProperty("SagaErrorConfig");
        if (sagaErrorConfigProperty != null)
        {
            return sagaErrorConfigProperty.GetValue(errorHandler.Handler) as SagaErrorConfiguration;
        }

        return null;
    }

    /// <summary>
    /// Executes compensation based on the compensation strategy
    /// </summary>
    private async Task ExecuteCompensationAsync<TWorkflowData>(
        SagaErrorConfiguration sagaErrorConfig,
        List<SagaExecutionStepInfo> successfulSteps,
        StepExecutionContext<TWorkflowData> context,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogInformation("Executing compensation: Strategy={Strategy}, SuccessfulSteps={StepCount}", 
            sagaErrorConfig.CompensationStrategy, successfulSteps.Count);

        switch (sagaErrorConfig.CompensationStrategy)
        {
            case CompensationStrategy.CompensateAll:
                await CompensateAllStepsAsync(successfulSteps, context, cancellationToken);
                break;
                
            case CompensationStrategy.CompensateUpTo:
                await CompensateUpToStepAsync(successfulSteps, sagaErrorConfig.CompensationTargetType, context, cancellationToken);
                break;
                
            case CompensationStrategy.None:
                _logger.LogInformation("No compensation: Strategy is None");
                break;
                
            default:
                _logger.LogWarning("Unknown compensation strategy: {Strategy}, defaulting to CompensateAll", 
                    sagaErrorConfig.CompensationStrategy);
                await CompensateAllStepsAsync(successfulSteps, context, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Compensates all successful saga steps in reverse order
    /// </summary>
    private async Task CompensateAllStepsAsync<TWorkflowData>(
        List<SagaExecutionStepInfo> successfulSteps,
        StepExecutionContext<TWorkflowData> context,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogInformation("Compensating all steps: {StepCount} steps", successfulSteps.Count);

        // Compensate in reverse order (LIFO)
        for (int i = successfulSteps.Count - 1; i >= 0; i--)
        {
            var stepInfo = successfulSteps[i];
            _logger.LogTrace("Compensating step {Index}: {ActivityType}", i, stepInfo.Step.ActivityType?.Name);
            await ExecuteStepCompensationAsync(stepInfo, context, cancellationToken);
        }
        
        _logger.LogDebug("All step compensations completed");
    }

    /// <summary>
    /// Compensates steps up to (and including) the specified target step type
    /// </summary>
    private async Task CompensateUpToStepAsync<TWorkflowData>(
        List<SagaExecutionStepInfo> successfulSteps,
        Type? targetStepType,
        StepExecutionContext<TWorkflowData> context,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        if (targetStepType == null)
        {
            _logger.LogWarning("Compensate up to: No target step type specified, compensating all");
            await CompensateAllStepsAsync(successfulSteps, context, cancellationToken);
            return;
        }

        _logger.LogInformation("Compensating up to: {TargetType}", targetStepType.Name);

        // Find the target step index (look for the LAST occurrence, in case there are multiple steps of same type)
        var targetStepIndex = -1;
        for (int i = successfulSteps.Count - 1; i >= 0; i--)
        {
            if (successfulSteps[i].Step.ActivityType == targetStepType)
            {
                targetStepIndex = i;
                _logger.LogTrace("Found target step at index {Index}", i);
                break;
            }
        }

        if (targetStepIndex == -1)
        {
            _logger.LogWarning("Compensate up to: Target step type {TargetType} not found, compensating all", 
                targetStepType.Name);
            await CompensateAllStepsAsync(successfulSteps, context, cancellationToken);
            return;
        }

        // Compensate from the target step index down to 0 (reverse order - LIFO)
        for (int i = targetStepIndex; i >= 0; i--)
        {
            var stepInfo = successfulSteps[i];
            _logger.LogTrace("Compensating step {Index}: {ActivityType}", i, stepInfo.Step.ActivityType?.Name);
            await ExecuteStepCompensationAsync(stepInfo, context, cancellationToken);
        }
        
        _logger.LogDebug("Compensate up to {TargetType} completed", targetStepType.Name);
    }

    /// <summary>
    /// Executes compensation for a single saga step
    /// </summary>
    private async Task ExecuteStepCompensationAsync<TWorkflowData>(
        SagaExecutionStepInfo stepInfo,
        StepExecutionContext<TWorkflowData> context,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogInformation("Compensating step {Index}: {ActivityType}", 
            stepInfo.StepIndex, stepInfo.Step.ActivityType?.Name);

        // Get compensation activities from step metadata
        if (stepInfo.Step.StepMetadata.TryGetValue("CompensationActivities", out var compensationObj))
        {
            if (compensationObj is List<CompensationActivityInfo> compensationActivities)
            {
                _logger.LogTrace("Found {Count} compensation activities", compensationActivities.Count);
                
                foreach (var compensationActivity in compensationActivities)
                {
                    try
                    {
                        await ExecuteCompensationActivityAsync(compensationActivity, context, stepInfo, cancellationToken);
                        _logger.LogTrace("Compensation activity completed: {ActivityType}", compensationActivity.ActivityType.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Compensation failed for step {Index}: {ActivityType}", 
                            stepInfo.StepIndex, compensationActivity.ActivityType.Name);
                        // Continue with other compensations even if one fails
                    }
                }
            }
            else
            {
                _logger.LogTrace("CompensationActivities is not List<CompensationActivityInfo>: {ActualType}", compensationObj?.GetType().Name);
            }
        }
        else
        {
            _logger.LogWarning("No compensation activities found for step {Index}: {ActivityType}", 
                stepInfo.StepIndex, stepInfo.Step.ActivityType?.Name);
        }
        
        _logger.LogTrace("Step compensation execution completed for step {Index}", stepInfo.StepIndex);
    }

    /// <summary>
    /// Executes a single compensation activity
    /// </summary>
    private async Task ExecuteCompensationActivityAsync<TWorkflowData>(
        CompensationActivityInfo compensationActivity,
        StepExecutionContext<TWorkflowData> context,
        SagaExecutionStepInfo stepInfo,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogTrace("Executing compensation activity: {ActivityType} for step {StepIndex}", 
            compensationActivity.ActivityType.Name, stepInfo.StepIndex);

        // Use reflection to create the correct generic type based on workflow data type
        var workflowDataType = typeof(TWorkflowData);
        // Use the current step's ActivityType as TCurrentStepData for CompensationContext
        var currentStepDataType = stepInfo.Step.ActivityType ?? typeof(object);
        
        _logger.LogTrace("Using currentStepDataType: {TypeName}", currentStepDataType.Name);

        // Get the generic method ExecuteTypedActivityAsync<TWorkflowData, TPreviousStepData>
        var executeMethod = typeof(IActivityExecutor).GetMethod(nameof(IActivityExecutor.ExecuteTypedActivityAsync));
        if (executeMethod == null)
            throw new InvalidOperationException("ExecuteTypedActivityAsync method not found");

        var genericExecuteMethod = executeMethod.MakeGenericMethod(workflowDataType, currentStepDataType);

        // Create the request using reflection
        var requestType = typeof(TypedActivityExecutionRequest<,>).MakeGenericType(workflowDataType, currentStepDataType);
        var request = Activator.CreateInstance(requestType);

        // For compensation, we want the current step data (step being compensated)
        // This creates CompensationContext<TWorkflowData, TCurrentStepData>
        object? currentStepResult = stepInfo.Result;

        // Create a proper WorkflowStep for the compensation activity
        var compensationStep = new WorkflowStep
        {
            Id = Guid.NewGuid().ToString(),
            ActivityType = compensationActivity.ActivityType,
            StepType = WorkflowStepType.Activity,
            InputMappings = compensationActivity.InputMappings ?? [],
            OutputMappings = compensationActivity.OutputMappings ?? [],
            PreviousStepDataType = currentStepDataType,
            StepMetadata = new Dictionary<string, object>
            {
                ["IsCompensationActivity"] = true,
                ["OriginalStepIndex"] = stepInfo.StepIndex
            }
        };


        // Set properties using reflection
        requestType.GetProperty("ActivityType")?.SetValue(request, compensationActivity.ActivityType);
        requestType.GetProperty("WorkflowData")?.SetValue(request, context.WorkflowData);
        requestType.GetProperty("PreviousStepData")?.SetValue(request, currentStepResult); 
        requestType.GetProperty("InputMappings")?.SetValue(request, compensationActivity.InputMappings);
        requestType.GetProperty("OutputMappings")?.SetValue(request, compensationActivity.OutputMappings);
        requestType.GetProperty("Step")?.SetValue(request, compensationStep);
        requestType.GetProperty("ExecutionContext")?.SetValue(request, context);
        requestType.GetProperty("CancellationToken")?.SetValue(request, cancellationToken);
        requestType.GetProperty("CatchException")?.SetValue(request, null);
        requestType.GetProperty("IsCompensationActivity")?.SetValue(request, true); 


        // Execute using activity executor
        var resultTask = (Task)genericExecuteMethod.Invoke(_activityExecutor, new[] { request })!;
        await resultTask;

        // Get the result using reflection
        var resultProperty = resultTask.GetType().GetProperty("Result");
        var result = resultProperty?.GetValue(resultTask) as TypedActivityExecutionResult<object>;

        if (result == null)
            throw new InvalidOperationException("Failed to get compensation activity execution result");

        if (!result.IsSuccess)
        {
            _logger.LogError("Compensation activity failed: {ActivityType} - {ErrorMessage}", 
                compensationActivity.ActivityType.Name, result.ErrorMessage);
            throw result.Exception ?? new Exception(result.ErrorMessage ?? "Compensation activity failed");
        }

        _logger.LogTrace("Compensation activity completed: {ActivityType}", 
            compensationActivity.ActivityType.Name);
    }

    /// <summary>
    /// Applies the saga continuation action after compensation
    /// </summary>
    private async Task ApplySagaContinuationActionAsync<TWorkflowData>(
        SagaErrorConfiguration sagaErrorConfig,
        Exception originalException,
        WorkflowStep sagaStep,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogInformation("Applying continuation action: {Action}", sagaErrorConfig.ContinuationAction);

        switch (sagaErrorConfig.ContinuationAction)
        {
            case SagaContinuationAction.Terminate:
                _logger.LogInformation("Terminating saga: Throwing SagaTerminatedException");
                throw new SagaTerminatedException($"Saga terminated due to {originalException.GetType().Name}: {originalException.Message}", originalException);
                
            case SagaContinuationAction.Continue:
                _logger.LogInformation("Continuing workflow: Saga error handled, continuing execution");
                break;
                
            case SagaContinuationAction.Retry:
                _logger.LogInformation("Retrying saga: Throwing special exception to trigger saga retry loop");
                throw new SagaRetryRequestedException(originalException, sagaErrorConfig.RetryPolicy);
                
            case SagaContinuationAction.Suspend:
                _logger.LogInformation("Suspending saga: Throwing special exception to trigger saga suspend loop");
                throw new SagaSuspendRequestedException(originalException, sagaErrorConfig);
                
            default:
                _logger.LogWarning("Unknown continuation action: {Action}, defaulting to Terminate", 
                    sagaErrorConfig.ContinuationAction);
                throw new SagaTerminatedException($"Saga terminated due to {originalException.GetType().Name}: {originalException.Message}", originalException);
        }
    }


    /// <summary>
    /// Applies default saga behavior when no explicit error handler is found
    /// </summary>
    private async Task ApplyDefaultSagaBehaviorAsync<TWorkflowData>(
        List<SagaExecutionStepInfo> successfulSteps,
        Exception originalException,
        StepExecutionContext<TWorkflowData> context,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogInformation("Applying default saga behavior: Compensate all + Terminate");

        // Default behavior: Compensate all successful steps and terminate
        await CompensateAllStepsAsync(successfulSteps, context, cancellationToken);
        
        _logger.LogInformation("Terminating saga (default): Throwing SagaTerminatedException");
        throw new SagaTerminatedException($"Saga terminated due to {originalException.GetType().Name}: {originalException.Message}", originalException);
    }

   /// <summary>
    /// Suspends saga execution using saga-specific suspend logic
    /// </summary>
    private async Task<StepExecutionResult> SuspendSagaAsync<TWorkflowData>(
        WorkflowStep sagaStep,
        StepExecutionContext<TWorkflowData> context, 
        ExecutionState executionState, 
        SagaErrorConfiguration sagaErrorConfig,
        Exception originalException,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Starting saga-specific suspend logic");
        
        try
        {
            // Find the suspend step in the catch block sequence steps
            WorkflowStep? suspendStep = null;
            
            // Look through all catch blocks for the one that matches our exception
            foreach (var catchBlock in sagaStep.CatchBlocks)
            {
                if (catchBlock.ExceptionType?.IsAssignableFrom(originalException.GetType()) == true)
                {
                    // Find the suspend step in this catch block's sequence
                    suspendStep = catchBlock.SequenceSteps
                        .FirstOrDefault(s => s.StepType == WorkflowStepType.SuspendResume);
                    
                    if (suspendStep != null)
                    {
                        _logger.LogTrace("Found suspend step in catch block for {ExceptionType}", catchBlock.ExceptionType?.Name);
                        break;
                    }
                }
            }
            
            if (suspendStep == null)
            {
                _logger.LogError("No suspend step found in catch blocks");
                throw new InvalidOperationException("Suspend step not found in saga catch blocks");
            }
            
            // Extract suspend configuration from the suspend step
            var resumeEventType = suspendStep.ResumeEventType;
            var postResumeAction = suspendStep.StepMetadata.ContainsKey("PostResumeAction") 
                ? suspendStep.StepMetadata["PostResumeAction"] 
                : null;
            var resumeCondition = suspendStep.StepMetadata.ContainsKey("ResumeCondition") 
                ? suspendStep.StepMetadata["ResumeCondition"] 
                : null;
            var suspendReason = suspendStep.StepMetadata.ContainsKey("SuspendReason") 
                ? suspendStep.StepMetadata["SuspendReason"] as string 
                : $"Saga suspend requested due to {originalException.GetType().Name}: {originalException.Message}";
                
            _logger.LogTrace("Extracted suspend config: ResumeEventType={ResumeEventType}, PostResumeAction={PostResumeAction}, SuspendReason={SuspendReason}", 
                resumeEventType?.Name, postResumeAction?.GetType().Name, suspendReason);
            
            // Create a new suspend step for execution (clone the configuration)
            var executionSuspendStep = new WorkflowStep
            {
                Id = Guid.NewGuid().ToString(),
                Name = "SagaSuspend",
                StepType = WorkflowStepType.SuspendResume,
                WorkflowDataType = typeof(TWorkflowData),
                PreviousStepDataType = sagaStep.PreviousStepDataType,
                ResumeEventType = resumeEventType,
                StepMetadata = new Dictionary<string, object>
                {
                    ["SuspendReason"] = suspendReason
                }
            };
            
            // Copy metadata from the original suspend step
            if (postResumeAction != null)
            {
                executionSuspendStep.StepMetadata["PostResumeAction"] = postResumeAction;
            }
            
            if (resumeCondition != null)
            {
                executionSuspendStep.StepMetadata["ResumeCondition"] = resumeCondition;
                
                // Also set the CompiledCondition if it exists on the original step
                if (suspendStep.CompiledCondition != null)
                {
                    executionSuspendStep.CompiledCondition = suspendStep.CompiledCondition;
                }
            }
            
            _logger.LogTrace("Created execution suspend step with {MetadataCount} metadata entries", executionSuspendStep.StepMetadata.Count);
            
            // Delegate to SuspendResumeExecutor to handle the actual suspension
            var result = await _suspendResumeExecutor.ExecuteSuspendResumeStepAsync(
                executionSuspendStep, context, executionState, cancellationToken);
                
            _logger.LogDebug("Suspend result: IsSuccess={IsSuccess}, ErrorMessage={ErrorMessage}", result.IsSuccess, result.ErrorMessage);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Suspend saga failed: {ErrorMessage}", ex.Message);
            return new StepExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = $"Saga suspend failed: {ex.Message}",
                Exception = ex
            };
        }
    }

    /// <summary>
    /// Extracts suspend configuration from saga error configuration
    /// </summary>
    private SuspendConfiguration? ExtractSuspendConfiguration(SagaErrorConfiguration sagaErrorConfig)
    {
        _logger.LogTrace("Extracting suspend config from saga error config");
        
        // Check if the saga error config has suspend-related metadata
        if (sagaErrorConfig.Metadata != null)
        {
            // Look for suspend configuration in metadata
            if (sagaErrorConfig.Metadata.TryGetValue("SuspendConfiguration", out var suspendConfigObj))
            {
                _logger.LogTrace("Found SuspendConfiguration in metadata: {ConfigType}", suspendConfigObj?.GetType().Name);
                return suspendConfigObj as SuspendConfiguration;
            }
            
            // Look for individual suspend properties
            Type? resumeEventType = null;
            object? postResumeAction = null;
            object? condition = null;
            
            if (sagaErrorConfig.Metadata.TryGetValue("ResumeEventType", out var eventTypeObj))
            {
                resumeEventType = eventTypeObj as Type;
            }
            
            if (sagaErrorConfig.Metadata.TryGetValue("PostResumeAction", out var postResumeObj))
            {
                postResumeAction = postResumeObj;
            }
            
            if (sagaErrorConfig.Metadata.TryGetValue("ResumeCondition", out var conditionObj))
            {
                condition = conditionObj;
            }
            
            if (resumeEventType != null)
            {
                _logger.LogTrace("Creating SuspendConfiguration from individual properties");
                return new SuspendConfiguration
                {
                    ResumeEventType = resumeEventType,
                    PostResumeAction = postResumeAction,
                    ResumeCondition = condition
                };
            }
        }

        _logger.LogTrace("No suspend config found in saga error config");
        return null;
    }

    /// <summary>
    /// Creates a WorkflowStep for saga suspension with metadata
    /// </summary>
    private WorkflowStep CreateSagaSuspendStep(SuspendConfiguration suspendConfig, SagaErrorConfiguration sagaErrorConfig)
    {
        _logger.LogTrace("Creating saga suspend step");
        
        var suspendStep = new WorkflowStep
        {
            Id = Guid.NewGuid().ToString(),
            StepType = WorkflowStepType.SuspendResume,
            StepMetadata = new Dictionary<string, object>()
        };

        // Store resume event type
        if (suspendConfig.ResumeEventType != null)
        {
            suspendStep.StepMetadata["ResumeEventType"] = suspendConfig.ResumeEventType.AssemblyQualifiedName!;
        }

        // Store PostResumeAction in metadata for WorkflowEngine to detect saga resumes
        if (suspendConfig.PostResumeAction != null)
        {
            suspendStep.StepMetadata["PostResumeAction"] = suspendConfig.PostResumeAction;
        }

        // Store resume condition if provided
        if (suspendConfig.ResumeCondition != null)
        {
            suspendStep.StepMetadata["ResumeCondition"] = suspendConfig.ResumeCondition;
        }

        // Store saga-specific metadata
        suspendStep.StepMetadata["IsSagaSuspend"] = true;
        suspendStep.StepMetadata["SagaErrorConfiguration"] = sagaErrorConfig;

        _logger.LogTrace("Created suspend step: {StepId} with {MetadataCount} metadata entries", suspendStep.Id, suspendStep.StepMetadata.Count);
        return suspendStep;
    }

    /// <summary>
    /// Handles saga resume after workflow is resumed - determines what to do based on PostResumeAction
    /// </summary>
    public async Task<WorkflowExecutionResult> HandleSagaResumeAsync<TEvent>(
        WorkflowInstance instance,
        TEvent @event,
        CancellationToken cancellationToken)
        where TEvent : class
    {
        _logger.LogInformation("Handling saga resume: {InstanceId} with event {EventType}", 
            instance.InstanceId, typeof(TEvent).Name);

        try
        {
            // Extract PostResumeAction from suspension metadata
            var postResumeAction = ExtractPostResumeAction(instance);
            if (postResumeAction == null)
            {
                _logger.LogError("No PostResumeAction found in suspension metadata for saga resume");
                
                return new WorkflowExecutionResult
                {
                    InstanceId = instance.InstanceId,
                    Status = WorkflowExecutionStatus.Faulted,
                    ErrorMessage = "No PostResumeAction found for saga resume"
                };
            }

            _logger.LogInformation("Processing PostResumeAction: {ActionType}", postResumeAction.GetType().Name);

            // Handle different PostResumeAction types
            if (postResumeAction is RetryErrorHandler retryHandler && 
                retryHandler.RetryPolicy?.MaximumAttempts > 0)
            {
                _logger.LogInformation("PostResumeAction: Retry saga with {MaxAttempts} attempts", 
                    retryHandler.RetryPolicy.MaximumAttempts);

                // For ThenRetrySaga: throw special exception to trigger saga retry
                // This will be caught by the saga retry loop in ExecuteSagaStepAsync
                throw new SagaRetryRequestedException(
                    new Exception("Saga resumed with retry request"), 
                    retryHandler.RetryPolicy);
            }

            // Handle other PostResumeAction types if needed
            _logger.LogWarning("Unsupported PostResumeAction: {ActionType}", postResumeAction.GetType().Name);

            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = WorkflowExecutionStatus.Faulted,
                ErrorMessage = $"Unsupported PostResumeAction type: {postResumeAction.GetType().Name}"
            };
        }
        catch (SagaRetryRequestedException)
        {
            _logger.LogInformation("Saga retry requested from resume - propagating to saga retry loop");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saga resume failed: {ErrorMessage}", ex.Message);

            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = WorkflowExecutionStatus.Faulted,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.StackTrace
            };
        }
    }

    /// <summary>
    /// Extracts PostResumeAction from WorkflowInstance suspension metadata
    /// </summary>
    private object? ExtractPostResumeAction(WorkflowInstance instance)
    {
        _logger.LogTrace("Extracting PostResumeAction from instance {InstanceId}", instance.InstanceId);
        
        if (instance.SuspensionInfo?.Metadata != null)
        {
            if (instance.SuspensionInfo.Metadata.TryGetValue("PostResumeAction", out var postResumeAction))
            {
                _logger.LogTrace("Found PostResumeAction: {ActionType}", postResumeAction?.GetType().Name);
                return postResumeAction;
            }
        }

        _logger.LogTrace("No PostResumeAction found in suspension metadata");
        return null;
    }

    /// <summary>
    /// Handles PostResumeAction logic for mid-saga resume scenarios
    /// </summary>
    private async Task HandlePostResumeActionAsync(object postResumeAction, SagaResumeInfo resumeInfo)
    {
        _logger.LogInformation("Handling PostResumeAction: {ActionType}", postResumeAction.GetType().Name);

        try
        {
            // Handle different PostResumeAction types
            if (postResumeAction is RetryErrorHandler retryHandler)
            {
                _logger.LogInformation("PostResumeAction: ThenRetrySaga - Resetting saga state for full retry");

                // For ThenRetrySaga: Reset resumeInfo to restart from step 0
                // This effectively ignores any previous progress and restarts the entire saga
                resumeInfo.ResumeFromStepIndex = 0;
                resumeInfo.CompletedSteps?.Clear();
                
                _logger.LogTrace("Saga state reset: Will restart from step 0");
                return;
            }

            // Handle ThenRetryFailedStep scenario
            if (postResumeAction.GetType().Name.Contains("RetryFailedStep"))
            {
                _logger.LogInformation("PostResumeAction: ThenRetryFailedStep - Resuming from failed step");

                // Resume from the failed step index (already set in resumeInfo)
                _logger.LogTrace("Will resume from failed step: {FailedStepIndex}", resumeInfo.FailedStepIndex);
                return;
            }

            // Handle ThenContinue scenario
            if (postResumeAction.GetType().Name.Contains("Continue"))
            {
                _logger.LogInformation("PostResumeAction: ThenContinue - Resuming from step after failed step");

                // Resume from the step after the failed step
                if (resumeInfo.FailedStepIndex.HasValue)
                {
                    resumeInfo.ResumeFromStepIndex = resumeInfo.FailedStepIndex.Value + 1;
                    _logger.LogTrace("Will resume from step after failed: {ResumeStepIndex}", resumeInfo.ResumeFromStepIndex);
                }
                return;
            }

            // Handle custom ThenExecute scenarios (future extensibility)
            _logger.LogWarning("Unsupported PostResumeAction: {ActionType} - treating as ThenRetrySaga", 
                postResumeAction.GetType().Name);

            // Default: treat as ThenRetrySaga
            resumeInfo.ResumeFromStepIndex = 0;
            resumeInfo.CompletedSteps?.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostResumeAction failed: {ErrorMessage}", ex.Message);
            
            // On error, default to ThenRetrySaga behavior
            resumeInfo.ResumeFromStepIndex = 0;
            resumeInfo.CompletedSteps?.Clear();
        }
    }
}

/// <summary>
/// Configuration for saga suspension
/// </summary>
internal class SuspendConfiguration
{
    public Type? ResumeEventType { get; set; }
    public object? PostResumeAction { get; set; }
    public object? ResumeCondition { get; set; }
}
