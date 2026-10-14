using System.Reflection;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Executes one activity and returns its typed result to the checkpoint runner.
/// </summary>
internal sealed class ActivityTransitionExecutor(IActivityExecutor executor)
{
    public async Task<PreparedActivity> PrepareAsync(
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object workflowData,
        object? previous,
        object? catchFault,
        CancellationToken cancellationToken,
        string invocationId,
        string attemptId,
        int stepNumber,
        bool applyInputMappings,
        IReadOnlyCollection<string>? codeInputs = null,
        CompensationInputs? compensationInputs = null,
        IDictionary<string, object>? metadata = null)
    {
        var method = GetType().GetMethod(nameof(PrepareTypedAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(workflowData.GetType(), step.PreviousStepDataType ?? typeof(object));
        var task = (Task<PreparedActivity>)method.Invoke(this,
            [step, definition, instance, workflowData, previous, catchFault, cancellationToken,
                invocationId, attemptId, stepNumber, applyInputMappings, codeInputs, compensationInputs, metadata])!;
        return await task;
    }

    private Task<PreparedActivity> PrepareTypedAsync<TData, TPrevious>(
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object workflowData,
        object? previous,
        object? catchFault,
        CancellationToken cancellationToken,
        string invocationId,
        string attemptId,
        int stepNumber,
        bool applyInputMappings,
        IReadOnlyCollection<string>? codeInputs,
        CompensationInputs? compensationInputs,
        IDictionary<string, object>? metadata)
    {
        var request = CreateRequest<TData, TPrevious>(step, definition, instance, workflowData,
            previous, catchFault, cancellationToken, stepNumber, compensationInputs, metadata);
        return executor.PrepareTypedActivityAsync(request, applyInputMappings, invocationId,
            attemptId, codeInputs);
    }

    private static TypedActivityExecutionRequest<TData, TPrevious> CreateRequest<TData, TPrevious>(
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object workflowData,
        object? previous,
        object? catchFault,
        CancellationToken cancellationToken,
        int stepNumber,
        CompensationInputs? compensationInputs,
        IDictionary<string, object>? metadata)
    {
        return new TypedActivityExecutionRequest<TData, TPrevious>
        {
            StepNumber = stepNumber,
            ActivityType = step.ActivityType
                ?? throw new InvalidOperationException($"Activity step '{step.Id}' has no activity type"),
            WorkflowData = (TData)workflowData,
            PreviousStepData = previous is TPrevious typed ? typed : default,
            CatchFault = step.StepMetadata.ContainsKey("IsCompensationActivity")
                ? null
                : catchFault,
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
    }
}
