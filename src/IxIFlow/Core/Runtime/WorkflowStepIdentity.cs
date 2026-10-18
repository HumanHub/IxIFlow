namespace IxIFlow.Core.Runtime;

internal static class WorkflowStepIdentity
{
    public static void Assign(List<WorkflowStep> steps, string id, string sourcePath, int sourceLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var step = steps.LastOrDefault() ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
            "A step ID requires a preceding step");
        step.Id = id;
        step.SourceLocation = sourcePath + ":" + sourceLine;
    }

    public static void Name(List<WorkflowStep> steps, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var step = steps.LastOrDefault() ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
            "A step name requires a preceding step");
        step.Name = name;
    }
}
