namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<Guid, StoreMember> _members = [];
    private readonly Dictionary<(string, string, string), InstalledActivityPackage> _installedPackages = [];
    private readonly Dictionary<Guid, IReadOnlyList<InstalledActivityPackage>> _hostPackages = [];

    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default)
    {
        member = MemberRegistry.Snapshot(member);
        lock (_claimLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now;
            PruneMembers(now);
            foreach (var package in member.InstalledPackages)
                _installedPackages.TryAdd((package.Id, package.Version, package.Fingerprint), package);
            _hostPackages[member.Id] = member.InstalledPackages;
            _members[member.Id] = new StoreMember(member,
                _members.TryGetValue(member.Id, out var prior) ? prior.StartedAtUtc : now, now);
        }
        return Task.CompletedTask;
    }

    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default)
    {
        MemberRegistry.ValidateId(memberId);
        var snapshot = MemberRegistry.SnapshotCapabilities(capabilities);
        lock (_claimLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now;
            PruneMembers(now);
            if (!_members.TryGetValue(memberId, out var member)) return Task.FromResult(false);
            _members[memberId] = member with
            {
                Registration = member.Registration with { Capabilities = snapshot }, HeartbeatAtUtc = now
            };
            return Task.FromResult(true);
        }
    }

    public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default)
    {
        MemberRegistry.ValidateId(memberId);
        lock (_claimLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PruneMembers(Now);
            _hostPackages.Remove(memberId);
            _members.Remove(memberId);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default)
    {
        lock (_claimLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now;
            PruneMembers(now);
            return Task.FromResult<IReadOnlyList<StoreMember>>(_members.Values
                .Where(member => MemberRegistry.IsLive(member, now)).OrderBy(member => member.Id).ToArray());
        }
    }

    public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null)
    {
        if (id != null) id = WorkflowPackageRules.NormalizeId(id);
        lock (_claimLock)
        {
            var now = Now;
            PruneMembers(now);
            var live = _members.Values.Where(member => MemberRegistry.IsLive(member, now))
                .Select(member => member.Id).ToHashSet();
            var hosts = _hostPackages.Where(pair => live.Contains(pair.Key))
                .SelectMany(pair => pair.Value.Select(mark => (Mark: mark, MemberId: pair.Key)))
                .ToLookup(pair => (pair.Mark.Id, pair.Mark.Version, pair.Mark.Fingerprint), pair => pair.MemberId);
            return Task.FromResult<IReadOnlyList<InstalledActivityPackage>>(_installedPackages.Values
                .Where(package => id == null || package.Id == id)
                .OrderBy(package => package.Id, StringComparer.Ordinal).ThenBy(package => package.Version, StringComparer.Ordinal)
                .ThenBy(package => package.Fingerprint, StringComparer.Ordinal)
                .Select(package => package with { Hosts = Array.AsReadOnly(
                    hosts[(package.Id, package.Version, package.Fingerprint)].Order().ToArray()) }).ToArray());
        }
    }

    // Removing a member also removes its capabilities and package marks; package content stays.
    private void PruneMembers(DateTime now)
    {
        foreach (var id in _members.Where(pair => now - pair.Value.HeartbeatAtUtc > MemberRegistry.PruneAfter)
                     .Select(pair => pair.Key).ToArray())
        {
            _hostPackages.Remove(id);
            _members.Remove(id);
        }
    }
}
