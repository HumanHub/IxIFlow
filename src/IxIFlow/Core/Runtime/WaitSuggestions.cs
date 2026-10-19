using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>Creates the suggestion once, before a wait is saved. Answers never use this value.</summary>
internal static class WaitSuggestions
{
    public static StoredJson? Create(WorkflowStep step, object workflowData, object? previous, ItemValues? items = null)
    {
        if (step.DefaultPayload == null && step.InputMappings.Count == 0)
            return null;
        var eventType = step.ResumeEventType!;
        // Copy the declared payload so applying mappings cannot change the definition or a later occurrence.
        var payload = step.DefaultPayload == null
            ? Activator.CreateInstance(eventType)!
            : JsonSerializer.Deserialize(JsonSerializer.Serialize(step.DefaultPayload, eventType), eventType)!;
        var values = new MappingContextValues(workflowData, previous);
        var mapped = new Dictionary<string, object?>();
        foreach (var mapping in step.InputMappings.Where(mapping => mapping.Direction == PropertyMappingDirection.Input))
        {
            var context = (items ?? ItemValues.None).On((mapping.ContextFactory ?? step.Runtime.Context)(values));
            var property = eventType.GetProperty(mapping.TargetProperty)!;
            var value = mapping.SourceFunction(context);
            property.SetValue(payload, value);
            mapped[property.Name] = value;
        }
        foreach (var property in eventType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.IsDefined(typeof(RequiredAttribute))))
            if ((property.GetMethod != null ? property.GetValue(payload) : mapped.GetValueOrDefault(property.Name)) is null or "")
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"InvalidInput: event input '{eventType.Name}.{property.Name}' is required and cannot be null or an empty string");
        return StoredJson.Inline(JsonSerializer.Serialize(payload, eventType));
    }
}
