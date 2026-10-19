namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<string, DateTime> _endedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkflowTombstone> _tombstones = new(StringComparer.Ordinal);

    private WorkflowTombstone? ActiveTombstone(string id, DateTime now) =>
        _tombstones.TryGetValue(id, out var row) && row.ExpiresAtUtc > now ? row : null;

    private void RefuseRemoved(string id, DateTime now)
    {
        if (ActiveTombstone(id, now) != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InstanceRemoved,
                $"Workflow instance '{id}' was removed by retention; its ID is reserved");
    }

    public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId)
    {
        lock (_claimLock)
            return Task.FromResult(ActiveTombstone(instanceId, Now));
    }

    public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention,
        int batchSize, CancellationToken cancellationToken = default)
    {
        RetentionSettings.Validate(finishedRetention, tombstoneRetention, batchSize);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_claimLock)
        {
            var now = Now;
            ExpireAdmissions(now, batchSize);
            // Sweep every publisher, including subscriptions no host currently serves.
            var unclaimed = _deliveries.Values.Where(d => d.Status == OutboxDeliveryStatus.Pending &&
                    d.Attempts == 0 && d.FirstAttemptAtUtc == null && now - d.Event.OccurredAtUtc >= _retryHorizon)
                .OrderBy(d => d.Event.OccurredAtUtc).ThenBy(d => d.Event.Id, StringComparer.Ordinal)
                .ThenBy(d => d.PublisherName, StringComparer.Ordinal).Take(batchSize).ToArray();
            foreach (var delivery in unclaimed)
                _deliveries[(delivery.Event.Id, delivery.PublisherName)] = delivery with
                {
                    Status = OutboxDeliveryStatus.GivenUp, LastError = OutboxRetry.NeverClaimedError,
                    LeaseToken = null, LeaseUntilUtc = null
                };
            var expired = _tombstones.Values.Where(t => t.ExpiresAtUtc <= now)
                .OrderBy(t => t.ExpiresAtUtc).ThenBy(t => t.InstanceId, StringComparer.Ordinal)
                .Take(batchSize).ToArray();
            foreach (var row in expired)
                _tombstones.Remove(row.InstanceId);
            var blocked = _deliveries.Values.Where(d => d.Status == OutboxDeliveryStatus.Pending &&
                    d.FirstAttemptAtUtc == null && _instances.TryGetValue(d.Event.InstanceId, out var instance) &&
                    WorkflowStoreRules.IsTerminal(instance.Status) &&
                    _endedAt.TryGetValue(instance.InstanceId, out var ended) && now - ended >= finishedRetention &&
                    !_instances.Values.Any(child => child.ParentInstanceId == instance.InstanceId && !WorkflowStoreRules.IsTerminal(child.Status)))
                .GroupBy(d => d.PublisherName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(d => d.Event.InstanceId).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
            var candidates = _instances.Values.Where(i => WorkflowStoreRules.IsTerminal(i.Status) &&
                    _endedAt.TryGetValue(i.InstanceId, out var ended) && now - ended >= finishedRetention &&
                    !_deliveries.Values.Any(d => d.Event.InstanceId == i.InstanceId && d.Status == OutboxDeliveryStatus.Pending) &&
                    !_instances.Values.Any(child => child.ParentInstanceId == i.InstanceId && !WorkflowStoreRules.IsTerminal(child.Status)))
                .OrderBy(i => _endedAt[i.InstanceId]).ThenBy(i => i.InstanceId, StringComparer.Ordinal)
                .Take(batchSize).Select(i => i.InstanceId).ToArray();
            foreach (var id in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Terminal children have already
                // copied their complete result into the parent's durable inbox, so no notice is needed.
                DeleteInstance(id);
                _tombstones[id] = new WorkflowTombstone(id, now, now + tombstoneRetention);
            }
            foreach (var setId in _demandSetMembers.Where(set => set.Value.Count == 0)
                         .Select(set => set.Key).Order(StringComparer.Ordinal).Take(batchSize).ToArray())
            {
                _demandSetMembers.Remove(setId);
                _demandSets.Remove(setId);
            }
            return Task.FromResult(new RetentionResult(candidates.Length, expired.Length) { SkippedUnattemptedByPublisher = blocked });
        }
    }
}
