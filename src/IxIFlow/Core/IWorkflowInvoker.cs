using IxIFlow.Core;

namespace IxIFlow.Core;

/// <summary>
/// Interface for workflow invocation operations
/// </summary>
public interface IWorkflowInvoker
{
    /// <summary>
    /// Executes a workflow invocation step with propagation-based exception handling
    /// </summary>
    /// <typeparam name="TWorkflowData">The parent workflow data type</typeparam>
    /// <param name="step">The workflow invocation step to execute</param>
    /// <param name="context">The step execution context</param>
    /// <param name="executionState">The current execution state</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The step execution result</returns>
    Task<StepExecutionResult> ExecuteWorkflowInvocationAsync<TWorkflowData>(
        WorkflowStep step,
        StepExecutionContext<TWorkflowData> context,
        ExecutionState executionState,
        string invocationId,
        CancellationToken cancellationToken)
        where TWorkflowData : class;
}

/// <summary>
/// Guarantees that a child workflow commits its initial checkpoint before any child activity
/// runs. A saved parent invocation without a child record can then be started after recovery.
/// </summary>
public interface ICheckpointedWorkflowInvoker : IWorkflowInvoker;
