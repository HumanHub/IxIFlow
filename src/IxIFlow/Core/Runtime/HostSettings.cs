using Microsoft.Extensions.Logging;

namespace IxIFlow.Core.Runtime;

/// <summary>How many instances the host runs at once and how long it drains on stop.</summary>
internal sealed record HostSettings(int Slots, TimeSpan DrainTimeout, TimeSpan PollInterval, string Name)
{
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private sealed record VersionCapability(string StructureHash, bool Compatible, string? ManifestHash);
    private sealed record CapabilitySnapshot(IWorkflowCatalog Catalog, long Revision, long CatalogRevision,
        IReadOnlyDictionary<(string Name, int Version), VersionCapability> Versions,
        IReadOnlyDictionary<string, string> LocalDefinitions, IReadOnlyDictionary<string, string> Values);
    private CapabilitySnapshot? _capabilitySnapshot;
    private long? _lastDemandRefresh;
    internal WorkflowHeartbeatService.CompatibilityMemo Compatibility { get; } = new();
    internal Microsoft.Extensions.Logging.ILogger<WorkflowHeartbeatService>? CapabilityLogger { get; init; }
    internal IReadOnlyDictionary<string, string>? LastCapabilities => Volatile.Read(ref _capabilitySnapshot)?.Values;

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
        // Capture local registration revision before enumeration so a racing registration
        // invalidates this snapshot on the next placement lookup.
        var revision = catalog.DefinitionRevision;
        var previous = _capabilitySnapshot;
        if (previous != null && !ReferenceEquals(previous.Catalog, catalog)) previous = null;
        var bundleHosting = BundleHostingEnabled && WorkflowSharedAssemblies.IsUsable;
        if (BundleHostingEnabled && !bundleHosting) Compatibility.WarnUnusableProfile(CapabilityLogger);
        // Only bundle hosts advertise durable versions; other hosts never read version rows.
        var catalogRevision = bundleHosting ? await catalog.GetCatalogRevisionAsync() : 0;
        if (previous != null && previous.Revision == revision && previous.CatalogRevision == catalogRevision)
            return previous.Values;

        // A revision below the cursor means the store was restored or replaced: reread every version.
        var reread = previous != null && catalogRevision < previous.CatalogRevision;
        if (reread)
            CapabilityLogger?.LogWarning("The catalog revision moved back from {Previous} to {Current}; rereading every workflow version",
                previous!.CatalogRevision, catalogRevision);
        IReadOnlyDictionary<(string Name, int Version), VersionCapability> versions = reread ? new Dictionary<(string, int), VersionCapability>()
            : previous?.Versions ?? new Dictionary<(string, int), VersionCapability>();
        if (bundleHosting && (previous == null || previous.CatalogRevision != catalogRevision))
        {
            var afterRevision = reread ? 0 : previous?.CatalogRevision ?? 0;
            var changes = await catalog.GetWorkflowVersionChangesAsync(afterRevision);
            // The store may have been restored between the scalar read and the delta.
            if (changes.Revision < afterRevision)
            {
                CapabilityLogger?.LogWarning("The catalog revision moved back from {Previous} to {Current}; rereading every workflow version",
                    afterRevision, changes.Revision);
                versions = new Dictionary<(string, int), VersionCapability>();
                changes = await catalog.GetWorkflowVersionChangesAsync(0);
            }
            // Use the delta's watermark, never the earlier scalar read (a writer may race it).
            catalogRevision = changes.Revision;
            var updated = new Dictionary<(string, int), VersionCapability>(versions);
            foreach (var row in changes.Versions)
            {
                var hashes = new HashSet<string>(StringComparer.Ordinal);
                var compatible = bundleHosting && row.ArtifactDigest != null &&
                    row.State != WorkflowVersionState.Draft &&
                    Compatibility.Compatible(row.ManifestJson, hashes, CapabilityLogger);
                updated[(row.Name, row.Version)] = new(row.StructureHash, compatible, hashes.SingleOrDefault());
            }
            versions = updated;
        }
        IReadOnlyDictionary<string, string> local = previous?.LocalDefinitions ?? new Dictionary<string, string>();
        if (previous == null || previous.Revision != revision)
        {
            var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var definition in await catalog.GetWorkflowDefinitionsAsync())
                if (await catalog.GetWorkflowStructureHashAsync(definition.Name, definition.Version) is { } hash)
                    definitions.Add($"workflow:{definition.Name}@{definition.Version}", hash);
            local = definitions;
        }
        var values = WorkflowHeartbeatService.BaseCapabilities(this);
        foreach (var (key, hash) in local) values.Add(key, hash);
        if (bundleHosting) values.Add("bundle-host", "");
        foreach (var (key, result) in versions)
        {
            var capability = $"workflow:{key.Name}@{key.Version}";
            // Retirement prevents new starts, but existing instances still demand this
            // executable capability during recovery. Admission checks enforce retirement.
            if (result.Compatible) values[capability] = result.StructureHash;
        }
        var snapshot = MemberRegistry.SnapshotCapabilities(values);
        Compatibility.Retain(versions.Values.Where(result => result.ManifestHash != null)
            .Select(result => result.ManifestHash!).ToHashSet(StringComparer.Ordinal));
        Volatile.Write(ref _capabilitySnapshot, new(catalog, revision, catalogRevision, versions, local, snapshot));
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
    public bool Debugging { get; init; }
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
