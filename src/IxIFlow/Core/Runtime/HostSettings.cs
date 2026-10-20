namespace IxIFlow.Core.Runtime;

/// <summary>How many instances the host runs at once and how long it drains on stop.</summary>
internal sealed record HostSettings(int Slots, TimeSpan DrainTimeout, TimeSpan PollInterval, string Name)
{
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private IReadOnlyDictionary<string, string>? _capabilitySnapshot;

    internal Task<IReadOnlyDictionary<string, string>> GetCapabilitiesAsync(IWorkflowCatalog catalog) =>
        CapabilitiesAsync(catalog, refresh: false);

    internal Task<IReadOnlyDictionary<string, string>> RefreshCapabilitiesAsync(IWorkflowCatalog catalog) =>
        CapabilitiesAsync(catalog, refresh: true);

    internal async Task RegisteredWorkflowAsync(WorkflowDefinition definition, string structureHash)
    {
        await _capabilityGate.WaitAsync();
        try
        {
            if (_capabilitySnapshot == null) return;
            var key = $"workflow:{definition.Name}@{definition.Version}";
            if (_capabilitySnapshot.GetValueOrDefault(key) == structureHash) return;
            var updated = new Dictionary<string, string>(_capabilitySnapshot, StringComparer.Ordinal) { [key] = structureHash };
            _capabilitySnapshot = MemberRegistry.SnapshotCapabilities(updated);
        }
        finally { _capabilityGate.Release(); }
    }

    private async Task<IReadOnlyDictionary<string, string>> CapabilitiesAsync(IWorkflowCatalog catalog, bool refresh)
    {
        await _capabilityGate.WaitAsync();
        try
        {
            if (refresh || _capabilitySnapshot == null)
                _capabilitySnapshot = MemberRegistry.SnapshotCapabilities(
                    await WorkflowHeartbeatService.CapabilitiesAsync(catalog, this));
            return _capabilitySnapshot;
        }
        finally { _capabilityGate.Release(); }
    }

    public const int DefaultMaxCrashes = 3;
    public int MaxCrashes { get; init; } = DefaultMaxCrashes;

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
