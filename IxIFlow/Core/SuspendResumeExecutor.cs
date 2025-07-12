using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
/// Handles suspend/resume operations with proper separation of concerns
/// </summary>
public class SuspendResumeExecutor(
    IWorkflowStateRepository stateRepository,
    IWorkflowTracer tracer,
    ILogger<SuspendResumeExecutor> logger,
    IServiceProvider serviceProvider)
    : ISuspendResumeExecutor
{
    private readonly IWorkflowStateRepository _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
    private readonly IWorkflowTracer _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
    private readonly ILogger<SuspendResumeExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    /// <summary>
    /// Executes a suspend/resume step with propagation-based exception handling
    /// </summary>
    public async Task<StepExecutionResult> ExecuteSuspendResumeStepAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Executing suspend/resume step: {StepId}", step.Id);

        try
        {
        // Extract resume event type from step configuration
        var resumeEventType = step.ResumeEventType?.AssemblyQualifiedName ?? "";
        if (string.IsNullOrEmpty(resumeEventType))
        {
            throw new InvalidOperationException($"Suspend step {step.Id} has no resume event type configured");
        }

            // Apply input mappings to create event data from workflow context
            // This is similar to how ActivityExecutor handles input mappings
            object? eventData = null;
            if (step.InputMappings.Count > 0)
            {
                // Create an instance of the event type to populate
                eventData = Activator.CreateInstance(step.ResumeEventType!);
                if (eventData == null)
                {
                    throw new InvalidOperationException($"Failed to create instance of resume event type: {step.ResumeEventType!.Name}");
                }

                // Apply input mappings: context -> event data
                foreach (var mapping in step.InputMappings)
                {
                    try
                    {
                        // Create context for mapping evaluation
                        var mappingContext = CreateEvaluationContext(context, executionState.LastStepResult);
                        
                        // Get value from context using source function
                        var sourceValue = mapping.SourceFunction(mappingContext);
                        
                        // Set value on event data using reflection (for now)
                        var targetProperty = eventData.GetType().GetProperty(mapping.TargetProperty);
                        if (targetProperty != null && targetProperty.CanWrite)
                        {
                            // Convert value if needed
                            var convertedValue = ConvertValue(sourceValue, targetProperty.PropertyType);
                            targetProperty.SetValue(eventData, convertedValue);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to apply input mapping {MappingId} for suspend step {StepId}", 
                            mapping.Id, step.Id);
                        throw new InvalidOperationException($"Failed to apply input mapping for suspend step: {ex.Message}", ex);
                    }
                }
            }

            // Serialize resume condition (if any)
            string resumeConditionJson = "";
            if (step.CompiledCondition != null)
            {
                // Store a marker that indicates a condition exists
                resumeConditionJson = JsonSerializer.Serialize(new { HasCondition = true });
            }

            // Serialize current workflow definition
            var workflowDefinitionJson = SerializeWorkflowDefinition(context.WorkflowDefinition);

            // Serialize current execution state
            var executionStateJson = SerializeExecutionState(context, executionState);

            // Get suspend reason from step metadata (set by WorkflowBuilder.Suspend method)
            var suspendReason = step.StepMetadata.TryGetValue("SuspendReason", out var reasonObj) && reasonObj is string reason
                ? reason
                : step.Name; // Fallback to step name if no suspend reason in metadata

            // Create and populate SuspensionInfo
            var suspensionInfo = new SuspensionInfo
            {
                SuspendedAt = DateTime.UtcNow,
                SuspendReason = suspendReason,
                ResumeEventType = resumeEventType,
                ResumeConditionJson = resumeConditionJson,
                ExpiresAt = null, // No expiration for now
                Metadata = new Dictionary<string, object>
                {
                    ["StepId"] = step.Id,
                    ["StepName"] = step.Name,
                    ["EventDataJson"] = eventData != null ? JsonSerializer.Serialize(eventData) : ""
                }
            };

            // Update workflow instance with suspension information
            context.WorkflowInstance.Status = WorkflowStatus.Suspended;
            context.WorkflowInstance.SuspensionInfo = suspensionInfo;
            context.WorkflowInstance.WorkflowDefinitionJson = workflowDefinitionJson;
            context.WorkflowInstance.ExecutionStateJson = executionStateJson;

            // Save workflow instance to repository
            await _stateRepository.SaveWorkflowInstanceAsync(context.WorkflowInstance);

            // Add suspension trace
            await _tracer.TraceAsync(context.WorkflowInstance.InstanceId, new ExecutionTraceEntry
            {
                StepNumber = context.WorkflowInstance.CurrentStepNumber,
                EntryType = TraceEntryType.WorkflowSuspended,
                ActivityName = step.Name,
                InputDataJson = eventData != null ? JsonSerializer.Serialize(eventData) : "",
                Metadata = new Dictionary<string, object>
                {
                    ["ResumeEventType"] = resumeEventType,
                    ["SuspendReason"] = suspensionInfo.SuspendReason
                }
            }, cancellationToken);

            _logger.LogInformation("Workflow suspended: {InstanceId} at step {StepName}, waiting for event type {EventType}",
                context.WorkflowInstance.InstanceId, step.Name, step.ResumeEventType?.Name);

            // Return failure to stop workflow execution (this is correct behavior for suspension)
            return new StepExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = $"Workflow suspended at step {step.Name}",
                OutputData = executionState.LastStepResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to suspend workflow at step {StepId}: {ErrorMessage}", step.Id, ex.Message);
            
            // Set pending exception for propagation
            executionState.PendingException = ex;
            
            return new StepExecutionResult
            {
                IsSuccess = true, // Continue execution to find catch blocks
                OutputData = executionState.LastStepResult
            };
        }
    }

    /// <summary>
    /// Validates and prepares a suspended workflow for resumption with an event
    /// Returns the prepared workflow data and validation result
    /// </summary>
    public async Task<ResumeValidationResult> ValidateAndPrepareResumeAsync<TEvent>(
        string instanceId,
        TEvent @event,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        _logger.LogInformation("Validating resume for workflow {InstanceId} with event of type {EventType}",
            instanceId, typeof(TEvent).Name);

        try
        {
            // Get the suspended workflow instance
            _logger.LogDebug("Getting workflow instance {InstanceId}", instanceId);
            var instance = await _stateRepository.GetWorkflowInstanceAsync(instanceId);
            if (instance == null)
            {
                _logger.LogError("Workflow instance {InstanceId} not found", instanceId);
                return ResumeValidationResult.Failed($"Workflow instance {instanceId} not found");
            }

            _logger.LogDebug("Found workflow instance {InstanceId}, Status: {Status}", 
                instanceId, instance.Status);

            if (instance.Status != WorkflowStatus.Suspended)
            {
                _logger.LogError("Workflow instance {InstanceId} is not suspended (Status: {Status})", 
                    instanceId, instance.Status);
                return ResumeValidationResult.Failed(
                    $"Workflow instance {instanceId} is not suspended (Status: {instance.Status})");
            }

            if (instance.SuspensionInfo == null)
            {
                _logger.LogError("Workflow instance {InstanceId} has no suspension information", instanceId);
                return ResumeValidationResult.Failed($"Workflow instance {instanceId} has no suspension information");
            }

            _logger.LogDebug("Workflow is suspended with suspension info");

            // Validate event type matches expected resume event type
            _logger.LogDebug("Validating event type. Expected: {ExpectedType}, Actual: {ActualType}",
                instance.SuspensionInfo.ResumeEventType, typeof(TEvent).FullName);

            var expectedEventType = Type.GetType(instance.SuspensionInfo.ResumeEventType);
            if (expectedEventType == null)
            {
                _logger.LogError("Failed to get expected resume event type: {ResumeEventType}", 
                    instance.SuspensionInfo.ResumeEventType);
                return ResumeValidationResult.Failed($"Failed to get expected resume event type: {instance.SuspensionInfo.ResumeEventType}");
            }

            if (!expectedEventType.IsAssignableFrom(typeof(TEvent)))
            {
                _logger.LogError("Event type mismatch. Expected: {ExpectedType}, Actual: {ActualType}",
                    expectedEventType.Name, typeof(TEvent).Name);
                return ResumeValidationResult.Failed(
                    $"Event type {typeof(TEvent).Name} does not match expected resume event type {expectedEventType.Name}");
            }

            _logger.LogDebug("Event type validation passed");

            // Get the workflow data type and deserialize FIRST (needed for both condition and mappings)
            _logger.LogDebug("Deserializing workflow data. Type: {WorkflowDataType}", instance.WorkflowDataType);

            var workflowDataType = Type.GetType(instance.WorkflowDataType);
            if (workflowDataType == null)
            {
                _logger.LogError("Failed to get workflow data type: {WorkflowDataType}", instance.WorkflowDataType);
                return ResumeValidationResult.Failed($"Failed to get workflow data type: {instance.WorkflowDataType}");
            }

            var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, workflowDataType);
            if (workflowData == null)
            {
                _logger.LogError("Failed to deserialize workflow data for instance {InstanceId}", instanceId);
                return ResumeValidationResult.Failed($"Failed to deserialize workflow data for instance {instanceId}");
            }

            _logger.LogDebug("Workflow data deserialized successfully");

            // OPTIMIZED: Evaluate condition AND apply mappings in one flow
            _logger.LogDebug("Evaluating condition and applying mappings using original suspend step");
            var conditionAndMappingResult = await EvaluateConditionAndApplyMappingsAsync(@event, instance, workflowData, workflowDataType);
            
            if (!conditionAndMappingResult.ConditionMet)
            {
                _logger.LogWarning("Resume condition not met for workflow {InstanceId}", instanceId);
                return ResumeValidationResult.ConditionNotMet("Resume condition not met");
            }

            // Update workflow data in instance (mappings were applied)
            instance.WorkflowDataJson = JsonSerializer.Serialize(workflowData);
            _logger.LogDebug("Condition evaluation and output mappings completed");

            // Add resume trace
            await _tracer.TraceAsync(instance.InstanceId, new ExecutionTraceEntry
            {
                StepNumber = instance.CurrentStepNumber,
                EntryType = TraceEntryType.WorkflowResumed,
                ActivityName = "Resume",
                InputDataJson = JsonSerializer.Serialize(@event),
                Metadata = new Dictionary<string, object>
                {
                    ["EventType"] = typeof(TEvent).Name,
                    ["ResumedAt"] = DateTime.UtcNow
                }
            }, cancellationToken);

            _logger.LogInformation("Workflow resume validation successful: {InstanceId}, Event: {EventType}",
                instanceId, typeof(TEvent).Name);

            return ResumeValidationResult.Success(instance, workflowData, @event);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate resume for workflow {InstanceId}: {ErrorMessage}", 
                instanceId, ex.Message);
            return ResumeValidationResult.Failed($"Validation failed: {ex.Message}");
        }
    }

    #region Helper Methods

    /// <summary>
    /// Creates evaluation context for suspend/resume mappings
    /// </summary>
    private object CreateEvaluationContext<TWorkflowData>(
        StepExecutionContext<TWorkflowData> context,
        object? previousStepData)
    {
        if (previousStepData != null)
        {
            // Create WorkflowContext<TWorkflowData, TPreviousStepData>
            var previousStepDataType = previousStepData.GetType();
            var contextType = typeof(WorkflowContext<,>).MakeGenericType(typeof(TWorkflowData), previousStepDataType);

            var contextInstance = Activator.CreateInstance(contextType);

            contextType.GetProperty("WorkflowData")?.SetValue(contextInstance, context.WorkflowData);
            contextType.GetProperty("PreviousStep")?.SetValue(contextInstance, previousStepData);

            return contextInstance!;
        }

        // Create WorkflowContext<TWorkflowData>
        return new WorkflowContext<TWorkflowData>
        {
            WorkflowData = context.WorkflowData
        };
    }

    /// <summary>
    /// Converts a value to the target type
    /// </summary>
    private object? ConvertValue(object? value, Type targetType)
    {
        if (value == null) return null;
        if (targetType.IsAssignableFrom(value.GetType())) return value;

        try
        {
            // Handle nullable types
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                var underlyingType = Nullable.GetUnderlyingType(targetType);
                return underlyingType != null ? Convert.ChangeType(value, underlyingType) : value;
            }

            return Convert.ChangeType(value, targetType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert value {Value} from {SourceType} to {TargetType}",
                value, value.GetType(), targetType);
            return value;
        }
    }

    /// <summary>
    /// Serializes workflow definition to JSON
    /// </summary>
    private string SerializeWorkflowDefinition(WorkflowDefinition definition)
    {
        // For now, serialize basic metadata - full serialization would be more complex
        var metadata = new
        {
            definition.Id,
            definition.Name,
            definition.Version,
            definition.Description,
            WorkflowDataTypeName = definition.WorkflowDataType.AssemblyQualifiedName,
            definition.CreatedAt,
            definition.CreatedBy,
            definition.Tags,
            definition.EstimatedStepCount,
            definition.SupportsSuspension,
            definition.UsesSagaPattern,
            definition.SupportsParallelExecution,
            StepCount = definition.Steps.Count
        };

        return JsonSerializer.Serialize(metadata);
    }

    /// <summary>
    /// Deserializes workflow definition from JSON
    /// </summary>
    private WorkflowDefinition DeserializeWorkflowDefinition(string json)
    {
        // For now, return a placeholder - full deserialization would need more work
        var metadata = JsonSerializer.Deserialize<dynamic>(json);
        
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Resumed Workflow",
            Version = 1,
            WorkflowDataType = typeof(object),
            CreatedAt = DateTime.UtcNow,
            Steps = new List<WorkflowStep>()
        };
    }

    /// <summary>
    /// Serializes execution state to JSON
    /// </summary>
    private string SerializeExecutionState<TWorkflowData>(
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState)
    {
        var state = new
        {
            CurrentStepNumber = context.WorkflowInstance.CurrentStepNumber,
            LastStepResultJson = executionState.LastStepResult != null ? JsonSerializer.Serialize(executionState.LastStepResult) : "",
            LastStepResultType = executionState.LastStepResult?.GetType().AssemblyQualifiedName ?? "",
            HasPendingException = executionState.PendingException != null,
            TryStackDepth = executionState.TryStack.Count,
            SerializedAt = DateTime.UtcNow
        };

        return JsonSerializer.Serialize(state);
    }

    /// <summary>
    /// Deserializes execution state from JSON
    /// </summary>
    private ExecutionState DeserializeExecutionState(string json)
    {
        // For now, return a basic execution state - full deserialization would need more work
        return new ExecutionState
        {
            LastStepResult = null,
            PendingException = null,
            TryStack = new Stack<WorkflowStep>()
        };
    }

    /// <summary>
    /// Applies proper output mappings from the original suspend step to workflow data
    /// This method reconstructs the workflow definition and finds the suspend step to get its output mappings
    /// </summary>
    private async Task ApplyProperOutputMappingsFromSuspendStepAsync<TEvent>(TEvent @event, WorkflowInstance instance, object workflowData, Type workflowDataType)
        where TEvent : class
    {
        _logger.LogDebug("Applying proper output mappings from original suspend step. Event: {EventType}, Instance: {InstanceId}",
            typeof(TEvent).Name, instance.InstanceId);

        try
        {
            // Step 1: Get the original workflow definition from the version registry
            _logger.LogDebug("Getting original workflow definition from registry: {WorkflowName} v{WorkflowVersion}",
                instance.WorkflowName, instance.WorkflowVersion);

            // We need to inject IWorkflowVersionRegistry to get the original definition
            var versionRegistry = _serviceProvider.GetRequiredService<IWorkflowVersionRegistry>();

            var workflowDefinition = await versionRegistry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
            if (workflowDefinition == null)
            {
                _logger.LogWarning("Workflow definition not found in registry: {WorkflowName} v{WorkflowVersion}, falling back to basic property matching",
                    instance.WorkflowName, instance.WorkflowVersion);
                ApplyBasicPropertyMatching(@event, workflowData);
                return;
            }

            _logger.LogDebug("Retrieved workflow definition from registry");

            // Step 2: Find the suspend step that caused the suspension
            var suspendStepId = instance.SuspensionInfo?.Metadata.TryGetValue("StepId", out var stepIdObj) == true && stepIdObj is string stepId
                ? stepId
                : null;

            if (string.IsNullOrEmpty(suspendStepId))
            {
                _logger.LogWarning("No suspend step ID found in suspension metadata, falling back to basic property matching");
                ApplyBasicPropertyMatching(@event, workflowData);
                return;
            }

            var suspendStep = FindStepById(workflowDefinition.Steps, suspendStepId);
            if (suspendStep == null)
            {
                _logger.LogWarning("Suspend step not found in workflow definition: {StepId}, falling back to basic property matching", suspendStepId);
                ApplyBasicPropertyMatching(@event, workflowData);
                return;
            }

            _logger.LogDebug("Found suspend step: {StepName} with {MappingCount} output mappings", 
                suspendStep.Name, suspendStep.OutputMappings.Count);

            // Step 3: Apply the suspend step's output mappings using the same pattern as ActivityExecutor
            ApplyProperOutputMappings(@event, suspendStep, workflowData, workflowDataType);

            _logger.LogDebug("Successfully applied proper output mappings from suspend step");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply proper output mappings for event type {EventType}", typeof(TEvent).Name);
            throw new InvalidOperationException($"Failed to apply proper output mappings: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Recursively finds a step by ID in the workflow definition
    /// </summary>
    private WorkflowStep? FindStepById(List<WorkflowStep> steps, string stepId)
    {
        foreach (var step in steps)
        {
            if (step.Id == stepId)
            {
                return step;
            }

            // Search in nested steps
            var found = FindStepById(step.SequenceSteps, stepId) ??
                       FindStepById(step.ThenSteps, stepId) ??
                       FindStepById(step.ElseSteps, stepId) ??
                       FindStepById(step.LoopBodySteps, stepId);

            if (found != null)
            {
                return found;
            }

            // Search in parallel branches
            if (step.ParallelBranches != null)
            {
                foreach (var branch in step.ParallelBranches)
                {
                    found = FindStepById(branch, stepId);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }

            // Search in catch blocks
            foreach (var catchBlock in step.CatchBlocks)
            {
                found = FindStepById(catchBlock.SequenceSteps, stepId);
                if (found != null)
                {
                    return found;
                }
            }

            // Search in finally steps
            found = FindStepById(step.FinallySteps, stepId);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies proper output mappings from suspend step to workflow data using compiled TargetAssignmentFunction
    /// This is the same pattern used by ActivityExecutor for output mappings
    /// </summary>
    private void ApplyProperOutputMappings<TEvent>(TEvent @event, WorkflowStep suspendStep, object workflowData, Type workflowDataType)
        where TEvent : class
    {
        _logger.LogDebug("Applying proper output mappings from suspend step: {StepName}, MappingCount: {MappingCount}",
            suspendStep.Name, suspendStep.OutputMappings.Count);

        if (suspendStep.OutputMappings.Count == 0)
        {
            _logger.LogDebug("No output mappings found on suspend step, falling back to basic property matching");
            ApplyBasicPropertyMatching(@event, workflowData);
            return;
        }

        // Create workflow evaluation context for the mappings
        var workflowContext = CreateWorkflowEvaluationContext(workflowData, workflowDataType);
        _logger.LogTrace("Created workflow context: {ContextType}", workflowContext.GetType().Name);

        foreach (var mapping in suspendStep.OutputMappings)
        {
            try
            {
                _logger.LogTrace("Processing output mapping {MappingId} -> {TargetProperty}", mapping.Id, mapping.TargetProperty);
                _logger.LogTrace("Event type = {EventType}, Property = {Property}", typeof(TEvent).Name, mapping.TargetProperty);
                _logger.LogTrace("TargetAssignmentFunction is null = {IsNull}", mapping.TargetAssignmentFunction == null);

                // Get the value from the event using the mapping's source property
                var eventProperty = typeof(TEvent).GetProperty(mapping.TargetProperty);
                if (eventProperty != null && eventProperty.CanRead)
                {
                    var eventValue = eventProperty.GetValue(@event);
                    _logger.LogTrace("Got event value: '{Value}' from property {Property}", eventValue, mapping.TargetProperty);

                    // Apply the mapping using the compiled TargetAssignmentFunction
                    if (mapping.TargetAssignmentFunction != null)
                    {
                        _logger.LogTrace("Calling TargetAssignmentFunction with context type {ContextType} and value '{Value}'", 
                            workflowContext.GetType().Name, eventValue);
                        
                        mapping.TargetAssignmentFunction(workflowContext, eventValue);
                        
                        _logger.LogTrace("Successfully applied compiled output mapping: {MappingId}", mapping.Id);
                        
                        // Verify assignment worked by checking workflow data
                        var workflowDataProperty = workflowData.GetType().GetProperty("SuspensionMessage");
                        if (workflowDataProperty != null)
                        {
                            var actualValue = workflowDataProperty.GetValue(workflowData);
                            _logger.LogTrace("After assignment, SuspensionMessage = '{Value}'", actualValue);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No TargetAssignmentFunction found for mapping {MappingId}, skipping", mapping.Id);
                    }
                }
                else
                {
                    // List all available properties for debugging
                    var availableProps = typeof(TEvent).GetProperties().Select(p => p.Name).ToArray();
                    
                    _logger.LogWarning("Event property not found or not readable: {Property} on {EventType}",
                        mapping.TargetProperty, typeof(TEvent).Name);
                    
                    _logger.LogTrace("Available properties on {EventType}: {Properties}", 
                        typeof(TEvent).Name, string.Join(", ", availableProps));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply output mapping for property {Property}", mapping.TargetProperty);
                throw new InvalidOperationException($"Failed to apply output mapping for property {mapping.TargetProperty}: {ex.Message}", ex);
            }
        }

        _logger.LogDebug("Successfully applied all proper output mappings from suspend step");
    }

    /// <summary>
    /// Creates workflow evaluation context for output mapping (same pattern as ActivityExecutor)
    /// </summary>
    private object CreateWorkflowEvaluationContext(object workflowData, Type workflowDataType)
    {
        // Create WorkflowContext<TWorkflowData> using the actual workflow data type
        var contextType = typeof(WorkflowContext<>).MakeGenericType(workflowDataType);
        var context = Activator.CreateInstance(contextType);
        
        contextType.GetProperty("WorkflowData")?.SetValue(context, workflowData);
        
        _logger.LogTrace("Created workflow evaluation context: {ContextType}", contextType.FullName);
        
        return context!;
    }

    /// <summary>
    /// Applies event data mappings to workflow data using suspend step's output mappings
    /// </summary>
    private void ApplyEventMappingsToWorkflowData<TEvent>(TEvent @event, WorkflowInstance instance, object workflowData)
        where TEvent : class
    {
        _logger.LogDebug("Applying event mappings for event type {EventType} to workflow data", typeof(TEvent).Name);

        try
        {
            // For now, we need to get the original suspend step's output mappings
            // Since we don't have full workflow definition reconstruction yet,
            // we'll use a simplified approach based on the stored metadata
            
            if (instance.SuspensionInfo?.Metadata.TryGetValue("EventDataJson", out var eventDataJsonObj) == true &&
                eventDataJsonObj is string eventDataJson && !string.IsNullOrEmpty(eventDataJson))
            {
                // The original suspend step stored expected event data structure
                // We can use this to understand what mappings were intended
                _logger.LogDebug("Found stored event data structure for mapping reference");
            }

            // For now, we'll use reflection to try basic property matching
            // This will be replaced when we implement workflow definition reconstruction
            ApplyBasicPropertyMatching(@event, workflowData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply event mappings for event type {EventType}", typeof(TEvent).Name);
            throw new InvalidOperationException($"Failed to apply event mappings: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Applies basic property matching between event and workflow data as fallback
    /// </summary>
    private void ApplyBasicPropertyMatching<TEvent>(TEvent @event, object workflowData)
        where TEvent : class
    {
        var eventType = typeof(TEvent);
        var workflowDataType = workflowData.GetType();

        _logger.LogDebug("Applying basic property matching from {EventType} to {WorkflowDataType}", 
            eventType.Name, workflowDataType.Name);

        var eventProperties = eventType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToList();

        var workflowProperties = workflowDataType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, p => p);

        foreach (var eventProperty in eventProperties)
        {
            if (workflowProperties.TryGetValue(eventProperty.Name, out var workflowProperty))
            {
                try
                {
                    var eventValue = eventProperty.GetValue(@event);
                    var convertedValue = ConvertValue(eventValue, workflowProperty.PropertyType);
                    workflowProperty.SetValue(workflowData, convertedValue);

                    _logger.LogTrace("Mapped property {PropertyName}: {Value}", 
                        eventProperty.Name, convertedValue);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to map property {PropertyName} from event to workflow data", 
                        eventProperty.Name);
                }
            }
        }
    }

    /// <summary>
    /// OPTIMIZED: Evaluates condition AND applies mappings in one efficient flow
    /// This avoids duplicate workflow definition retrieval and context creation
    /// </summary>
    private async Task<(bool ConditionMet, bool MappingsApplied)> EvaluateConditionAndApplyMappingsAsync<TEvent>(
        TEvent @event, 
        WorkflowInstance instance, 
        object workflowData, 
        Type workflowDataType)
        where TEvent : class
    {
        _logger.LogDebug("Evaluating condition and applying mappings for event type {EventType}, Instance: {InstanceId}",
            typeof(TEvent).Name, instance.InstanceId);

        try
        {
            // Step 1: Get the original workflow definition from the version registry (ONCE)
            _logger.LogTrace("Getting workflow definition from version registry: {WorkflowName} v{WorkflowVersion}",
                instance.WorkflowName, instance.WorkflowVersion);
            
            var versionRegistry = _serviceProvider.GetRequiredService<IWorkflowVersionRegistry>();
            var workflowDefinition = await versionRegistry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
            
            if (workflowDefinition == null)
            {
                _logger.LogWarning("Workflow definition not found for {WorkflowName} v{WorkflowVersion}, falling back to basic property matching",
                    instance.WorkflowName, instance.WorkflowVersion);
                ApplyBasicPropertyMatching(@event, workflowData);
                return (true, true); // Allow resume with basic mapping
            }

            _logger.LogTrace("Found workflow definition with {StepCount} steps", workflowDefinition.Steps.Count);

            // Step 2: Find the suspend step that caused the suspension (ONCE)
            var suspendStepId = instance.SuspensionInfo?.Metadata.TryGetValue("StepId", out var stepIdObj) == true && stepIdObj is string stepId
                ? stepId
                : null;

            _logger.LogTrace("Suspend step ID from suspension metadata: {StepId}", suspendStepId);

            if (string.IsNullOrEmpty(suspendStepId))
            {
                _logger.LogWarning("No suspend step ID found in suspension metadata, falling back to basic property matching");
                ApplyBasicPropertyMatching(@event, workflowData);
                return (true, true); // Allow resume with basic mapping
            }

            _logger.LogTrace("Searching for suspend step with ID: {StepId}", suspendStepId);
            var suspendStep = FindStepById(workflowDefinition.Steps, suspendStepId);
            if (suspendStep == null)
            {
                _logger.LogTrace("Suspend step not found in top-level steps, searching in outcome branches...");
                
                // Try searching in outcome branches stored in metadata
                suspendStep = FindStepInOutcomeBranches(workflowDefinition.Steps, suspendStepId);
                if (suspendStep != null)
                {
                    _logger.LogDebug("Found suspend step in outcome branches: {StepName}", suspendStep.Name);
                }
                else
                {
                    _logger.LogWarning("Suspend step not found: {StepId}, falling back to basic property matching", suspendStepId);
                    ApplyBasicPropertyMatching(@event, workflowData);
                    return (true, true); // Allow resume with basic mapping
                }
            }
            else
            {
                _logger.LogDebug("Found suspend step in top-level steps: {StepName}", suspendStep.Name);
            }

            _logger.LogDebug("Found suspend step: {StepName} with {MappingCount} output mappings", 
                suspendStep.Name, suspendStep.OutputMappings.Count);

            // Step 3: Create the workflow context (ONCE) - used for both condition and mappings
            var workflowContext = CreateWorkflowEvaluationContext(workflowData, workflowDataType);

            // Step 4: Evaluate condition if present
            bool conditionMet = true; // Default to true if no condition
            
            if (suspendStep.CompiledCondition != null)
            {
                _logger.LogDebug("Evaluating resume condition");
                
                // The CompiledCondition is Func<object, bool> - it expects a single context object
                // that contains both the event and workflow context combined
                var combinedContext = CreateCombinedConditionContext(@event, workflowContext);
                conditionMet = suspendStep.CompiledCondition(combinedContext);
                
                _logger.LogDebug("Condition evaluation result: {ConditionResult}", conditionMet);
                
                if (!conditionMet)
                {
                    _logger.LogDebug("Condition not met, skipping output mappings");
                    return (false, false); // Don't apply mappings if condition fails
                }
            }
            else
            {
                _logger.LogDebug("No condition on suspend step, proceeding to mappings");
            }

            // Step 5: Apply output mappings (only if condition was met)
            _logger.LogDebug("Applying output mappings using same context");
            ApplyProperOutputMappings(@event, suspendStep, workflowData, workflowDataType);
            _logger.LogDebug("Output mappings applied successfully");

            return (true, true); // Both condition met and mappings applied
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate condition and apply mappings for event type {EventType}", typeof(TEvent).Name);
            
            // Fallback: apply basic property matching and allow resume
            _logger.LogWarning("Falling back to basic property matching due to error");
            ApplyBasicPropertyMatching(@event, workflowData);
            return (true, true); // Be permissive on errors
        }
    }

    /// <summary>
    /// Creates a combined context for condition evaluation that matches the WorkflowBuilder pattern
    /// The CompiledCondition expects a ResumeEventContext<TWorkflowData, TResumeEvent>
    /// </summary>
    private object CreateCombinedConditionContext<TEvent>(TEvent @event, object workflowContext)
        where TEvent : class
    {
        // Extract the workflow data from the workflow context
        var workflowDataProperty = workflowContext.GetType().GetProperty("WorkflowData");
        var workflowData = workflowDataProperty?.GetValue(workflowContext);
        
        if (workflowData == null)
        {
            throw new InvalidOperationException("Failed to extract WorkflowData from workflow context");
        }

        var workflowDataType = workflowData.GetType();
        var eventType = typeof(TEvent);

        // Create ResumeEventContext<TWorkflowData, TResumeEvent>
        var contextType = typeof(ResumeEventContext<,>).MakeGenericType(workflowDataType, eventType);
        var context = Activator.CreateInstance(contextType);

        // Set the properties
        contextType.GetProperty("WorkflowData")?.SetValue(context, workflowData);
        contextType.GetProperty("ResumeEvent")?.SetValue(context, @event);

        _logger.LogTrace("Created combined condition context: {ContextType}", contextType.FullName);
        
        return context!;
    }

    /// <summary>
    /// Searches for a step in OutcomeOn branches stored in step metadata
    /// This handles the case where steps are nested in outcome-based branching
    /// </summary>
    private WorkflowStep? FindStepInOutcomeBranches(List<WorkflowStep> steps, string stepId)
    {
        _logger.LogTrace("Searching in outcome branches for step ID: {StepId}", stepId);
        
        foreach (var step in steps)
        {
            // Check if this step has outcome branches in metadata
            if (step.StepMetadata.TryGetValue("OutcomeBranches", out var branchesObj))
            {
                _logger.LogTrace("Found OutcomeBranches in step {StepName}", step.Name);
                
                // The branches are stored as an IList, we need to iterate through them
                if (branchesObj is System.Collections.IList branches)
                {
                    foreach (var branchObj in branches)
                    {
                        _logger.LogTrace("Checking outcome branch...");
                        
                        // Use reflection to get the Steps property from the branch
                        var branchType = branchObj.GetType();
                        var stepsProperty = branchType.GetProperty("Steps");
                        
                        if (stepsProperty?.GetValue(branchObj) is List<WorkflowStep> branchSteps)
                        {
                            _logger.LogTrace("Branch has {StepCount} steps", branchSteps.Count);
                            
                            // Search recursively in branch steps
                            var found = FindStepById(branchSteps, stepId);
                            if (found != null)
                            {
                                _logger.LogTrace("Found step in outcome branch!");
                                return found;
                            }
                        }
                    }
                }
            }
            
            // Check if this step has a default branch in metadata
            if (step.StepMetadata.TryGetValue("DefaultBranch", out var defaultBranchObj) && defaultBranchObj != null)
            {
                _logger.LogTrace("Found DefaultBranch in step {StepName}", step.Name);
                
                // Use reflection to get the Steps property from the default branch
                var branchType = defaultBranchObj.GetType();
                var stepsProperty = branchType.GetProperty("Steps");
                
                if (stepsProperty?.GetValue(defaultBranchObj) is List<WorkflowStep> defaultBranchSteps)
                {
                    _logger.LogTrace("Default branch has {StepCount} steps", defaultBranchSteps.Count);
                    
                    // Search recursively in default branch steps
                    var found = FindStepById(defaultBranchSteps, stepId);
                    if (found != null)
                    {
                        _logger.LogTrace("Found step in default branch!");
                        return found;
                    }
                }
            }
            
            // Recursively search in nested steps
            var nestedFound = FindStepInOutcomeBranches(step.SequenceSteps, stepId) ??
                             FindStepInOutcomeBranches(step.ThenSteps, stepId) ??
                             FindStepInOutcomeBranches(step.ElseSteps, stepId) ??
                             FindStepInOutcomeBranches(step.LoopBodySteps, stepId);
            
            if (nestedFound != null)
            {
                return nestedFound;
            }
        }
        
        _logger.LogTrace("Step not found in any outcome branches");
        return null;
    }

    #endregion
}
