namespace IxIFlow.Core;

/// <summary>
/// Message bus interface for distributed workflow communication
/// Provides pub/sub messaging for workflow commands and events between hosts
/// </summary>
public interface IMessageBus
{
    /// <summary>
    /// Publish a message to the bus
    /// </summary>
    Task PublishAsync<T>(T message) where T : class;
    
    /// <summary>
    /// Consume messages of a specific type from the bus
    /// </summary>
    IAsyncEnumerable<T> ConsumeAsync<T>() where T : class;
    
    /// <summary>
    /// Stop the message bus and cleanup resources
    /// </summary>
    Task StopAsync();
}

/// <summary>
/// Workflow commands for distributed execution
/// </summary>
public class ExecuteWorkflowCommand
{
    public string InstanceId { get; set; } = "";
    public string TargetHostId { get; set; } = "";
    public WorkflowDefinition Definition { get; set; } = null!;
    public string WorkflowDataJson { get; set; } = "";
    public string WorkflowDataType { get; set; } = "";
    public WorkflowOptions? Options { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public int Priority { get; set; } = 0;
}

public class ResumeWorkflowCommand
{
    public string InstanceId { get; set; } = "";
    public string TargetHostId { get; set; } = "";
    public string EventDataJson { get; set; } = "";
    public string EventDataType { get; set; } = "";
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}

public class CancelWorkflowCommand
{
    public string InstanceId { get; set; } = "";
    public string TargetHostId { get; set; } = "";
    public CancellationReason Reason { get; set; } = null!;
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Workflow events for distributed coordination
/// </summary>
public class WorkflowExecutionStartedEvent
{
    public string InstanceId { get; set; } = "";
    public string HostId { get; set; } = "";
    public string WorkflowName { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

public class WorkflowExecutionCompletedEvent
{
    public string InstanceId { get; set; } = "";
    public string HostId { get; set; } = "";
    public WorkflowExecutionStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public TimeSpan ExecutionTime { get; set; }
}

public class HostHeartbeatEvent
{
    public string HostId { get; set; } = "";
    public int CurrentWorkflowCount { get; set; }
    public int MaxWorkflowCount { get; set; }
    public string[] Tags { get; set; } = Array.Empty<string>();
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public string Status { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
