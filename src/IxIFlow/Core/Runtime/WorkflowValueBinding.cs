namespace IxIFlow.Core.Runtime;

/// <summary>
/// Applies declared activity and event outputs to workflow data.
/// </summary>
internal static class WorkflowValueBinding
{
    public static bool Matches<TEvent>(WorkflowStep step, TEvent @event, object workflowData,
        object? previous = null)
        where TEvent : class
    {
        if (step.CompiledCondition == null)
            return true;

        var typedPrevious = step.StepMetadata.ContainsKey("IsSagaStep") &&
            step.PreviousStepDataType != null && previous != null;
        var contextType = typedPrevious
            ? typeof(ResumeEventContext<,,>).MakeGenericType(workflowData.GetType(),
                step.ResumeEventType!, step.PreviousStepDataType!)
            : typeof(ResumeEventContext<,>).MakeGenericType(workflowData.GetType(), step.ResumeEventType!);
        var context = Activator.CreateInstance(contextType)!;
        contextType.GetProperty(nameof(WorkflowContext<object>.WorkflowData))!.SetValue(context, workflowData);
        contextType.GetProperty(nameof(ResumeEventContext<object, object>.ResumeEvent))!.SetValue(context, @event);
        if (typedPrevious)
            contextType.GetProperty(nameof(WorkflowContext<object, object>.PreviousStep))!
                .SetValue(context, previous);
        return step.CompiledCondition(context);
    }

    public static void ApplyEventOutputs<TEvent>(WorkflowStep step, TEvent @event, object workflowData)
        where TEvent : class
    {
        var context = EvaluationContext(workflowData, null, null);
        foreach (var mapping in step.OutputMappings)
        {
            var property = @event.GetType().GetProperty(mapping.TargetProperty)
                ?? throw new InvalidOperationException($"Event has no '{mapping.TargetProperty}' output");
            var assign = mapping.TargetAssignmentFunction
                ?? throw new InvalidOperationException($"Wait output '{mapping.TargetProperty}' has no assignment");
            assign(context, property.GetValue(@event));
        }
    }

    public static void ApplyActivityOutputs(WorkflowStep step, object? output, object workflowData)
    {
        if (output == null)
            return;

        var context = EvaluationContext(workflowData, null, null);
        foreach (var mapping in step.OutputMappings)
        {
            var property = output.GetType().GetProperty(mapping.TargetProperty)
                ?? throw new InvalidOperationException($"Activity has no '{mapping.TargetProperty}' output");
            var assign = mapping.TargetAssignmentFunction
                ?? throw new InvalidOperationException($"Activity output '{mapping.TargetProperty}' has no assignment");
            assign(context, property.GetValue(output));
        }
    }

    public static object EvaluationContext(object workflowData, Type? previousType, object? previous)
    {
        var type = previousType == null
            ? typeof(WorkflowContext<>).MakeGenericType(workflowData.GetType())
            : typeof(WorkflowContext<,>).MakeGenericType(workflowData.GetType(), previousType);
        var context = Activator.CreateInstance(type)!;
        type.GetProperty(nameof(WorkflowContext<object>.WorkflowData))!.SetValue(context, workflowData);
        if (previousType != null && previous != null)
            type.GetProperty(nameof(WorkflowContext<object, object>.PreviousStep))!.SetValue(context, previous);
        return context;
    }
}
