namespace IxIFlow.Core.Runtime;

/// <summary>
/// Applies declared activity and event outputs to workflow data.
/// </summary>
internal static class WorkflowValueBinding
{
    public static bool Matches<TEvent>(WorkflowStep step, TEvent @event, object workflowData,
        object? previous = null, ItemValues? items = null)
        where TEvent : class
    {
        if (step.CompiledCondition == null)
            return true;

        // An answer that arrives before its WaitFor is reached, or while its saga compensates, is checked
        // without a previous step of the declared type, which exists only once the WaitFor is reached.
        var typedPrevious = step.StepMetadata.ContainsKey("IsSagaStep") &&
            step.PreviousStepDataType?.IsInstanceOfType(previous) == true;
        var factory = typedPrevious ? step.Runtime.PreviousEventContext! : step.Runtime.EventContext!;
        var context = factory(new MappingContextValues(workflowData, previous, Event: @event));
        return step.CompiledCondition((items ?? ItemValues.None).On(context));
    }

    public static void ApplyEventOutputs<TEvent>(WorkflowStep step, TEvent @event, object workflowData,
        ItemValues? items = null)
        where TEvent : class
    {
        var context = (items ?? ItemValues.None).On(step.Runtime.OutputContext(new MappingContextValues(workflowData)));
        foreach (var mapping in step.OutputMappings)
        {
            var property = @event.GetType().GetProperty(mapping.TargetProperty)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Event has no '{mapping.TargetProperty}' output");
            var assign = mapping.TargetAssignmentFunction
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Wait output '{mapping.TargetProperty}' has no assignment");
            assign(context, property.GetValue(@event));
        }
    }

    public static void ApplyActivityOutputs(WorkflowStep step, object? output, object workflowData,
        ItemValues? items = null)
    {
        if (step.OutputMappings.Count == 0)
            return;
        // Outputs are never skipped for want of a value to read them from (F-23).
        if (output == null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Step '{step.Id}' has outputs, but its child workflow ended without data");

        var context = (items ?? ItemValues.None).On(step.Runtime.OutputContext(new MappingContextValues(workflowData)));
        foreach (var mapping in step.OutputMappings)
        {
            var property = output.GetType().GetProperty(mapping.TargetProperty)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Activity has no '{mapping.TargetProperty}' output");
            var assign = mapping.TargetAssignmentFunction
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Activity output '{mapping.TargetProperty}' has no assignment");
            assign(context, property.GetValue(output));
        }
    }

    public static object EvaluationContext(WorkflowStep step, object workflowData, object? previous, ItemValues? items = null) =>
        (items ?? ItemValues.None).On(step.Runtime.Context
            (new MappingContextValues(workflowData, previous)));
}
