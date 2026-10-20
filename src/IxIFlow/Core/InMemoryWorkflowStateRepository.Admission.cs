namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<(string Sender, string Trigger, string Key), WorkflowAdmissionRecord> _admissions = new();
    private readonly Dictionary<string, (byte[] Hash, EventAppendResult Receipt, DateTime Expires)> _webhookDeliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Guid Incarnation, WorkflowOwnership Metadata)> _ownership = new(StringComparer.Ordinal);

    public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
        Commit(commit, admission ?? throw new ArgumentNullException(nameof(admission)));

    public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key)
    {
        lock (_claimLock)
            return Task.FromResult(_admissions.TryGetValue((sender, trigger, key), out var admission) && admission.ExpiresAtUtc > Now
                ? admission : null);
    }

    public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId)
    {
        InstanceIds.Validate(instanceId);
        lock (_claimLock)
            return Task.FromResult(_ownership.TryGetValue(instanceId, out var ownership) &&
                (_instances.TryGetValue(instanceId, out var instance) ? instance.Incarnation == ownership.Incarnation
                    : ActiveTombstone(instanceId, Now) != null) ? ownership.Metadata : null);
    }

    private void ExpireAdmissions(DateTime now, int limit)
    {
        foreach (var key in _webhookDeliveries.Where(p => p.Value.Expires <= now).Take(limit).Select(p => p.Key).ToArray())
            _webhookDeliveries.Remove(key);
        foreach (var pair in _admissions.Where(p => p.Value.ExpiresAtUtc <= now)
                     .OrderBy(p => p.Value.ExpiresAtUtc).Take(limit).ToArray())
            _admissions.Remove(pair.Key);
        foreach (var id in _ownership.Keys.Where(id => (!_instances.TryGetValue(id, out var instance) || instance.Incarnation != _ownership[id].Incarnation) && ActiveTombstone(id, now) == null)
                     .Take(limit).ToArray())
            _ownership.Remove(id);
    }
}
