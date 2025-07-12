namespace IxIFlow.Core;

/// <summary>
/// Interface for handling suspend/resume operations with proper separation of concerns
/// </summary>
public interface ISuspendResumeExecutor
{
    /// <summary>
    /// Executes a suspend/resume step with propagation-based exception handling
    /// </summary>
    Task<StepExecutionResult> ExecuteSuspendResumeStepAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken)
        where TWorkflowData : class;

    /// <summary>
    /// Validates and prepares a suspended workflow for resumption with an event
    /// Returns the prepared workflow data and validation result
    /// </summary>
    Task<ResumeValidationResult> ValidateAndPrepareResumeAsync<TEvent>(
        string instanceId,
        TEvent @event,
        CancellationToken cancellationToken = default)
        where TEvent : class;
}
