using System.Data.Common;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    // Used only by the single-host provider, under its writer gate. A new owned repository can
    // replace the advertisement left by its previous process, even before that row becomes stale.
    private Guid? _singleHostMember;

    private sealed class MemberRow
    {
        public Guid MemberId { get; set; }
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Endpoint { get; set; }
        public int Slots { get; set; }
        public string EngineVersion { get; set; } = "";
        public long HeartbeatIntervalTicks { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime HeartbeatAtUtc { get; set; }
    }

    private sealed class CapabilityRow
    {
        public Guid MemberId { get; set; }
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    // All registry operations use the same database lock, so a read cannot combine a heartbeat
    // with the previous capability set, nor can pruning race a renewal. Sample time after locking.
    private async Task<T> RegistryAsync<T>(Func<DbConnection, DbTransaction?, DateTime, Task<T>> action,
        CancellationToken cancellationToken, Action? committed = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.Registry, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await BeginTransactionAsync(connection, Operation.Registry);
            if (Sql(Statement.RegistryLock) is { } sql)
                await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction,
                    cancellationToken: cancellationToken));
            var now = await OutboxNowAsync(connection, transaction);
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.PruneMembers),
                WithTimes(new { }, ("PruneBefore", now - MemberRegistry.PruneAfter)), transaction,
                cancellationToken: cancellationToken));
            var result = await action(connection, transaction, now);
            cancellationToken.ThrowIfCancellationRequested();
            await CompleteAsync(transaction);
            committed?.Invoke();
            return result;
        });
    }

    public async Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default)
    {
        member = MemberRegistry.Snapshot(member);
        if (SingleHostRegistry && member.Kind == MemberKind.Coordinator)
            throw new InvalidOperationException("SQLite accepts one host and no coordinator.");
        await RegistryAsync(async (connection, transaction, now) =>
        {
            if (SingleHostRegistry && _singleHostMember is { } owner && owner != member.Id)
                throw new InvalidOperationException("SQLite accepts one host and no coordinator.");
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.RegisterMember),
                WithTimes(new { MemberId = member.Id, Kind = member.Kind == MemberKind.Host ? "host" : "coordinator",
                    member.Name, member.Endpoint, member.Slots, member.EngineVersion,
                    HeartbeatIntervalTicks = member.HeartbeatInterval.Ticks }, ("Now", now)), transaction,
                cancellationToken: cancellationToken));
            await ReplaceCapabilitiesAsync(connection, transaction, member.Id, member.Capabilities, cancellationToken);
            return true;
        }, cancellationToken, () => { if (SingleHostRegistry) _singleHostMember = member.Id; });
    }

    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default)
    {
        MemberRegistry.ValidateId(memberId);
        var snapshot = MemberRegistry.SnapshotCapabilities(capabilities);
        return RegistryAsync(async (connection, transaction, now) =>
        {
            var changed = await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.MemberHeartbeat),
                WithTimes(new { MemberId = memberId }, ("Now", now)), transaction, cancellationToken: cancellationToken));
            if (changed != 1) return false;
            await ReplaceCapabilitiesAsync(connection, transaction, memberId, snapshot, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default)
    {
        MemberRegistry.ValidateId(memberId);
        await RegistryAsync(async (connection, transaction, _) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.UnregisterMember),
                new { MemberId = memberId }, transaction, cancellationToken: cancellationToken));
            return true;
        }, cancellationToken, () => { if (SingleHostRegistry && _singleHostMember == memberId) _singleHostMember = null; });
    }

    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
        RegistryAsync<IReadOnlyList<StoreMember>>(async (connection, transaction, now) =>
            await ReadLiveMembersAsync(connection, transaction, now, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<StoreMember>> ReadLiveMembersAsync(DbConnection connection,
        DbTransaction? transaction, DateTime now, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<MemberRow>(new CommandDefinition(RequiredSql(Statement.Members),
            transaction: transaction, cancellationToken: cancellationToken));
        var capabilities = (await connection.QueryAsync<CapabilityRow>(new CommandDefinition(
            RequiredSql(Statement.MemberCapabilities), transaction: transaction, cancellationToken: cancellationToken)))
            .ToLookup(row => row.MemberId);
        return rows.Select(row => new StoreMember(new MemberRegistration(row.MemberId,
                row.Kind == "host" ? MemberKind.Host : MemberKind.Coordinator, row.Name, row.Endpoint, row.Slots,
                row.EngineVersion, TimeSpan.FromTicks(row.HeartbeatIntervalTicks),
                MemberRegistry.SnapshotCapabilities(capabilities[row.MemberId].ToDictionary(c => c.Key, c => c.Value,
                    StringComparer.Ordinal))), ReadTime(row.StartedAtUtc), ReadTime(row.HeartbeatAtUtc)))
            .Where(member => MemberRegistry.IsLive(member, now)).OrderBy(member => member.Id).ToArray();
    }

    private async Task ReplaceCapabilitiesAsync(DbConnection connection, DbTransaction? transaction, Guid memberId,
        IReadOnlyDictionary<string, string> capabilities, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DeleteCapabilities),
            new { MemberId = memberId }, transaction, cancellationToken: cancellationToken));
        if (capabilities.Count != 0)
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.InsertCapability),
                capabilities.Select(pair => new { MemberId = memberId, pair.Key, pair.Value }), transaction,
                cancellationToken: cancellationToken));
    }
}
