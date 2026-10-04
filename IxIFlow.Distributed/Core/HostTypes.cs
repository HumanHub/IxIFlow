namespace IxIFlow.Core;

/// <summary>
/// Defines the capabilities and configuration of a workflow host
/// </summary>
public class HostCapabilities
{
    /// <summary>
    /// Unique identifier for the host
    /// </summary>
    public string HostId { get; set; } = "";
    
    /// <summary>
    /// Maximum number of workflows this host can run concurrently
    /// </summary>
    public int MaxConcurrentWorkflows { get; set; } = 100;
    
    /// <summary>
    /// Weight for load balancing (higher = more capacity)
    /// </summary>
    public int Weight { get; set; } = 1;
    
    /// <summary>
    /// Host capability tags (e.g., "gpu", "ml", "compliance", "eu-region")
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();
    
    /// <summary>
    /// HTTP endpoint URL for direct communication
    /// </summary>
    public string EndpointUrl { get; set; } = "";
    
    /// <summary>
    /// Whether this host supports immediate execution bypassing the queue
    /// </summary>
    public bool AllowImmediateExecution { get; set; } = true;
    
    /// <summary>
    /// Whether this host allows capacity override for emergency execution
    /// </summary>
    public bool AllowCapacityOverride { get; set; } = false;
    
    /// <summary>
    /// Health check reporting interval
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Current runtime status of a workflow host
/// </summary>
public class HostStatus
{
    /// <summary>
    /// Unique identifier for the host
    /// </summary>
    public string HostId { get; set; } = "";
    
    /// <summary>
    /// Whether the host is currently healthy and available
    /// </summary>
    public bool IsHealthy { get; set; }
    
    /// <summary>
    /// Current number of workflows running on this host
    /// </summary>
    public int CurrentWorkflowCount { get; set; }
    
    /// <summary>
    /// Maximum number of workflows this host can run concurrently
    /// </summary>
    public int MaxConcurrentWorkflows { get; set; }
    
    /// <summary>
    /// Host weight for load balancing calculations
    /// </summary>
    public int Weight { get; set; } = 1;
    
    /// <summary>
    /// Current host capability tags
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();
    
    /// <summary>
    /// HTTP endpoint URL for direct communication
    /// </summary>
    public string EndpointUrl { get; set; } = "";
    
    /// <summary>
    /// Last heartbeat timestamp from the host
    /// </summary>
    public DateTime LastHeartbeat { get; set; }
    
    /// <summary>
    /// Current CPU usage percentage (0-100)
    /// </summary>
    public double CpuUsage { get; set; }
    
    /// <summary>
    /// Current memory usage percentage (0-100)
    /// </summary>
    public double MemoryUsage { get; set; }
    
    /// <summary>
    /// Host status string (Available, Busy, Full, Offline)
    /// </summary>
    public string Status { get; set; } = "Unknown";
}

/// <summary>
/// Response from host health check API
/// </summary>
public class HostHealthResponse
{
    /// <summary>
    /// Whether the host is healthy
    /// </summary>
    public bool IsHealthy { get; set; }
    
    /// <summary>
    /// Host identifier
    /// </summary>
    public string HostId { get; set; } = "";
    
    /// <summary>
    /// When the health check was performed
    /// </summary>
    public DateTime CheckedAt { get; set; }
    
    /// <summary>
    /// Current number of workflows running
    /// </summary>
    public int CurrentWorkflowCount { get; set; }
    
    /// <summary>
    /// Maximum workflow capacity
    /// </summary>
    public int MaxWorkflowCount { get; set; }
    
    /// <summary>
    /// Current CPU usage percentage
    /// </summary>
    public double CpuUsage { get; set; }
    
    /// <summary>
    /// Current memory usage percentage
    /// </summary>
    public double MemoryUsage { get; set; }
    
    /// <summary>
    /// Host capability tags
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();
    
    /// <summary>
    /// Host status string
    /// </summary>
    public string Status { get; set; } = "Unknown";
}

/// <summary>
/// Configuration options for a workflow host
/// </summary>
public class WorkflowHostOptions
{
    /// <summary>
    /// Unique identifier for this host (defaults to machine name)
    /// </summary>
    public string HostId { get; set; } = Environment.MachineName;
    
    /// <summary>
    /// Maximum number of concurrent workflows
    /// </summary>
    public int MaxConcurrentWorkflows { get; set; } = 100;
    
    /// <summary>
    /// Weight for load balancing (higher = more capacity)
    /// </summary>
    public int Weight { get; set; } = 1;
    
    /// <summary>
    /// Host capability tags
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();
    
    /// <summary>
    /// HTTP endpoint URL for this host
    /// </summary>
    public string EndpointUrl { get; set; } = "";
    
    /// <summary>
    /// Whether to allow immediate execution bypassing the queue
    /// </summary>
    public bool AllowImmediateExecution { get; set; } = true;
    
    /// <summary>
    /// Whether to allow capacity override for emergency execution
    /// </summary>
    public bool AllowCapacityOverride { get; set; } = false;
    
    /// <summary>
    /// Health check reporting interval
    /// </summary>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    
    /// <summary>
    /// Connection string for distributed state repository
    /// </summary>
    public string StateRepositoryConnectionString { get; set; } = "";
    
    /// <summary>
    /// Connection string for message bus
    /// </summary>
    public string MessageBusConnectionString { get; set; } = "";
}

/// <summary>
/// Options for immediate workflow execution that bypasses normal queuing
/// </summary>
public class ImmediateExecutionOptions
{
    /// <summary>
    /// Whether to bypass the normal queue
    /// </summary>
    public bool BypassQueue { get; set; } = true;
    
    /// <summary>
    /// Whether to override capacity limits for emergency execution
    /// </summary>
    public bool OverrideCapacityLimit { get; set; } = false;
    
    /// <summary>
    /// Maximum time to wait for execution
    /// </summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// Execution priority (higher = more priority)
    /// </summary>
    public int Priority { get; set; } = int.MaxValue;
}

/// <summary>
/// Host registration information for service discovery
/// </summary>
public class HostRegistration
{
    /// <summary>
    /// Unique host identifier
    /// </summary>
    public string HostId { get; set; } = "";
    
    /// <summary>
    /// HTTP endpoint URL for direct communication
    /// </summary>
    public string EndpointUrl { get; set; } = "";
    
    /// <summary>
    /// Host capability tags
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();
    
    /// <summary>
    /// Host weight for load balancing
    /// </summary>
    public int Weight { get; set; }
    
    /// <summary>
    /// Maximum concurrent workflows
    /// </summary>
    public int MaxConcurrentWorkflows { get; set; }
    
    /// <summary>
    /// Last heartbeat timestamp
    /// </summary>
    public DateTime LastHeartbeat { get; set; }
    
    /// <summary>
    /// Whether the host is actively registered
    /// </summary>
    public bool IsActive { get; set; }
}

/// <summary>
/// Detailed status response from host
/// </summary>
public class HostStatusResponse
{
    /// <summary>
    /// Host identifier
    /// </summary>
    public string HostId { get; set; } = "";
    
    /// <summary>
    /// Current host status
    /// </summary>
    public HostStatus Status { get; set; } = new();
    
    /// <summary>
    /// Additional host metrics
    /// </summary>
    public Dictionary<string, object> Metrics { get; set; } = new();
    
    /// <summary>
    /// When the status was collected
    /// </summary>
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
}
