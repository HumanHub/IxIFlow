using System.Reflection;

namespace IxIFlow.Core;

/// <summary>Declares the name and positive version of a workflow class.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WorkflowAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public int Version { get; set; } = 1;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string[] Tags { get; set; } = [];
    public string? Icon { get; set; }
    public string? Category { get; set; }

    internal static WorkflowAttribute For(Type workflowType)
    {
        var identity = workflowType.GetCustomAttribute<WorkflowAttribute>(inherit: false)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Workflow class '{workflowType.FullName}' requires [Workflow(\"Name\", Version = n)]");
        if (string.IsNullOrWhiteSpace(identity.Name) || identity.Version < 1)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Workflow class '{workflowType.FullName}' requires a nonblank name and positive version in [Workflow]");
        return identity;
    }
}
