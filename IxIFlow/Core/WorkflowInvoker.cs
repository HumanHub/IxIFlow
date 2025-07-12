using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
/// Handles workflow invocation operations with proper separation of concerns
/// </summary>
public class WorkflowInvoker : IWorkflowInvoker
{
    private readonly IWorkflowTracer _tracer;
    private readonly ILogger<WorkflowInvoker> _logger;
    private readonly IServiceProvider _serviceProvider;

    public WorkflowInvoker(
        IWorkflowTracer tracer,
        ILogger<WorkflowInvoker> logger,
        IServiceProvider serviceProvider)
    {
        _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Executes a workflow invocation step with propagation-based exception handling
    /// </summary>
    public async Task<StepExecutionResult> ExecuteWorkflowInvocationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class
    {
        _logger.LogDebug("Executing workflow invocation step with propagation: {StepId}", step.Id);

        try
        {
            // Create child workflow data instance
            object childWorkflowData;
            Type childWorkflowDataType;

            // Determine child workflow data type from step metadata
            if (step.WorkflowType != null)
            {
                // Get the workflow data type from the workflow interface
                var workflowInterface = step.WorkflowType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IWorkflow<>));

                if (workflowInterface == null)
                    throw new InvalidOperationException(
                        $"Workflow type {step.WorkflowType} does not implement IWorkflow<>");

                childWorkflowDataType = workflowInterface.GetGenericArguments()[0];
            }
            else
            {
                // For name/version invocation, we need to infer the type from the mappings
                // This is a simplified approach - in a real system, you'd have a workflow registry
                var firstInputMapping = step.InputMappings.FirstOrDefault();
                if (firstInputMapping == null)
                    throw new InvalidOperationException(
                        $"Cannot determine child workflow data type for step {step.Id}");

                childWorkflowDataType = firstInputMapping.SourceType.DeclaringType ?? firstInputMapping.SourceType;
            }

            // Create instance of child workflow data
            childWorkflowData = Activator.CreateInstance(childWorkflowDataType)
                                ?? throw new InvalidOperationException(
                                    $"Failed to create instance of {childWorkflowDataType}");

            _logger.LogDebug("Created child workflow data instance: {DataType}", childWorkflowDataType.Name);

            // Apply input mappings to populate child workflow data
            await ApplyInputMappingsAsync(childWorkflowData, step.InputMappings, context, context.PreviousStepData,
                cancellationToken);

            // Create child workflow definition
            WorkflowDefinition childWorkflowDefinition;

            if (step.WorkflowType != null)
            {
                // Create workflow instance and build definition
                var childWorkflow = Activator.CreateInstance(step.WorkflowType);
                if (childWorkflow == null)
                    throw new InvalidOperationException(
                        $"Failed to create workflow instance of type {step.WorkflowType}");

                // Create builder and get definition
                var builderType = typeof(WorkflowBuilder<>).MakeGenericType(childWorkflowDataType);
                var builder = Activator.CreateInstance(builderType, step.WorkflowName ?? step.WorkflowType.Name,
                    step.WorkflowVersion ?? 1);

                // Call Build method on the workflow
                var buildMethod = step.WorkflowType.GetMethod("Build");
                if (buildMethod == null)
                    throw new InvalidOperationException($"Workflow {step.WorkflowType} does not have a Build method");

                buildMethod.Invoke(childWorkflow, new[] { builder });

                // Get the workflow definition
                var buildDefinitionMethod = builderType.GetMethod("Build",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (buildDefinitionMethod == null)
                    throw new InvalidOperationException($"Builder {builderType} does not have a Build method");

                childWorkflowDefinition = (WorkflowDefinition)buildDefinitionMethod.Invoke(builder, null)!;
            }
            else
            {
                // For name/version invocation, we'd typically look up the workflow from a registry
                // For now, we'll create a simple placeholder workflow
                childWorkflowDefinition = new WorkflowDefinition
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = step.WorkflowName ?? "Unknown",
                    Version = step.WorkflowVersion ?? 1,
                    WorkflowDataType = childWorkflowDataType,
                    CreatedAt = DateTime.UtcNow,
                    Steps = new List<WorkflowStep>()
                };
            }

            // Add child workflow started trace
            await _tracer.TraceAsync(context.WorkflowInstance.InstanceId, new ExecutionTraceEntry
            {
                StepNumber = context.WorkflowInstance.CurrentStepNumber,
                EntryType = TraceEntryType.ActivityStarted,
                ActivityName = $"Workflow: {childWorkflowDefinition.Name}",
                InputDataJson = JsonSerializer.Serialize(childWorkflowData)
            }, cancellationToken);

            var startTime = DateTime.UtcNow;

            // Execute child workflow using generic method
            var executeMethod = GetType().GetMethod(nameof(ExecuteChildWorkflowAsync),
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (executeMethod == null)
                throw new InvalidOperationException("ExecuteChildWorkflowAsync method not found");

            var genericExecuteMethod = executeMethod.MakeGenericMethod(childWorkflowDataType);
            var childResult = await (Task<WorkflowExecutionResult>)genericExecuteMethod.Invoke(this,
                [childWorkflowDefinition, childWorkflowData, cancellationToken])!;

            var duration = DateTime.UtcNow - startTime;

            if (!childResult.IsSuccess)
            {
                await _tracer.TraceAsync(context.WorkflowInstance.InstanceId, new ExecutionTraceEntry
                {
                    StepNumber = context.WorkflowInstance.CurrentStepNumber,
                    EntryType = TraceEntryType.ActivityFailed,
                    ActivityName = $"Workflow: {childWorkflowDefinition.Name}",
                    Duration = duration,
                    ErrorMessage = childResult.ErrorMessage,
                    StackTrace = childResult.ErrorStackTrace
                }, cancellationToken);

                // Set pending exception for propagation
                executionState.PendingException = new Exception(childResult.ErrorMessage ?? "Child workflow failed");

                return new StepExecutionResult
                {
                    IsSuccess = true, // Continue execution to find catch blocks
                    OutputData = executionState.LastStepResult
                };
            }

            // Apply output mappings to transfer results back to parent
            await ApplyOutputMappingsAsync(childResult.WorkflowData!, step.OutputMappings, context,
                context.PreviousStepData, cancellationToken);

            // Add child workflow completed trace
            await _tracer.TraceAsync(context.WorkflowInstance.InstanceId, new ExecutionTraceEntry
            {
                StepNumber = context.WorkflowInstance.CurrentStepNumber,
                EntryType = TraceEntryType.ActivityCompleted,
                ActivityName = $"Workflow: {childWorkflowDefinition.Name}",
                Duration = duration,
                OutputDataJson = JsonSerializer.Serialize(childResult.WorkflowData)
            }, cancellationToken);

            _logger.LogDebug("Workflow invocation step completed: {StepId}, Child workflow: {ChildWorkflowName}",
                step.Id, childWorkflowDefinition.Name);

            // Update execution state
            executionState.LastStepResult = childResult.WorkflowData;

            // Return child workflow data as the output for the next step to access
            return new StepExecutionResult
            {
                IsSuccess = true,
                OutputData = childResult.WorkflowData
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow invocation step failed: {StepId}", step.Id);

            await _tracer.TraceAsync(context.WorkflowInstance.InstanceId, new ExecutionTraceEntry
            {
                StepNumber = context.WorkflowInstance.CurrentStepNumber,
                EntryType = TraceEntryType.ActivityFailed,
                ActivityName = $"Workflow: {step.WorkflowName ?? step.WorkflowType?.Name ?? "Unknown"}",
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            }, cancellationToken);

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
    /// Executes a child workflow with proper generic typing
    /// </summary>
    private async Task<WorkflowExecutionResult> ExecuteChildWorkflowAsync<TChildWorkflowData>(
        WorkflowDefinition childDefinition,
        TChildWorkflowData childData,
        CancellationToken cancellationToken)
    where TChildWorkflowData : class
    {
        // Get the workflow engine from DI
        var workflowEngine = _serviceProvider.GetRequiredService<WorkflowEngine>();

        // Create child workflow options
        var childOptions = new WorkflowOptions
        {
            PersistState = false, // Child workflows typically don't persist state separately
            CorrelationId = Guid.NewGuid().ToString()
        };

        // Execute child workflow recursively
        return await workflowEngine.ExecuteWorkflowAsync(childDefinition, childData, childOptions, cancellationToken);
    }

    /// <summary>
    /// Applies input mappings for workflow invocation
    /// </summary>
    private async Task ApplyInputMappingsAsync<TWorkflowData>(
        object childWorkflowData,
        List<PropertyMapping> inputMappings,
        StepExecutionContext<TWorkflowData> context,
        object? previousStepData,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Applying {Count} workflow input mappings", inputMappings.Count);

        foreach (var mapping in inputMappings.Where(m => m.Direction == PropertyMappingDirection.Input))
        {
            try
            {
                // Create evaluation context for parent workflow
                var evaluationContext = CreateEvaluationContext(context, previousStepData);

                // Execute the compiled source function
                var sourceValue = mapping.SourceFunction(evaluationContext);

                // Set target property on child workflow data
                var targetProperty = childWorkflowData.GetType().GetProperty(mapping.TargetProperty);
                if (targetProperty != null && targetProperty.CanWrite)
                {
                    var convertedValue = ConvertValue(sourceValue, targetProperty.PropertyType);
                    targetProperty.SetValue(childWorkflowData, convertedValue);

                    _logger.LogTrace("Applied workflow input mapping: {Property} = {Value}",
                        mapping.TargetProperty, convertedValue);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply workflow input mapping for property {Property}",
                    mapping.TargetProperty);
                throw;
            }
        }
    }

    /// <summary>
    /// Applies output mappings for workflow invocation
    /// </summary>
    private async Task ApplyOutputMappingsAsync<TWorkflowData>(
        object childWorkflowData,
        List<PropertyMapping> outputMappings,
        StepExecutionContext<TWorkflowData> context,
        object? previousStepData,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Applying {Count} workflow output mappings", outputMappings.Count);

        foreach (var mapping in outputMappings.Where(m => m.Direction == PropertyMappingDirection.Output))
        {
            try
            {
                // Get value from child workflow data
                var sourceProperty = childWorkflowData.GetType().GetProperty(mapping.TargetProperty);
                if (sourceProperty != null && sourceProperty.CanRead)
                {
                    var sourceValue = sourceProperty.GetValue(childWorkflowData);

                    // Create evaluation context for parent workflow
                    var evaluationContext = CreateEvaluationContext(context, previousStepData);

                    // Use the compiled target assignment function
                    if (mapping.TargetAssignmentFunction != null)
                    {
                        mapping.TargetAssignmentFunction(evaluationContext, sourceValue);

                        _logger.LogTrace("Applied workflow output mapping: {Property} = {Value}",
                            mapping.TargetProperty, sourceValue);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply workflow output mapping for property {Property}",
                    mapping.TargetProperty);
                throw;
            }
        }
    }

    /// <summary>
    /// Creates evaluation context for workflow invocation mappings
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
}
