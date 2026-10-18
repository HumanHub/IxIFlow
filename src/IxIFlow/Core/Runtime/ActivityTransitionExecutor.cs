namespace IxIFlow.Core.Runtime;

internal sealed record ActivityPreparation(WorkflowStep Step, WorkflowDefinition Definition, WorkflowInstance Instance,
    object Data, object? Previous, object? Fault, CancellationToken CancellationToken, string InvocationId,
    string AttemptId, int StepNumber, bool ApplyInputs, IReadOnlyCollection<string>? CodeInputs,
    CompensationInputs? CompensationInputs, IDictionary<string, object>? Metadata, ItemValues Items);

/// <summary>Dispatches an activity through the delegate captured by its builder.</summary>
internal sealed class ActivityTransitionExecutor(IActivityExecutor executor)
{
    public Task<PreparedActivity> PrepareAsync(WorkflowStep step, WorkflowDefinition definition,
        WorkflowInstance instance, object workflowData, object? previous, object? catchFault,
        CancellationToken cancellationToken, string invocationId, string attemptId, int stepNumber,
        bool applyInputMappings, IReadOnlyCollection<string>? codeInputs = null,
        CompensationInputs? compensationInputs = null, IDictionary<string, object>? metadata = null,
        ItemValues? items = null) =>
        step.Runtime.Prepare!(executor, new ActivityPreparation(step, definition, instance, workflowData,
            previous, catchFault, cancellationToken, invocationId, attemptId, stepNumber, applyInputMappings,
            codeInputs, compensationInputs, metadata, items ?? ItemValues.None));

    public static Func<IActivityExecutor, ActivityPreparation, Task<PreparedActivity>> Bind<TData, TPrevious>() =>
        (executor, values) =>
        {
            var request = CreateRequest<TData, TPrevious>(values.Step, values.Definition, values.Instance,
                values.Data, values.Previous, values.Fault, values.CancellationToken, values.StepNumber,
                values.CompensationInputs, values.Metadata);
            request.Items = values.Items;
            return executor.PrepareTypedActivityAsync(request, values.ApplyInputs, values.InvocationId,
                values.AttemptId, values.CodeInputs);
        };

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
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Activity step '{step.Id}' has no activity type"),
            WorkflowData = (TData)workflowData,
            PreviousStepData = previous is TPrevious typed ? typed : default,
            CatchFault = step.StepMetadata.ContainsKey("IsCompensationActivity")
                ? null
                : catchFault,
            CompensationPreviousStepData = compensationInputs?.PreviousStep,
            CompensationPreviousChainData = compensationInputs?.PreviousCompensation,
            CompensationReply = compensationInputs?.Reply,
            CompensationAnswered = compensationInputs?.Answered ?? false,
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
