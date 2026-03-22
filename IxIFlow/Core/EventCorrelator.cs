using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     Implementation of IEventCorrelator that matches events with suspended workflows
/// </summary>
public class EventCorrelator : IEventCorrelator
{
    private readonly IExpressionEvaluator _expressionEvaluator;
    private readonly ILogger<EventCorrelator> _logger;
    private readonly IWorkflowStateRepository _stateRepository;
    private readonly IWorkflowVersionRegistry _versionRegistry;

    public EventCorrelator(
        IWorkflowStateRepository stateRepository,
        IExpressionEvaluator expressionEvaluator,
        IWorkflowVersionRegistry versionRegistry,
        ILogger<EventCorrelator> logger)
    {
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        _expressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
        _versionRegistry = versionRegistry ?? throw new ArgumentNullException(nameof(versionRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<WorkflowInstance>> FindMatchingWorkflowsAsync<TEvent>(
        TEvent @event,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        _logger.LogDebug("Finding workflows matching event of type {EventType}", typeof(TEvent).Name);

        // Get all suspended workflows
        var suspendedWorkflows = await _stateRepository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Suspended);

        // Filter workflows by event type
        var eventTypeName = typeof(TEvent).AssemblyQualifiedName;
        var matchingWorkflows = suspendedWorkflows
            .Where(w => w.SuspensionInfo != null &&
                        IsEventTypeMatch(w.SuspensionInfo.ResumeEventType, eventTypeName))
            .ToList();

        _logger.LogDebug("Found {Count} workflows with matching event type", matchingWorkflows.Count);

        // Evaluate resume conditions for each matching workflow
        var result = new List<WorkflowInstance>();
        foreach (var workflow in matchingWorkflows)
            if (await EvaluateResumeConditionAsync(@event, workflow, cancellationToken))
                result.Add(workflow);

        _logger.LogDebug("Found {Count} workflows with matching resume conditions", result.Count);

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> EvaluateResumeConditionAsync<TEvent>(
        TEvent @event,
        WorkflowInstance workflowInstance,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        if (workflowInstance.SuspensionInfo == null)
        {
            _logger.LogWarning("Cannot evaluate resume condition for workflow {InstanceId} - SuspensionInfo is null",
                workflowInstance.InstanceId);
            return false;
        }

        // If no resume condition is specified, any event of the correct type is a match
        if (string.IsNullOrEmpty(workflowInstance.SuspensionInfo.ResumeConditionJson))
        {
            _logger.LogDebug(
                "No resume condition specified for workflow {InstanceId} - any event of the correct type is a match",
                workflowInstance.InstanceId);
            return true;
        }

        try
        {
            var suspendStep = await GetSuspendStepAsync(workflowInstance);
            if (suspendStep == null)
            {
                _logger.LogWarning("Suspend step could not be resolved for workflow {InstanceId}", workflowInstance.InstanceId);
                return false;
            }

            if (suspendStep.CompiledCondition == null)
            {
                return true;
            }

            var workflowDataType = Type.GetType(workflowInstance.WorkflowDataType);
            if (workflowDataType == null)
            {
                _logger.LogWarning("Workflow data type {WorkflowDataType} could not be resolved for workflow {InstanceId}",
                    workflowInstance.WorkflowDataType, workflowInstance.InstanceId);
                return false;
            }

            var workflowData = JsonSerializer.Deserialize(workflowInstance.WorkflowDataJson, workflowDataType);
            if (workflowData == null)
            {
                _logger.LogWarning("Workflow data could not be deserialized for workflow {InstanceId}", workflowInstance.InstanceId);
                return false;
            }

            return suspendStep.CompiledCondition(CreateCombinedConditionContext(@event, workflowData, workflowInstance));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating resume condition for workflow {InstanceId}",
                workflowInstance.InstanceId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CheckResumeConditionAsync<TEvent>(
        string workflowInstanceId,
        TEvent @event,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        _logger.LogDebug("Checking resume condition for workflow {WorkflowInstanceId} with event of type {EventType}",
            workflowInstanceId, typeof(TEvent).Name);

        // Get the workflow instance
        var workflowInstance = await _stateRepository.GetWorkflowInstanceAsync(workflowInstanceId);
        if (workflowInstance == null)
        {
            _logger.LogWarning("Workflow instance {WorkflowInstanceId} not found",
                workflowInstanceId);
            return false;
        }

        if (workflowInstance.Status != WorkflowStatus.Suspended)
        {
            _logger.LogWarning("Workflow instance {WorkflowInstanceId} is not suspended (Status: {Status})",
                workflowInstanceId, workflowInstance.Status);
            return false;
        }

        // Evaluate the resume condition
        return await EvaluateResumeConditionAsync(@event, workflowInstance, cancellationToken);
    }

    /// <summary>
    ///     Checks if the event type matches the expected resume event type
    /// </summary>
    private bool IsEventTypeMatch(string expectedTypeName, string? actualTypeName)
    {
        if (string.IsNullOrEmpty(expectedTypeName) || string.IsNullOrEmpty(actualTypeName)) return false;

        // Get the type from the type name
        var expectedType = Type.GetType(expectedTypeName);
        var actualType = Type.GetType(actualTypeName);

        if (expectedType == null || actualType == null) return false;

        // Check if the actual type is assignable to the expected type
        return expectedType.IsAssignableFrom(actualType);
    }

    private async Task<WorkflowStep?> GetSuspendStepAsync(WorkflowInstance workflowInstance)
    {
        var definition = await _versionRegistry.GetWorkflowDefinitionAsync(workflowInstance.WorkflowName, workflowInstance.WorkflowVersion);
        if (definition == null)
        {
            return null;
        }

        if (workflowInstance.SuspensionInfo?.Metadata.TryGetValue("StepId", out var stepIdObj) != true || stepIdObj is not string stepId)
        {
            return null;
        }

        return FindStepById(definition.Steps, stepId);
    }

    private static WorkflowStep? FindStepById(List<WorkflowStep> steps, string stepId)
    {
        foreach (var step in steps)
        {
            if (step.Id == stepId)
            {
                return step;
            }

            var nested = FindStepById(step.SequenceSteps, stepId)
                         ?? FindStepById(step.ThenSteps, stepId)
                         ?? FindStepById(step.ElseSteps, stepId)
                         ?? FindStepById(step.LoopBodySteps, stepId)
                         ?? FindStepById(step.FinallySteps, stepId);
            if (nested != null)
            {
                return nested;
            }

            foreach (var branch in step.ParallelBranches)
            {
                nested = FindStepById(branch, stepId);
                if (nested != null)
                {
                    return nested;
                }
            }

            foreach (var catchBlock in step.CatchBlocks)
            {
                nested = FindStepById(catchBlock.SequenceSteps, stepId);
                if (nested != null)
                {
                    return nested;
                }
            }

            if (step.StepMetadata.TryGetValue("OutcomeBranches", out var branchesObj) && branchesObj is System.Collections.IList branches)
            {
                foreach (var branchObj in branches)
                {
                    if (branchObj.GetType().GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)?.GetValue(branchObj) is List<WorkflowStep> branchSteps)
                    {
                        nested = FindStepById(branchSteps, stepId);
                        if (nested != null)
                        {
                            return nested;
                        }
                    }
                }
            }

            if (step.StepMetadata.TryGetValue("DefaultBranch", out var defaultBranchObj) && defaultBranchObj != null)
            {
                if (defaultBranchObj.GetType().GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)?.GetValue(defaultBranchObj) is List<WorkflowStep> defaultBranchSteps)
                {
                    nested = FindStepById(defaultBranchSteps, stepId);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }

    private static object CreateCombinedConditionContext<TEvent>(TEvent @event, object workflowData, WorkflowInstance workflowInstance)
        where TEvent : class
    {
        var contextType = typeof(ResumeEventContext<,>).MakeGenericType(workflowData.GetType(), typeof(TEvent));
        var context = Activator.CreateInstance(contextType) ?? throw new InvalidOperationException($"Failed to create resume event context for {contextType.FullName}");

        contextType.GetProperty(nameof(ResumeEventContext<object, object>.WorkflowData))?.SetValue(context, workflowData);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.ResumeEvent))?.SetValue(context, @event);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.WorkflowInstanceId))?.SetValue(context, workflowInstance.InstanceId);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.WorkflowName))?.SetValue(context, workflowInstance.WorkflowName);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.WorkflowVersion))?.SetValue(context, workflowInstance.WorkflowVersion);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.CurrentStepNumber))?.SetValue(context, workflowInstance.CurrentStepNumber);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.StartedAt))?.SetValue(context, workflowInstance.StartedAt ?? workflowInstance.CreatedAt);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.CorrelationId))?.SetValue(context, workflowInstance.CorrelationId);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.Properties))?.SetValue(context, workflowInstance.Properties);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.SuspendedAt))?.SetValue(context, workflowInstance.SuspensionInfo?.SuspendedAt ?? workflowInstance.CreatedAt);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.ResumedAt))?.SetValue(context, DateTime.UtcNow);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.SuspendReason))?.SetValue(context, workflowInstance.SuspensionInfo?.SuspendReason ?? string.Empty);

        var suspendedAt = workflowInstance.SuspensionInfo?.SuspendedAt ?? workflowInstance.CreatedAt;
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.SuspensionDuration))?.SetValue(context, DateTime.UtcNow - suspendedAt);

        return context;
    }
}
