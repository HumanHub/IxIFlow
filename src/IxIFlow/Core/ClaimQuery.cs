namespace IxIFlow.Core;

/// <summary>A host's immutable placement snapshot for a runnable scan or execution claim.</summary>
public sealed record ClaimQuery
{
    public ClaimQuery(Guid hostId, IReadOnlyDictionary<string, string> capabilities, int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        HostId = hostId;
        Capabilities = MemberRegistry.SnapshotCapabilities(capabilities);
        Limit = limit;
    }

    public Guid HostId { get; }
    public IReadOnlyDictionary<string, string> Capabilities { get; }
    public int Limit { get; private init; }
    // Room for 1,024 refusals and 256 pass candidates; spare room also holds deferrals.
    internal const int MaxExcludedInstanceIds = 1280;
    public IReadOnlyList<string> ExcludedInstanceIds { get; private init; } = Array.Empty<string>();

    internal ClaimQuery Excluding(IEnumerable<string> ids, int limit)
    {
        var excluded = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (excluded.Any(id => id is null)) throw new ArgumentNullException(nameof(ids));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(excluded.Length, MaxExcludedInstanceIds);
        return this with { ExcludedInstanceIds = Array.AsReadOnly(excluded), Limit = limit };
    }
}

/// <summary>Operator claims may enter stopped work. Termination claims require a pending command and ignore placement.</summary>
public enum ExecutionClaimKind { Runnable, Operator, Termination }

/// <summary>The ordinal matching rule shared by hosts and the in-memory store.</summary>
public static class DemandMatching
{
    public static bool Meets(IEnumerable<WorkflowDemand> demands, ClaimQuery query,
        DateTime storeTimeUtc) => demands.All(demand => demand.ExpiresAtUtc <= storeTimeUtc ||
        query.Capabilities.TryGetValue(demand.Key, out var value) &&
            (demand.Value == null || demand.ValueExpiresAtUtc <= storeTimeUtc ||
                string.Equals(value, demand.Value, StringComparison.Ordinal)));
}
