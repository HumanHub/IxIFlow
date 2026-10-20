namespace IxIFlow.Core;

/// <summary>The last row of a page ordered by runnable time, then ordinal instance ID.</summary>
public sealed record UnschedulableCursor(DateTime RunnableAtUtc, string InstanceId);

/// <summary>Due work that no live host can run. Demands describe absent capabilities or an incompatible combination.</summary>
public sealed record UnschedulableInstance(string InstanceId, string WorkflowName, int WorkflowVersion,
    DateTime RunnableAtUtc, IReadOnlyList<WorkflowDemand> UnmetDemands);

/// <summary>A bounded diagnostic read, evaluated at StoreTimeUtc. Paging is a fresh read, not a frozen snapshot.</summary>
public sealed record UnschedulablePage(IReadOnlyList<UnschedulableInstance> Items,
    UnschedulableCursor? NextCursor, DateTime StoreTimeUtc);

internal static class UnschedulableWork
{
    internal static void Validate(int limit, UnschedulableCursor? cursor)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be 1 through 1000");
        if (cursor != null) ArgumentException.ThrowIfNullOrWhiteSpace(cursor.InstanceId);
    }

    internal static ClaimQuery[] LiveHosts(IEnumerable<StoreMember> members, DateTime now) => members
        .Where(member => member.Kind == MemberKind.Host && MemberRegistry.IsLive(member, now))
        .Select(member => new ClaimQuery(member.Id, member.Capabilities)).ToArray();

    internal static WorkflowDemand[] Explain(IEnumerable<WorkflowDemand> demands, ClaimQuery[] hosts, DateTime now)
    {
        var active = demands.Where(d => !(d.ExpiresAtUtc <= now)).OrderBy(d => d.Key, StringComparer.Ordinal).ToArray();
        var absent = active.Where(d => !hosts.Any(host => DemandMatching.Meets([d], host, now))).ToArray();
        // When different hosts cover different constraints, the whole combination explains the blockage.
        return absent.Length == 0 ? active : absent;
    }

    internal static UnschedulablePage Page(UnschedulableInstance[] rows, int limit, DateTime now)
    {
        var items = rows.Take(limit).ToArray();
        var last = items.LastOrDefault();
        return new(items, rows.Length > limit && last != null ? new(last.RunnableAtUtc, last.InstanceId) : null, now);
    }
}
