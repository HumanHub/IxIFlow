using System.Reflection;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The build rules of mappings (D-26, spec F-22). An activity that marks its properties with
/// <see cref="WorkflowInputAttribute"/> or <see cref="WorkflowOutputAttribute"/> takes inputs only into its marked
/// inputs and gives outputs only from its marked outputs, and each input it requires is mapped unless it has a
/// default. An activity without the attributes takes inputs into properties with a public setter and gives outputs
/// from properties with a public getter. A child workflow's data takes inputs into public settable properties and
/// gives outputs from public readable ones, and an event gives outputs from public readable ones. A mapping that
/// breaks a rule fails the build with a message naming the type and property.
/// </summary>
internal static class MappingRules
{
    /// <summary>Checks the rules and returns <paramref name="steps"/>.</summary>
    public static List<WorkflowStep> Checked(List<WorkflowStep> steps)
    {
        Validate(steps);
        return steps;
    }

    public static void Validate(IReadOnlyList<WorkflowStep> steps)
    {
        foreach (var step in ParallelRules.Flatten(steps))
        {
            try
            {
                if (step.ActivityType != null)
                    CheckActivity(step.ActivityType, step.InputMappings, step.OutputMappings);
                if (step.StepType == WorkflowStepType.WorkflowInvocation && step.InvokedDataType != null)
                    CheckData(step.InvokedDataType, step.InputMappings, step.OutputMappings, "Child workflow");
                if (step.ResumeEventType != null && step.ActivityType == null &&
                    step.StepType != WorkflowStepType.WorkflowInvocation)
                    CheckData(step.ResumeEventType, [], step.OutputMappings, "Event");
                if (step.StepMetadata.TryGetValue("CompensationActivities", out var value) &&
                    value is List<CompensationActivityInfo> compensations)
                    foreach (var compensation in compensations)
                        CheckActivity(compensation.ActivityType, compensation.InputMappings, compensation.OutputMappings);
            }
            catch (WorkflowEngineException error)
            {
                throw WorkflowBuildError.At(step, error);
            }
        }
    }

    private static void CheckActivity(Type activity, IEnumerable<PropertyMapping> inputs,
        IEnumerable<PropertyMapping> outputs)
    {
        var properties = activity.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var marked = properties.Any(property =>
            property.IsDefined(typeof(WorkflowInputAttribute)) || property.IsDefined(typeof(WorkflowOutputAttribute)));
        var mappedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in inputs.Where(item => item.Direction == PropertyMappingDirection.Input))
        {
            var property = Property(activity, mapping, "Activity");
            mappedInputs.Add(property.Name);
            if (marked && !property.IsDefined(typeof(WorkflowInputAttribute)))
                throw Refused("Activity", activity, property, "is not an input: it has no [WorkflowInput]");
            if (!marked && property.SetMethod is not { IsPublic: true })
                throw Refused("Activity", activity, property, "is not an input: it has no public setter");
        }
        foreach (var mapping in outputs.Where(item => item.Direction == PropertyMappingDirection.Output))
        {
            var property = Property(activity, mapping, "Activity");
            if (marked && !property.IsDefined(typeof(WorkflowOutputAttribute)))
                throw Refused("Activity", activity, property, "is not an output: it has no [WorkflowOutput]");
            if (!marked && property.GetMethod is not { IsPublic: true })
                throw Refused("Activity", activity, property, "is not an output: it has no public getter");
        }
        foreach (var property in properties)
            if (property.GetCustomAttribute<WorkflowInputAttribute>() is { Required: true, Default: null } &&
                !mappedInputs.Contains(property.Name))
                throw Refused("Activity", activity, property, "is a required input and is not mapped");
    }

    private static void CheckData(Type data, IEnumerable<PropertyMapping> inputs, IEnumerable<PropertyMapping> outputs,
        string owner)
    {
        foreach (var mapping in inputs.Where(item => item.Direction == PropertyMappingDirection.Input))
            if (Property(data, mapping, owner).SetMethod is not { IsPublic: true })
                throw Refused(owner, data, Property(data, mapping, owner), "is not an input: it has no public setter");
        foreach (var mapping in outputs.Where(item => item.Direction == PropertyMappingDirection.Output))
            if (Property(data, mapping, owner).GetMethod is not { IsPublic: true })
                throw Refused(owner, data, Property(data, mapping, owner), "is not an output: it has no public getter");
    }

    private static PropertyInfo Property(Type owner, PropertyMapping mapping, string kind) =>
        owner.GetProperty(mapping.TargetProperty, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
            $"{kind} '{owner.Name}' has no public property '{mapping.TargetProperty}' to map");

    private static WorkflowEngineException Refused(string kind, Type owner, PropertyInfo property, string reason) =>
        new(WorkflowErrorKind.InvalidDefinition, $"{kind} property '{owner.Name}.{property.Name}' {reason}");
}
