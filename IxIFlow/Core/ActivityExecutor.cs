using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
/// Enhanced activity execution context that supports typed workflow and previous step data
/// </summary>
/// <typeparam name="TWorkflowData">Type of workflow data</typeparam>
/// <typeparam name="TPreviousStepData">Type of previous step data</typeparam>
public class TypedActivityExecutionContext<TWorkflowData, TPreviousStepData> : IActivityContext
{
    public IServiceProvider Services { get; }
    public string WorkflowInstanceId { get; }
    public string CorrelationId { get; }
    public string ActivityName { get; }
    public int StepNumber { get; }
    public string WorkflowName { get; }
    public int WorkflowVersion { get; }
    public DateTime ActivityStartedAt { get; }
    public ILogger Logger { get; }
    
    // Typed data access
    public TWorkflowData WorkflowData { get; }
    public TPreviousStepData? PreviousStepData { get; }
    public IDictionary<string, object> Metadata { get; }

    // Explicit interface implementation for untyped access
    object IActivityContext.WorkflowData => WorkflowData ?? throw new InvalidOperationException("WorkflowData cannot be null"); 
    object? IActivityContext.PreviousStepData => PreviousStepData;

    public TypedActivityExecutionContext(
        IServiceProvider services,
        string workflowInstanceId,
        string correlationId,
        string activityName,
        int stepNumber,
        string workflowName,
        int workflowVersion,
        DateTime activityStartedAt,
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData,
        IDictionary<string, object>? metadata = null,
        ILogger? logger = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        WorkflowInstanceId = workflowInstanceId ?? throw new ArgumentNullException(nameof(workflowInstanceId));
        CorrelationId = correlationId ?? throw new ArgumentNullException(nameof(correlationId));
        ActivityName = activityName ?? throw new ArgumentNullException(nameof(activityName));
        StepNumber = stepNumber;
        WorkflowName = workflowName ?? throw new ArgumentNullException(nameof(workflowName));
        WorkflowVersion = workflowVersion;
        ActivityStartedAt = activityStartedAt;
        WorkflowData = workflowData;
        PreviousStepData = previousStepData;
        Metadata = metadata ?? new Dictionary<string, object>();
        Logger = logger ?? services.GetService<ILogger<TypedActivityExecutionContext<TWorkflowData, TPreviousStepData>>>() 
                 ?? throw new ArgumentNullException(nameof(logger));
    }
}

/// <summary>
/// Enhanced activity execution request with typed data support
/// </summary>
public class TypedActivityExecutionRequest<TWorkflowData, TPreviousStepData>
{
    public Type ActivityType { get; set; } = null!;
    public TWorkflowData WorkflowData { get; set; } = default!;
    public TPreviousStepData? PreviousStepData { get; set; }
    public List<PropertyMapping> InputMappings { get; set; } = new();
    public List<PropertyMapping> OutputMappings { get; set; } = new();
    public WorkflowStep Step { get; set; } = null!;
    public StepExecutionContext<TWorkflowData> ExecutionContext { get; set; } = null!;
    public CancellationToken CancellationToken { get; set; } = default;
    public Exception? CatchException { get; set; } = null; // For catch block execution
    public bool IsCompensationActivity { get; set; } = false; // For compensation activity detection
    public IDictionary<string, object>? StepMetadata { get; set; } = null; // Step-level metadata from ExecutionState
}

/// <summary>
/// Enhanced activity execution result with typed output
/// </summary>
public class TypedActivityExecutionResult<TOutput>
{
    public bool IsSuccess { get; set; }
    public TOutput? OutputData { get; set; }
    public Exception? Exception { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorStackTrace { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public List<PropertyMappingTrace> InputTraces { get; set; } = new();
    public List<PropertyMappingTrace> OutputTraces { get; set; } = new();
}

/// <summary>
///     Tracing information for property mappings
/// </summary>
public class PropertyMappingTrace
{
    public string PropertyName { get; set; } = string.Empty;
    public object? SourceValue { get; set; }
    public object? TargetValue { get; set; }
    public string? SourceExpression { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Enhanced activity executor that centralizes activity execution logic
/// and supports typed workflow and previous step data
/// </summary>
public interface IActivityExecutor
{
    /// <summary>
    /// Executes an activity with typed workflow and previous step data
    /// </summary>
    Task<TypedActivityExecutionResult<object>> ExecuteTypedActivityAsync<TWorkflowData, TPreviousStepData>(
        TypedActivityExecutionRequest<TWorkflowData, TPreviousStepData> request);

    /// <summary>
    /// Creates a typed activity execution context
    /// </summary>
    TypedActivityExecutionContext<TWorkflowData, TPreviousStepData> CreateTypedActivityContext<TWorkflowData, TPreviousStepData>(
        StepExecutionContext<TWorkflowData> stepContext,
        TPreviousStepData? previousStepData,
        WorkflowStep step,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates evaluation context for property mappings
    /// </summary>
    object CreateEvaluationContext<TWorkflowData, TPreviousStepData>(
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData);

    /// <summary>
    /// Creates catch evaluation context for exception handling
    /// </summary>
    object CreateCatchEvaluationContext<TWorkflowData, TPreviousStepData>(
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData,
        Exception exception);
}

/// <summary>
/// Implementation of enhanced activity executor
/// </summary>
public class ActivityExecutor : IActivityExecutor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ActivityExecutor> _logger;
    private readonly IWorkflowTracer _tracer;

    public ActivityExecutor(
        IServiceProvider serviceProvider,
        ILogger<ActivityExecutor> logger,
        IWorkflowTracer tracer)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
    }

    public async Task<TypedActivityExecutionResult<object>> ExecuteTypedActivityAsync<TWorkflowData, TPreviousStepData>(
        TypedActivityExecutionRequest<TWorkflowData, TPreviousStepData> request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.ActivityType == null) throw new InvalidOperationException("Activity type is null");

        var startTime = DateTime.UtcNow;
        var result = new TypedActivityExecutionResult<object>();

        try
        {
            _logger.LogDebug("Executing activity {ActivityType} with typed context", request.ActivityType.Name);

            // Create typed activity context
            var activityContext = CreateTypedActivityContext(
                request.ExecutionContext,
                request.PreviousStepData,
                request.Step,
                request.StepMetadata,
                request.CancellationToken);

            // Create activity instance
            var activity = CreateActivityInstance(request.ActivityType);

            // Apply input mappings using typed context (catch-aware)
            await ApplyTypedInputMappingsAsync(
                activity,
                request.InputMappings,
                request.WorkflowData,
                request.PreviousStepData,
                request.CatchException,
                result,
                request.IsCompensationActivity);

            // Add activity started trace
            await _tracer.TraceAsync(request.ExecutionContext.WorkflowInstance.InstanceId, new ExecutionTraceEntry
            {
                StepNumber = request.ExecutionContext.WorkflowInstance.CurrentStepNumber,
                EntryType = TraceEntryType.ActivityStarted,
                ActivityName = request.ActivityType.Name,
                InputDataJson = JsonSerializer.Serialize(activity)
            }, request.CancellationToken);

            // Execute the activity
            await activity.ExecuteAsync(activityContext, request.CancellationToken);

            // Apply output mappings using typed context (catch-aware)
            await ApplyTypedOutputMappingsAsync(
                activity,
                request.OutputMappings,
                request.WorkflowData,
                request.PreviousStepData,
                request.CatchException,
                result);

            // Add activity completed trace
            var duration = DateTime.UtcNow - startTime;
            await _tracer.TraceAsync(request.ExecutionContext.WorkflowInstance.InstanceId, new ExecutionTraceEntry
            {
                StepNumber = request.ExecutionContext.WorkflowInstance.CurrentStepNumber,
                EntryType = TraceEntryType.ActivityCompleted,
                ActivityName = request.ActivityType.Name,
                Duration = duration,
                OutputDataJson = JsonSerializer.Serialize(activity)
            }, request.CancellationToken);

            result.IsSuccess = true;
            result.OutputData = activity; // Return the activity instance as the output data
            result.ExecutionTime = duration;

            _logger.LogDebug("Activity execution completed successfully: {ActivityType}", request.ActivityType.Name);
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.Exception = ex;
            result.ErrorMessage = ex.Message;
            result.ErrorStackTrace = ex.StackTrace;
            result.ExecutionTime = DateTime.UtcNow - startTime;

            _logger.LogError(ex, "Activity execution failed: {ActivityType}", request.ActivityType.Name);

            // Add activity failed trace
            await _tracer.TraceAsync(request.ExecutionContext.WorkflowInstance.InstanceId, new ExecutionTraceEntry
            {
                StepNumber = request.ExecutionContext.WorkflowInstance.CurrentStepNumber,
                EntryType = TraceEntryType.ActivityFailed,
                ActivityName = request.ActivityType.Name,
                Duration = result.ExecutionTime,
                ErrorMessage = ex.Message,
                StackTrace = ex.StackTrace
            }, request.CancellationToken);

            // We always return results instead of throwing exceptions
            // The WorkflowEngine will handle exception propagation through ExecutionState
            _logger.LogDebug("Activity failed, returning error result for propagation handling: {ActivityType}", request.ActivityType.Name);
        }

        return result;
    }

    public TypedActivityExecutionContext<TWorkflowData, TPreviousStepData> CreateTypedActivityContext<TWorkflowData, TPreviousStepData>(
        StepExecutionContext<TWorkflowData> stepContext,
        TPreviousStepData? previousStepData,
        WorkflowStep step,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        return new TypedActivityExecutionContext<TWorkflowData, TPreviousStepData>(
            _serviceProvider,
            stepContext.WorkflowInstance.InstanceId,
            stepContext.WorkflowInstance.CorrelationId,
            step.ActivityType?.Name ?? "Unknown",
            stepContext.WorkflowInstance.CurrentStepNumber,
            stepContext.WorkflowDefinition.Name,
            stepContext.WorkflowDefinition.Version,
            DateTime.UtcNow,
            stepContext.WorkflowData,
            previousStepData,
            metadata: metadata,
            logger: _logger
        );
    }

    public object CreateEvaluationContext<TWorkflowData, TPreviousStepData>(
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData)
    {
        _logger.LogTrace("Creating evaluation context with TWorkflowData={WorkflowDataType}, TPreviousStepData={PreviousStepDataType}", 
            typeof(TWorkflowData).Name, typeof(TPreviousStepData).Name);

        // Always use the COMPILE-TIME generic type parameters, not runtime types
        // This ensures the context type matches what the compiled expressions expect
        var workflowDataType = typeof(TWorkflowData);
        var expectedPreviousStepDataType = typeof(TPreviousStepData);
        
        if (expectedPreviousStepDataType != typeof(object))
        {
            // Create WorkflowContext<TWorkflowData, TPreviousStepData> using EXPECTED types
            _logger.LogTrace("Creating WorkflowContext with expected types: WorkflowData={WorkflowDataType}, PreviousStepData={PreviousStepDataType}", 
                workflowDataType.Name, expectedPreviousStepDataType.Name);
            
            var contextType = typeof(WorkflowContext<,>).MakeGenericType(workflowDataType, expectedPreviousStepDataType);
            var context = Activator.CreateInstance(contextType);

            contextType.GetProperty("WorkflowData")?.SetValue(context, workflowData);
            contextType.GetProperty("PreviousStep")?.SetValue(context, previousStepData);

            _logger.LogTrace("Created context instance: {ContextInstanceType}", context?.GetType().FullName);
            
            return context!;
        }

        // Create WorkflowContext<TWorkflowData> for first step or when no previous step expected
        var singleGenericContext = new WorkflowContext<TWorkflowData>
        {
            WorkflowData = workflowData
        };
        
        _logger.LogTrace("Created single generic context: {ContextType}", singleGenericContext.GetType().FullName);
        
        return singleGenericContext;
    }

    /// <summary>
    /// Creates compensation evaluation context for compensation activities
    /// </summary>
    public object CreateCompensationEvaluationContext<TWorkflowData, TCurrentStepData>(
        TWorkflowData workflowData,
        TCurrentStepData? currentStepData)
    {
        _logger.LogTrace("Creating compensation evaluation context with TWorkflowData={WorkflowDataType}, TCurrentStepData={CurrentStepDataType}", 
            typeof(TWorkflowData).Name, typeof(TCurrentStepData).Name);

        var workflowDataType = typeof(TWorkflowData);
        var currentStepDataType = typeof(TCurrentStepData);
        
        if (currentStepDataType != typeof(object))
        {
            // Create CompensationContext<TWorkflowData, TCurrentStepData>
            _logger.LogTrace("Creating CompensationContext with types: WorkflowData={WorkflowDataType}, CurrentStepData={CurrentStepDataType}", 
                workflowDataType.Name, currentStepDataType.Name);
            
            var contextType = typeof(CompensationContext<,>).MakeGenericType(workflowDataType, currentStepDataType);
            var context = Activator.CreateInstance(contextType);

            contextType.GetProperty("WorkflowData")?.SetValue(context, workflowData);
            contextType.GetProperty("CurrentStep")?.SetValue(context, currentStepData);

            _logger.LogTrace("Created compensation context instance: {ContextInstanceType}", context?.GetType().FullName);
            
            return context!;
        }

        // Fallback to single generic context (shouldn't happen for compensation)
        var singleGenericContext = new WorkflowContext<TWorkflowData>
        {
            WorkflowData = workflowData
        };
        
        _logger.LogTrace("Created fallback single generic context: {ContextType}", singleGenericContext.GetType().FullName);
        
        return singleGenericContext;
    }

    public object CreateCatchEvaluationContext<TWorkflowData, TPreviousStepData>(
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData,
        Exception exception)
    {
        // Use compile-time generic type parameters for consistent context creation
        var workflowDataType = typeof(TWorkflowData);
        var exceptionType = exception.GetType();
        var expectedPreviousStepDataType = typeof(TPreviousStepData);

        if (expectedPreviousStepDataType != typeof(object))
        {
            // Create CatchContext<TWorkflowData, TException, TPreviousStepData> using EXPECTED types
            var contextType = typeof(CatchContext<,,>).MakeGenericType(workflowDataType, exceptionType, expectedPreviousStepDataType);
            var context = Activator.CreateInstance(contextType);

            contextType.GetProperty("WorkflowData")?.SetValue(context, workflowData);
            contextType.GetProperty("Exception")?.SetValue(context, exception);
            contextType.GetProperty("PreviousStep")?.SetValue(context, previousStepData);

            return context!;
        }
        else
        {
            // Create CatchContext<TWorkflowData, TException> for first step or when no previous step expected
            var contextType = typeof(CatchContext<,>).MakeGenericType(workflowDataType, exceptionType);
            var context = Activator.CreateInstance(contextType);

            contextType.GetProperty("WorkflowData")?.SetValue(context, workflowData);
            contextType.GetProperty("Exception")?.SetValue(context, exception);

            return context!;
        }
    }

    private IAsyncActivity CreateActivityInstance(Type activityType)
    {
        try
        {
            // Try to create via DI first
            var activity = _serviceProvider.GetService(activityType) as IAsyncActivity;
            if (activity != null)
            {
                _logger.LogTrace("Created activity {ActivityType} via dependency injection", activityType.Name);
                return activity;
            }

            // Fall back to Activator.CreateInstance
            activity = Activator.CreateInstance(activityType) as IAsyncActivity;
            if (activity == null)
                throw new InvalidOperationException($"Failed to create activity instance of type {activityType.Name}");

            _logger.LogTrace("Created activity {ActivityType} via Activator.CreateInstance", activityType.Name);
            return activity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create activity instance of type {ActivityType}", activityType.Name);
            throw new ActivityInstantiationException($"Failed to create activity instance of type {activityType.Name}", ex);
        }
    }

    private async Task ApplyTypedInputMappingsAsync<TWorkflowData, TPreviousStepData>(
        IAsyncActivity activity,
        List<PropertyMapping> inputMappings,
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData,
        Exception? catchException,
        TypedActivityExecutionResult<object> result,
        bool isCompensationActivity = false)
    {
        _logger.LogTrace("Applying typed input mappings - Activity: {ActivityType}, Mappings: {Count}, TWorkflowData: {WorkflowDataType}, TPreviousStepData: {PreviousStepDataType}, CatchException: {CatchException}",
            activity.GetType().Name, inputMappings.Count, typeof(TWorkflowData).Name, typeof(TPreviousStepData).Name, catchException?.GetType().Name ?? "null");
        
        _logger.LogDebug("Applying {Count} typed input mappings to activity {ActivityType}",
            inputMappings.Count, activity.GetType().Name);

        foreach (var mapping in inputMappings.Where(m => m.Direction == PropertyMappingDirection.Input))
        {
            var trace = new PropertyMappingTrace
            {
                PropertyName = mapping.TargetProperty
            };

            try
            {
                object evaluationContext;
                
                if (catchException != null)
                {
                    _logger.LogTrace("Catch exception detected: {ExceptionType}, creating CatchContext", catchException.GetType().Name);
                    // Use the dedicated method for creating catch contexts
                    evaluationContext = CreateCatchEvaluationContext(workflowData, previousStepData, catchException);
                    _logger.LogTrace("Created CatchContext for exception type {ExceptionType}: {ContextType}", 
                        catchException.GetType().Name, evaluationContext.GetType().FullName);
                }
                else
                {
                    _logger.LogTrace("No catch exception, checking if this is a compensation activity: {IsCompensation}", isCompensationActivity);
                    
                    // Check if this is a compensation activity and use appropriate context
                    // For compensation activities, TPreviousStepData is actually TCurrentStepData
                    if (isCompensationActivity)
                    {
                        _logger.LogTrace("Compensation activity detected, creating CompensationContext");
                        // Use reflection to call the appropriate context creation method
                        var method = GetType().GetMethod(nameof(CreateCompensationEvaluationContext));
                        if (method != null)
                        {
                            var genericMethod = method.MakeGenericMethod(typeof(TWorkflowData), typeof(TPreviousStepData));
                            evaluationContext = (object)genericMethod.Invoke(this, new object[] { workflowData, previousStepData })!;
                            _logger.LogTrace("Created CompensationContext: {ContextType}", evaluationContext.GetType().FullName);
                        }
                        else
                        {
                            throw new InvalidOperationException("CreateCompensationEvaluationContext method not found");
                        }
                    }
                    else
                    {
                        _logger.LogTrace("Regular activity, creating WorkflowContext");
                        // Use regular workflow context for non-catch activities
                        evaluationContext = CreateEvaluationContext(workflowData, previousStepData);
                        _logger.LogTrace("Created WorkflowContext: {ContextType}", evaluationContext.GetType().FullName);
                    }
                }

                _logger.LogTrace("Executing mapping.SourceFunction with context type: {ContextType}", evaluationContext.GetType().FullName);

                // Execute the compiled source function
                var sourceValue = mapping.SourceFunction(evaluationContext);
                trace.SourceValue = sourceValue;

                // Set target property on activity
                var targetProperty = activity.GetType().GetProperty(mapping.TargetProperty);
                if (targetProperty != null && targetProperty.CanWrite)
                {
                    var convertedValue = ConvertValue(sourceValue, targetProperty.PropertyType);
                    targetProperty.SetValue(activity, convertedValue);
                    trace.TargetValue = convertedValue;
                    trace.IsSuccess = true;

                    _logger.LogTrace("Applied typed input mapping: {Property} = {Value}",
                        mapping.TargetProperty, convertedValue);
                }
                else
                {
                    trace.IsSuccess = false;
                    trace.ErrorMessage = $"Property {mapping.TargetProperty} not found or not writable";
                    _logger.LogWarning("Target property not found or not writable: {Property} on {ActivityType}",
                        mapping.TargetProperty, activity.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                trace.IsSuccess = false;
                trace.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Failed to apply typed input mapping for property {Property}",
                    mapping.TargetProperty);
                throw new InvalidOperationException(
                    $"Failed to apply input mapping for property {mapping.TargetProperty}: {ex.Message}", ex);
            }

            result.InputTraces.Add(trace);
        }
    }

    private async Task ApplyTypedOutputMappingsAsync<TWorkflowData, TPreviousStepData>(
        IAsyncActivity activity,
        List<PropertyMapping> outputMappings,
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData,
        Exception? catchException,
        TypedActivityExecutionResult<object> result)
    {
        _logger.LogDebug("Applying {Count} typed output mappings from activity {ActivityType}",
            outputMappings.Count, activity.GetType().Name);

        foreach (var mapping in outputMappings.Where(m => m.Direction == PropertyMappingDirection.Output))
        {
            var trace = new PropertyMappingTrace
            {
                PropertyName = mapping.TargetProperty
            };

            try
            {
                // Get value from activity property
                var sourceProperty = activity.GetType().GetProperty(mapping.TargetProperty);
                if (sourceProperty != null && sourceProperty.CanRead)
                {
                    var sourceValue = sourceProperty.GetValue(activity);
                    trace.SourceValue = sourceValue;

                    // Create appropriate evaluation context for target assignment
                    object evaluationContext;
                    
                    if (catchException != null)
                    {
                        // Use the dedicated method for creating catch contexts
                        evaluationContext = CreateCatchEvaluationContext(workflowData, previousStepData, catchException);
                    }
                    else
                    {
                        // Use regular workflow context for non-catch activities
                        evaluationContext = CreateEvaluationContext(workflowData, previousStepData);
                    }

                    // Use the compiled target assignment function
                    if (mapping.TargetAssignmentFunction != null)
                    {
                        mapping.TargetAssignmentFunction(evaluationContext, sourceValue);
                        trace.TargetValue = sourceValue;
                        trace.IsSuccess = true;

                        _logger.LogTrace("Applied typed output mapping: {Property} = {Value}",
                            mapping.TargetProperty, sourceValue);
                    }
                    else
                    {
                        trace.IsSuccess = false;
                        trace.ErrorMessage = "No target assignment function";
                        _logger.LogWarning("No target assignment function for typed output mapping: {Property}",
                            mapping.TargetProperty);
                    }
                }
                else
                {
                    trace.IsSuccess = false;
                    trace.ErrorMessage = $"Property {mapping.TargetProperty} not found or not readable";
                    _logger.LogWarning("Source property not found or not readable: {Property} on {ActivityType}",
                        mapping.TargetProperty, activity.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                trace.IsSuccess = false;
                trace.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Failed to apply typed output mapping for property {Property}",
                    mapping.TargetProperty);
                throw new InvalidOperationException(
                    $"Failed to apply output mapping for property {mapping.TargetProperty}: {ex.Message}", ex);
            }

            result.OutputTraces.Add(trace);
        }
    }

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
