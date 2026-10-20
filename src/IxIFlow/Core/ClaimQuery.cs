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
    // Recovery's ordered continuation; ordinary discovery and claims start at zero.
    internal long ScanOffset { get; init; }
    internal ClaimQuery Continue(long offset, int limit) => this with { ScanOffset = offset, Limit = limit };
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
