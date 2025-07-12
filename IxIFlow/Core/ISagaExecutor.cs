using IxIFlow.Builders;

namespace IxIFlow.Core;

/// <summary>
/// Interface for saga execution with compensation and error handling
/// </summary>
public interface ISagaExecutor
{
    /// <summary>
    /// Executes a saga step with enhanced suspend/resume support including mid-saga resume
    /// </summary>
    Task<StepExecutionResult> ExecuteSagaStepAsync<TWorkflowData>(
        WorkflowStep sagaStep,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        CancellationToken cancellationToken,
        SagaResumeInfo? resumeInfo = null)
        where TWorkflowData : class;

    /// <summary>
    /// Handles saga resume after workflow is resumed - determines what to do based on PostResumeAction
    /// </summary>
    Task<WorkflowExecutionResult> HandleSagaResumeAsync<TEvent>(
        WorkflowInstance instance,
        TEvent @event,
        CancellationToken cancellationToken)
        where TEvent : class;
}
