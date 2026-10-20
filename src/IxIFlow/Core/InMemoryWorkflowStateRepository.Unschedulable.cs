namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demands);
        lock (_claimLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now;
            return Task.FromResult(UnschedulableWork.LiveHosts(_members.Values, now)
                .Any(host => DemandMatching.Meets(demands, host, now)));
        }
    }

    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        UnschedulableWork.Validate(limit, cursor);
        lock (_claimLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now;
            var hosts = UnschedulableWork.LiveHosts(_members.Values, now);
            var candidates = new List<WorkflowInstance>();
            foreach (var (setId, demands) in DueDemandSets(now))
            {
                var matching = hosts.Where(host => DemandMatching.Meets(demands, host, now)).ToArray();
                // Schedulable sets only visit their pin bucket, never their ordinary instance population.
                var matchingPins = matching.Select(host => host.Capabilities.GetValueOrDefault("host"))
                    .Where(pin => pin != null).ToHashSet(StringComparer.Ordinal);
                IEnumerable<string> members = matching.Length == 0 ? DueMembers(setId, now) :
                    _pinnedSetMembers.GetValueOrDefault(setId)?.Where(bucket => !matchingPins.Contains(bucket.Key))
                        .SelectMany(bucket => bucket.Value.GetViewBetween(new(now, "", true),
                            new(DateTime.MaxValue, "", true))).Select(pin => pin.Id) ?? [];
                foreach (var id in members)
                {
                    var instance = _instances[id];
                    if (!(instance.RunnableAtUtc <= now) ||
                        _leases.TryGetValue(id, out var lease) && lease.ExpiresAtUtc > now ||
                        cursor != null && (instance.RunnableAtUtc < cursor.RunnableAtUtc ||
                            instance.RunnableAtUtc == cursor.RunnableAtUtc &&
                            StringComparer.Ordinal.Compare(id, cursor.InstanceId) <= 0)) continue;
                    if (matching.Length != 0 && (_placement[id].Pin is not { } pin ||
                        matching.Any(host => DemandMatching.Meets([pin], host, now)))) continue;
                    candidates.Add(instance);
                }
            }
            var rows = candidates.OrderBy(i => i.RunnableAtUtc).ThenBy(i => i.InstanceId, StringComparer.Ordinal)
                .Take(limit + 1).Select(i => new UnschedulableInstance(i.InstanceId, i.Incarnation, i.WorkflowName, i.WorkflowVersion,
                    i.RunnableAtUtc!.Value, UnschedulableWork.Explain(SavedDemands(i.InstanceId), hosts, now))).ToArray();
            return Task.FromResult(UnschedulableWork.Page(rows, limit, now));
        }
    }
}
