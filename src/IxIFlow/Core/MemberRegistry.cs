using System.Collections.ObjectModel;

namespace IxIFlow.Core;

/// <summary>The role advertised by a registry member.</summary>
public enum MemberKind { Host, Coordinator }

/// <summary>A process advertisement. Its identity is fresh for each start; its name is for display.</summary>
public sealed record MemberRegistration(Guid Id, MemberKind Kind, string Name, string? Endpoint, int Slots,
    string EngineVersion, TimeSpan HeartbeatInterval, IReadOnlyDictionary<string, string> Capabilities)
{
    /// <summary>The installed activity declarations selected by this host.</summary>
    public IReadOnlyList<InstalledActivityPackage> InstalledPackages { get; init; } = [];
}

/// <summary>A member and its capability snapshot, stamped by the store's clock.</summary>
public sealed record StoreMember(MemberRegistration Registration, DateTime StartedAtUtc, DateTime HeartbeatAtUtc)
{
    public Guid Id => Registration.Id;
    public MemberKind Kind => Registration.Kind;
    public string Name => Registration.Name;
    public string? Endpoint => Registration.Endpoint;
    public int Slots => Registration.Slots;
    public string EngineVersion => Registration.EngineVersion;
    public TimeSpan HeartbeatInterval => Registration.HeartbeatInterval;
    public IReadOnlyDictionary<string, string> Capabilities => Registration.Capabilities;
}

internal static class MemberRegistry
{
    internal static readonly TimeSpan PruneAfter = TimeSpan.FromHours(1);
    internal static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(5);

    internal static void ValidateInterval(TimeSpan interval)
    {
        // A member must reach three missed beats before the fixed one-hour prune horizon.
        if (interval < TimeSpan.FromMilliseconds(1) || interval > PruneAfter / 3)
            throw new ArgumentOutOfRangeException(nameof(interval), "The heartbeat interval must be between one millisecond and twenty minutes");
    }

    internal static MemberRegistration Snapshot(MemberRegistration member)
    {
        ArgumentNullException.ThrowIfNull(member);
        ValidateId(member.Id);
        if (!Enum.IsDefined(member.Kind)) throw new ArgumentOutOfRangeException(nameof(member.Kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(member.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(member.EngineVersion);
        if (member.Name.Length > 200 || member.EngineVersion.Length > 100 || member.Endpoint?.Length > 2048)
            throw new ArgumentException("Member names, engine versions and endpoints are limited to 200, 100 and 2048 characters");
        if (member.Slots < 0 || member.Kind == MemberKind.Host && member.Slots == 0)
            throw new ArgumentOutOfRangeException(nameof(member.Slots));
        ValidateInterval(member.HeartbeatInterval);
        var packages = member.InstalledPackages.Select(package => InstalledPackageRules.Snapshot(package)).ToArray();
        var identities = new HashSet<(string, string, string)>();
        foreach (var package in packages)
            if (!identities.Add((package.Id, package.Version, package.Fingerprint)))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Installed activity package '{package.Id}@{package.Version}' repeats fingerprint '{package.Fingerprint}'");
        if (member.Kind == MemberKind.Coordinator && packages.Length != 0)
            throw new ArgumentException("Coordinators cannot register installed activity packages");
        return member with { Capabilities = SnapshotCapabilities(member.Capabilities),
            InstalledPackages = Array.AsReadOnly(packages) };
    }

    internal static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A member ID cannot be empty", nameof(id));
    }

    internal static IReadOnlyDictionary<string, string> SnapshotCapabilities(IReadOnlyDictionary<string, string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in capabilities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            if (key.Length > 400) throw new ArgumentException("Capability keys are limited to 400 characters");
            snapshot.Add(key, value);
        }
        return new ReadOnlyDictionary<string, string>(snapshot);
    }

    internal static bool IsLive(StoreMember member, DateTime now) =>
        IsLive(member.HeartbeatAtUtc, member.HeartbeatInterval, now);

    internal static bool IsLive(DateTime heartbeatAtUtc, TimeSpan heartbeatInterval, DateTime now) =>
        now - heartbeatAtUtc < heartbeatInterval * 3;
}
