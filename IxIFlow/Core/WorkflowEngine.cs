using System.Collections;
using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     Main workflow execution engine that orchestrates workflow execution
///     using WorkflowDefinition structures and ActivityExecutor
/// </summary>
public class WorkflowEngine : IWorkflowEngine
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IActivityExecutor _activityExecutor;
    private readonly IWorkflowInvoker _workflowInvoker;
    private readonly ISuspendResumeExecutor _suspendResumeExecutor;
    private readonly ISagaExecutor _sagaExecutor;
    private readonly IWorkflowStateRepository _stateRepository;
    private readonly IEventStore _eventStore;
    private readonly ILogger<WorkflowEngine> _logger;
    private readonly IWorkflowTracer _tracer;
    private readonly IWorkflowVersionRegistry _versionRegistry;

    public WorkflowEngine(
        IServiceProvider serviceProvider,
        IActivityExecutor activityExecutor,
        IWorkflowInvoker workflowInvoker,
        ISuspendResumeExecutor suspendResumeExecutor,
        ISagaExecutor sagaExecutor,
        IWorkflowStateRepository stateRepository,
        IEventStore eventStore,
        ILogger<WorkflowEngine> logger,
        IWorkflowTracer tracer,
        IWorkflowVersionRegistry versionRegistry)
    {
        _activityExecutor = activityExecutor ?? throw new ArgumentNullException(nameof(activityExecutor));
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _workflowInvoker = workflowInvoker ?? throw new ArgumentNullException(nameof(workflowInvoker));
        _suspendResumeExecutor = suspendResumeExecutor ?? throw new ArgumentNullException(nameof(suspendResumeExecutor));
        _sagaExecutor = sagaExecutor ?? throw new ArgumentNullException(nameof(sagaExecutor));
        _versionRegistry = versionRegistry ?? throw new ArgumentNullException(nameof(versionRegistry));
    }

    /// <summary>
    ///     Executes a workflow from start to completion
    /// </summary>
    public async Task<WorkflowExecutionResult> ExecuteWorkflowAsync<TWorkflowData>(
        WorkflowDefinition definition,
        TWorkflowData workflowData,
        WorkflowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWorkflowData : class
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (workflowData == null) throw new ArgumentNullException(nameof(workflowData));

        options ??= new WorkflowOptions();
        var instanceId = Guid.NewGuid().ToString();

        _logger.LogInformation("Starting workflow execution. Instance: {InstanceId}, Workflow: {WorkflowName}",
            instanceId, definition.Name);

        // Create workflow instance
        var instance = new WorkflowInstance
        {
            InstanceId = instanceId,
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            Status = WorkflowStatus.Running,
            CorrelationId = options.CorrelationId ?? Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            TotalSteps = definition.Steps.Count,
            WorkflowDataJson = JsonSerializer.Serialize(workflowData),
            WorkflowDataType = typeof(TWorkflowData).AssemblyQualifiedName ?? "",
            Properties = new Dictionary<string, object>(options.Properties)
        };

        try
        {
            // Register workflow definition for resume operations
            await _versionRegistry.RegisterWorkflowAsync(definition);
            
            // Save initial state
            if (options.PersistState) await _stateRepository.SaveWorkflowInstanceAsync(instance);

            // Add workflow started trace
            await _tracer.TraceAsync(instance.InstanceId, new ExecutionTraceEntry
            {
                EntryType = TraceEntryType.WorkflowStarted,
                ActivityName = definition.Name,
                InputDataJson = instance.WorkflowDataJson
            }, cancellationToken);

            // Execute workflow steps using propagation approach
            var executionContext = new StepExecutionContext<TWorkflowData>
            {
                WorkflowInstance = instance,
                WorkflowDefinition = definition,
                WorkflowData = workflowData,
                PreviousStepData = null
            };

            var result = await ExecuteStepsAsync(definition.Steps, executionContext, cancellationToken);

            // Check if workflow was suspended (don't override suspension status)
            if (instance.Status != WorkflowStatus.Suspended)
            {
                // Update final state only if not suspended
                instance.Status = result.IsSuccess ? WorkflowStatus.Completed : WorkflowStatus.Failed;
                instance.CompletedAt = DateTime.UtcNow;

                if (!result.IsSuccess)
                {
                    instance.LastError = result.ErrorMessage;
                    instance.LastErrorStackTrace = result.ErrorStackTrace;
                }

                instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData);
                // Save final state
                if (options.PersistState) await _stateRepository.SaveWorkflowInstanceAsync(instance);
            }

            // Add workflow completion trace
            await _tracer.TraceAsync(instance.InstanceId, new ExecutionTraceEntry
            {
                EntryType = result.IsSuccess ? TraceEntryType.WorkflowCompleted : TraceEntryType.WorkflowFailed,
                ActivityName = definition.Name,
                OutputDataJson = JsonSerializer.Serialize(executionContext.WorkflowData),
                ErrorMessage = result.ErrorMessage,
                StackTrace = result.ErrorStackTrace,
                Duration = DateTime.UtcNow - instance.StartedAt
            }, cancellationToken);

            _logger.LogInformation("Workflow execution completed. Instance: {InstanceId}, Success: {Success}, Status: {Status}",
                instanceId, result.IsSuccess, instance.Status);

            // For suspended workflows, return success=false but with specific suspended status
            var isSuccessful = result.IsSuccess && instance.Status != WorkflowStatus.Suspended;

            // Map workflow instance status to execution result status
            var executionStatus = instance.Status switch
            {
                WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
                WorkflowStatus.Suspended => WorkflowExecutionStatus.Suspended,
                WorkflowStatus.Failed => WorkflowExecutionStatus.Faulted,
                WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
                _ => WorkflowExecutionStatus.Faulted
            };

            return new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = executionStatus,
                WorkflowData = executionContext.WorkflowData,
                ErrorMessage = instance.Status == WorkflowStatus.Suspended ? $"Workflow suspended: {instance.SuspensionInfo?.SuspendReason}" : result.ErrorMessage,
                ErrorStackTrace = result.ErrorStackTrace,
                ExecutionTime = DateTime.UtcNow - instance.StartedAt!.Value,
                TraceEntries = [.. instance.ExecutionHistory]
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow execution failed with unhandled exception. Instance: {InstanceId}",
                instanceId);

            // Update instance state
            instance.Status = WorkflowStatus.Failed;
            instance.CompletedAt = DateTime.UtcNow;
            instance.LastError = ex.Message;
            instance.LastErrorStackTrace = ex.StackTrace;

            if (options.PersistState) await _stateRepository.SaveWorkflowInstanceAsync(instance);

            // Add error trace
            await _tracer.TraceAsync(instance.InstanceId, new ExecutionTraceEntry
            {
                EntryType = TraceEntryType.WorkflowFailed,
                ActivityName = definition.Name,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace,
                Duration = DateTime.UtcNow - instance.StartedAt
            }, cancellationToken);

            return new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = WorkflowExecutionStatus.Faulted,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.StackTrace,
                ExecutionTime = DateTime.UtcNow - instance.StartedAt!.Value
            };
        }
    }

    /// <summary>
    ///     Resumes a suspended workflow with an event
    /// </summary>
    public async Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(
        string instanceId,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class
    {
        _logger.LogInformation("Resuming workflow {InstanceId} with event of type {EventType}",
            instanceId, typeof(TEventData).Name);

        try
        {
            // Step 1: Validate and prepare resume using SuspendResumeExecutor
            _logger.LogDebug("Validating and preparing resume for workflow {InstanceId}", instanceId);
            var validationResult = await _suspendResumeExecutor.ValidateAndPrepareResumeAsync(instanceId, @event, cancellationToken);
            
            _logger.LogDebug("Resume validation result: IsValid={IsValid}, IsConditionMet={IsConditionMet}, ErrorMessage={ErrorMessage}",
                validationResult.IsValid, validationResult.IsConditionMet, validationResult.ErrorMessage);
            
            if (!validationResult.IsValid)
            {
                if (!validationResult.IsConditionMet)
                {
                    // Condition not met - workflow should remain suspended
                    _logger.LogWarning("Resume condition not met for workflow {InstanceId}, keeping suspended", instanceId);
                    return new WorkflowExecutionResult
                    {
                        InstanceId = instanceId,
                        Status = WorkflowExecutionStatus.Suspended,
                        ErrorMessage = validationResult.ErrorMessage
                    };
                }
                else
                {
                    // Validation failed
                    _logger.LogError("Resume validation failed for workflow {InstanceId}: {ErrorMessage}", 
                        instanceId, validationResult.ErrorMessage);
                    return new WorkflowExecutionResult
                    {
                        InstanceId = instanceId,
                        Status = WorkflowExecutionStatus.Faulted,
                        ErrorMessage = validationResult.ErrorMessage
                    };
                }
            }

            _logger.LogDebug("Resume validation passed for workflow {InstanceId}", instanceId);

            // Step 2: Get validated data from result
            _logger.LogTrace("Getting validated data from resume result");
            var instance = validationResult.WorkflowInstance!;
            var workflowData = validationResult.WorkflowData!;
            _logger.LogTrace("Retrieved workflow instance and data from resume result");
            var restoredExecutionState = _suspendResumeExecutor.RestoreExecutionState(instance.ExecutionStateJson);
            restoredExecutionState.LastStepResult = @event;

            // Step 2.5: Check if this is a saga resume and delegate to SagaExecutor
            _logger.LogTrace("Checking for saga resume indicators");
            if (instance.SuspensionInfo?.Metadata?.ContainsKey("PostResumeAction") == true)
            {
                _logger.LogInformation("Saga resume detected, delegating to SagaExecutor for saga-specific resume handling");
                
                // Clear suspension info and update status before delegating to saga executor
                instance.Status = WorkflowStatus.Running;
                var suspensionInfo = instance.SuspensionInfo; // Keep reference for saga executor
                instance.SuspensionInfo = null;
                instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData);
                await _stateRepository.SaveWorkflowInstanceAsync(instance);
                
                // Restore suspension info temporarily for saga executor to access PostResumeAction
                instance.SuspensionInfo = suspensionInfo;
                
                try
                {
                    // Delegate to SagaExecutor for saga-specific resume handling
                    _logger.LogDebug("Delegating to SagaExecutor: HandleSagaResumeAsync");
                    
                    // Use reflection to call the generic method since we don't know TEvent at compile time
                    var eventType = @event.GetType();
                    var handleSagaResumeMethod = typeof(ISagaExecutor).GetMethod(nameof(ISagaExecutor.HandleSagaResumeAsync));
                    if (handleSagaResumeMethod == null)
                    {
                        throw new InvalidOperationException("HandleSagaResumeAsync method not found");
                    }
                    
                    var genericHandleSagaResumeMethod = handleSagaResumeMethod.MakeGenericMethod(eventType);
                    var sagaResumeTask = (Task)genericHandleSagaResumeMethod.Invoke(_sagaExecutor, new object[] { instance, @event, cancellationToken })!;
                    await sagaResumeTask;
                    
                    // Get the result using reflection
                    var resultProperty = sagaResumeTask.GetType().GetProperty("Result");
                    var sagaResumeResult = resultProperty?.GetValue(sagaResumeTask) as WorkflowExecutionResult;
                    
                    if (sagaResumeResult != null)
                    {
                        _logger.LogInformation("Saga resume completed with status: {Status}", sagaResumeResult.Status);
                        return sagaResumeResult;
                    }
                    else
                    {
                        throw new InvalidOperationException("Failed to get saga resume result");
                    }
                }
                catch (Exception ex)
                {
                    var sagaRetryException = ex as SagaRetryRequestedException ?? ex.InnerException as SagaRetryRequestedException;
                    if (sagaRetryException != null)
                    {
                        _logger.LogInformation("Saga resume requested retry continuation for workflow {InstanceId}", instanceId);

                        instance.SuspensionInfo = null;
                        instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData);
                        await _stateRepository.SaveWorkflowInstanceAsync(instance);

                        var workflowDefinition = await _versionRegistry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
                        if (workflowDefinition == null)
                        {
                            return new WorkflowExecutionResult
                            {
                                InstanceId = instanceId,
                                Status = WorkflowExecutionStatus.Faulted,
                                ErrorMessage = $"Workflow definition not found: {instance.WorkflowName} v{instance.WorkflowVersion}"
                            };
                        }

                        return await ContinueWorkflowExecutionAsync(workflowDefinition, instance, workflowData, restoredExecutionState, @event, cancellationToken);
                    }

                    _logger.LogError(ex, "Saga resume failed: {ErrorMessage}", ex.Message);
                    
                    return new WorkflowExecutionResult
                    {
                        InstanceId = instanceId,
                        Status = WorkflowExecutionStatus.Faulted,
                        ErrorMessage = ex.Message,
                        ErrorStackTrace = ex.StackTrace
                    };
                }
                finally
                {
                    // Clear suspension info permanently after saga resume handling
                    instance.SuspensionInfo = null;
                    await _stateRepository.SaveWorkflowInstanceAsync(instance);
                    _logger.LogTrace("Cleared suspension info after saga resume handling");
                }
            }

            // Step 3: Clear suspension info and update status (for regular resumes)
            _logger.LogTrace("Clearing suspension info and updating workflow status to Running");
            instance.Status = WorkflowStatus.Running;
            instance.SuspensionInfo = null;
            instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData);
            await _stateRepository.SaveWorkflowInstanceAsync(instance);
            _logger.LogDebug("Workflow {InstanceId} status updated to Running, suspension info cleared", instanceId);

            // Step 4: Continue workflow execution from the suspend point (for regular resumes)
            _logger.LogTrace("Continuing workflow execution from suspend point");
            
            // Get the original workflow definition from registry
            var definition = await _versionRegistry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
            if (definition == null)
            {
                throw new InvalidOperationException($"Workflow definition not found: {instance.WorkflowName} v{instance.WorkflowVersion}");
            }
            
            _logger.LogTrace("Retrieved workflow definition from registry");
            
            // Continue workflow execution from the step after the suspend point
            var result = await ContinueWorkflowExecutionAsync(definition, instance, workflowData, restoredExecutionState, @event, cancellationToken);
            
            _logger.LogInformation("Workflow resume completed: {InstanceId}, Status: {Status}", 
                instanceId, result.Status);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume workflow {InstanceId}: {ErrorMessage}", instanceId, ex.Message);
            
            return new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = WorkflowExecutionStatus.Faulted,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.StackTrace
            };
        }
    }

    /// <summary>
    ///     Executes a list of workflow steps using the new propagation-based exception handling
    /// </summary>
    private async Task<StepExecutionResult> ExecuteStepsAsync<TWorkflowData>(
        List<WorkflowStep> steps,
        StepExecutionContext<TWorkflowData> context,
        CancellationToken cancellationToken,
        ExecutionState? initialExecutionState = null)
        where TWorkflowData : class
    {
        var executionState = initialExecutionState ?? new ExecutionState();
        executionState.LastStepResult ??= context.PreviousStepData;

        _logger.LogDebug("Starting propagation-based execution of {StepCount} steps", steps.Count);

        var startIndex = GetContinuationStartIndex(steps, executionState);
        for (var index = startIndex; index < steps.Count; index++)
        {
            var step = steps[index];
            context.WorkflowInstance.CurrentStepNumber++;

            var stepResult = await ExecuteStepWithPropagationAsync(step, context, executionState, cancellationToken);

            if (!stepResult.IsSuccess)
            {
                return stepResult;
            }

            // An exception belongs to the active scope. A later Try step must not catch it.
            if (executionState.PendingException != null)
            {
                break;
            }

            // Update execution state
            executionState.LastStepResult = stepResult.OutputData;
            context.PreviousStepData = stepResult.OutputData;
        }

        // Check if we have any unhandled exceptions at the end
        if (executionState.PendingException != null)
        {
            _logger.LogError("Workflow completed with unhandled exception: {ExceptionType}",
                executionState.PendingException.GetType().Name);

            return new StepExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = $"{executionState.PendingException.GetType().Name}: {executionState.PendingException.Message}",
                ErrorStackTrace = executionState.PendingException.StackTrace,
                Exception = executionState.PendingException
            };
        }

        return new StepExecutionResult
        {
            IsSuccess = true,
            OutputData = executionState.LastStepResult
        };
    }

    /// <summary>
    ///     Executes a single workflow step using propagation-based exception handling
    /// </summary>
    private async Task<StepExecutionResult> ExecuteStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Executing step with propagation: {StepId} ({StepType})", step.Id, step.StepType);

        return step.StepType switch
        {
            WorkflowStepType.TryCatch => await ExecuteTryCatchStepWithPropagationAsync(step, context, executionState, cancellationToken),
            WorkflowStepType.Activity => await ExecuteActivityStepWithPropagationAsync(step, context, executionState, cancellationToken),
            WorkflowStepType.Sequence => await ExecuteSequenceStepWithPropagationAsync(step, context, executionState, cancellationToken),
            WorkflowStepType.Conditional => await ExecuteConditionalStepWithPropagationAsync(step, context, executionState, cancellationToken),
            WorkflowStepType.Parallel => await ExecuteParallelStepWithPropagationAsync(step, context, executionState, cancellationToken),
            WorkflowStepType.Loop => await ExecuteLoopStepWithPropagationAsync(step, context, executionState, cancellationToken),
            WorkflowStepType.Saga => await ExecuteSagaStepWithPropagationAsync(step, context, executionState, cancellationToken),
            WorkflowStepType.WorkflowInvocation => await ExecuteWorkflowInvocationStepWithPropagationAsync(step, context, executionState, cancellationToken),
            WorkflowStepType.SuspendResume => await ExecuteSuspendResumeStepWithPropagationAsync(step, context, executionState, cancellationToken),
            _ => throw new NotSupportedException($"Step type {step.StepType} is not yet implemented in propagation mode")
        };
    }

    /// <summary>
    ///     Executes a try/catch step with propagation-based exception handling
    /// </summary>
    private async Task<StepExecutionResult> ExecuteTryCatchStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
    where TWorkflowData : class
    {
        _logger.LogDebug("Executing try/catch step with propagation: {StepId}", step.Id);

        // Catch blocks use the value that entered the try scope, including after resume.
        var resumeStepId = GetResumeStepId(executionState);
        var resumingCatchIndex = resumeStepId == null
            ? -1
            : step.CatchBlocks.FindIndex(catchBlock => ContainsStep(catchBlock.SequenceSteps, resumeStepId));
        var resumingFinally = resumeStepId != null && ContainsStep(step.FinallySteps, resumeStepId);
        var resumingTry = resumeStepId != null && ContainsStep(step.SequenceSteps, resumeStepId);

        // Push try block onto stack for nested exception handling
        executionState.TryStack.Push(step);
        var tryFrame = EnterFrame(executionState, step, WorkflowStepType.TryCatch.ToString());
        if ((resumingTry || resumingCatchIndex >= 0 || resumingFinally) &&
            !tryFrame.State.ContainsKey("EntryType") && !tryFrame.State.ContainsKey("EntryIsNull") &&
            !tryFrame.RuntimeValues.ContainsKey("Entry"))
        {
            throw new InvalidOperationException($"Handler frame for step {step.Id} is missing its entry value");
        }
        var stepBeforeTryCatch = resumeStepId != null
            ? RestoreFrameValue(tryFrame, "Entry")
            : context.PreviousStepData;
        if (resumeStepId == null)
        {
            SaveFrameValue(tryFrame, "Entry", stepBeforeTryCatch);
        }

        if (resumingCatchIndex < 0 && !resumingFinally)
        {
            try
            {
                // Execute try block steps
                var tryStartIndex = resumingTry
                    ? GetContinuationStartIndex(step.SequenceSteps, executionState)
                    : 0;
                for (var tryIndex = tryStartIndex; tryIndex < step.SequenceSteps.Count; tryIndex++)
                {
                    var tryStep = step.SequenceSteps[tryIndex];
                    _logger.LogTrace("Executing try step: {StepType} - {StepId}", tryStep.StepType, tryStep.Id);

                    var result = await ExecuteStepWithPropagationAsync(tryStep, context, executionState, cancellationToken);

                    _logger.LogTrace("Try step result: Success={IsSuccess}, HasPendingException={HasPendingException}",
                        result.IsSuccess, executionState.PendingException != null);

                    if (!result.IsSuccess)
                    {
                        _logger.LogError("Try step failed: {StepId} - {ErrorMessage}", tryStep.Id, result.ErrorMessage);
                        return result;
                    }

                    if (executionState.PendingException != null)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                executionState.PendingException = ex;
            }
        }
        else if (resumingCatchIndex >= 0)
        {
            executionState.PendingException = RestoreHandlerException(tryFrame, "HandledException")
                ?? throw new InvalidOperationException($"Handler frame for step {step.Id} has no caught exception");
        }

        // Process catch blocks if we have a pending exception
        if (!resumingFinally && executionState.PendingException != null)
        {
            _logger.LogDebug("Processing catch blocks for exception: {ExceptionType}, CatchBlockCount: {CatchBlockCount}",
                executionState.PendingException.GetType().Name, step.CatchBlocks.Count);

            for (var catchIndex = 0; catchIndex < step.CatchBlocks.Count; catchIndex++)
            {
                if (resumingCatchIndex >= 0 && catchIndex != resumingCatchIndex)
                {
                    continue;
                }

                var catchBlock = step.CatchBlocks[catchIndex];
                _logger.LogTrace("Checking catch block: {CatchBlockName}, ExceptionType: {CatchType}",
                    catchBlock.Name, catchBlock.ExceptionType?.Name ?? "null");

                // Check if this catch block can handle the pending exception
                if (catchBlock.ExceptionType == null ||
                    !catchBlock.ExceptionType.IsAssignableFrom(executionState.PendingException.GetType()))
                {
                    _logger.LogTrace("Skipping catch block {CatchBlockName} - exception type mismatch. CatchType: {CatchType}, ThrownType: {ThrownType}",
                        catchBlock.Name, catchBlock.ExceptionType?.Name ?? "null", executionState.PendingException.GetType().Name);
                    continue;
                }

                _logger.LogDebug("Executing catch block {CatchBlockName} for exception {ExceptionType}",
                    catchBlock.Name, executionState.PendingException.GetType().Name);

                // Store the original exception being handled
                var originalException = executionState.PendingException;
                SaveHandlerException(tryFrame, "HandledException", originalException);
                tryFrame.State["Phase"] = "Catch";
                tryFrame.State["CatchIndex"] = catchIndex.ToString();
                executionState.PendingException = null;

                try
                {
                    _logger.LogTrace("Starting catch block execution: {CatchBlockName}", catchBlock.Name);

                    // Execute catch block activities with exception context
                    var catchStartIndex = catchIndex == resumingCatchIndex
                        ? GetContinuationStartIndex(catchBlock.SequenceSteps, executionState)
                        : 0;
                    for (var catchStepIndex = catchStartIndex; catchStepIndex < catchBlock.SequenceSteps.Count; catchStepIndex++)
                    {
                        var catchStep = catchBlock.SequenceSteps[catchStepIndex];
                        _logger.LogTrace("Executing catch step: {StepType} - {StepId}", catchStep.StepType, catchStep.Id);

                        // Create typed execution request with catch exception context
                        if (catchStep.StepType == WorkflowStepType.Activity)
                        {
                            // For catch blocks, use the step that came BEFORE the try/catch block (pass-through behavior)
                            // This matches the compiled expression expectations from the builder
                            var workflowDataType = typeof(TWorkflowData);
                            var stepBeforeTryCatchType = stepBeforeTryCatch?.GetType() ?? typeof(object);

                            _logger.LogTrace("Creating catch block typed execution request with WorkflowData={WorkflowDataType}, StepBeforeTryCatch={StepBeforeTryCatchType}, Exception={ExceptionType}",
                                workflowDataType.Name, stepBeforeTryCatchType.Name, originalException?.GetType().Name);

                            // Get the generic method ExecuteTypedActivityAsync<TWorkflowData, TPreviousStepData>
                            var executeMethod = typeof(IActivityExecutor).GetMethod(nameof(IActivityExecutor.ExecuteTypedActivityAsync));
                            if (executeMethod == null)
                                throw new InvalidOperationException("ExecuteTypedActivityAsync method not found");

                            var genericExecuteMethod = executeMethod.MakeGenericMethod(workflowDataType, stepBeforeTryCatchType);

                            // Create the request using reflection
                            var requestType = typeof(TypedActivityExecutionRequest<,>).MakeGenericType(workflowDataType, stepBeforeTryCatchType);
                            var request = Activator.CreateInstance(requestType);

                            _logger.LogTrace("Created request type: {RequestType}", requestType.Name);

                            // Set properties using reflection
                            requestType.GetProperty("ActivityType")?.SetValue(request, catchStep.ActivityType);
                            requestType.GetProperty("WorkflowData")?.SetValue(request, context.WorkflowData);
                            requestType.GetProperty("PreviousStepData")?.SetValue(request, stepBeforeTryCatch);
                            requestType.GetProperty("InputMappings")?.SetValue(request, catchStep.InputMappings);
                            requestType.GetProperty("OutputMappings")?.SetValue(request, catchStep.OutputMappings);
                            requestType.GetProperty("Step")?.SetValue(request, catchStep);
                            requestType.GetProperty("ExecutionContext")?.SetValue(request, context);
                            requestType.GetProperty("CancellationToken")?.SetValue(request, cancellationToken);
                            requestType.GetProperty("CatchException")?.SetValue(request, originalException);

                            _logger.LogTrace("Set all request properties, calling ActivityExecutor");

                            // Execute using enhanced activity executor with correct generic types
                            var resultTask = (Task)genericExecuteMethod.Invoke(_activityExecutor, new[] { request })!;
                            await resultTask;

                            _logger.LogTrace("ActivityExecutor completed, getting result");

                            // Get the result using reflection
                            var resultProperty = resultTask.GetType().GetProperty("Result");
                            var activityResult = resultProperty?.GetValue(resultTask) as TypedActivityExecutionResult<object>;

                            if (activityResult == null)
                                throw new InvalidOperationException("Failed to get catch block activity execution result");

                            _logger.LogTrace("Got activity result: Success={IsSuccess}", activityResult.IsSuccess);

                            if (!activityResult.IsSuccess)
                            {
                                _logger.LogError("Catch block activity failed: {ErrorMessage}", activityResult.ErrorMessage);
                                // Catch block activity failed - throw exception to be caught by outer catch
                                throw activityResult.Exception ?? new Exception(activityResult.ErrorMessage ?? "Catch block activity failed");
                            }

                            // Update execution state with activity result
                            executionState.LastStepResult = activityResult.OutputData;
                            _logger.LogTrace("Catch block activity succeeded");
                        }
                        else
                        {
                            // For non-activity steps in catch blocks, execute normally
                            var result = await ExecuteStepWithPropagationAsync(catchStep, context, executionState, cancellationToken);
                            if (!result.IsSuccess)
                            {
                                if (context.WorkflowInstance.Status == WorkflowStatus.Suspended)
                                {
                                    return result;
                                }
                                // Catch block step failed - throw exception
                                throw result.Exception ?? new Exception(result.ErrorMessage ?? "Catch block step failed");
                            }
                            if (executionState.PendingException != null)
                            {
                                throw executionState.PendingException;
                            }
                        }
                    }

                    // If we get here, all catch steps succeeded - clear the exception
                    _logger.LogDebug("Catch block handled exception successfully: {CatchBlockName}", catchBlock.Name);
                    executionState.PendingException = null; // Exception handled successfully
                    resumingCatchIndex = -1;
                    break; // Exit catch block processing
                }
                catch (Exception catchEx)
                {
                    _logger.LogError(catchEx, "Catch block threw exception: {CatchBlockName} - {ExceptionType} - {Message}",
                        catchBlock.Name, catchEx.GetType().Name, catchEx.Message);

                    // Catch block threw an exception - set as new pending exception
                    executionState.PendingException = catchEx;
                    resumingCatchIndex = -1;
                    _logger.LogDebug("Continuing to next catch block to handle new exception: {ExceptionType}", catchEx.GetType().Name);
                    // Continue to next catch block to see if it can handle this new exception
                }
            }
        }

        // Execute finally blocks regardless of exception state
        if (step.FinallySteps.Count > 0)
        {
            _logger.LogDebug("Executing finally block with {Count} steps: {StepId}", step.FinallySteps.Count, step.Id);

            var pendingExceptionBeforeFinally = resumingFinally
                ? RestoreHandlerException(tryFrame, "FinallyException")
                : executionState.PendingException;
            if (!resumingFinally)
            {
                SaveHandlerException(tryFrame, "FinallyException", pendingExceptionBeforeFinally);
            }
            tryFrame.State["Phase"] = "Finally";
            executionState.PendingException = null;

            try
            {
                var finallyStartIndex = resumingFinally
                    ? GetContinuationStartIndex(step.FinallySteps, executionState)
                    : 0;
                for (var finallyIndex = finallyStartIndex; finallyIndex < step.FinallySteps.Count; finallyIndex++)
                {
                    var finallyStep = step.FinallySteps[finallyIndex];
                    var result = await ExecuteStepWithPropagationAsync(finallyStep, context, executionState, cancellationToken);
                    if (!result.IsSuccess)
                    {
                        if (context.WorkflowInstance.Status == WorkflowStatus.Suspended)
                        {
                            return result;
                        }
                        // Finally block failed - this takes precedence over any pending exception
                        _logger.LogError("Finally block step failed: {StepId} - {Error}", finallyStep.Id, result.ErrorMessage);
                        executionState.PendingException = result.Exception ??
                            new Exception(result.ErrorMessage ?? "Finally block failed");
                        break;
                    }

                    // Check if the finally step set a pending exception (from activity failure)
                    if (executionState.PendingException != null)
                    {
                        // Finally block activity threw an exception - this takes precedence over any pending exception
                        _logger.LogError("Finally block activity threw exception: {StepId} - {ExceptionType}",
                            finallyStep.Id, executionState.PendingException.GetType().Name);
                        break;
                    }

                    // Update the main execution state with finally results
                    executionState.LastStepResult = result.OutputData;
                }

                // If finally block succeeded and we had a pending exception before, restore it
                if (executionState.PendingException == null && pendingExceptionBeforeFinally != null)
                {
                    executionState.PendingException = pendingExceptionBeforeFinally;
                }
            }
            catch (Exception finallyEx)
            {
                // Finally block threw an exception - this takes precedence over any pending exception
                _logger.LogError(finallyEx, "Finally block threw exception: {StepId}", step.Id);
                executionState.PendingException = finallyEx;
            }
        }

        // Pop try block from stack
        if (executionState.TryStack.Count > 0)
        {
            executionState.TryStack.Pop();
        }

        RemoveFrame(executionState, step.Id);

        // If we still have a pending exception after all catch/finally processing, 
        // it means it wasn't handled and should propagate
        if (executionState.PendingException != null)
        {
            _logger.LogDebug("Try/catch/finally completed with unhandled exception: {ExceptionType}",
                executionState.PendingException.GetType().Name);

            // Continue execution - the exception will be checked at the end of ExecuteStepsAsync
            return new StepExecutionResult
            {
                IsSuccess = true, // Continue execution to allow exception to propagate
                OutputData = executionState.LastStepResult
            };
        }

        _logger.LogDebug("Try/catch/finally completed successfully: {StepId}", step.Id);

        // Try/catch blocks are transparent pass-through containers
        // The step after the try/catch should see the step that came BEFORE the try/catch as its PreviousStep,
        // not the last step within the try/catch
        return new StepExecutionResult
        {
            IsSuccess = true,
            OutputData = stepBeforeTryCatch // Pass through the step that came before the try/catch
        };
    }

    /// <summary>
    ///     Executes an activity step with propagation-based exception handling
    /// </summary>
    private async Task<StepExecutionResult> ExecuteActivityStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
    {
        // Skip execution if we have a pending exception (activities should be skipped when exceptions are pending)
        if (executionState.PendingException != null)
        {
            _logger.LogDebug("Skipping activity step {StepId} due to pending exception", step.Id);
            return new StepExecutionResult
            {
                IsSuccess = true,
                OutputData = executionState.LastStepResult
            };
        }

        if (step.ActivityType == null)
            throw new InvalidOperationException($"Activity type is null for step {step.Id}");

        _logger.LogDebug("Executing activity step {StepName} with propagation", step.Name);

        try
        {
            // Use reflection to create the correct generic type based on step.PreviousStepDataType
            var workflowDataType = typeof(TWorkflowData);
            var previousStepDataType = step.PreviousStepDataType ?? typeof(object);

            _logger.LogTrace("Creating typed execution request with WorkflowData={WorkflowDataType}, PreviousStepData={PreviousStepDataType}",
                workflowDataType.Name, previousStepDataType.Name);

            // Get the generic method ExecuteTypedActivityAsync<TWorkflowData, TPreviousStepData>
            var executeMethod = typeof(IActivityExecutor).GetMethod(nameof(IActivityExecutor.ExecuteTypedActivityAsync));
            if (executeMethod == null)
                throw new InvalidOperationException("ExecuteTypedActivityAsync method not found");

            var genericExecuteMethod = executeMethod.MakeGenericMethod(workflowDataType, previousStepDataType);

            // Create the request using reflection
            var requestType = typeof(TypedActivityExecutionRequest<,>).MakeGenericType(workflowDataType, previousStepDataType);
            var request = Activator.CreateInstance(requestType);

            // Set properties using reflection with proper type conversion
            requestType.GetProperty("ActivityType")?.SetValue(request, step.ActivityType);
            requestType.GetProperty("WorkflowData")?.SetValue(request, context.WorkflowData);

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
                    // If types don't match, create a default instance of the expected type
                    // This ensures the context has the correct generic type signature
                    try
                    {
                        convertedPreviousStepData = Activator.CreateInstance(previousStepDataType);
                    }
                    catch
                    {
                        // If we can't create an instance, use null but this will cause context type issues
                        convertedPreviousStepData = null;
                    }
                }
            }
            else if (previousStepDataType != typeof(object))
            {
                // No runtime result but we need a specific type - create default instance
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
                // Use the actual runtime result or null for object type
                convertedPreviousStepData = executionState.LastStepResult;
            }

            requestType.GetProperty("PreviousStepData")?.SetValue(request, convertedPreviousStepData);
            requestType.GetProperty("InputMappings")?.SetValue(request, step.InputMappings);
            requestType.GetProperty("OutputMappings")?.SetValue(request, step.OutputMappings);
            requestType.GetProperty("Step")?.SetValue(request, step);
            requestType.GetProperty("ExecutionContext")?.SetValue(request, context);
            requestType.GetProperty("CancellationToken")?.SetValue(request, cancellationToken);
            requestType.GetProperty("CatchException")?.SetValue(request, executionState.PendingException);

            // Execute using enhanced activity executor with correct generic types
            var resultTask = (Task)genericExecuteMethod.Invoke(_activityExecutor, new[] { request })!;
            await resultTask;

            // Get the result using reflection
            var resultProperty = resultTask.GetType().GetProperty("Result");
            var result = resultProperty?.GetValue(resultTask) as TypedActivityExecutionResult<object>;

            if (result == null)
                throw new InvalidOperationException("Failed to get activity execution result");

            if (result.IsSuccess)
            {
                _logger.LogDebug("Activity step {StepName} completed successfully", step.Name);

                // Add success trace
                await _tracer.TraceAsync(context.WorkflowInstance.InstanceId, new ExecutionTraceEntry
                {
                    StepNumber = context.WorkflowInstance.CurrentStepNumber,
                    EntryType = TraceEntryType.ActivityCompleted,
                    ActivityName = step.ActivityType.Name,
                    Duration = result.ExecutionTime,
                    OutputDataJson = JsonSerializer.Serialize(result.OutputData)
                }, cancellationToken);

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
                _logger.LogError("Activity step {StepName} failed: {ErrorMessage}", step.Name, result.ErrorMessage);

                // Add failure trace
                await _tracer.TraceAsync(context.WorkflowInstance.InstanceId, new ExecutionTraceEntry
                {
                    StepNumber = context.WorkflowInstance.CurrentStepNumber,
                    EntryType = TraceEntryType.ActivityFailed,
                    ActivityName = step.ActivityType.Name,
                    Duration = result.ExecutionTime,
                    ErrorMessage = result.ErrorMessage,
                    StackTrace = result.Exception?.StackTrace
                }, cancellationToken);

                // Set pending exception
                executionState.PendingException = result.Exception ?? new Exception(result.ErrorMessage ?? "Activity failed");

                return new StepExecutionResult
                {
                    IsSuccess = true, // Continue execution to find catch blocks
                    OutputData = executionState.LastStepResult
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute activity step {StepName}", step.Name);

            // Set pending exception
            executionState.PendingException = ex;

            return new StepExecutionResult
            {
                IsSuccess = true, // Continue execution to find catch blocks
                OutputData = executionState.LastStepResult
            };
        }
    }

    /// <summary>
    ///     Executes a sequence step with propagation
    /// </summary>
    private async Task<StepExecutionResult> ExecuteSequenceStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Executing sequence step: {StepId}", step.Id);

        // Execute sequence steps with current execution state
        var startIndex = GetContinuationStartIndex(step.SequenceSteps, executionState);
        for (var index = startIndex; index < step.SequenceSteps.Count; index++)
        {
            var sequenceStep = step.SequenceSteps[index];
            var result = await ExecuteStepWithPropagationAsync(sequenceStep, context, executionState, cancellationToken);
            if (!result.IsSuccess)
            {
                return result;
            }

            if (executionState.PendingException != null)
            {
                break;
            }
        }

        return new StepExecutionResult
        {
            IsSuccess = true,
            OutputData = executionState.LastStepResult
        };
    }

    /// <summary>
    ///     Executes a conditional step with propagation
    /// </summary>
    private async Task<StepExecutionResult> ExecuteConditionalStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        if (step.CompiledCondition == null)
            throw new InvalidOperationException($"CompiledCondition is null for conditional step {step.Id}");

        _logger.LogDebug("Executing conditional step: {StepId}", step.Id);

        // On resume the selected branch is fixed by the suspended step's path.
        var resumeStepId = GetResumeStepId(executionState);
        var resumingBranch = resumeStepId != null &&
            (ContainsStep(step.ThenSteps, resumeStepId) || ContainsStep(step.ElseSteps, resumeStepId));
        var conditionalFrame = EnterFrame(executionState, step, WorkflowStepType.Conditional.ToString());
        var originalPreviousStepData = resumingBranch
            ? RestoreFrameValue(conditionalFrame, "Entry")
            : context.PreviousStepData;
        if (!resumingBranch)
        {
            SaveFrameValue(conditionalFrame, "Entry", originalPreviousStepData);
        }

        var conditionResult = resumeStepId != null && ContainsStep(step.ThenSteps, resumeStepId)
            ? true
            : resumeStepId != null && ContainsStep(step.ElseSteps, resumeStepId)
                ? false
                : step.CompiledCondition(CreateConditionEvaluationContext(context, executionState.LastStepResult, step));

        _logger.LogDebug("Condition evaluated to: {Result} for step: {StepId}", conditionResult, step.Id);

        // Choose which branch to execute
        var branchSteps = conditionResult ? step.ThenSteps : step.ElseSteps;

        if (branchSteps.Count == 0)
        {
            RemoveFrame(executionState, step.Id);
            return new StepExecutionResult
            {
                IsSuccess = true,
                OutputData = executionState.LastStepResult
            };
        }

        // Track the previous step data as we execute branch steps
        var currentStepPreviousData = resumingBranch ? executionState.LastStepResult : originalPreviousStepData;

        // Execute chosen branch
        var branchStartIndex = GetContinuationStartIndex(branchSteps, executionState);
        for (var branchIndex = branchStartIndex; branchIndex < branchSteps.Count; branchIndex++)
        {
            var branchStep = branchSteps[branchIndex];
            // Each step in the conditional branch should see the previous step in the branch
            // The first step sees the step before the conditional, subsequent steps see the previous step in the branch
            context.PreviousStepData = currentStepPreviousData;

            var result = await ExecuteStepWithPropagationAsync(branchStep, context, executionState, cancellationToken);
            if (!result.IsSuccess)
            {
                // Restore original context before returning
                context.PreviousStepData = originalPreviousStepData;
                if (context.WorkflowInstance.Status != WorkflowStatus.Suspended)
                {
                    RemoveFrame(executionState, step.Id);
                }
                return result;
            }

            // Update the previous step data for the next step in the branch
            currentStepPreviousData = result.OutputData;
        }

        // Conditional blocks are transparent pass-through containers
        // The step after the conditional should see the step that came BEFORE the conditional
        // as its PreviousStep, not the last step within the conditional branch
        // So we restore the original previous step data for the next step
        context.PreviousStepData = originalPreviousStepData;
        RemoveFrame(executionState, step.Id);

        return new StepExecutionResult
        {
            IsSuccess = true,
            OutputData = originalPreviousStepData // Pass through the original previous step data
        };
    }

    /// <summary>
    ///     Executes a parallel step with propagation
    /// </summary>
    private async Task<StepExecutionResult> ExecuteParallelStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        if (step.ParallelBranches == null || step.ParallelBranches.Count == 0)
            throw new InvalidOperationException($"Parallel step {step.Id} has no branches");

        var resumeStepId = GetResumeStepId(executionState);
        var resumingParallel = resumeStepId != null && ContainsStep(step, resumeStepId);
        var parallelFrame = EnterFrame(executionState, step, WorkflowStepType.Parallel.ToString());
        var completedBranches = parallelFrame.State.TryGetValue("CompletedBranches", out var savedBranches)
            ? JsonSerializer.Deserialize<HashSet<int>>(savedBranches) ?? []
            : new HashSet<int>();
        var branchTasks = new List<(int Index, Task<(StepExecutionResult Result, ExecutionState State)> Task)>();

        for (var index = 0; index < step.ParallelBranches.Count; index++)
        {
            if (completedBranches.Contains(index))
            {
                continue;
            }

            var branch = step.ParallelBranches[index];
            var ownsResumeStep = resumingParallel && ContainsStep(branch, resumeStepId!);
            if (resumingParallel && !ownsResumeStep)
            {
                throw new InvalidOperationException($"Parallel branch {index} has no checkpoint for continuation");
            }

            var branchState = new ExecutionState
            {
                LastStepResult = executionState.LastStepResult,
                Frames = new List<ExecutionFrame>(executionState.Frames),
                StepMetadata = ownsResumeStep
                    ? new Dictionary<string, object>(executionState.StepMetadata)
                    : new Dictionary<string, object>()
            };
            branchTasks.Add((index, ExecuteBranchWithStateAsync(branch, context, branchState, cancellationToken)));
        }

        try
        {
            await Task.WhenAll(branchTasks.Select(branch => branch.Task));
            var results = branchTasks.Select(branch =>
                (branch.Index, branch.Task.Result.Result, branch.Task.Result.State)).ToList();
            var waiting = results.Where(branch => !branch.Result.IsSuccess &&
                branch.Result.ErrorMessage?.StartsWith("Workflow suspended", StringComparison.Ordinal) == true).ToList();
            var failures = results.Where(branch => branch.State.PendingException != null ||
                (!branch.Result.IsSuccess && !waiting.Any(wait => wait.Index == branch.Index))).ToList();

            if (failures.Count > 0)
            {
                var firstFailure = failures[0];
                if (waiting.Count > 0)
                {
                    context.WorkflowInstance.Status = WorkflowStatus.Failed;
                    context.WorkflowInstance.SuspensionInfo = null;
                }
                executionState.PendingException = firstFailure.State.PendingException ?? firstFailure.Result.Exception ??
                    new Exception(firstFailure.Result.ErrorMessage ?? "Parallel branch failed");
                RemoveFrame(executionState, step.Id);
                return new StepExecutionResult { IsSuccess = true, OutputData = executionState.LastStepResult };
            }

            foreach (var branch in results.Where(branch => branch.Result.IsSuccess))
            {
                completedBranches.Add(branch.Index);
            }
            parallelFrame.State["CompletedBranches"] = JsonSerializer.Serialize(completedBranches.Order());

            if (waiting.Count > 0)
            {
                if (waiting.Count != 1)
                {
                    throw new NotSupportedException("Parallel execution with multiple simultaneous waits needs multiple event pointers");
                }

                var instance = context.WorkflowInstance;
                var suspendedState = JsonSerializer.Deserialize<SuspendedExecutionState>(instance.ExecutionStateJson)
                    ?? throw new InvalidOperationException("Parallel wait has no saved execution state");
                var savedParallelFrame = suspendedState.Frames.FirstOrDefault(frame => frame.StepId == step.Id)
                    ?? throw new InvalidOperationException("Parallel wait has no saved branch frame");
                savedParallelFrame.State["CompletedBranches"] = parallelFrame.State["CompletedBranches"];
                instance.ExecutionStateJson = JsonSerializer.Serialize(suspendedState);
                instance.WorkflowDataJson = JsonSerializer.Serialize(context.WorkflowData);
                var snapshotFrame = instance.ExecutionSnapshot?.Frames.FirstOrDefault(frame => frame.StepId == step.Id);
                if (snapshotFrame != null)
                {
                    snapshotFrame.State["CompletedBranches"] = parallelFrame.State["CompletedBranches"];
                }
                await _stateRepository.SaveWorkflowInstanceAsync(instance);
                if (resumingParallel)
                {
                    executionState.StepMetadata.Remove("ResumeStepId");
                }
                return waiting[0].Result;
            }

            if (resumingParallel)
            {
                executionState.StepMetadata.Remove("ResumeStepId");
            }
            RemoveFrame(executionState, step.Id);
            return new StepExecutionResult { IsSuccess = true, OutputData = executionState.LastStepResult };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parallel step execution failed with exception: {StepId}", step.Id);
            if (context.WorkflowInstance.Status == WorkflowStatus.Suspended)
            {
                context.WorkflowInstance.Status = WorkflowStatus.Failed;
                context.WorkflowInstance.SuspensionInfo = null;
            }
            RemoveFrame(executionState, step.Id);
            executionState.PendingException = ex;
            return new StepExecutionResult { IsSuccess = true, OutputData = executionState.LastStepResult };
        }
    }

    /// <summary>
    ///     Executes a parallel branch with propagation
    /// </summary>
    private async Task<StepExecutionResult> ExecuteBranchWithPropagationAsync<TWorkflowData>(
        List<WorkflowStep> branchSteps,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState branchExecutionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        var startIndex = GetResumeStepId(branchExecutionState) != null
            ? GetContinuationStartIndex(branchSteps, branchExecutionState)
            : 0;
        for (var index = startIndex; index < branchSteps.Count; index++)
        {
            var branchStep = branchSteps[index];
            var result = await ExecuteStepWithPropagationAsync(branchStep, context, branchExecutionState, cancellationToken);
            if (!result.IsSuccess)
            {
                return result;
            }
            if (branchExecutionState.PendingException != null)
            {
                break;
            }
        }

        return new StepExecutionResult
        {
            IsSuccess = true,
            OutputData = branchExecutionState.LastStepResult
        };
    }

    /// <summary>
    ///     Executes a parallel branch with state tracking
    /// </summary>
    private async Task<(StepExecutionResult Result, ExecutionState State)> ExecuteBranchWithStateAsync<TWorkflowData>(
        List<WorkflowStep> branchSteps,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState branchExecutionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        var result = await ExecuteBranchWithPropagationAsync(branchSteps, context, branchExecutionState, cancellationToken);
        return (result, branchExecutionState);
    }

    /// <summary>
    ///     Executes a loop step with propagation
    /// </summary>
    private async Task<StepExecutionResult> ExecuteLoopStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        if (step.CompiledCondition == null)
            throw new InvalidOperationException($"CompiledCondition is null for loop step {step.Id}");

        var isDoWhile = step.LoopType == LoopType.DoWhile;
        _logger.LogDebug("Executing {LoopType} loop step: {StepId}", step.LoopType, step.Id);

        var resumeStepId = GetResumeStepId(executionState);
        var resumingIteration = resumeStepId != null && ContainsStep(step.LoopBodySteps, resumeStepId);
        const int maxIterations = 1000;
        var loopFrame = EnterFrame(executionState, step, step.LoopType.ToString());
        var iterationCount = resumingIteration && loopFrame.State.TryGetValue("IterationCount", out var savedIteration) &&
                             int.TryParse(savedIteration, out var parsedIteration)
            ? parsedIteration - 1
            : 0;

        // Store the original previous step data from before the loop
        // Loop conditions should always be evaluated with the data from the step that came BEFORE the loop
        var originalPreviousStepData = executionState.LastStepResult;

        // For nested loops, we need to track the context that was active when this loop started
        // This is the context that should be used for condition evaluation
        var loopEntryContext = resumingIteration
            ? RestoreFrameValue(loopFrame, "Entry")
            : context.PreviousStepData;
        if (!resumingIteration)
        {
            SaveFrameValue(loopFrame, "Entry", loopEntryContext);
        }

        while (iterationCount < maxIterations)
        {
            iterationCount++;
            executionState.Frames[^1].State["IterationCount"] = iterationCount.ToString();

            bool shouldExecuteBody;
            if (isDoWhile)
            {
                // DoWhile: Execute body first, then check condition (except for first iteration)
                shouldExecuteBody = resumingIteration || iterationCount == 1 || EvaluateLoopCondition(step, context, loopEntryContext);
            }
            else
            {
                // WhileDo: Check condition first, then execute body
                shouldExecuteBody = resumingIteration || EvaluateLoopCondition(step, context, loopEntryContext);
            }

            if (!shouldExecuteBody)
            {
                break;
            }

            // Execute loop body with correct context
            // Store the current context's PreviousStepData and restore it after loop body execution
            var originalContextPreviousStepData = context.PreviousStepData;

            // Set context's PreviousStepData to the step that came before the loop
            // This ensures activities inside the loop get the correct context type for their mappings
            context.PreviousStepData = loopEntryContext;

            // Track the previous step data as we execute loop body steps
            var currentStepPreviousData = loopEntryContext;

            var bodyStartIndex = resumingIteration ? GetContinuationStartIndex(step.LoopBodySteps, executionState) : 0;
            for (var bodyIndex = bodyStartIndex; bodyIndex < step.LoopBodySteps.Count; bodyIndex++)
            {
                var bodyStep = step.LoopBodySteps[bodyIndex];
                // Each step in the loop body should see the previous step in the loop body
                // The first step sees the step before the loop, subsequent steps see the previous step in the loop
                context.PreviousStepData = currentStepPreviousData;

                var result = await ExecuteStepWithPropagationAsync(bodyStep, context, executionState, cancellationToken);
                if (!result.IsSuccess)
                {
                    // Restore original context before returning
                    context.PreviousStepData = originalContextPreviousStepData;
                    return result;
                }

                // Update the previous step data for the next step in the loop body
                currentStepPreviousData = result.OutputData;
            }

            // Restore the original context's PreviousStepData after loop body execution
            context.PreviousStepData = originalContextPreviousStepData;
            resumingIteration = false;

            // For DoWhile, check condition after execution using loop entry context
            if (isDoWhile && !EvaluateLoopCondition(step, context, loopEntryContext))
            {
                break;
            }
        }

        if (iterationCount >= maxIterations)
        {
            var errorMessage = $"Loop exceeded maximum iterations ({maxIterations})";
            _logger.LogError("{ErrorMessage} for step: {StepId}", errorMessage, step.Id);
            executionState.PendingException = new InvalidOperationException(errorMessage);
        }

        RemoveFrame(executionState, step.Id);

        // Loops are transparent pass-through containers
        // The step after the loop should see the step that came BEFORE the loop
        // as its PreviousStep, not the last iteration result
        return new StepExecutionResult
        {
            IsSuccess = true,
            OutputData = loopEntryContext // Pass through the loop entry context
        };
    }

    /// <summary>
    ///     Evaluates loop condition using the original previous step data from before the loop
    /// </summary>
    private bool EvaluateLoopCondition<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        object? originalPreviousStepData)
    where TWorkflowData : class
    {
        try
        {
            // Always use the original previous step data from before the loop
            // This ensures the condition context type matches what was compiled in the builder
            var conditionContext = CreateConditionEvaluationContext(context, originalPreviousStepData, step);
            return step.CompiledCondition!(conditionContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate loop condition for step: {StepId}", step.Id);
            throw;
        }
    }

    /// <summary>
    ///     Executes a saga step with propagation using dedicated SagaExecutor
    /// </summary>
    private async Task<StepExecutionResult> ExecuteSagaStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Delegating saga execution to SagaExecutor: {StepId}", step.Id);

        SagaResumeInfo? resumeInfo = null;
        if (executionState.StepMetadata.TryGetValue("SagaResumeParentStepId", out var sagaResumeParentObj) &&
            sagaResumeParentObj is string sagaResumeParentId &&
            sagaResumeParentId == step.Id &&
            executionState.StepMetadata.TryGetValue("SagaResumeFromStepIndex", out var sagaResumeIndexObj) &&
            sagaResumeIndexObj is int sagaResumeIndex)
        {
            resumeInfo = new SagaResumeInfo
            {
                ResumeFromStepIndex = sagaResumeIndex,
                ResumeEventData = executionState.LastStepResult,
                CompletedSteps = new List<SagaExecutionStepInfo>()
            };

            executionState.StepMetadata.Remove("SagaResumeParentStepId");
            executionState.StepMetadata.Remove("SagaResumeFromStepIndex");
        }

        // Delegate to the dedicated SagaExecutor
        var result = await _sagaExecutor.ExecuteSagaStepAsync(step, context, executionState, cancellationToken, resumeInfo);
        if (resumeInfo != null)
        {
            executionState.StepMetadata.Remove("ResumeStepId");
        }

        if (context.WorkflowInstance.Status != WorkflowStatus.Suspended)
        {
            RemoveFrame(executionState, step.Id);
        }
        
        _logger.LogTrace("SagaExecutor returned - IsSuccess={IsSuccess}, HasPendingException={HasPendingException}", 
            result.IsSuccess, executionState.PendingException != null);
        
        return result;
    }


    /// <summary>
    ///     Executes a workflow invocation step with propagation
    /// </summary>
    private async Task<StepExecutionResult> ExecuteWorkflowInvocationStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        // Skip execution if we have a pending exception
        if (executionState.PendingException != null)
        {
            _logger.LogDebug("Skipping workflow invocation step {StepId} due to pending exception", step.Id);
            return new StepExecutionResult
            {
                IsSuccess = true,
                OutputData = executionState.LastStepResult
            };
        }

        // Delegate to the dedicated WorkflowInvoker
        return await _workflowInvoker.ExecuteWorkflowInvocationAsync(step, context, executionState, cancellationToken);
    }

    /// <summary>
    ///     Executes a suspend/resume step with propagation
    /// </summary>
    private async Task<StepExecutionResult> ExecuteSuspendResumeStepWithPropagationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
    where TWorkflowData : class
    {
        // Skip execution if we have a pending exception
        if (executionState.PendingException != null)
        {
            _logger.LogDebug("Skipping suspend/resume step {StepId} due to pending exception", step.Id);
            return new StepExecutionResult
            {
                IsSuccess = true,
                OutputData = executionState.LastStepResult
            };
        }

        // Delegate to the dedicated SuspendResumeExecutor
        return await _suspendResumeExecutor.ExecuteSuspendResumeStepAsync(step, context, executionState, cancellationToken);
    }

    /// <summary>
    ///     Continues workflow execution from a suspend point
    /// </summary>
    private async Task<WorkflowExecutionResult> ContinueWorkflowExecutionAsync(
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object workflowData,
        ExecutionState restoredExecutionState,
        object resumeEvent,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Continuing workflow {InstanceId} from step {CurrentStep}", 
            instance.InstanceId, instance.CurrentStepNumber);

        try
        {
            // Get the workflow data type
            var workflowDataType = Type.GetType(instance.WorkflowDataType);
            if (workflowDataType == null)
            {
                throw new InvalidOperationException($"Failed to get workflow data type: {instance.WorkflowDataType}");
            }

            // Create execution context using reflection to handle generic types
            var contextType = typeof(StepExecutionContext<>).MakeGenericType(workflowDataType);
            var executionContext = Activator.CreateInstance(contextType);

            // Set context properties using reflection
            contextType.GetProperty("WorkflowInstance")?.SetValue(executionContext, instance);
            contextType.GetProperty("WorkflowDefinition")?.SetValue(executionContext, definition);
            contextType.GetProperty("WorkflowData")?.SetValue(executionContext, workflowData);
            contextType.GetProperty("PreviousStepData")?.SetValue(executionContext, resumeEvent);

            var resumeStepId = restoredExecutionState.StepMetadata.TryGetValue("ResumeStepId", out var resumeStepIdObj) && resumeStepIdObj is string stepId && !string.IsNullOrWhiteSpace(stepId)
                ? stepId
                : null;

            var resumeFromStepIndex = restoredExecutionState.StepMetadata.TryGetValue("ResumeFromStepIndex", out var resumeIndexObj) && resumeIndexObj is int resumeIndex
                ? resumeIndex
                : instance.CurrentStepNumber + 1;

            if (!string.IsNullOrWhiteSpace(resumeStepId))
            {
                PopulateSagaResumeMetadata(definition.Steps, resumeStepId, restoredExecutionState.StepMetadata);
            }

            // Re-enter the owning control-flow scopes instead of flattening their children.
            var remainingSteps = !string.IsNullOrWhiteSpace(resumeStepId)
                ? definition.Steps
                : definition.Steps.Skip(resumeFromStepIndex).ToList();
            
            _logger.LogDebug("Executing remaining steps: {RemainingStepCount} steps from index {StartIndex}", 
                remainingSteps.Count, resumeFromStepIndex);

            // Execute remaining steps using reflection to call the generic method
            var executeMethod = typeof(WorkflowEngine).GetMethod(nameof(ExecuteStepsAsync), BindingFlags.NonPublic | BindingFlags.Instance);
            if (executeMethod == null)
            {
                throw new InvalidOperationException("ExecuteStepsAsync method not found");
            }

            var genericExecuteMethod = executeMethod.MakeGenericMethod(workflowDataType);
            var executeTask = (Task)genericExecuteMethod.Invoke(this, new object?[] { remainingSteps, executionContext, cancellationToken, restoredExecutionState })!;
            await executeTask;

            // Get the result using reflection
            var resultProperty = executeTask.GetType().GetProperty("Result");
            var stepResult = resultProperty?.GetValue(executeTask) as StepExecutionResult;

            if (stepResult == null)
            {
                throw new InvalidOperationException("Failed to get step execution result");
            }

            if (GetResumeStepId(restoredExecutionState) is string unhandledResumeStepId)
            {
                throw new InvalidOperationException($"Suspended step {unhandledResumeStepId} was not reached during continuation");
            }

            // Update final state only if not suspended again
            if (instance.Status != WorkflowStatus.Suspended)
            {
                instance.Status = stepResult.IsSuccess ? WorkflowStatus.Completed : WorkflowStatus.Failed;
                instance.CompletedAt = DateTime.UtcNow;

                if (!stepResult.IsSuccess)
                {
                    instance.LastError = stepResult.ErrorMessage;
                    instance.LastErrorStackTrace = stepResult.ErrorStackTrace;
                }

                // Update workflow data
                instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData);

                // Save final state
                await _stateRepository.SaveWorkflowInstanceAsync(instance);
            }

            // Add workflow completion trace
            await _tracer.TraceAsync(instance.InstanceId, new ExecutionTraceEntry
            {
                EntryType = stepResult.IsSuccess ? TraceEntryType.WorkflowCompleted : TraceEntryType.WorkflowFailed,
                ActivityName = definition.Name,
                OutputDataJson = JsonSerializer.Serialize(workflowData),
                ErrorMessage = stepResult.ErrorMessage,
                StackTrace = stepResult.ErrorStackTrace,
                Duration = DateTime.UtcNow - instance.StartedAt
            }, cancellationToken);

            // Map workflow instance status to execution result status
            var executionStatus = instance.Status switch
            {
                WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
                WorkflowStatus.Suspended => WorkflowExecutionStatus.Suspended,
                WorkflowStatus.Failed => WorkflowExecutionStatus.Faulted,
                WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
                _ => WorkflowExecutionStatus.Faulted
            };

            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = executionStatus,
                WorkflowData = workflowData,
                ErrorMessage = stepResult.ErrorMessage,
                ErrorStackTrace = stepResult.ErrorStackTrace,
                ExecutionTime = DateTime.UtcNow - instance.StartedAt!.Value,
                TraceEntries = [.. instance.ExecutionHistory]
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to continue workflow {InstanceId}: {ErrorMessage}", 
                instance.InstanceId, ex.Message);
            
            // Update instance state
            instance.Status = WorkflowStatus.Failed;
            instance.CompletedAt = DateTime.UtcNow;
            instance.LastError = ex.Message;
            instance.LastErrorStackTrace = ex.StackTrace;

            await _stateRepository.SaveWorkflowInstanceAsync(instance);

            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = WorkflowExecutionStatus.Faulted,
                WorkflowData = workflowData,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.StackTrace,
                ExecutionTime = DateTime.UtcNow - instance.StartedAt!.Value
            };
        }
    }

    private static string? GetResumeStepId(ExecutionState executionState)
    {
        return executionState.StepMetadata.TryGetValue("ResumeStepId", out var value) && value is string id && !string.IsNullOrWhiteSpace(id)
            ? id
            : null;
    }

    private static ExecutionFrame EnterFrame(ExecutionState executionState, WorkflowStep step, string kind)
    {
        if (GetResumeStepId(executionState) != null)
        {
            var savedFrame = executionState.Frames.FirstOrDefault(frame => frame.StepId == step.Id && frame.Kind == kind);
            if (savedFrame != null)
            {
                return savedFrame;
            }
        }

        var frame = new ExecutionFrame
        {
            Kind = kind,
            StepId = step.Id,
            ParentFrameId = executionState.Frames.LastOrDefault()?.FrameId
        };
        executionState.Frames.Add(frame);
        return frame;
    }

    private static void RemoveFrame(ExecutionState executionState, string stepId)
    {
        var index = executionState.Frames.FindLastIndex(frame => frame.StepId == stepId);
        if (index >= 0)
        {
            executionState.Frames.RemoveAt(index);
        }
    }

    private static void SaveFrameValue(ExecutionFrame frame, string key, object? value)
    {
        frame.RuntimeValues[key] = value;
        if (value == null)
        {
            frame.State[$"{key}IsNull"] = "true";
        }
    }

    private static void SaveHandlerException(ExecutionFrame frame, string key, Exception? exception)
    {
        frame.State.Remove($"{key}Type");
        frame.State.Remove($"{key}Message");
        if (exception == null)
        {
            return;
        }

        frame.State[$"{key}Type"] = exception.GetType().AssemblyQualifiedName!;
        frame.State[$"{key}Message"] = exception.Message;
    }

    private static Exception? RestoreHandlerException(ExecutionFrame frame, string key)
    {
        if (!frame.State.TryGetValue($"{key}Type", out var typeName))
        {
            return null;
        }

        var type = Type.GetType(typeName)
            ?? throw new InvalidOperationException($"Saved exception type {typeName} is unavailable");
        if (!typeof(Exception).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"Saved handler type {typeName} is not an exception");
        }

        frame.State.TryGetValue($"{key}Message", out var message);
        var messageConstructor = type.GetConstructor([typeof(string)]);
        if (messageConstructor != null)
        {
            return (Exception)messageConstructor.Invoke([message]);
        }

        var emptyConstructor = type.GetConstructor(Type.EmptyTypes);
        return emptyConstructor != null
            ? (Exception)emptyConstructor.Invoke(null)
            : throw new InvalidOperationException($"Saved exception type {typeName} cannot be reconstructed");
    }

    private static object? RestoreFrameValue(ExecutionFrame frame, string key)
    {
        if (frame.RuntimeValues.TryGetValue(key, out var runtimeValue))
        {
            return runtimeValue;
        }

        if (!frame.State.TryGetValue($"{key}Type", out var typeName) ||
            !frame.State.TryGetValue($"{key}Json", out var json))
        {
            return null;
        }

        var valueType = Type.GetType(typeName)
            ?? throw new InvalidOperationException($"Saved workflow value type {typeName} is unavailable");
        return JsonSerializer.Deserialize(json, valueType);
    }

    private static int GetContinuationStartIndex(List<WorkflowStep> steps, ExecutionState executionState)
    {
        var resumeStepId = GetResumeStepId(executionState);
        if (resumeStepId == null)
        {
            return 0;
        }

        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].Id == resumeStepId)
            {
                executionState.StepMetadata.Remove("ResumeStepId");
                return index + 1;
            }

            if (ContainsStep(steps[index], resumeStepId))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Suspended step {resumeStepId} was not found in the active workflow scope");
    }

    private void PopulateSagaResumeMetadata(List<WorkflowStep> steps, string suspendedStepId, Dictionary<string, object> metadata)
    {
        if (TryFindSagaResumePoint(steps, suspendedStepId, out var sagaStepId, out var resumeIndex))
        {
            metadata["SagaResumeParentStepId"] = sagaStepId;
            metadata["SagaResumeFromStepIndex"] = resumeIndex;
        }
    }

    private bool TryFindSagaResumePoint(List<WorkflowStep> steps, string suspendedStepId, out string sagaStepId, out int resumeIndex)
    {
        foreach (var step in steps)
        {
            if (step.StepType == WorkflowStepType.Saga)
            {
                for (var index = 0; index < step.SequenceSteps.Count; index++)
                {
                    if (step.SequenceSteps[index].Id == suspendedStepId ||
                        ContainsStep(step.SequenceSteps[index], suspendedStepId))
                    {
                        sagaStepId = step.Id;
                        resumeIndex = step.SequenceSteps[index].Id == suspendedStepId ? index + 1 : index;
                        return true;
                    }
                }
            }

            if (TryFindSagaResumePoint(step.SequenceSteps, suspendedStepId, out sagaStepId, out resumeIndex) ||
                TryFindSagaResumePoint(step.ThenSteps, suspendedStepId, out sagaStepId, out resumeIndex) ||
                TryFindSagaResumePoint(step.ElseSteps, suspendedStepId, out sagaStepId, out resumeIndex) ||
                TryFindSagaResumePoint(step.LoopBodySteps, suspendedStepId, out sagaStepId, out resumeIndex) ||
                TryFindSagaResumePoint(step.FinallySteps, suspendedStepId, out sagaStepId, out resumeIndex))
            {
                return true;
            }

            foreach (var branch in step.ParallelBranches)
            {
                if (TryFindSagaResumePoint(branch, suspendedStepId, out sagaStepId, out resumeIndex))
                {
                    return true;
                }
            }

            if (step.StepMetadata.TryGetValue("OutcomeBranches", out var branchesObj) && branchesObj is System.Collections.IList branches)
            {
                foreach (var branchObj in branches)
                {
                    if (branchObj.GetType().GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)?.GetValue(branchObj) is List<WorkflowStep> outcomeSteps &&
                        TryFindSagaResumePoint(outcomeSteps, suspendedStepId, out sagaStepId, out resumeIndex))
                    {
                        return true;
                    }
                }
            }

            if (step.StepMetadata.TryGetValue("DefaultBranch", out var defaultBranchObj) && defaultBranchObj != null &&
                defaultBranchObj.GetType().GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)?.GetValue(defaultBranchObj) is List<WorkflowStep> defaultSteps &&
                TryFindSagaResumePoint(defaultSteps, suspendedStepId, out sagaStepId, out resumeIndex))
            {
                return true;
            }
        }

        sagaStepId = string.Empty;
        resumeIndex = -1;
        return false;
    }

    private static bool ContainsStep(WorkflowStep step, string targetStepId)
    {
        if (step.Id == targetStepId)
        {
            return true;
        }

        if (ContainsStep(step.SequenceSteps, targetStepId) ||
            ContainsStep(step.ThenSteps, targetStepId) ||
            ContainsStep(step.ElseSteps, targetStepId) ||
            ContainsStep(step.LoopBodySteps, targetStepId) ||
            ContainsStep(step.FinallySteps, targetStepId))
        {
            return true;
        }

        foreach (var catchBlock in step.CatchBlocks)
        {
            if (ContainsStep(catchBlock.SequenceSteps, targetStepId))
            {
                return true;
            }
        }

        foreach (var branch in step.ParallelBranches)
        {
            if (ContainsStep(branch, targetStepId))
            {
                return true;
            }
        }

        if (step.StepMetadata.TryGetValue("OutcomeBranches", out var branchesObj) && branchesObj is IList branches)
        {
            foreach (var branchObj in branches)
            {
                if (branchObj.GetType().GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)?.GetValue(branchObj) is List<WorkflowStep> outcomeBranchSteps &&
                    ContainsStep(outcomeBranchSteps, targetStepId))
                {
                    return true;
                }
            }
        }

        return step.StepMetadata.TryGetValue("DefaultBranch", out var defaultBranchObj) &&
               defaultBranchObj != null &&
               defaultBranchObj.GetType().GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)?.GetValue(defaultBranchObj) is List<WorkflowStep> defaultBranchSteps &&
               ContainsStep(defaultBranchSteps, targetStepId);
    }

    private static bool ContainsStep(List<WorkflowStep> steps, string targetStepId)
    {
        foreach (var step in steps)
        {
            if (ContainsStep(step, targetStepId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Creates context for condition evaluation
    /// </summary>
    private object CreateConditionEvaluationContext<TWorkflowData>(
        StepExecutionContext<TWorkflowData> stepContext,
        object? previousStepData,
        WorkflowStep step)
    where TWorkflowData : class
    {
        // This will create the appropriate WorkflowContext<TWorkflowData> or 
        // WorkflowContext<TWorkflowData, TPreviousStepData> based on actual data
        if (previousStepData != null)
        {
            // Use the actual type of previousStepData instead of step.PreviousStepDataType
            // This is especially important for loops where the iteration data type might be different
            var actualPreviousStepDataType = previousStepData.GetType();

            // Create WorkflowContext<TWorkflowData, TPreviousStepData>
            var contextType =
                typeof(WorkflowContext<,>).MakeGenericType(typeof(TWorkflowData), actualPreviousStepDataType);
            var context = Activator.CreateInstance(contextType);

            // Set properties using reflection for now
            contextType.GetProperty("WorkflowData")?.SetValue(context, stepContext.WorkflowData);
            contextType.GetProperty("PreviousStep")?.SetValue(context, previousStepData);

            return context!;
        }

        // Create WorkflowContext<TWorkflowData>
        return new WorkflowContext<TWorkflowData>
        {
            WorkflowData = stepContext.WorkflowData
        };
    }

}
