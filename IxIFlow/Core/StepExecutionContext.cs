namespace IxIFlow.Core;

/// <summary>
///     Context for step execution within a workflow
/// </summary>
public class StepExecutionContext<TWorkflowData>
{
    public WorkflowInstance WorkflowInstance { get; set; } = null!;
    public WorkflowDefinition WorkflowDefinition { get; set; } = null!;
    public TWorkflowData WorkflowData { get; set; } = default!;
    public object? PreviousStepData { get; set; }
}