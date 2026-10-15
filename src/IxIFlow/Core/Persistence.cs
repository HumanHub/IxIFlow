using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>
///     Repository interface for workflow state persistence
/// </summary>
public interface IWorkflowStateRepository
{
    /// <summary>
    /// Records a cancellation request outside the revisioned checkpoint so a remote
    /// caller never overwrites the running owner's state. The first request wins.
    /// Returns false when the instance is absent or terminal.
    /// </summary>
    Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason);

    /// <summary>Reads a durable cancellation request for an instance.</summary>
    Task<CancellationReason?> GetCancellationRequestAsync(string instanceId);

    /// <summary>
    /// Lists running instances without a live owner and suspended instances whose
    /// durable cancellation request still needs to be unwound.
    /// </summary>
    Task<IEnumerable<WorkflowInstance>> GetWorkflowsRequiringRecoveryAsync();

    /// <summary>
    /// Atomically saves a snapshot at expectedRevision + 1. Revision zero also represents
    /// an absent instance. Repeating a commit ID with the same payload returns its original
    /// receipt, even after subsequent commits. Does not mutate the supplied instance.
    /// </summary>
    Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(
        WorkflowInstance instance, long expectedRevision, string commitId);

    /// <summary>Acquires an absent or expired execution lease for one instance.</summary>
    Task<bool> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration);

    /// <summary>Extends a lease held by the same execution token.</summary>
    Task<bool> RenewExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration);

    /// <summary>Releases a lease only when the caller still owns it.</summary>
    Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token);

    /// <summary>
    /// Makes a suspended instance due for recovery now, for example when a child it waits for finishes.
    /// Stores that cannot do this leave the instance on its own schedule.
    /// </summary>
    Task MakeRunnableAsync(string instanceId) => Task.CompletedTask;

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
}

/// <summary>Tracks terminal checkpoints whose completion event still needs publication.</summary>
public interface IWorkflowCompletionOutbox
{
    Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId);
    Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync();
    Task<IEnumerable<WorkflowInstance>> ClaimUnpublishedCompletionsAsync(
        string? instanceId, string token, TimeSpan duration);
    Task<bool> MarkCompletionPublishedAsync(string instanceId, long revision, string token);
    Task ReleaseCompletionClaimAsync(string instanceId, string token);
}

/// <summary>
///     In-memory implementation of workflow state repository
/// </summary>
public class InMemoryWorkflowStateRepository : IWorkflowStateRepository, IWorkflowCompletionOutbox
{
    private readonly TimeProvider _time;

    public InMemoryWorkflowStateRepository() : this(TimeProvider.System)
    {
    }

    public InMemoryWorkflowStateRepository(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private readonly ConcurrentDictionary<string, WorkflowInstance> _instances = new();
    private readonly object _claimLock = new();
    private readonly Dictionary<(string InstanceId, string CommitId), (long ExpectedRevision, byte[] PayloadHash, long Revision)> _commits = new();
    private readonly Dictionary<string, (string Token, DateTime ExpiresAtUtc)> _leases = new();
    private readonly Dictionary<string, CancellationReason> _cancellations = new();
    private readonly HashSet<string> _acknowledgedCancellations = new();
    private readonly HashSet<string> _publishedCompletions = new();
    private readonly Dictionary<string, (string Token, DateTime ExpiresAtUtc)> _completionClaims = new();

    public Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId)
    {
        lock (_claimLock)
        {
            return Task.FromResult(_instances.TryGetValue(instanceId, out var instance) &&
                IsTerminal(instance.Status) && !_publishedCompletions.Contains(instanceId)
                ? SnapshotStructured(instance) : null);
        }
    }

    public Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync()
    {
        lock (_claimLock)
        {
            var instances = _instances.Values
                .Where(instance => IsTerminal(instance.Status) &&
                    !_publishedCompletions.Contains(instance.InstanceId))
                .Take(100)
                .Select(SnapshotStructured)
                .ToArray();
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }
    }

    public Task<IEnumerable<WorkflowInstance>> ClaimUnpublishedCompletionsAsync(
        string? instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_claimLock)
        {
            var now = Now;
            var instances = _instances.Values
                .Where(instance => (instanceId == null || instance.InstanceId == instanceId) &&
                    IsTerminal(instance.Status) && !_publishedCompletions.Contains(instance.InstanceId) &&
                    (!_completionClaims.TryGetValue(instance.InstanceId, out var claim) ||
                     claim.ExpiresAtUtc <= now))
                .Take(100).ToArray();
            foreach (var instance in instances)
                _completionClaims[instance.InstanceId] = (token, now.Add(duration));
            return Task.FromResult<IEnumerable<WorkflowInstance>>(
                instances.Select(SnapshotStructured).ToArray());
        }
    }

    public Task<bool> MarkCompletionPublishedAsync(string instanceId, long revision, string token)
    {
        lock (_claimLock)
        {
            if (!_instances.TryGetValue(instanceId, out var instance) ||
                instance.Revision != revision || !IsTerminal(instance.Status) ||
                !_completionClaims.TryGetValue(instanceId, out var claim) ||
                claim.Token != token || claim.ExpiresAtUtc <= Now)
                return Task.FromResult(false);
            _completionClaims.Remove(instanceId);
            return Task.FromResult(_publishedCompletions.Add(instanceId));
        }
    }

    public Task ReleaseCompletionClaimAsync(string instanceId, string token)
    {
        lock (_claimLock)
        {
            if (_completionClaims.TryGetValue(instanceId, out var claim) && claim.Token == token)
                _completionClaims.Remove(instanceId);
        }
        return Task.CompletedTask;
    }

    private static bool IsTerminal(WorkflowStatus status) => status is
        WorkflowStatus.Completed or WorkflowStatus.Failed or
        WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut;

    public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        lock (_claimLock)
        {
            if (!_instances.TryGetValue(instanceId, out var instance) ||
                instance.Status is not (WorkflowStatus.Running or WorkflowStatus.Suspended or
                    WorkflowStatus.NeedsResolution))
                return Task.FromResult(false);
            if (!_cancellations.ContainsKey(instanceId))
            {
                _cancellations.Add(instanceId,
                    JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))!);
                _acknowledgedCancellations.Remove(instanceId);
            }
            return Task.FromResult(true);
        }
    }

    public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        lock (_claimLock)
        {
            return Task.FromResult(_cancellations.TryGetValue(instanceId, out var reason)
                ? JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))
                : null);
        }
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowsRequiringRecoveryAsync()
    {
        lock (_claimLock)
        {
            var now = Now;
            var instances = _instances.Values
                .Where(instance =>
                    (instance.Status == WorkflowStatus.Running &&
                     (!_leases.TryGetValue(instance.InstanceId, out var lease) || lease.ExpiresAtUtc <= now)) ||
                    (instance.Status == WorkflowStatus.Suspended &&
                     _cancellations.ContainsKey(instance.InstanceId) &&
                     !_acknowledgedCancellations.Contains(instance.InstanceId)) ||
                    (instance.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution &&
                     instance.NextDueAtUtc <= now))
                .Select(SnapshotStructured)
                .ToArray();
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }
    }

    public Task<bool> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        ValidateLeaseArguments(instanceId, token, duration);
        lock (_claimLock)
        {
            var now = Now;
            if (_leases.TryGetValue(instanceId, out var lease) && lease.ExpiresAtUtc > now)
                return Task.FromResult(false);
            _leases[instanceId] = (token, now.Add(duration));
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration)
    {
        ValidateLeaseArguments(instanceId, token, duration);
        lock (_claimLock)
        {
            var now = Now;
            if (!_leases.TryGetValue(instanceId, out var lease) ||
                lease.Token != token || lease.ExpiresAtUtc <= now)
                return Task.FromResult(false);
            _leases[instanceId] = (token, now.Add(duration));
            return Task.FromResult(true);
        }
    }

    public Task MakeRunnableAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        lock (_claimLock)
        {
            if (_instances.TryGetValue(instanceId, out var instance) &&
                instance.Status == WorkflowStatus.Suspended)
                instance.NextDueAtUtc = Now;
        }
        return Task.CompletedTask;
    }

    public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_claimLock)
        {
            if (!_leases.TryGetValue(instanceId, out var lease) || lease.Token != token)
                return Task.FromResult(false);
            _leases.Remove(instanceId);
            return Task.FromResult(true);
        }
    }

    public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(
        WorkflowInstance instance, long expectedRevision, string commitId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (commitId.Length > 100)
            throw new ArgumentException("Commit IDs must be at most 100 characters", nameof(commitId));
        var snapshot = JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(instance))!;
        snapshot.Revision = checked(expectedRevision + 1);
        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)));
        lock (_claimLock)
        {
            var key = (instance.InstanceId, commitId);
            var revision = _instances.TryGetValue(instance.InstanceId, out var stored) ? stored.Revision : 0;
            var hasLease = _leases.TryGetValue(instance.InstanceId, out var lease);
            if (_commits.TryGetValue(key, out var receipt))
            {
                if (receipt.ExpectedRevision != expectedRevision || !receipt.PayloadHash.SequenceEqual(payloadHash))
                    throw new InvalidOperationException("A commit ID cannot be reused for a different transition");
                if (snapshot.Status == WorkflowStatus.Running && instance.ExecutionLeaseToken != null &&
                    (!hasLease || lease.Token != instance.ExecutionLeaseToken ||
                     lease.ExpiresAtUtc <= Now))
                    return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.Conflict, revision));
                return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, receipt.Revision));
            }

            if (revision != expectedRevision ||
                (hasLease && (lease.Token != instance.ExecutionLeaseToken ||
                              lease.ExpiresAtUtc <= Now)) ||
                (!hasLease && instance.ExecutionLeaseToken != null) ||
                ((snapshot.Status is WorkflowStatus.Cancelled or WorkflowStatus.Terminated or
                    WorkflowStatus.TimedOut) &&
                 _cancellations.ContainsKey(instance.InstanceId) &&
                 snapshot.CancellationReason == null))
                return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.Conflict, revision));
            _instances[instance.InstanceId] = snapshot;
            _commits.Add(key, (expectedRevision, payloadHash, snapshot.Revision));
            if (snapshot.CancellationReason != null && _cancellations.ContainsKey(instance.InstanceId))
                _acknowledgedCancellations.Add(instance.InstanceId);
            if (snapshot.Status != WorkflowStatus.Running)
                _leases.Remove(instance.InstanceId);
            return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.Applied, snapshot.Revision));
        }
    }

    public Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        lock (_claimLock)
        {
            if (instance.Revision != 0 ||
                (_instances.TryGetValue(instance.InstanceId, out var stored) && stored.Revision != 0))
                throw new InvalidOperationException("Revisioned instances require an atomic commit");
            _instances[instance.InstanceId] = SnapshotStructured(instance);
        }
        return Task.CompletedTask;
    }

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        _instances.TryGetValue(instanceId, out var instance);
        return Task.FromResult(instance == null ? null : SnapshotStructured(instance));
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName)
    {
        var instances = _instances.Values.Where(i => i.WorkflowName == workflowName).Select(SnapshotStructured).ToList();
        return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status)
    {
        var instances = _instances.Values.Where(i => i.Status == status).Select(SnapshotStructured).ToList();
        return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
    }

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId)
    {
        var instances = _instances.Values.Where(i => i.CorrelationId == correlationId).Select(SnapshotStructured).ToList();
        return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
    }

    public Task DeleteWorkflowInstanceAsync(string instanceId)
    {
        lock (_claimLock)
        {
            _instances.TryRemove(instanceId, out _);
            _leases.Remove(instanceId);
            _cancellations.Remove(instanceId);
            _acknowledgedCancellations.Remove(instanceId);
            _publishedCompletions.Remove(instanceId);
            foreach (var key in _commits.Keys.Where(key => key.InstanceId == instanceId).ToArray())
                _commits.Remove(key);
        }
        return Task.CompletedTask;
    }

    private static WorkflowInstance SnapshotStructured(WorkflowInstance instance) =>
        instance.Revision != 0 || ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson)
            ? JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(instance))!
            : instance;

    private static void ValidateLeaseArguments(string instanceId, string token, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
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

    public WorkflowVersionRegistry() { }

    public WorkflowVersionRegistry(IEnumerable<WorkflowDefinition> definitions)
    {
        foreach (var definition in definitions)
            RegisterWorkflow(definition);
    }

    public Task RegisterWorkflowAsync(WorkflowDefinition definition)
    {
        RegisterWorkflow(definition);
        return Task.CompletedTask;
    }

    private void RegisterWorkflow(WorkflowDefinition definition)
    {
        Runtime.WorkflowDefinitionIdentity.AssignImplicitName(definition);
        var key = $"{definition.Name}:{definition.Version}";
        var published = _workflows.GetOrAdd(key, definition);
        if (!ReferenceEquals(published, definition) &&
            (published.Description != definition.Description ||
             published.WorkflowDataType != definition.WorkflowDataType ||
             !published.Tags.SequenceEqual(definition.Tags) ||
             new WorkflowScopeCatalog(published).Fingerprint !=
             new WorkflowScopeCatalog(definition).Fingerprint))
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow {definition.Name} v{definition.Version} is already registered");
        }
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
                Id = definition.Id,
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
                Steps = definition.Steps,
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
