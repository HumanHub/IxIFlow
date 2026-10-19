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

/// <summary>Operator claims may enter an idle resolution or stopped checkpoint, never a future park.</summary>
public enum ExecutionClaimKind { Runnable, Operator }

/// <summary>The ordinal matching rule shared by hosts and the in-memory store.</summary>
public static class DemandMatching
{
    public static bool Meets(IEnumerable<WorkflowDemand> demands, IReadOnlyDictionary<string, string> capabilities,
        DateTime storeTimeUtc) => demands.All(demand => demand.ExpiresAtUtc <= storeTimeUtc ||
        capabilities.Any(capability => string.Equals(capability.Key, demand.Key, StringComparison.Ordinal) &&
            (demand.Value == null || string.Equals(capability.Value, demand.Value, StringComparison.Ordinal))));
}
