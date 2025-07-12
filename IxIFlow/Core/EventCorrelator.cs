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

    public EventCorrelator(
        IWorkflowStateRepository stateRepository,
        IExpressionEvaluator expressionEvaluator,
        ILogger<EventCorrelator> logger)
    {
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        _expressionEvaluator = expressionEvaluator ?? throw new ArgumentNullException(nameof(expressionEvaluator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<WorkflowInstance>> FindMatchingWorkflowsAsync<TEvent>(
        TEvent @event,
        CancellationToken cancellationToken = default)
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
            // NOTE: We're not deserializing the condition from JSON because compiled conditions
            // cannot be easily serialized/deserialized. Instead, we need to get the original
            // WorkflowStep that contains the CompiledCondition.
            // 
            // For now, we'll return true if a condition marker exists, indicating that
            // condition evaluation should be handled by the workflow engine during resume.
            // This is a limitation of the current implementation - proper condition evaluation
            // would require either:
            // 1. Storing the original workflow definition with the instance
            // 2. Implementing expression serialization/deserialization
            // 3. Re-evaluating conditions during workflow continuation
            
            var conditionData = JsonSerializer.Deserialize<dynamic>(workflowInstance.SuspensionInfo.ResumeConditionJson);
            if (conditionData != null)
            {
                _logger.LogDebug("Resume condition marker found for workflow {InstanceId} - deferring evaluation to workflow engine",
                    workflowInstance.InstanceId);
                return true; // Defer to workflow engine for proper condition evaluation
            }

            return false;
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
}
