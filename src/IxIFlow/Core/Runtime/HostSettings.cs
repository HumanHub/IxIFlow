namespace IxIFlow.Core.Runtime;

/// <summary>How many instances the host runs at once and how long it drains on stop.</summary>
internal sealed record HostSettings(int Slots, TimeSpan DrainTimeout, TimeSpan PollInterval, string Name)
{
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
