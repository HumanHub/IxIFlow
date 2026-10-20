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

    public async Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention,
        int batchSize, CancellationToken cancellationToken = default)
    {
        RetentionSettings.Validate(finishedRetention, tombstoneRetention, batchSize);
        cancellationToken.ThrowIfCancellationRequested();
        string[] candidates;
        Dictionary<string, int> blocked;
        int expiredCount;
        DateTime cutoff;
        lock (_claimLock)
        {
            var now = Now;
            cutoff = now - finishedRetention;
            ExpireAdmissions(now, batchSize);
            // Sweep every publisher, including subscriptions no host currently serves.
            var unclaimed = _deliveries.Values.Where(d => d.Status == OutboxDeliveryStatus.Pending &&
                    d.Attempts == 0 && d.FirstAttemptAtUtc == null && now - (d.RetriedAtUtc ?? d.Event.OccurredAtUtc) >= _retryHorizon)
                .OrderBy(d => d.Event.OccurredAtUtc).ThenBy(d => d.Event.Id, StringComparer.Ordinal)
                .ThenBy(d => d.PublisherName, StringComparer.Ordinal).Take(batchSize).ToArray();
            foreach (var delivery in unclaimed)
                _deliveries[(delivery.Event.Id, delivery.PublisherName)] = delivery with
                {
                    Status = OutboxDeliveryStatus.GivenUp, GivenUpReason = OutboxGivenUpReason.Horizon, LastError = OutboxRetry.NeverClaimedError,
                    LeaseToken = null, LeaseUntilUtc = null
                };
            var expired = _tombstones.Values.Where(t => t.ExpiresAtUtc <= now)
                .OrderBy(t => t.ExpiresAtUtc).ThenBy(t => t.InstanceId, StringComparer.Ordinal)
                .Take(batchSize).ToArray();
            foreach (var row in expired)
                _tombstones.Remove(row.InstanceId);
            expiredCount = expired.Length;
            blocked = _deliveries.Values.Where(d => d.Status == OutboxDeliveryStatus.Pending &&
                    d.FirstAttemptAtUtc == null && _instances.TryGetValue(d.Event.InstanceId, out var instance) &&
                    WorkflowStoreRules.IsTerminal(instance.Status) &&
                    _endedAt.TryGetValue(instance.InstanceId, out var ended) && now - ended >= finishedRetention &&
                    !_instances.Values.Any(child => child.ParentInstanceId == instance.InstanceId))
                .GroupBy(d => d.PublisherName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(d => d.Event.InstanceId).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
            candidates = _instances.Values.Where(i => WorkflowStoreRules.IsTerminal(i.Status) &&
                    _endedAt.TryGetValue(i.InstanceId, out var ended) && now - ended >= finishedRetention &&
                    !_deliveries.Values.Any(d => d.Event.InstanceId == i.InstanceId && d.Status == OutboxDeliveryStatus.Pending) &&
                    !_instances.Values.Any(child => child.ParentInstanceId == i.InstanceId))
                .OrderBy(i => _endedAt[i.InstanceId]).ThenBy(i => i.InstanceId, StringComparer.Ordinal)
                .Take(batchSize).Select(i => i.InstanceId).ToArray();
        }
        var removed = 0;
        foreach (var id in candidates)
            if (await DeleteWithPayloadsAsync(id, tombstoneRetention, cutoff, cancellationToken)) removed++;
        lock (_claimLock)
        {
            foreach (var setId in _demandSetMembers.Where(set => set.Value.Count == 0)
                         .Select(set => set.Key).Order(StringComparer.Ordinal).Take(batchSize).ToArray())
            {
                _demandSetMembers.Remove(setId);
                _runnableSetMembers.Remove(setId);
                _pinnedSetMembers.Remove(setId);
                _demandSets.Remove(setId);
            }
        }
        return new RetentionResult(removed, expiredCount) { SkippedUnattemptedByPublisher = blocked };
    }
}
