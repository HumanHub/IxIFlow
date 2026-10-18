using System.ComponentModel;
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
    public string InvocationId { get; }
    public string AttemptId { get; }
    public object? RecoveryState { get; internal set; }
    public ActivityAttempt Attempt { get; internal set; } = ActivityAttempt.First;
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
    public ReplyAddress? Reply { get; internal set; }

    // Explicit interface implementation for untyped access
    object IActivityContext.WorkflowData => WorkflowData ??
        throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "WorkflowData cannot be null");
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
        ILogger? logger = null,
        string invocationId = "",
        string attemptId = "",
        object? recoveryState = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        WorkflowInstanceId = workflowInstanceId ?? throw new ArgumentNullException(nameof(workflowInstanceId));
        InvocationId = invocationId;
        AttemptId = attemptId;
        RecoveryState = recoveryState;
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
    public int StepNumber { get; set; }
    public Type ActivityType { get; set; } = null!;
    public TWorkflowData WorkflowData { get; set; } = default!;
    public TPreviousStepData? PreviousStepData { get; set; }
    public List<PropertyMapping> InputMappings { get; set; } = new();
    public List<PropertyMapping> OutputMappings { get; set; } = new();
    public WorkflowStep Step { get; set; } = null!;
    public StepExecutionContext<TWorkflowData> ExecutionContext { get; set; } = null!;
    public CancellationToken CancellationToken { get; set; } = default;
    public object? CatchFault { get; set; }

    /// <summary>The items of the <c>ForEach</c> bodies the activity runs in.</summary>
    internal ItemValues Items { get; set; } = ItemValues.None;
    public object? CompensationPreviousStepData { get; set; }
    public object? CompensationPreviousChainData { get; set; }

    /// <summary>For a request's compensation, the answer the request received, and whether one came.</summary>
    public object? CompensationReply { get; set; }
    public bool CompensationAnswered { get; set; }
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
/// An activity whose inputs and execution context have been prepared before invocation.
/// </summary>
public sealed class PreparedActivity
{
    private readonly Func<Task> _applyOutputs;
    private readonly Action<object?> _setRecoveryState;

    internal PreparedActivity(IAsyncActivity activity, IActivityContext context,
        Func<Task> applyOutputs, Action<object?> setRecoveryState, Action<ReplyAddress> setReply,
        Action<ActivityAttempt> setAttempt, Runtime.ActivityInputSnapshot inputs)
    {
        Activity = activity;
        Context = context;
        Inputs = inputs;
        _applyOutputs = applyOutputs;
        _setRecoveryState = setRecoveryState;
        _setReply = setReply;
        _setAttempt = setAttempt;
    }

    private readonly Action<ReplyAddress> _setReply;
    private readonly Action<ActivityAttempt> _setAttempt;

    public IAsyncActivity Activity { get; }
    public IActivityContext Context { get; }

    /// <summary>The inputs as saved; the activity holds copies read back from them.</summary>
    internal Runtime.ActivityInputSnapshot Inputs { get; }

    internal Task ApplyOutputsAsync() => _applyOutputs();
    internal void SetRecoveryState(object? state) => _setRecoveryState(state);
    internal void SetReply(ReplyAddress reply) => _setReply(reply);
    internal void SetAttempt(ActivityAttempt attempt) => _setAttempt(attempt);
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
    /// Creates an activity and applies its inputs without invoking user code.
    /// Frozen inputs are restored by the runner when applyInputMappings is false.
    /// </summary>
    Task<PreparedActivity> PrepareTypedActivityAsync<TWorkflowData, TPreviousStepData>(
        TypedActivityExecutionRequest<TWorkflowData, TPreviousStepData> request,
        bool applyInputMappings,
        string invocationId,
        string attemptId,
        IReadOnlyCollection<string>? codeInputs = null);

    /// <summary>
    /// Creates a typed activity execution context
    /// </summary>
    TypedActivityExecutionContext<TWorkflowData, TPreviousStepData> CreateTypedActivityContext<TWorkflowData, TPreviousStepData>(
        StepExecutionContext<TWorkflowData> stepContext,
        TPreviousStepData? previousStepData,
        WorkflowStep step,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);


}

/// <summary>
/// Implementation of enhanced activity executor
/// </summary>
public class ActivityExecutor : IActivityExecutor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ActivityExecutor> _logger;
    private readonly TimeProvider _time;

    public ActivityExecutor(
        IServiceProvider serviceProvider,
        ILogger<ActivityExecutor> logger,
        TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PreparedActivity> PrepareTypedActivityAsync<TWorkflowData, TPreviousStepData>(
        TypedActivityExecutionRequest<TWorkflowData, TPreviousStepData> request,
        bool applyInputMappings,
        string invocationId,
        string attemptId,
        IReadOnlyCollection<string>? codeInputs = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var activity = CreateActivityInstance(request.ActivityType);
        var context = new TypedActivityExecutionContext<TWorkflowData, TPreviousStepData>(
            _serviceProvider,
            request.ExecutionContext.WorkflowInstance.InstanceId,
            request.ExecutionContext.WorkflowInstance.CorrelationId,
            request.ActivityType.Name,
            request.StepNumber,
            request.ExecutionContext.WorkflowDefinition.Name,
            request.ExecutionContext.WorkflowDefinition.Version,
            _time.GetUtcNow().UtcDateTime,
            request.WorkflowData,
            request.PreviousStepData,
            request.StepMetadata,
            _logger,
            invocationId,
            attemptId);

        var mappingResult = new TypedActivityExecutionResult<object>();
        var inputs = new Runtime.ActivityInputSnapshot();
        if (applyInputMappings || codeInputs is { Count: > 0 })
            await ApplyTypedInputMappingsAsync(activity, inputs,
                applyInputMappings ? request.InputMappings : request.InputMappings
                    .Where(mapping => codeInputs!.Contains(mapping.TargetProperty)).ToList(),
                request.WorkflowData,
                request.PreviousStepData, request.CatchFault, mappingResult,
                request.Step.Runtime?.Context, request.CompensationPreviousStepData,
                request.CompensationPreviousChainData, request.Items, request.CompensationReply,
                request.CompensationAnswered);

        if (applyInputMappings)
        {
            var properties = activity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var marked = properties.Any(Runtime.ActivityContract.IsMarked);
            foreach (var property in properties.Where(property => Runtime.ActivityContract.IsInput(property, marked) &&
                         property.IsDefined(typeof(DefaultValueAttribute)) && !inputs.Properties.ContainsKey(property.Name)))
            {
                var (saved, copy) = Runtime.ActivityInputValue.Of(property,
                    Runtime.ActivityContract.Default(property, activity.GetType()), _serviceProvider);
                inputs.Properties[property.Name] = saved;
                property.SetValue(activity, copy);
            }
        }

        return new PreparedActivity(activity, context,
            () => ApplyTypedOutputMappingsAsync(activity, request.OutputMappings,
                request.WorkflowData, request.PreviousStepData, request.CatchFault,
                new TypedActivityExecutionResult<object>(), request.Items),
            state => context.RecoveryState = state,
            reply => context.Reply = reply,
            attempt => context.Attempt = attempt,
            inputs);
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
            _time.GetUtcNow().UtcDateTime,
            stepContext.WorkflowData,
            previousStepData,
            metadata: metadata,
            logger: _logger
        );
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
            activity = (IAsyncActivity)Activator.CreateInstance(activityType)!;

            _logger.LogTrace("Created activity {ActivityType} via Activator.CreateInstance", activityType.Name);
            return activity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create activity instance of type {ActivityType}", activityType.Name);
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Activity '{activityType.Name}' cannot be created in this host: {ex.Message}", ex);
        }
    }

    private async Task ApplyTypedInputMappingsAsync<TWorkflowData, TPreviousStepData>(
        IAsyncActivity activity,
        Runtime.ActivityInputSnapshot inputs,
        List<PropertyMapping> inputMappings,
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData,
        object? catchFault,
        TypedActivityExecutionResult<object> result,
        Func<Runtime.MappingContextValues, object>? defaultContext = null,
        object? compensationPreviousStep = null,
        object? compensationPreviousChain = null,
        ItemValues? items = null,
        object? compensationReply = null,
        bool compensationAnswered = false)
    {
        _logger.LogTrace("Applying typed input mappings - Activity: {ActivityType}, Mappings: {Count}, TWorkflowData: {WorkflowDataType}, TPreviousStepData: {PreviousStepDataType}, CatchFault: {CatchFault}",
            activity.GetType().Name, inputMappings.Count, typeof(TWorkflowData).Name, typeof(TPreviousStepData).Name, catchFault?.GetType().Name ?? "null");
        
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
                var evaluationContext = mapping.CompensationSource switch
                {
                    CompensationInputSource.PreviousStep => compensationPreviousStep
                        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                            $"Compensation input '{mapping.TargetProperty}' has no saved previous step"),
                    CompensationInputSource.PreviousCompensation => compensationPreviousChain
                        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                            $"Compensation input '{mapping.TargetProperty}' has no previous compensation result"),
                    _ => (mapping.ContextFactory ?? defaultContext ?? throw new WorkflowEngineException(
                        WorkflowErrorKind.InvalidDefinition, "The activity has no captured input context"))(new Runtime.MappingContextValues(workflowData,
                        previousStepData, catchFault, compensationReply, compensationAnswered))
                };

                if (mapping.CompensationSource is CompensationInputSource.PreviousStep or CompensationInputSource.PreviousCompensation &&
                    !mapping.SourceType.IsInstanceOfType(evaluationContext))
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        $"Compensation input '{mapping.TargetProperty}' requires {mapping.SourceType.Name}, but saved value is {evaluationContext.GetType().Name}");

                (items ?? ItemValues.None).On(evaluationContext);
                _logger.LogTrace("Executing mapping.SourceFunction with context type: {ContextType}", evaluationContext.GetType().FullName);

                // Execute the compiled source function
                var sourceValue = mapping.SourceFunction(evaluationContext);
                trace.SourceValue = sourceValue;

                // Set target property on activity
                var targetProperty = activity.GetType().GetProperty(mapping.TargetProperty);
                if (targetProperty != null && targetProperty.CanWrite)
                {
                    // The activity gets a copy of what its attempt saves, set once (A-09).
                    var (saved, convertedValue) = Runtime.ActivityInputValue.Of(targetProperty,
                        ConvertValue(sourceValue, activity.GetType(), targetProperty), _serviceProvider);
                    inputs.Properties[targetProperty.Name] = saved;
                    targetProperty.SetValue(activity, convertedValue);
                    trace.TargetValue = convertedValue;
                    trace.IsSuccess = true;

                    _logger.LogTrace("Applied typed input mapping: {Property} = {Value}",
                        mapping.TargetProperty, convertedValue);
                }
                else
                {
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"Activity input '{activity.GetType().Name}.{mapping.TargetProperty}' is not writable");
                }
            }
            catch (Exception ex)
            {
                trace.IsSuccess = false;
                trace.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Failed to apply typed input mapping for property {Property}",
                    mapping.TargetProperty);
                throw;
            }

            result.InputTraces.Add(trace);
        }
    }

    private async Task ApplyTypedOutputMappingsAsync<TWorkflowData, TPreviousStepData>(
        IAsyncActivity activity,
        List<PropertyMapping> outputMappings,
        TWorkflowData workflowData,
        TPreviousStepData? previousStepData,
        object? catchFault,
        TypedActivityExecutionResult<object> result,
        ItemValues? items = null)
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

                    var evaluationContext = mapping.ContextFactory!(
                        new Runtime.MappingContextValues(workflowData, previousStepData, catchFault));
                    (items ?? ItemValues.None).On(evaluationContext);

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
                        throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                            $"Activity output '{mapping.TargetProperty}' has no assignment");
                    }
                }
                else
                {
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"Activity output '{activity.GetType().Name}.{mapping.TargetProperty}' is not readable");
                }
            }
            catch (Exception ex)
            {
                trace.IsSuccess = false;
                trace.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Failed to apply typed output mapping for property {Property}",
                    mapping.TargetProperty);
                throw;
            }

            result.OutputTraces.Add(trace);
        }
    }

    // A value the input cannot take is an invalid definition, never passed on unconverted (F-23).
    private static object? ConvertValue(object? value, Type activityType, PropertyInfo target)
    {
        if (value == null || target.PropertyType.IsInstanceOfType(value)) return value;
        var underlying = Nullable.GetUnderlyingType(target.PropertyType) ?? target.PropertyType;
        try
        {
            // A whole number takes an enum property as reflection assigns it.
            return underlying.IsEnum && Type.GetTypeCode(value.GetType()) is >= TypeCode.SByte and <= TypeCode.UInt64
                ? Enum.ToObject(underlying, value)
                : Convert.ChangeType(value, underlying);
        }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Activity input '{activityType.Name}.{target.Name}' takes {target.PropertyType.Name}, " +
                $"not {value.GetType().Name}", error);
        }
    }
}
