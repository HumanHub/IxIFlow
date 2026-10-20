namespace IxIFlow.Core.Runtime;

internal static class BreakpointConditionInputs
{
    public static BreakpointConditionRequest Request(WorkflowDefinition definition, string stepId, string text)
    {
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var step = scopes.Step(stepId);
        return new(scopes.StructureHash, text, definition.WorkflowDataType,
            step.PreviousStepDataType, ItemScope(scopes, stepId)?.ItemType);
    }

    public static ItemScope? ItemScope(WorkflowScopeCatalog scopes, string stepId) => scopes.AllSteps
        .Where(pair => pair.Value.ForEachItem != null &&
            stepId.StartsWith(scopes.ForEachScope(pair.Value) + "/", StringComparison.Ordinal))
        .OrderByDescending(pair => pair.Key.Length).Select(pair => pair.Value.ForEachItem).FirstOrDefault();
}
