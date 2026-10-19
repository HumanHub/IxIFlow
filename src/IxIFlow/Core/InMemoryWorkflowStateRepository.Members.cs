namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<Guid, StoreMember> _members = [];

    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default)
    {
        member = MemberRegistry.Snapshot(member);
        lock (_claimLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now;
            PruneMembers(now);
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

    // The member owns its immutable capability snapshot, so removing it removes both memory tables.
    private void PruneMembers(DateTime now)
    {
        foreach (var id in _members.Where(pair => now - pair.Value.HeartbeatAtUtc > MemberRegistry.PruneAfter)
                     .Select(pair => pair.Key).ToArray())
            _members.Remove(id);
    }
}
