using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    internal abstract string AllDemandSetsSql { get; }
    internal abstract string UnschedulableSql { get; }

    private sealed class SetDemandRow
    {
        public string DemandSetId { get; set; } = "";
        public string? Key { get; set; }
        public string? Value { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime? ValueExpiresAtUtc { get; set; }
        public bool Inherit { get; set; }
    }

    private sealed class UnschedulableRow
    {
        public string InstanceId { get; set; } = "";
        public string WorkflowName { get; set; } = "";
        public int WorkflowVersion { get; set; }
        public DateTime RunnableAtUtc { get; set; }
        public string DemandSetId { get; set; } = "";
        public string? PinnedHost { get; set; }
        public DateTime? PinExpiresAtUtc { get; set; }
        public bool PinInherit { get; set; }
    }

    public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demands);
        return RegistryAsync(async (connection, transaction, now) =>
        {
            var members = await ReadLiveMembersAsync(connection, transaction, now, cancellationToken);
            return UnschedulableWork.LiveHosts(members, now).Any(host => DemandMatching.Meets(demands, host, now));
        }, cancellationToken);
    }

    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        UnschedulableWork.Validate(limit, cursor);
        // Registry lock joins heartbeat and capability snapshots. Time is read after this lock,
        // exactly as for live-member reads and the start-time Fail policy.
        return RegistryAsync(async (connection, transaction, now) =>
        {
            var hosts = UnschedulableWork.LiveHosts(
                await ReadLiveMembersAsync(connection, transaction, now, cancellationToken), now);
            var rows = await connection.QueryAsync<SetDemandRow>(new CommandDefinition(AllDemandSetsSql,
                WithTimes(new { }, ("Now", now)), transaction: transaction, cancellationToken: cancellationToken));
            var sets = rows.GroupBy(row => row.DemandSetId).ToDictionary(group => group.Key,
                group => group.Where(row => row.Key != null).Select(row => new WorkflowDemand(row.Key!, row.Value,
                    row.ExpiresAtUtc is { } expiry ? ReadTime(expiry) : null, row.Inherit)
                    { ValueExpiresAtUtc = row.ValueExpiresAtUtc is { } valueExpiry ? ReadTime(valueExpiry) : null }).ToArray(), StringComparer.Ordinal);
            var placement = sets.Select(set =>
            {
                var matching = hosts.Where(host => DemandMatching.Meets(set.Value, host, now)).ToArray();
                return new { Id = set.Key, Failed = matching.Length == 0,
                    Pins = matching.Select(host => host.Capabilities.GetValueOrDefault("host"))
                        .Where(pin => pin != null).Distinct(StringComparer.Ordinal).ToArray() };
            }).ToArray();
            var parameters = WithTimes(new { Sets = JsonSerializer.Serialize(placement), Limit = limit + 1,
                AfterId = cursor?.InstanceId }, ("Now", now), ("AfterTime", cursor?.RunnableAtUtc));
            var candidates = await connection.QueryAsync<UnschedulableRow>(new CommandDefinition(UnschedulableSql,
                parameters, transaction, cancellationToken: cancellationToken));
            var result = candidates.Select(row =>
            {
                IEnumerable<WorkflowDemand> demands = sets[row.DemandSetId];
                if (row.PinnedHost != null) demands = demands.Append(new("host", row.PinnedHost,
                    row.PinExpiresAtUtc is { } expiry ? ReadTime(expiry) : null, row.PinInherit));
                return new UnschedulableInstance(row.InstanceId, row.WorkflowName, row.WorkflowVersion,
                    ReadTime(row.RunnableAtUtc), UnschedulableWork.Explain(demands, hosts, now));
            }).ToArray();
            return UnschedulableWork.Page(result, limit, now);
        }, cancellationToken);
    }
}
