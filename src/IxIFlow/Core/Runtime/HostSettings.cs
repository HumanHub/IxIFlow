namespace IxIFlow.Core.Runtime;

/// <summary>How many instances the host runs at once and how long it drains on stop.</summary>
internal sealed record HostSettings(int Slots, TimeSpan DrainTimeout, TimeSpan PollInterval, string Name)
{
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private sealed record CapabilitySnapshot(IWorkflowCatalog Catalog, long Revision, IReadOnlyDictionary<string, string> Values);
    private CapabilitySnapshot? _capabilitySnapshot;
    private long? _lastDemandRefresh;
    internal WorkflowHeartbeatService.CompatibilityMemo Compatibility { get; } = new();
    internal Microsoft.Extensions.Logging.ILogger<WorkflowHeartbeatService>? CapabilityLogger { get; init; }

    internal Task<IReadOnlyDictionary<string, string>> GetCapabilitiesAsync(IWorkflowCatalog catalog)
    {
        // Ordinary placement reads keep using a valid snapshot while a refresh is in flight.
        var snapshot = Volatile.Read(ref _capabilitySnapshot);
        return snapshot != null && ReferenceEquals(snapshot.Catalog, catalog) && snapshot.Revision == catalog.DefinitionRevision
            ? Task.FromResult(snapshot.Values) : CapabilitiesAsync(catalog, refresh: false);
    }

    internal Task<IReadOnlyDictionary<string, string>> RefreshCapabilitiesAsync(IWorkflowCatalog catalog) =>
        CapabilitiesAsync(catalog, refresh: true);

    internal async Task<IReadOnlyDictionary<string, string>?> RefreshDemandCapabilitiesAsync(IWorkflowCatalog catalog, TimeProvider time)
    {
        // A demand refresh never queues behind another refresh while holding an instance
        // lease. Failed attempts also consume the interval, so outages cannot cause a storm.
        if (!await _capabilityGate.WaitAsync(0)) return null;
        try
        {
            var now = time.GetTimestamp();
            if (_lastDemandRefresh is { } previous && time.GetElapsedTime(previous, now) < HeartbeatInterval) return null;
            _lastDemandRefresh = now;
            return await ReadCapabilitiesAsync(catalog);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { return null; }
        finally { _capabilityGate.Release(); }
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadCapabilitiesAsync(IWorkflowCatalog catalog)
    {
        // Capture the revision before enumeration so a concurrent registration invalidates it.
        var revision = catalog.DefinitionRevision;
        var snapshot = MemberRegistry.SnapshotCapabilities(await WorkflowHeartbeatService.CapabilitiesAsync(catalog, this));
        Volatile.Write(ref _capabilitySnapshot, new(catalog, revision, snapshot));
        return snapshot;
    }

    private async Task<IReadOnlyDictionary<string, string>> CapabilitiesAsync(IWorkflowCatalog catalog, bool refresh)
    {
        await _capabilityGate.WaitAsync();
        try
        {
            // Read the revision before enumerating. A registration racing the enumeration then
            // invalidates this snapshot on the next lookup instead of being cached as already seen.
            var revision = catalog.DefinitionRevision;
            if (refresh || _capabilitySnapshot == null || !ReferenceEquals(_capabilitySnapshot.Catalog, catalog) ||
                _capabilitySnapshot.Revision != revision)
            {
                await ReadCapabilitiesAsync(catalog);
            }
            return _capabilitySnapshot!.Values;
        }
        finally { _capabilityGate.Release(); }
    }

    public const int DefaultMaxCrashes = 3;
    public int MaxCrashes { get; init; } = DefaultMaxCrashes;
    internal bool BundleHostingEnabled { get; init; }

    public Guid Id { get; } = Guid.NewGuid();
    public TimeSpan UnschedulableScanInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan HeartbeatInterval { get; init; } = MemberRegistry.DefaultHeartbeatInterval;
    public IReadOnlyDictionary<string, string> Capabilities { get; init; } =
        MemberRegistry.SnapshotCapabilities(new Dictionary<string, string>());

    /// <summary>How a host names itself when the application does not: its process ID and machine name.</summary>
    public static string DefaultName { get; } = $"{Environment.ProcessId}@{Environment.MachineName}";

    public const int DefaultSlots = 64;

    public static TimeSpan DefaultDrainTimeout { get; } = TimeSpan.FromSeconds(20);

    public static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromSeconds(1);

    public static HostSettings Default => new(DefaultSlots, DefaultDrainTimeout, DefaultPollInterval, DefaultName);
}
