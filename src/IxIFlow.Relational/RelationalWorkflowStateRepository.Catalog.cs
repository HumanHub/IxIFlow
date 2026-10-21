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

    private sealed class UnfinishedCallerRow
    {
        public string Name { get; set; } = "";
        public int Version { get; set; }
    }

    private sealed class PackageRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Digest { get; set; } = "";
        public string MetadataJson { get; set; } = "";
        public string DependencyGroupsJson { get; set; } = "";
        public WorkflowPackage Value
        {
            get
            {
                var package = JsonSerializer.Deserialize<WorkflowPackage>(MetadataJson)!;
                if (package.Id != Id || package.Version != Version || package.Digest.Value != Digest || JsonSerializer.Serialize(package.DependencyGroups) != DependencyGroupsJson)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Package catalog columns differ from its metadata");
                WorkflowPackageRules.Validate(package);
                return package;
            }
        }
    }

    public async Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null) =>
        await CatalogConnectionAsync<IReadOnlyList<WorkflowPackage>>(async (connection, transaction) =>
            (await connection.QueryAsync<PackageRow>(RequiredSql(Statement.CatalogPackages), new { Id = id?.ToLowerInvariant() }, transaction))
                .Select(row => row.Value).ToArray());

    public async Task<WorkflowPackage?> GetPackageAsync(string id, string version) =>
        await CatalogConnectionAsync(async (connection, transaction) =>
            (await connection.QuerySingleOrDefaultAsync<PackageRow>(RequiredSql(Statement.CatalogPackage),
                new { Id = id.ToLowerInvariant(), Version = version }, transaction))?.Value);

    public async Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return await CatalogConnectionAsync<IReadOnlyList<WorkflowPackage>>(async (connection, transaction) =>
            (await connection.QueryAsync<PackageRow>(RequiredSql(Statement.CatalogPackagePage),
                new { Id = id.ToLowerInvariant(), AfterVersion = afterVersion, Limit = limit }, transaction)).Select(row => row.Value).ToArray());
    }

    public Task RegisterPackageAsync(WorkflowPackage package, IArtifactStore artifacts)
    {
        WorkflowPackageRules.Validate(package);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            if (!await artifacts.ExistsAsync(package.Digest))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Package artifact {package.Digest} is unavailable");
            var existing = (await connection.QuerySingleOrDefaultAsync<PackageRow>(RequiredSql(Statement.CatalogPackage), new { package.Id, package.Version }, transaction))?.Value;
            WorkflowPackageRules.CheckRegistration(existing, package);
            if (existing == null)
                await connection.ExecuteAsync(RequiredSql(Statement.CatalogPackageInsert), new
                { package.Id, package.Version, Digest = package.Digest.Value, MetadataJson = JsonSerializer.Serialize(package), DependencyGroupsJson = JsonSerializer.Serialize(package.DependencyGroups) }, transaction);
            return true;
        });
    }

    private async Task<IReadOnlyList<WorkflowVersion>> ReadVersionsAsync(DbConnection connection, DbTransaction? transaction) =>
        (await connection.QueryAsync<VersionRow>(RequiredSql(Statement.CatalogVersions), transaction: transaction))
        .Select(row => row.Value).ToArray();

    // A provider operation forwarded through decorators reuses the transaction holding the catalog lock.
    private readonly AsyncLocal<(DbConnection Connection, DbTransaction Transaction)?> _catalogTransaction = new();

    private async Task<T> CatalogConnectionAsync<T>(Func<DbConnection, DbTransaction?, Task<T>> action,
        Operation? operation = null)
    {
        await EnsureSchemaAsync();
        if (_catalogTransaction.Value is { } current)
            return await action(current.Connection, current.Transaction);
        return operation is { } write ? await ExecuteOperationAsync(write, RunAsync) : await RunAsync();

        async Task<T> RunAsync()
        {
            await using var connection = await OpenConnectionAsync();
            return await action(connection, null);
        }
    }

    private async Task<T> CatalogWriteAsync<T>(Func<DbConnection, DbTransaction, Task<T>> action)
    {
        await EnsureSchemaAsync();
        if (_catalogTransaction.Value is { } current)
            return await action(current.Connection, current.Transaction);
        return await ExecuteOperationAsync(Operation.Catalog, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await BeginTransactionAsync(connection, Operation.Catalog)
                ?? throw new InvalidOperationException("Catalog writes require a transaction");
            await ExecuteOptionalAsync(connection, transaction, Statement.CatalogLock, null);
            var previous = _catalogTransaction.Value;
            _catalogTransaction.Value = (connection, transaction);
            try
            {
                var result = await action(connection, transaction);
                await transaction.CommitAsync();
                return result;
            }
            finally { _catalogTransaction.Value = previous; }
        });
    }

    public async Task<WorkflowDraft?> GetDefinitionAsync(string name, int version)
    {
        WorkflowCatalogRules.ValidateName(name);
        return await CatalogConnectionAsync(async (connection, transaction) =>
            (await connection.QuerySingleOrDefaultAsync<DraftRow>(RequiredSql(Statement.CatalogDefinition),
                new { Name = name, Version = version }, transaction))?.Value);
    }

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision)
    {
        WorkflowCatalogRules.ValidateName(definition.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.Version, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        return SaveDraftAsync();

        Task<WorkflowDraft> SaveDraftAsync() => CatalogConnectionAsync(async (connection, current) =>
        {
            if (current != null) return await WriteAsync(current);
            await using var transaction = await BeginTransactionAsync(connection, Operation.Catalog)
                ?? throw new InvalidOperationException("Saving a definition requires a transaction");
            var result = await WriteAsync(transaction);
            await transaction.CommitAsync();
            return result;

            async Task<WorkflowDraft> WriteAsync(DbTransaction transaction)
            {
                var saved = definition with { Revision = checked(expectedRevision + 1) };
                // Insert-if-absent and compare-and-swap updates need no catalog lock.
                var changed = await connection.ExecuteAsync(RequiredSql(expectedRevision == 0
                    ? Statement.CatalogDefinitionInsert : Statement.CatalogDefinitionUpdate),
                    new { saved.Name, saved.Version, saved.Kind, saved.Source, saved.Revision, ExpectedRevision = expectedRevision }, transaction);
                // Check after the write so a concurrent publication that deleted the old draft
                // cannot let an insert recreate it. A refusal rolls back this transaction.
                if (await connection.QuerySingleOrDefaultAsync<VersionRow>(RequiredSql(Statement.CatalogVersion),
                    new { saved.Name, saved.Version }, transaction) != null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A published version cannot be edited");
                if (changed == 0)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The definition revision changed");
                return saved;
            }
        }, Operation.Catalog);
    }

    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore? artifacts = null) =>
        CatalogWriteAsync(async (connection, transaction) =>
        {
            var versions = await ReadVersionsAsync(connection, transaction);
            var existing = versions.SingleOrDefault(row => row.Name == draftId.Name && row.Version == draftId.Version);
            var draft = await connection.QuerySingleOrDefaultAsync<DraftRow>(RequiredSql(Statement.CatalogDefinition), draftId, transaction);
            WorkflowCatalogRules.CheckPublication(draftId, version, draft?.Value, existing);
            if (existing != null) return existing;
            await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts ?? this);
            foreach (var dependency in version.Dependencies ?? [])
                WorkflowCatalogRules.CheckStartable(versions.SingleOrDefault(row => row.Name == dependency.Name && row.Version == dependency.Version), dependency.Name, dependency.Version);
            var revision = await AdvanceCatalogRevisionAsync(connection, transaction);
            await connection.ExecuteAsync(RequiredSql(Statement.CatalogVersionInsert), new
            {
                version.Name, version.Version, version.StructureHash, State = version.State.ToString(),
                ArtifactDigest = version.ArtifactDigest!.Value.Value, MetadataJson = JsonSerializer.Serialize(version), CatalogRevision = revision
            }, transaction);
            if (await connection.ExecuteAsync(RequiredSql(Statement.CatalogDefinitionDelete), draftId, transaction) != 1)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The definition revision changed");
            return version;
        });

    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null)
    {
        WorkflowCatalogRules.Validate(version);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts ?? this);
            var versions = await ReadVersionsAsync(connection, transaction);
            var existing = versions.SingleOrDefault(row => row.Name == version.Name && row.Version == version.Version);
            if (existing != null) WorkflowCatalogRules.CheckRegistration(existing, version);
            else
            {
                if (version.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated)
                    foreach (var dependency in version.Dependencies ?? [])
                        if (versions.SingleOrDefault(row => row.Name == dependency.Name && row.Version == dependency.Version) is { } child)
                            WorkflowCatalogRules.CheckStartable(child, child.Name, child.Version);
                var revision = await AdvanceCatalogRevisionAsync(connection, transaction);
                await connection.ExecuteAsync(RequiredSql(Statement.CatalogVersionInsert), new
                {
                    version.Name, version.Version, version.StructureHash, State = version.State.ToString(),
                    ArtifactDigest = version.ArtifactDigest?.Value, MetadataJson = JsonSerializer.Serialize(version), CatalogRevision = revision
                }, transaction);
            }
            return true;
        });
    }

    public async Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version)
    {
        WorkflowCatalogRules.ValidateName(name);
        return await CatalogConnectionAsync(async (connection, transaction) =>
            (await connection.QuerySingleOrDefaultAsync<VersionRow>(RequiredSql(Statement.CatalogVersion),
                new { Name = name, Version = version }, transaction))?.Value);
    }

    public async Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null)
    {
        if (name != null) WorkflowCatalogRules.ValidateName(name);
        return await CatalogConnectionAsync<IReadOnlyList<WorkflowVersion>>(async (connection, transaction) =>
            (await connection.QueryAsync<VersionRow>(RequiredSql(name == null ? Statement.CatalogVersions : Statement.CatalogNamedVersions),
                new { Name = name }, transaction)).Select(row => row.Value).ToArray());
    }

    public Task<long> GetCatalogRevisionAsync() => CatalogConnectionAsync((connection, transaction) =>
        connection.QuerySingleAsync<long>(RequiredSql(Statement.CatalogRevision), transaction: transaction));

    private async Task<long> AdvanceCatalogRevisionAsync(DbConnection connection, DbTransaction transaction)
    {
        await connection.ExecuteAsync(RequiredSql(Statement.CatalogRevisionAdvance), transaction: transaction);
        return await connection.QuerySingleAsync<long>(RequiredSql(Statement.CatalogRevision), transaction: transaction);
    }

    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        // The catalog lock excludes writers until both the watermark and rows have been read.
        // SQLite's immediate transaction supplies that lock; server providers use transaction locks.
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var revision = await connection.QuerySingleAsync<long>(RequiredSql(Statement.CatalogRevision), transaction: transaction);
            var rows = await connection.QueryAsync<VersionRow>(RequiredSql(Statement.CatalogVersionChanges),
                new { AfterRevision = afterRevision }, transaction);
            return new WorkflowCatalogChanges(revision, rows.Select(row => row.Value).ToArray());
        });
    }

    public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null)
    {
        WorkflowCatalogRules.ValidateName(name);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var versions = await ReadVersionsAsync(connection, transaction);
            var row = versions.SingleOrDefault(row => row.Name == name && row.Version == version)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow version not found");
            WorkflowCatalogRules.CheckTransition(row.State, state);
            if (state == WorkflowVersionState.Published)
                await WorkflowCatalogRules.CheckArtifactsAsync(row, artifacts ?? this);
            if (row.State == state) return true;
            if (state == WorkflowVersionState.Retired)
            {
                var reference = new WorkflowVersionReference(name, version);
                var callers = versions.Where(caller => (caller.Dependencies ?? []).Contains(reference)).ToArray();
                WorkflowCatalogRules.CheckRetirement(row, callers, null);
                if (callers.Length != 0)
                {
                    var pairs = callers.Select(caller => new WorkflowVersionReference(caller.Name, caller.Version));
                    var parent = await connection.QuerySingleOrDefaultAsync<UnfinishedCallerRow>(
                        RequiredSql(Statement.CatalogUnfinishedCallers), new { Callers = JsonSerializer.Serialize(pairs) }, transaction);
                    WorkflowCatalogRules.CheckRetirement(row, callers,
                        parent == null ? null : new WorkflowVersionReference(parent.Name, parent.Version));
                }
            }
            if (state == WorkflowVersionState.Published)
                foreach (var dependency in row.Dependencies ?? [])
                    if ((dependency.Name, dependency.Version) != (name, version))
                        WorkflowCatalogRules.CheckStartable(versions.SingleOrDefault(child => child.Name == dependency.Name && child.Version == dependency.Version),
                            dependency.Name, dependency.Version);
            var revision = await AdvanceCatalogRevisionAsync(connection, transaction);
            await connection.ExecuteAsync(RequiredSql(Statement.CatalogVersionState),
                new { Name = name, Version = version, State = state.ToString(), CatalogRevision = revision }, transaction);
            return true;
        });
    }

    private async Task CheckCatalogStartsAsync(DbConnection connection, DbTransaction transaction,
        CheckpointCommit commit, bool newInstance)
    {
        if (!newInstance && commit.ChildStarts.Count == 0) return;
        var starts = commit.ChildStarts.Select(child => JsonSerializer.Deserialize<WorkflowInstance>(child.Payload)!).ToList();
        if (newInstance) starts.Add(commit.ReadTrunk());
        foreach (var start in starts)
        {
            WorkflowCatalogRules.ValidateNameWhitespace(start.WorkflowName);
            // Low-level commits may store instances without a catalog registration. Engine starts register first.
            var row = (await connection.QuerySingleOrDefaultAsync<VersionRow>(RequiredSql(Statement.CatalogVersion),
                new { Name = start.WorkflowName, Version = start.WorkflowVersion }, transaction))?.Value;
            if (row != null)
                WorkflowCatalogRules.CheckStartable(row, start.WorkflowName, start.WorkflowVersion);
            WorkflowCatalogRules.CheckInstancePin(row, start);
        }
    }

    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var digest = ArtifactDigest.Of(bytes);
        await CatalogConnectionAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.ArtifactPut), WithTimes(new
            { Digest = digest.Value, Kind = kind.ToString(), Content = bytes }, ("Now", await OutboxNowAsync(connection, transaction))),
                transaction, cancellationToken: cancellationToken));
            return true;
        }, Operation.Artifact);
        return digest;
    }

    public async ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        var content = await CatalogConnectionAsync((connection, transaction) =>
            connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(RequiredSql(Statement.ArtifactRead),
                new { Digest = digest.Value }, transaction, cancellationToken: cancellationToken)));
        return content == null ? null : new MemoryStream(content, writable: false);
    }

    public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        return await CatalogConnectionAsync((connection, transaction) =>
            connection.ExecuteScalarAsync<int>(new CommandDefinition(RequiredSql(Statement.ArtifactExists),
                new { Digest = digest.Value }, transaction, cancellationToken: cancellationToken))) != 0;
    }

    public async ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        await CatalogConnectionAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.ArtifactDelete),
                new { Digest = digest.Value }, transaction, cancellationToken: cancellationToken));
            return true;
        }, Operation.Artifact);
    }

    public async Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versions = await GetWorkflowVersionsAsync();
            var pins = await CatalogConnectionAsync(async (connection, transaction) =>
                (await connection.QueryAsync<string>(RequiredSql(Statement.CatalogInstanceDigests), transaction: transaction))
                    .Select(value => new ArtifactDigest(value)).ToArray());
            var prepared = await WorkflowCatalogRules.ReadOrphanPinsAsync(versions, pins, artifacts, cancellationToken);
            var result = await CatalogWriteAsync<int?>(async (connection, transaction) =>
            {
                var references = WorkflowCatalogRules.RetentionReferences(await ReadVersionsAsync(connection, transaction),
                    (await connection.QueryAsync<string>(RequiredSql(Statement.CatalogInstanceDigests), transaction: transaction)).Select(value => new ArtifactDigest(value)).ToArray(),
                    (await connection.QueryAsync<PackageRow>(RequiredSql(Statement.CatalogPackages), new { Id = (string?)null }, transaction)).Select(row => row.Value).ToArray(), prepared);
                if (references == null) return null;
                var cutoff = await OutboxNowAsync(connection, transaction) - retention;
                return await artifacts.PurgeUnreferencedAsync(references, cutoff, batchSize, cancellationToken);
            });
            if (result.HasValue) return result.Value;
        }
    }

    internal virtual object ArtifactReferencesParameter(IReadOnlySet<ArtifactDigest> references) =>
        JsonSerializer.Serialize(references.Select(digest => digest.Value));

    public async ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
        int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (_catalogTransaction.Value is { } current)
            return await CollectAsync(current.Connection, current.Transaction);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.Purge, async () =>
        {
            await using var connection = await OpenConnectionAsync();
            // Purge is a multi-statement atomic write on every provider; Artifact may autocommit.
            await using var transaction = await BeginTransactionAsync(connection, Operation.Purge)
                ?? throw new InvalidOperationException("Artifact purge requires a transaction");
            var removed = await CollectAsync(connection, transaction);
            await transaction.CommitAsync();
            return removed;
        });

        async Task<int> CollectAsync(DbConnection connection, DbTransaction transaction)
        {
            var candidates = await connection.QueryAsync<string>(new CommandDefinition(RequiredSql(Statement.ArtifactCandidates),
                WithTimes(new { References = ArtifactReferencesParameter(references), Limit = batchSize }, ("Cutoff", olderThanUtc)),
                transaction, cancellationToken: cancellationToken));
            var removed = 0;
            foreach (var digest in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                removed += await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.ArtifactDeleteOld),
                    WithTimes(new { Digest = digest }, ("Cutoff", olderThanUtc)), transaction, cancellationToken: cancellationToken));
            }
            return removed;
        }
    }
}
