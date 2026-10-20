namespace IxIFlow.Core.Runtime;

/// <summary>How many instances the host runs at once and how long it drains on stop.</summary>
internal sealed record HostSettings(int Slots, TimeSpan DrainTimeout, TimeSpan PollInterval, string Name)
{
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private IReadOnlyDictionary<string, string>? _capabilitySnapshot;
    private IWorkflowCatalog? _capabilityCatalog;
    private long _definitionRevision;

    internal Task<IReadOnlyDictionary<string, string>> GetCapabilitiesAsync(IWorkflowCatalog catalog) =>
        CapabilitiesAsync(catalog, refresh: false);

    internal Task<IReadOnlyDictionary<string, string>> RefreshCapabilitiesAsync(IWorkflowCatalog catalog) =>
        CapabilitiesAsync(catalog, refresh: true);

    private async Task<IReadOnlyDictionary<string, string>> CapabilitiesAsync(IWorkflowCatalog catalog, bool refresh)
    {
        await _capabilityGate.WaitAsync();
        try
        {
            // Read the revision before enumerating. A registration racing the enumeration then
            // invalidates this snapshot on the next lookup instead of being cached as already seen.
            var revision = catalog.DefinitionRevision;
            if (refresh || _capabilitySnapshot == null || !ReferenceEquals(_capabilityCatalog, catalog) ||
                _definitionRevision != revision)
            {
                _capabilitySnapshot = MemberRegistry.SnapshotCapabilities(
                    await WorkflowHeartbeatService.CapabilitiesAsync(catalog, this));
                _capabilityCatalog = catalog;
                _definitionRevision = revision;
            }
            return _capabilitySnapshot;
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
