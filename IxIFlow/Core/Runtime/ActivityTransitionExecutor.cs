using System.Reflection;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Executes one activity and returns its typed result to the checkpoint runner.
/// </summary>
internal sealed class ActivityTransitionExecutor(IActivityExecutor executor)
{
    public async Task<object?> ExecuteAsync(
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object workflowData,
        object? previous,
        Exception? catchException,
        CancellationToken cancellationToken,
        CompensationInputs? compensationInputs = null,
        IDictionary<string, object>? metadata = null)
    {
        var method = GetType().GetMethod(nameof(ExecuteTypedAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(workflowData.GetType(), step.PreviousStepDataType ?? typeof(object));
        var task = (Task<object?>)method.Invoke(this,
            [step, definition, instance, workflowData, previous, catchException, cancellationToken,
                compensationInputs, metadata])!;
        return await task;
    }

    private async Task<object?> ExecuteTypedAsync<TData, TPrevious>(
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object workflowData,
        object? previous,
        Exception? catchException,
        CancellationToken cancellationToken,
        CompensationInputs? compensationInputs,
        IDictionary<string, object>? metadata)
    {
        var request = new TypedActivityExecutionRequest<TData, TPrevious>
        {
            ActivityType = step.ActivityType
                ?? throw new InvalidOperationException($"Activity step '{step.Id}' has no activity type"),
            WorkflowData = (TData)workflowData,
            PreviousStepData = previous is TPrevious typed ? typed : default,
            CatchException = step.StepMetadata.ContainsKey("IsCompensationActivity")
                ? null
                : catchException,
            IsCompensationActivity = step.StepMetadata.ContainsKey("IsCompensationActivity"),
            CompensationPreviousStepData = compensationInputs?.PreviousStep,
            CompensationPreviousChainData = compensationInputs?.PreviousCompensation,
            InputMappings = step.InputMappings,
            OutputMappings = step.OutputMappings,
            Step = step,
            StepMetadata = metadata,
            ExecutionContext = new StepExecutionContext<TData>
            {
                WorkflowInstance = instance,
                WorkflowDefinition = definition,
                WorkflowData = (TData)workflowData,
                PreviousStepData = previous
            },
            CancellationToken = cancellationToken
        };
        var result = await executor.ExecuteTypedActivityAsync(request);
        if (!result.IsSuccess)
            throw result.Exception ?? new InvalidOperationException(result.ErrorMessage ?? "Activity failed");
        return result.OutputData;
    }
}
