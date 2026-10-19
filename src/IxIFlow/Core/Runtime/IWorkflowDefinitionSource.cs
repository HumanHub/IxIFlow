namespace IxIFlow.Core.Runtime;

/// <summary>Read-only definition access on the executor side of the boundary.</summary>
internal interface IWorkflowDefinitionSource
{
    Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version);
}

// The registry stays host-only. The public registry can extend this contract when the contract is public.
internal sealed class RegisteredWorkflowDefinitionSource(IWorkflowVersionRegistry registry) : IWorkflowDefinitionSource
{
    public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version) =>
        registry.GetWorkflowDefinitionAsync(name, version);
}
