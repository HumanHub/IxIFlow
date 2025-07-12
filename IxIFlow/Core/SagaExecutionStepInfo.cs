namespace IxIFlow.Core;

/// <summary>
///     Information about a saga step that was executed (for compensation tracking)
/// </summary>
public class SagaExecutionStepInfo
{
    public int StepIndex { get; set; }
    public WorkflowStep Step { get; set; } = null!;
    public object? Result { get; set; }
    public bool IsSuccessful { get; set; }
    public DateTime ExecutedAt { get; set; }
    public DateTime CompletedAt { get; set; }
}
