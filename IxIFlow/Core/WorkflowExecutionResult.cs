using System.ComponentModel.DataAnnotations;

namespace IxIFlow.Core;

/// <summary>
///     Result of executing an entire workflow
/// </summary>
public class WorkflowExecutionResult
{
    public string InstanceId { get; set; } = "";
    
    /// <summary>
    /// The execution status of the workflow
    /// </summary>
    public WorkflowExecutionStatus Status { get; set; }
    
    /// <summary>
    /// Legacy property for backward compatibility. Returns true only if Status is Success.
    /// </summary>
    public bool IsSuccess => Status == WorkflowExecutionStatus.Success;
    
    public string? ErrorMessage { get; set; }
    public string? ErrorStackTrace { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public List<ExecutionTraceEntry> TraceEntries { get; set; } = new();
    public object? WorkflowData { get; set; }
}
