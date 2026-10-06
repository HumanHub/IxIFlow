using System.Reflection;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Runs a child workflow as an asynchronous transition of its parent continuation.
/// The child enters the same workflow engine through the invoker.
/// </summary>
internal sealed class WorkflowInvocationTransitionExecutor(IWorkflowInvoker invoker)
{
    public async Task<WorkflowInvocationTransitionResult> ExecuteAsync(
        WorkflowStep step, WorkflowDefinition definition,
        WorkflowInstance instance, object workflowData, object? previous, string invocationId,
        CancellationToken cancellationToken)
    {
        var method = GetType().GetMethod(nameof(ExecuteTypedAsync),
            BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(workflowData.GetType());
        var task = (Task<WorkflowInvocationTransitionResult>)method.Invoke(this,
            [step, definition, instance, workflowData, previous, invocationId, cancellationToken])!;
        return await task;
    }

    private async Task<WorkflowInvocationTransitionResult> ExecuteTypedAsync<TData>(WorkflowStep step,
        WorkflowDefinition definition, WorkflowInstance instance, object workflowData,
        object? previous, string invocationId, CancellationToken cancellationToken) where TData : class
    {
        var executionState = new ExecutionState { LastStepResult = previous };
        var context = new StepExecutionContext<TData>
        {
            WorkflowInstance = instance,
            WorkflowDefinition = definition,
            WorkflowData = (TData)workflowData,
            PreviousStepData = previous
        };
        var result = await invoker.ExecuteWorkflowInvocationAsync(step, context,
            executionState, invocationId, cancellationToken);
        if (executionState.PendingException != null)
            throw executionState.PendingException;
        if (result.WorkflowStatus is WorkflowExecutionStatus.Suspended or
            WorkflowExecutionStatus.Running or WorkflowExecutionStatus.NeedsResolution)
            return new WorkflowInvocationTransitionResult(result.ChildInstanceId!, null, true);
        if (!result.IsSuccess)
            throw result.Exception ?? new InvalidOperationException(result.ErrorMessage ??
                "Child workflow invocation failed");
        return new WorkflowInvocationTransitionResult(result.ChildInstanceId, result.OutputData, false);
    }
}

internal sealed record WorkflowInvocationTransitionResult(
    string? ChildInstanceId, object? Output, bool Suspended);
