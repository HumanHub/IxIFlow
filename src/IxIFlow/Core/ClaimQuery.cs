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
    public int Limit { get; }
}

/// <summary>Operator claims may enter a resolution or stopped checkpoint regardless of its next timer.</summary>
public enum ExecutionClaimKind { Runnable, Operator }

/// <summary>The ordinal matching rule shared by hosts and the in-memory store.</summary>
public static class DemandMatching
{
    public static bool Meets(IEnumerable<WorkflowDemand> demands, ClaimQuery query,
        DateTime storeTimeUtc) => demands.All(demand => demand.ExpiresAtUtc <= storeTimeUtc ||
        query.Capabilities.TryGetValue(demand.Key, out var value) &&
            (demand.Value == null || string.Equals(value, demand.Value, StringComparison.Ordinal)));
}
