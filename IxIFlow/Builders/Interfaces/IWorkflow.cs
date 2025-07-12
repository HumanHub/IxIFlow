namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Represents a workflow definition with versioning support
/// </summary>
/// <typeparam name="TWorkflowData">The workflow data type</typeparam>
public interface IWorkflow<TWorkflowData>
{
    /// <summary>
    /// Gets the version of this workflow definition
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Builds the workflow definition using the provided builder
    /// </summary>
    /// <param name="builder">The workflow builder instance</param>
    void Build(IWorkflowBuilder<TWorkflowData> builder);
}
