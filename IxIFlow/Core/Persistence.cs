using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core;

/// <summary>
///     Repository interface for workflow state persistence
/// </summary>
public interface IWorkflowStateRepository
{
    /// <summary>
    ///     Save workflow instance state
    /// </summary>
    Task SaveWorkflowInstanceAsync(WorkflowInstance instance);

    /// <summary>
    ///     Get workflow instance by ID
    /// </summary>
    Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId);

    /// <summary>
    ///     Get workflow instances by workflow name
    /// </summary>
    Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName);

    /// <summary>
    ///     Get workflow instances by status
    /// </summary>
    Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status);

    /// <summary>
    ///     Get workflow instances by correlation ID
    /// </summary>
    Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId);

    /// <summary>
    ///     Delete workflow instance
    /// </summary>
    Task DeleteWorkflowInstanceAsync(string instanceId);

    /// <summary>
    ///     Get suspended workflow instances ready for resumption
    /// </summary>
    Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsReadyForResumptionAsync();
}

/// <summary>
///     Event store interface for workflow events
/// </summary>
public interface IEventStore
{
    /// <summary>
    ///     Append event to workflow instance
    /// </summary>
    Task AppendEventAsync(string workflowInstanceId, WorkflowEvent workflowEvent);

    /// <summary>
    ///     Get events for workflow instance
    /// </summary>
    Task<IEnumerable<WorkflowEvent>> GetEventsAsync(string workflowInstanceId);

    /// <summary>
    ///     Get events for workflow instance from a specific sequence number
    /// </summary>
    Task<IEnumerable<WorkflowEvent>> GetEventsFromSequenceAsync(string workflowInstanceId, long fromSequence);

    /// <summary>
    ///     Get the latest sequence number for a workflow instance
    /// </summary>
    Task<long> GetLatestSequenceNumberAsync(string workflowInstanceId);
}

/// <summary>
///     Workflow event for event sourcing
/// </summary>
public class WorkflowEvent
{
    /// <summary>
    ///     Unique event identifier
    /// </summary>
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Workflow instance ID
    /// </summary>
    public string WorkflowInstanceId { get; set; } = "";

    /// <summary>
    ///     Event sequence number
    /// </summary>
    public long SequenceNumber { get; set; }

    /// <summary>
    ///     Event type
    /// </summary>
    public string EventType { get; set; } = "";

    /// <summary>
    ///     Event data (JSON serialized)
    /// </summary>
    public string EventDataJson { get; set; } = "";

    /// <summary>
    ///     Event metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    ///     When the event occurred
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Version of the event schema
    /// </summary>
    public int EventVersion { get; set; } = 1;
}

/// <summary>
///     Activity state for persistence
/// </summary>
public class ActivityState
{
    /// <summary>
    ///     Activity name
    /// </summary>
    public string ActivityName { get; set; } = "";

    /// <summary>
    ///     Step number in workflow
    /// </summary>
    public int StepNumber { get; set; }

    /// <summary>
    ///     Activity execution status
    /// </summary>
    public ActivityExecutionStatus Status { get; set; }

    /// <summary>
    ///     Input data (JSON serialized)
    /// </summary>
    public string InputDataJson { get; set; } = "";

    /// <summary>
    ///     Output data (JSON serialized)
    /// </summary>
    public string OutputDataJson { get; set; } = "";

    /// <summary>
    ///     When activity started
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    ///     When activity completed
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    ///     Error message if failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Number of retry attempts
    /// </summary>
    public int RetryAttempts { get; set; }

    /// <summary>
    ///     Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
///     Activity execution status
/// </summary>
public enum ActivityExecutionStatus
{
    /// <summary>
    ///     Activity is ready to execute
    /// </summary>
    Ready,

    /// <summary>
    ///     Activity is currently executing
    /// </summary>
    Running,

    /// <summary>
    ///     Activity completed successfully
    /// </summary>
    Completed,

    /// <summary>
    ///     Activity failed
    /// </summary>
    Failed,

    /// <summary>
    ///     Activity was skipped
    /// </summary>
    Skipped,

    /// <summary>
    ///     Activity is suspended
    /// </summary>
    Suspended
}

/// <summary>
///     Workflow checkpoint for state reconstruction
/// </summary>
public class WorkflowCheckpoint
{
    /// <summary>
    ///     Checkpoint identifier
    /// </summary>
    public string CheckpointId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Workflow instance ID
    /// </summary>
    public string WorkflowInstanceId { get; set; } = "";

    /// <summary>
    ///     Checkpoint sequence number
    /// </summary>
    public long SequenceNumber { get; set; }

    /// <summary>
    ///     Workflow state at checkpoint
    /// </summary>
    public WorkflowInstance WorkflowState { get; set; } = null!;

    /// <summary>
    ///     When checkpoint was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Checkpoint metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
///     Workflow serializer for state persistence
/// </summary>
public class WorkflowSerializer
{
    private readonly JsonSerializerOptions _jsonOptions;

    public WorkflowSerializer()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    ///     Serialize object to JSON string
    /// </summary>
    public string Serialize<T>(T obj)
    {
        return JsonSerializer.Serialize(obj, _jsonOptions);
    }

    /// <summary>
    ///     Deserialize JSON string to object
    /// </summary>
    public T? Deserialize<T>(string json)
    {
        if (string.IsNullOrEmpty(json))
            return default;

        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    /// <summary>
    ///     Deserialize JSON string to object of specified type
    /// </summary>
    public object? Deserialize(string json, Type type)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        return JsonSerializer.Deserialize(json, type, _jsonOptions);
    }
}

/// <summary>
///     In-memory implementation of workflow state repository
/// </summary>
public class InMemoryWorkflowStateRepository : IWorkflowStateRepository
{
    private readonly ConcurrentDictionary<string, WorkflowInstance> _instances = new();

    public Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        _instances.AddOrUpdate(instance.InstanceId, instance, (key, existing) => instance);
        return Task.CompletedTask;
    }

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        _instances.TryGetValue(instanceId, out var instance);
        return Task.FromResult(instance);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName)
    {
        var instances = _instances.Values.Where(i => i.WorkflowName == workflowName);
        return Task.FromResult(instances);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status)
    {
        var instances = _instances.Values.Where(i => i.Status == status);
        return Task.FromResult(instances);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId)
    {
        var instances = _instances.Values.Where(i => i.CorrelationId == correlationId);
        return Task.FromResult(instances);
    }

    public Task DeleteWorkflowInstanceAsync(string instanceId)
    {
        _instances.TryRemove(instanceId, out _);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsReadyForResumptionAsync()
    {
        var suspendedInstances = _instances.Values.Where(i =>
            i.Status == WorkflowStatus.Suspended &&
            (i.SuspensionInfo?.ExpiresAt == null || i.SuspensionInfo.ExpiresAt <= DateTime.UtcNow));
        return Task.FromResult(suspendedInstances);
    }
}

/// <summary>
///     In-memory implementation of event store
/// </summary>
public class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, List<WorkflowEvent>> _events = new();
    private readonly object _lock = new();

    public Task AppendEventAsync(string workflowInstanceId, WorkflowEvent workflowEvent)
    {
        lock (_lock)
        {
            var events = _events.GetOrAdd(workflowInstanceId, _ => new List<WorkflowEvent>());
            workflowEvent.SequenceNumber = events.Count + 1;
            events.Add(workflowEvent);
        }

        return Task.CompletedTask;
    }

    public Task<IEnumerable<WorkflowEvent>> GetEventsAsync(string workflowInstanceId)
    {
        _events.TryGetValue(workflowInstanceId, out var events);
        return Task.FromResult(events?.AsEnumerable() ?? Enumerable.Empty<WorkflowEvent>());
    }

    public Task<IEnumerable<WorkflowEvent>> GetEventsFromSequenceAsync(string workflowInstanceId, long fromSequence)
    {
        _events.TryGetValue(workflowInstanceId, out var events);
        var filteredEvents = events?.Where(e => e.SequenceNumber >= fromSequence) ?? Enumerable.Empty<WorkflowEvent>();
        return Task.FromResult(filteredEvents);
    }

    public Task<long> GetLatestSequenceNumberAsync(string workflowInstanceId)
    {
        _events.TryGetValue(workflowInstanceId, out var events);
        var latestSequence = events?.LastOrDefault()?.SequenceNumber ?? 0;
        return Task.FromResult(latestSequence);
    }
}

/// <summary>
///     Workflow version registry interface
/// </summary>
public interface IWorkflowVersionRegistry
{
    /// <summary>
    ///     Register a workflow version
    /// </summary>
    Task RegisterWorkflowAsync(WorkflowDefinition definition);

    /// <summary>
    ///     Get workflow definition by name and version
    /// </summary>
    Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version);

    /// <summary>
    ///     Get latest workflow definition by name
    /// </summary>
    Task<WorkflowDefinition?> GetLatestWorkflowDefinitionAsync(string name);

    /// <summary>
    ///     Get all versions of a workflow
    /// </summary>
    Task<IEnumerable<WorkflowDefinition>> GetWorkflowVersionsAsync(string name);

    /// <summary>
    ///     Get all registered workflows
    /// </summary>
    Task<IEnumerable<WorkflowDefinition>> GetAllWorkflowDefinitionsAsync();

    /// <summary>
    ///     Deactivate a workflow version
    /// </summary>
    Task DeactivateWorkflowVersionAsync(string name, int version);

    /// <summary>
    ///     Set default workflow version
    /// </summary>
    Task SetDefaultWorkflowVersionAsync(string name, int version);
}

/// <summary>
///     Concrete implementation of workflow version registry
/// </summary>
public class WorkflowVersionRegistry : IWorkflowVersionRegistry
{
    private readonly ConcurrentDictionary<string, WorkflowDefinition> _workflows = new();

    public Task RegisterWorkflowAsync(WorkflowDefinition definition)
    {
        var key = $"{definition.Name}:{definition.Version}";
        _workflows.AddOrUpdate(key, definition, (k, existing) => definition);
        return Task.CompletedTask;
    }

    public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version)
    {
        var key = $"{name}:{version}";
        _workflows.TryGetValue(key, out var definition);
        return Task.FromResult(definition);
    }

    public Task<WorkflowDefinition?> GetLatestWorkflowDefinitionAsync(string name)
    {
        var versions = _workflows.Values
            .Where(w => w.Name == name)
            .OrderByDescending(w => w.Version)
            .FirstOrDefault();
        return Task.FromResult(versions);
    }

    public Task<IEnumerable<WorkflowDefinition>> GetWorkflowVersionsAsync(string name)
    {
        var versions = _workflows.Values
            .Where(w => w.Name == name)
            .OrderByDescending(w => w.Version)
            .AsEnumerable();
        return Task.FromResult(versions);
    }

    public Task<IEnumerable<WorkflowDefinition>> GetAllWorkflowDefinitionsAsync()
    {
        return Task.FromResult(_workflows.Values.AsEnumerable());
    }

    public Task DeactivateWorkflowVersionAsync(string name, int version)
    {
        var key = $"{name}:{version}";
        if (_workflows.TryGetValue(key, out var definition))
        {
            // Create a copy with IsActive = false
            var updatedDefinition = new WorkflowDefinition
            {
                Name = definition.Name,
                Version = definition.Version,
                Description = definition.Description,
                WorkflowDataType = definition.WorkflowDataType,
                CreatedAt = definition.CreatedAt,
                CreatedBy = definition.CreatedBy,
                Tags = definition.Tags,
                EstimatedStepCount = definition.EstimatedStepCount,
                SupportsSuspension = definition.SupportsSuspension,
                UsesSagaPattern = definition.UsesSagaPattern,
                SupportsParallelExecution = definition.SupportsParallelExecution,
                WorkflowFactory = definition.WorkflowFactory,
                Metadata = new Dictionary<string, object>(definition.Metadata)
                {
                    ["IsActive"] = false
                }
            };
            _workflows.TryUpdate(key, updatedDefinition, definition);
        }

        return Task.CompletedTask;
    }

    public Task SetDefaultWorkflowVersionAsync(string name, int version)
    {
        // First, unset any existing default
        var existingVersions = _workflows.Values.Where(w => w.Name == name).ToList();
        foreach (var existing in existingVersions)
            if (existing.Metadata.ContainsKey("IsDefault") && (bool)existing.Metadata["IsDefault"])
                existing.Metadata["IsDefault"] = false;

        // Set the new default
        var key = $"{name}:{version}";
        if (_workflows.TryGetValue(key, out var definition)) definition.Metadata["IsDefault"] = true;

        return Task.CompletedTask;
    }
}

/// <summary>
///     Workflow registration information
/// </summary>
public class WorkflowRegistration
{
    /// <summary>
    ///     Workflow definition
    /// </summary>
    public WorkflowDefinition Definition { get; set; } = null!;

    /// <summary>
    ///     When the workflow was registered
    /// </summary>
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Who registered the workflow
    /// </summary>
    public string RegisteredBy { get; set; } = "";

    /// <summary>
    ///     Registration metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}