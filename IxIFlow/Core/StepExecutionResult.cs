namespace IxIFlow.Core;

/// <summary>
///     Result of executing a workflow step
/// </summary>
public class StepExecutionResult
{
    public bool IsSuccess { get; set; }
    public object? OutputData { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorStackTrace { get; set; }
    public Exception? Exception { get; set; }
}