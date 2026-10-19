namespace IxIFlow.Core;

/// <summary>Read-only definition access on the executor side of the boundary.</summary>
public interface IWorkflowDefinitionSource
{
    Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version);
}

