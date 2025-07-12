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
    public bool IsSuccessful { get; set; }
    public bool IsSuccess { get; set; }
    public object? ActivityResult { get; set; }
    public CompensationActivityInfo? CompensationActivity => CompensationActivities.FirstOrDefault();
}