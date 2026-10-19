using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private sealed class DraftRow
    {
        public string Name { get; set; } = "";
        public int Version { get; set; }
        public string Kind { get; set; } = "";
        public string Source { get; set; } = "";
        public long Revision { get; set; }
        public WorkflowDraft Value => new(Name, Version, Kind, Source, Revision);
    }

    private sealed class VersionRow
    {
        public string MetadataJson { get; set; } = "";
        public string State { get; set; } = "";
        public WorkflowVersion Value => JsonSerializer.Deserialize<WorkflowVersion>(MetadataJson)! with
        { State = Enum.Parse<WorkflowVersionState>(State) };
    }

    private async Task<IReadOnlyList<WorkflowVersion>> ReadVersionsAsync(DbConnection connection, DbTransaction? transaction) =>
        (await connection.QueryAsync<VersionRow>(RequiredSql(Statement.CatalogVersions), transaction: transaction))
        .Select(row => row.Value).ToArray();

    private async Task<T> CatalogWriteAsync<T>(Func<DbConnection, DbTransaction, Task<T>> action)
    {
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.Catalog, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = (await BeginTransactionAsync(connection, Operation.Catalog))!;
            await ExecuteOptionalAsync(connection, transaction, Statement.CatalogLock, null);
            var result = await action(connection, transaction);
            await transaction.CommitAsync();
            return result;
        });
    }

    public async Task<WorkflowDraft?> GetDefinitionAsync(string name, int version)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        return (await connection.QuerySingleOrDefaultAsync<DraftRow>(RequiredSql(Statement.CatalogDefinition), new { Name = name, Version = version }))?.Value;
    }

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.Version, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var existing = await connection.QuerySingleOrDefaultAsync<DraftRow>(RequiredSql(Statement.CatalogDefinition),
                new { definition.Name, definition.Version }, transaction);
            if ((existing?.Revision ?? 0) != expectedRevision)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The definition revision changed");
            var saved = definition with { Revision = checked(expectedRevision + 1) };
            await connection.ExecuteAsync(RequiredSql(existing == null ? Statement.CatalogDefinitionInsert : Statement.CatalogDefinitionUpdate), saved, transaction);
            return saved;
        });
    }

    public Task RegisterWorkflowVersionAsync(WorkflowVersion version)
    {
        WorkflowCatalogRules.Validate(version);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var versions = await ReadVersionsAsync(connection, transaction);
            var existing = versions.SingleOrDefault(row => row.Name == version.Name && row.Version == version.Version);
            if (existing != null) WorkflowCatalogRules.CheckRegistration(existing, version);
            else
            {
                if (version.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated)
                    foreach (var dependency in version.Dependencies ?? [])
                        if (versions.SingleOrDefault(row => row.Name == dependency.Name && row.Version == dependency.Version) is { } child)
                            WorkflowCatalogRules.CheckStartable(child, child.Name, child.Version);
                await connection.ExecuteAsync(RequiredSql(Statement.CatalogVersionInsert), new
                {
                    version.Name, version.Version, version.StructureHash, State = version.State.ToString(),
                    ArtifactDigest = version.ArtifactDigest?.Value, MetadataJson = JsonSerializer.Serialize(version)
                }, transaction);
            }
            return true;
        });
    }

    public async Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) =>
        (await GetWorkflowVersionsAsync(name)).SingleOrDefault(row => row.Version == version);

    public async Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null)
    {
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        return (await ReadVersionsAsync(connection, null)).Where(row => name == null || row.Name == name).ToArray();
    }

    public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state) =>
        CatalogWriteAsync(async (connection, transaction) =>
        {
            var versions = await ReadVersionsAsync(connection, transaction);
            var row = versions.SingleOrDefault(row => row.Name == name && row.Version == version)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow version not found");
            WorkflowCatalogRules.CheckTransition(row.State, state);
            if (row.State == state) return true;
            if (state == WorkflowVersionState.Retired)
            {
                var trunks = await connection.QueryAsync<StoredRow>(QuerySql("1=1"), transaction: transaction);
                WorkflowCatalogRules.CheckRetirement(row, versions,
                    trunks.Select(trunk => JsonSerializer.Deserialize<WorkflowInstance>(trunk.StateJson)!));
            }
            if (state == WorkflowVersionState.Published)
                foreach (var dependency in row.Dependencies ?? [])
                    if ((dependency.Name, dependency.Version) != (name, version))
                        WorkflowCatalogRules.CheckStartable(versions.SingleOrDefault(child => child.Name == dependency.Name && child.Version == dependency.Version),
                            dependency.Name, dependency.Version);
            await connection.ExecuteAsync(RequiredSql(Statement.CatalogVersionState), new { Name = name, Version = version, State = state.ToString() }, transaction);
            return true;
        });

    private async Task CheckCatalogStartsAsync(DbConnection connection, DbTransaction transaction,
        CheckpointCommit commit, bool newInstance)
    {
        if (!newInstance && commit.ChildStarts.Count == 0) return;
        var versions = await ReadVersionsAsync(connection, transaction);
        var starts = commit.ChildStarts.Select(child => JsonSerializer.Deserialize<WorkflowInstance>(child.Payload)!).ToList();
        if (newInstance) starts.Add(commit.ReadTrunk());
        foreach (var start in starts)
            // Low-level commits may store instances without a catalog registration. Engine starts register first.
            if (versions.SingleOrDefault(row => row.Name == start.WorkflowName && row.Version == start.WorkflowVersion) is { } row)
                WorkflowCatalogRules.CheckStartable(row, start.WorkflowName, start.WorkflowVersion);
    }

    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var digest = ArtifactDigest.Of(bytes);
        await EnsureSchemaAsync();
        await ExecuteOperationAsync(Operation.Artifact, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await connection.ExecuteAsync(RequiredSql(Statement.ArtifactPut), WithTimes(new
            { Digest = digest.Value, Kind = kind.ToString(), Content = bytes }, ("Now", await OutboxNowAsync(connection, null))));
            return true;
        });
        return digest;
    }

    public async ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var content = await connection.QuerySingleOrDefaultAsync<byte[]>(RequiredSql(Statement.ArtifactRead), new { Digest = digest.Value });
        return content == null ? null : new MemoryStream(content, writable: false);
    }

    public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(RequiredSql(Statement.ArtifactExists), new { Digest = digest.Value }) != 0;
    }

    public async ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        await EnsureSchemaAsync();
        await ExecuteOperationAsync(Operation.Artifact, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await connection.ExecuteAsync(RequiredSql(Statement.ArtifactDelete), new { Digest = digest.Value });
            return true;
        });
    }

    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();
        artifacts = RepositoryArtifactStore.Unwrap(artifacts);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var references = WorkflowCatalogRules.ArtifactReferences(await ReadVersionsAsync(connection, transaction));
            var cutoff = await OutboxNowAsync(connection, transaction) - retention;
            if (!ReferenceEquals(artifacts, this))
                return await ((IArtifactRetentionStore)artifacts).PurgeUnreferencedAsync(references, cutoff, batchSize, cancellationToken);
            var candidates = await connection.QueryAsync<string>(RequiredSql(Statement.ArtifactCandidates),
                WithTimes(new { }, ("Cutoff", cutoff)), transaction);
            var removed = 0;
            foreach (var digest in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (references.Contains(new ArtifactDigest(digest))) continue;
                await connection.ExecuteAsync(RequiredSql(Statement.ArtifactDelete), new { Digest = digest }, transaction);
                if (++removed == batchSize) break;
            }
            return removed;
        });
    }
}

