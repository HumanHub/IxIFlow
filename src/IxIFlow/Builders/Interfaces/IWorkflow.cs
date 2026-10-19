namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builds a workflow whose identity is declared by WorkflowAttribute on the class
/// </summary>
/// <typeparam name="TWorkflowData">The workflow data type</typeparam>
public interface IWorkflow<TWorkflowData>
{
    /// <summary>
    /// Builds the workflow definition using the provided builder
    /// </summary>
    /// <param name="builder">The workflow builder instance</param>
    void Build(IWorkflowBuilder<TWorkflowData> builder);
}
