namespace IxIFlow.Builders;

/// <summary>
///     Information about saga steps for compensation tracking
/// </summary>
public class SagaStepInfo
{
    public string StepId { get; set; } = "";
    public Type ActivityType { get; set; } = null!;
    public int Order { get; set; }
    public List<CompensationActivityInfo> CompensationActivities { get; set; } = [];
}