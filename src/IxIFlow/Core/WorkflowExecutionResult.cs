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
    /// For a result of sending an event, what became of it: consumed by a wait, rejected with the
    /// reason, still pending while the instance is busy or stopped, or closed because the instance
    /// finished first. <see cref="Status"/> is always the instance's own status.
    /// </summary>
    public EventReceipt? Event { get; set; }
    
    public string? ErrorMessage { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorStackTrace { get; set; }
    /// <summary>Safe identity for correlating this error with inspection and diagnostics.</summary>
    public InspectionError? Diagnostic { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public List<ExecutionTraceEntry> TraceEntries { get; set; } = new();
    public object? WorkflowData { get; set; }
}

/// <summary>What became of an event sent to an instance.</summary>
public sealed record EventReceipt(string EventId, EventOutcome Outcome, EventRejection? Rejection = null)
{
    /// <summary>True when a wait took the event.</summary>
    public bool IsConsumed => Outcome == EventOutcome.Consumed;

    public static EventReceipt Of(StoredWorkflowEvent stored) =>
        new(stored.EventId, stored.Outcome, stored.Rejection);
}
