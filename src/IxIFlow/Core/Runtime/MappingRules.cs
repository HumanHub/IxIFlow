using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The build rules of mappings. An activity that marks its properties with
/// <see cref="ActivityInputAttribute"/> or <see cref="ActivityOutputAttribute"/> takes inputs only into its marked
/// inputs and gives outputs only from its marked outputs, and each input it requires is mapped unless it has a
/// default. An activity without the attributes takes inputs into properties with a public setter and gives outputs
/// from properties with a public getter. A child workflow's data takes inputs into public settable properties and
/// gives outputs from public readable ones. An event takes suggestion inputs into public settable properties and
/// gives outputs from public readable ones. A mapping that
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
                if (step.DefaultPayload is { } payload &&
                    (step.ResumeEventType == null || !step.ResumeEventType.IsInstanceOfType(payload)))
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"Wait default payload has type {payload.GetType().Name}, expected {step.ResumeEventType?.Name ?? "an event type"}");
                if (step.ActivityType != null)
                    CheckActivity(step.ActivityType, step.InputMappings, step.OutputMappings);
                if (step.StepType == WorkflowStepType.WorkflowInvocation && step.InvokedDataType != null)
                    CheckData(step.InvokedDataType, step.InputMappings, step.OutputMappings, "Child workflow");
                if (step.ResumeEventType != null && step.ActivityType == null &&
                    step.StepType != WorkflowStepType.WorkflowInvocation)
                    CheckEvent(step);
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
        var contractProperties = activity.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var marked = contractProperties.Any(ActivityContract.IsMarked);
        foreach (var property in contractProperties)
        {
            if (ActivityContract.IsMarked(property))
            {
                if (ActivityContract.IsInput(property, true) && property.SetMethod is not { IsPublic: true })
                    throw Refused("Activity", activity, property, "is marked as an input but has no public setter");
                if (ActivityContract.IsOutput(property, true) && property.GetMethod is not { IsPublic: true })
                    throw Refused("Activity", activity, property, "is marked as an output but has no public getter");
            }
            if (property.IsDefined(typeof(RequiredAttribute)) && ActivityContract.IsOutput(property, marked) &&
                !ActivityContract.IsInput(property, marked))
                throw Refused("Activity", activity, property, "has [Required] but is output-only");
            if (property.GetCustomAttribute<DefaultValueAttribute>() != null)
            {
                if (property.SetMethod is not { IsPublic: true })
                    throw Refused("Activity", activity, property, "has [DefaultValue] but has no public setter");
                _ = ActivityContract.Default(property, activity);
            }
        }
        var mappedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in inputs.Where(item => item.Direction == PropertyMappingDirection.Input))
        {
            var property = Property(activity, mapping, "Activity");
            mappedInputs.Add(property.Name);
            if (marked && !ActivityContract.IsInput(property, marked))
                throw Refused("Activity", activity, property, "is not an input: it has no [ActivityInput]");
            if (property.SetMethod is not { IsPublic: true })
                throw Refused("Activity", activity, property, "is not an input: it has no public setter");
        }
        foreach (var mapping in outputs.Where(item => item.Direction == PropertyMappingDirection.Output))
        {
            var property = Property(activity, mapping, "Activity");
            if (marked && !ActivityContract.IsOutput(property, marked))
                throw Refused("Activity", activity, property, "is not an output: it has no [ActivityOutput]");
            if (property.GetMethod is not { IsPublic: true })
                throw Refused("Activity", activity, property, "is not an output: it has no public getter");
        }
        foreach (var property in properties)
            if (ActivityContract.IsInput(property, marked) && property.IsDefined(typeof(RequiredAttribute)) &&
                property.GetCustomAttribute<DefaultValueAttribute>() == null &&
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

    private static void CheckEvent(WorkflowStep step)
    {
        var type = step.ResumeEventType!;
        CheckData(type, step.InputMappings, step.OutputMappings, "Event");
        if (step.DefaultPayload == null && step.InputMappings.Count == 0)
            return;
        if (step.DefaultPayload == null && (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Event '{type.Name}' needs a public parameterless constructor to create a suggested payload");
        foreach (var mapping in step.InputMappings)
        {
            var property = Property(type, mapping, "Event");
            if (property.GetIndexParameters().Length != 0)
                throw Refused("Event", type, property, "is an indexer and cannot be mapped");
            if (mapping.Expression is { } expression && !property.PropertyType.IsAssignableFrom(expression.ReturnType))
                throw Refused("Event", type, property, $"takes {property.PropertyType.Name}, not {expression.ReturnType.Name}");
        }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.IsDefined(typeof(RequiredAttribute))))
        {
            if (property.GetIndexParameters().Length != 0)
                throw Refused("Event", type, property, "is an indexer and cannot supply a required input");
            if (!step.InputMappings.Any(mapping => mapping.TargetProperty == property.Name) &&
                (step.DefaultPayload == null || property.GetMethod == null || property.GetValue(step.DefaultPayload) is null or ""))
                throw Refused("Event", type, property, "is a required input and is not mapped or supplied by the default payload");
        }
    }

    private static PropertyInfo Property(Type owner, PropertyMapping mapping, string kind) =>
        owner.GetProperty(mapping.TargetProperty, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
            $"{kind} '{owner.Name}' has no public property '{mapping.TargetProperty}' to map");

    private static WorkflowEngineException Refused(string kind, Type owner, PropertyInfo property, string reason) =>
        new(WorkflowErrorKind.InvalidDefinition, $"{kind} property '{owner.Name}.{property.Name}' {reason}");
}
