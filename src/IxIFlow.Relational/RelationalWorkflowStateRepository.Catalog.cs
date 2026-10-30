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
        public Guid Generation { get; set; }

        public object? GenerationValue
        {
            set => Generation = ReadGuid(value);
        }

        public int SourceLength { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }

        public WorkflowDraft Value
        {
            get
            {
                if (SourceLength != Source.Length)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                        "Draft source length differs from its projection");

                return new(Name, Version, Kind, Source, Revision)
                    { Generation = Generation, UpdatedAtUtc = Utc(UpdatedAtUtc), UpdatedBy = UpdatedBy };
            }
        }

        public WorkflowDraftSummary Summary => new(Kind, Name, Version, Generation, Revision, SourceLength, Utc(UpdatedAtUtc), UpdatedBy);
    }

    private sealed class VersionRow : WorkflowSummaryRow
    {
        public long CatalogRevision { get; set; }
        public string MetadataJson { get; set; } = "";
        public Guid? PublishedDraftGeneration { get; set; }

        public object? PublishedDraftGenerationValue
        {
            set => PublishedDraftGeneration = ReadOptionalGuid(value);
        }

        public long? PublishedDraftRevision { get; set; }
        public DateTime? PublishedAtUtc { get; set; }
        public string? PublishedBy { get; set; }
        public DateTime? StateChangedAtUtc { get; set; }
        public string? StateChangedBy { get; set; }

        public WorkflowVersion Value
        {
            get
            {
                var row = JsonSerializer.Deserialize<WorkflowVersion>(MetadataJson)!;

                if (row.Name != Name || row.Version != Version || row.StructureHash != StructureHash ||
                    row.ArtifactDigest?.Value != ArtifactDigest ||
                    row.Descriptor != Summary.Descriptor || row.ContractHash != ContractHash ||
                    row.State.ToString() != State || row.PublishedDraft?.Generation != PublishedDraftGeneration ||
                    row.PublishedDraft?.Revision != PublishedDraftRevision ||
                    row.PublishedAtUtc != PublishedAtUtc || row.PublishedBy != PublishedBy || row.StateChangedAtUtc != StateChangedAtUtc ||
                    row.StateChangedBy != StateChangedBy || row.LifecycleRevision != LifecycleRevision)
                {
                    // A forged document can replace both inventory and audit stamps. Preserve
                    // the inventory's specific refusal before reporting unrelated projections.
                    WorkflowCatalogRules.CheckManifestInvocations(row);

                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                        "Workflow catalog columns differ from its metadata");
                }

                if (row.PublishedDraft is { } draft &&
                    (draft.Kind != WorkflowDraftKinds.Workflow || draft.Name != row.Name || draft.Version != row.Version))
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                        "Workflow receipt identity differs from its catalog identity");

                return row;
            }
        }
    }

    private object VersionParameters(WorkflowVersion row, long? catalogRevision = null) => WithTimes(new
    {
        row.Name, row.Version, row.StructureHash, State = row.State.ToString(), ArtifactDigest = row.ArtifactDigest?.Value,
        MetadataJson = JsonSerializer.Serialize(row), CatalogRevision = catalogRevision ?? row.LifecycleRevision, row.LifecycleRevision,
        row.ContractHash, row.Descriptor.DisplayName, row.Descriptor.Category, Tags = WorkflowDescriptor.TagProjection(row.Descriptor),
        SearchText = CatalogActivityRules.Search(row.Name, row.Descriptor.DisplayName), DescriptorJson = row.Descriptor.ToJson(),
        PublishedDraftGeneration = row.PublishedDraft?.Generation, PublishedDraftRevision = row.PublishedDraft?.Revision,
        row.PublishedBy, row.StateChangedBy
    }, ("PublishedAtUtc", row.PublishedAtUtc), ("StateChangedAtUtc", row.StateChangedAtUtc));

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
        public string Origin { get; set; } = "";
        public string Extraction { get; set; } = "";
        public string? ExtractionDetail { get; set; }
        public string? ClosureJson { get; set; }
        public bool Withdrawn { get; set; }
        public DateTime? WithdrawnAtUtc { get; set; }
        public string? WithdrawnBy { get; set; }
        public DateTime RegisteredAtUtc { get; set; }
        public string? RegisteredBy { get; set; }
        public Guid? PublishedDraftGeneration { get; set; }

        public object? PublishedDraftGenerationValue
        {
            set => PublishedDraftGeneration = ReadOptionalGuid(value);
        }

        public long? PublishedDraftRevision { get; set; }
        public string? PublishedDraftName { get; set; }
        public int? PublishedDraftVersion { get; set; }
        public DateTime? PublishedAtUtc { get; set; }
        public string? PublishedBy { get; set; }
        public long CatalogRevision { get; set; }
        public long LifecycleRevision { get; set; }

        public WorkflowPackage Value
        {
            get
            {
                var package = JsonSerializer.Deserialize<WorkflowPackage>(MetadataJson)!;

                if (package.Id != Id || package.Version != Version || package.Digest.Value != Digest ||
                    JsonSerializer.Serialize(package.DependencyGroups) != DependencyGroupsJson ||
                    package.Origin.ToString() != Origin || !Enum.TryParse<PackageExtraction>(Extraction, out var extraction) ||
                    !Enum.IsDefined(extraction) ||
                    (package.Closure == null ? null : JsonSerializer.Serialize(package.Closure)) != ClosureJson ||
                    package.Withdrawn != Withdrawn || package.WithdrawnAtUtc != WithdrawnAtUtc || package.WithdrawnBy != WithdrawnBy ||
                    package.RegisteredAtUtc != RegisteredAtUtc || package.RegisteredBy != RegisteredBy ||
                    package.PublishedDraft?.Generation != PublishedDraftGeneration ||
                    package.PublishedDraft?.Revision != PublishedDraftRevision ||
                    package.PublishedDraft?.Name != PublishedDraftName || package.PublishedDraft?.Version != PublishedDraftVersion ||
                    package.PublishedAtUtc != PublishedAtUtc || package.PublishedBy != PublishedBy ||
                    package.LifecycleRevision != LifecycleRevision || CatalogRevision != LifecycleRevision)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                        "Package catalog columns differ from its metadata");

                WorkflowPackageRules.Validate(package);

                return package;
            }
        }
    }

    public async Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);

        return await CatalogConnectionAsync<IReadOnlyList<WorkflowPackage>>(async (connection, transaction) =>
            (await connection.QueryAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackages),
                new { Id = id?.ToLowerInvariant() }, transaction, cancellationToken: cancellationToken)))
            .Select(row => row.Value).ToArray(), cancellationToken: cancellationToken);
    }

    public async Task<WorkflowPackage?> GetPackageAsync(string id, string version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);
        WorkflowCatalogRules.ValidateIdentifier(version);

        return await CatalogConnectionAsync(async (connection, transaction) =>
                (await connection.QuerySingleOrDefaultAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackage),
                    new { Id = id.ToLowerInvariant(), Version = version }, transaction, cancellationToken: cancellationToken)))?.Value,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);
        WorkflowCatalogRules.ValidateIdentifier(afterVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return await CatalogConnectionAsync<IReadOnlyList<WorkflowPackage>>(async (connection, transaction) =>
            (await connection.QueryAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackagePage),
                new { Id = id.ToLowerInvariant(), AfterVersion = afterVersion, Limit = limit }, transaction,
                cancellationToken: cancellationToken))).Select(row => row.Value).ToArray(), cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<WorkflowVersion>> ReadVersionsAsync(DbConnection connection, DbTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        (await connection.QueryAsync<VersionRow>(new CommandDefinition(RequiredSql(Statement.CatalogVersions), transaction: transaction,
            cancellationToken: cancellationToken)))
        .Select(row => row.Value).ToArray();

    // A provider operation forwarded through decorators reuses the transaction holding the catalog lock.
    private readonly AsyncLocal<(DbConnection Connection, DbTransaction Transaction)?> _catalogTransaction = new();

    private async Task<T> CatalogConnectionAsync<T>(Func<DbConnection, DbTransaction?, Task<T>> action,
        Operation? operation = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        if (_catalogTransaction.Value is { } current)
            return await ExecuteStoreAsync(() => action(current.Connection, current.Transaction), cancellationToken);

        return operation is { } write
            ? await ExecuteOperationAsync(write, RunAsync, cancellationToken: cancellationToken)
            : await ExecuteStoreAsync(RunAsync, cancellationToken);

        async Task<T> RunAsync()
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            return await action(connection, null);
        }
    }

    private async Task<T> CatalogWriteAsync<T>(Func<DbConnection, DbTransaction, Task<T>> action, bool protectsStarts = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        if (_catalogTransaction.Value is { } current)
            return await ExecuteStoreAsync(() => action(current.Connection, current.Transaction), cancellationToken);

        return await ExecuteOperationAsync(Operation.Catalog, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            await using var transaction = await BeginTransactionAsync(connection, Operation.Catalog, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Catalog writes require a transaction");

            // Starts share the executable-catalog lock, but do not read drafts, attempts or
            // the revision counter. Serialize content separately, including consistent pages.
            // Wait for content before excluding starts: a queued mutation must not convoy
            // starts behind a content writer. Starts never request the content lock.
            await ExecuteOptionalAsync(connection, transaction, Statement.CatalogContentLock, null, cancellationToken: cancellationToken);

            if (protectsStarts)
                await ExecuteOptionalAsync(connection, transaction, Statement.CatalogLock, null, cancellationToken: cancellationToken);

            var previous = _catalogTransaction.Value;
            _catalogTransaction.Value = (connection, transaction);

            try
            {
                var result = await action(connection, transaction);
                await transaction.CommitAsync(cancellationToken: cancellationToken);

                return result;
            }
            finally
            {
                _catalogTransaction.Value = previous;
            }
        }, cancellationToken: cancellationToken);
    }

    public async Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Key(kind, name, version);

        return await CatalogConnectionAsync(async (connection, transaction) =>
                (await connection.QuerySingleOrDefaultAsync<DraftRow>(new CommandDefinition(RequiredSql(Statement.CatalogDefinition),
                    new { Kind = kind, Name = name, Version = version }, transaction, cancellationToken: cancellationToken)))?.Value,
            cancellationToken: cancellationToken);
    }

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Save(definition, expected, actor);

        return CatalogWriteAsync(async (connection, transaction) =>
        {
            if (definition.Kind == WorkflowDraftKinds.Template && await ReadTemplateAsync(connection, transaction, definition.Name,
                    definition.Version, cancellationToken: cancellationToken) != null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A published identity cannot be edited");

            if (definition.Kind == WorkflowDraftKinds.Workflow && await connection.QuerySingleOrDefaultAsync<VersionRow>(
                    new CommandDefinition(RequiredSql(Statement.CatalogVersion),
                        new { definition.Name, definition.Version }, transaction, cancellationToken: cancellationToken)) != null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A published identity cannot be edited");

            if (definition.Kind == WorkflowDraftKinds.Activity && await connection.QuerySingleOrDefaultAsync<PackageRow>(
                    new CommandDefinition(RequiredSql(Statement.CatalogPackage),
                        new
                        {
                            Id = definition.Name,
                            Version = definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0.0"
                        }, transaction, cancellationToken: cancellationToken)) != null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A published identity cannot be edited");

            var current = await connection.QuerySingleOrDefaultAsync<DraftRow>(
                new CommandDefinition(RequiredSql(Statement.CatalogDefinition), definition, transaction,
                    cancellationToken: cancellationToken));

            if (expected == null ? current != null : current?.Value.Id != expected)
                throw new CatalogPreconditionException("The definition identity changed");

            var generation = expected?.Generation ?? Guid.NewGuid();

            if (expected == null)
                while (await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.DraftGenerationInsert),
                           new { definition.Kind, definition.Name, definition.Version, Generation = generation }, transaction,
                           cancellationToken: cancellationToken)) == 0)
                    generation = Guid.NewGuid();

            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            var saved = definition with
            {
                Generation = generation, Revision = checked((expected?.Revision ?? 0) + 1), UpdatedAtUtc = now, UpdatedBy = actor
            };

            var changed = await connection.ExecuteAsync(new CommandDefinition(
                RequiredSql(expected == null ? Statement.CatalogDefinitionInsert : Statement.CatalogDefinitionUpdate),
                WithTimes(new
                {
                    saved.Kind, saved.Name, saved.Version, saved.Generation, saved.Source, SourceLength = saved.Source.Length,
                    saved.Revision, saved.UpdatedBy, ExpectedRevision = expected?.Revision
                }, ("UpdatedAtUtc", now)), transaction, cancellationToken: cancellationToken));

            if (changed != 1)
                throw new CatalogPreconditionException("The definition identity changed");

            await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);

            return saved;
        }, protectsStarts: false, cancellationToken: cancellationToken);
    }

    public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Id(expected);

        return CatalogWriteAsync(async (connection, transaction) =>
        {
            if (await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogDefinitionDelete), expected, transaction,
                    cancellationToken: cancellationToken)) != 1)
                throw new CatalogPreconditionException("The definition identity changed");

            await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);

            return true;
        }, protectsStarts: false, cancellationToken: cancellationToken);
    }

    // Conformance synchronization after each unprotected hint read, outside any catalog transaction.
    internal Func<string, Task>? PublicationPreparationReadAsync { get; set; }

    public async Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Actor(actor);
        ArgumentNullException.ThrowIfNull(artifacts);
        WorkflowCatalogRules.CheckPublicationArguments(draftId, version);
        var existing = await GetWorkflowVersionAsync(draftId.Name, draftId.Version, cancellationToken: cancellationToken);

        if (PublicationPreparationReadAsync is { } versionRead)
            await versionRead("Version");

        var preliminaryDraft = await GetDefinitionAsync(draftId.Kind, draftId.Name, draftId.Version, cancellationToken: cancellationToken);

        if (PublicationPreparationReadAsync is { } draftRead)
            await draftRead("Draft");

        // These independent reads are hints only. Even a refusal must first resolve a concurrent
        // same-ID winner under the final transaction. Missing bytes can also belong to a losing build.
        if (existing == null && preliminaryDraft?.Id == draftId)
        {
            try
            {
                await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts, cancellationToken);
            }
            catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.ArtifactUnavailable)
            {
            }
        }

        return await CatalogWriteAsync(async (connection, transaction) =>
        {
            var versions = await ReadVersionsAsync(connection, transaction, cancellationToken: cancellationToken);
            var winner = versions.SingleOrDefault(row => row.Name == draftId.Name && row.Version == draftId.Version);

            var draft = await connection.QuerySingleOrDefaultAsync<DraftRow>(new CommandDefinition(RequiredSql(Statement.CatalogDefinition),
                draftId, transaction, cancellationToken: cancellationToken));

            WorkflowCatalogRules.CheckPublication(draftId, version, draft?.Value, winner);

            if (winner != null)
                return winner;

            WorkflowCatalogRules.CheckInvocations(version, versions);
            await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts, cancellationToken);

            WorkflowCatalogRules.CheckAuthoringPackages(version,
                (await connection.QueryAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackages),
                    new { Id = (string?)null }, transaction, cancellationToken: cancellationToken))).Select(row => row.Value), versions);

            foreach (var dependency in version.Dependencies ?? [])
                WorkflowCatalogRules.CheckStartable(
                    versions.SingleOrDefault(row => row.Name == dependency.Name && row.Version == dependency.Version), dependency.Name,
                    dependency.Version);

            var revision = await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);
            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            var saved = version with
            {
                PublishedAtUtc = now, PublishedBy = actor, StateChangedAtUtc = now, StateChangedBy = actor, LifecycleRevision = revision
            };

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogVersionInsert), VersionParameters(saved),
                transaction, cancellationToken: cancellationToken));

            if (await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogDefinitionDelete), draftId, transaction,
                    cancellationToken: cancellationToken)) != 1)
                throw new CatalogPreconditionException("The definition identity changed");

            return saved;
        }, cancellationToken: cancellationToken);
    }

    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.Validate(version);

        return CatalogWriteAsync(async (connection, transaction) =>
        {
            await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts ?? this, cancellationToken);
            var versions = await ReadVersionsAsync(connection, transaction, cancellationToken: cancellationToken);
            var existing = versions.SingleOrDefault(row => row.Name == version.Name && row.Version == version.Version);

            if (existing != null)
                WorkflowCatalogRules.CheckRegistration(existing, version);

            if (existing != null && existing.ArtifactDigest == null && existing.Descriptor != version.Descriptor)
            {
                var revision = await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);
                var saved = existing with { Descriptor = version.Descriptor };

                var fields = new[]
                {
                    ("display_name", "DisplayName"), ("category", "Category"), ("tags", "Tags"), ("search_text", "SearchText"),
                    ("descriptor_json", "DescriptorJson"), ("metadata_json", "MetadataJson"), ("catalog_revision", "CatalogRevision")
                };

                await connection.ExecuteAsync(new CommandDefinition("UPDATE " + CatalogTable("workflow_versions") + " SET " +
                    string.Join(", ", fields.Select(field => CatalogColumn(field.Item1) + "=@" + field.Item2)) +
                    " WHERE " + CatalogColumn("name") + "=@Name AND " + CatalogColumn("version") + "=@Version",
                    VersionParameters(saved, revision), transaction, cancellationToken: cancellationToken));
            }

            WorkflowCatalogRules.CheckInvocations(version, versions);

            if (existing == null)
            {
                if (version.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated)
                    foreach (var dependency in version.Dependencies ?? [])
                        if (versions.SingleOrDefault(row => row.Name == dependency.Name && row.Version == dependency.Version) is { } child)
                            WorkflowCatalogRules.CheckStartable(child, child.Name, child.Version);

                var revision = await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);
                var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

                var saved = version with
                {
                    PublishedDraft = null, PublishedAtUtc = null, PublishedBy = null,
                    StateChangedAtUtc = now, StateChangedBy = null, LifecycleRevision = revision
                };

                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogVersionInsert), VersionParameters(saved),
                    transaction, cancellationToken: cancellationToken));
            }

            return true;
        }, cancellationToken: cancellationToken);
    }

    public async Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateName(name);

        return await CatalogConnectionAsync(async (connection, transaction) =>
                (await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(RequiredSql(Statement.CatalogVersion),
                    new { Name = name, Version = version }, transaction, cancellationToken: cancellationToken)))?.Value,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (name != null)
            WorkflowCatalogRules.ValidateName(name);

        return await CatalogConnectionAsync<IReadOnlyList<WorkflowVersion>>(async (connection, transaction) =>
                (await connection.QueryAsync<VersionRow>(new CommandDefinition(
                    RequiredSql(name == null ? Statement.CatalogVersions : Statement.CatalogNamedVersions),
                    new { Name = name }, transaction, cancellationToken: cancellationToken))).Select(row => row.Value).ToArray(),
            cancellationToken: cancellationToken);
    }

    public Task<long> GetCatalogRevisionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return CatalogConnectionAsync(
            (connection, transaction) => ReadCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);
    }

    private async Task<long> ReadCatalogRevisionAsync(DbConnection connection, DbTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(RequiredSql(Statement.CatalogRevision),
            transaction: transaction, cancellationToken: cancellationToken))
        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
            "The catalog revision row is missing; recreate the catalog schema");

    private async Task<long> AdvanceCatalogRevisionAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogRevisionAdvance), transaction: transaction,
                cancellationToken: cancellationToken)) != 1)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "The catalog revision row is missing; recreate the catalog schema");

        return await ReadCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);
    }

    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);

        // This read takes no catalog lock. Under SQL Server locking read committed, the
        // revision read can wait for a writer's final statements; the heartbeat bounds its
        // refresh wait to one interval and keeps advertising its last capability snapshot.
        // Writers advance the counter and stamp their row in one transaction and serialize on the
        // counter row, so revisions commit in order: once the watermark is read, every row stamped
        // at or below it is visible. Rows committed after the watermark may also be returned; they
        // are read again after the next revision, which is harmless.
        return CatalogConnectionAsync(async (connection, transaction) =>
        {
            var revision = await ReadCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);

            var rows = await connection.QueryAsync<VersionRow>(new CommandDefinition(RequiredSql(Statement.CatalogVersionChanges),
                new { AfterRevision = afterRevision }, transaction, cancellationToken: cancellationToken));

            return new WorkflowCatalogChanges(revision, rows.Select(row => row.Value).ToArray());
        }, cancellationToken: cancellationToken);
    }

    public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state,
        CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateName(name);
        WorkflowPublicationRules.Actor(actor);

        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var versions = await ReadVersionsAsync(connection, transaction, cancellationToken: cancellationToken);

            var row = versions.SingleOrDefault(row => row.Name == name && row.Version == version)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow version not found");

            WorkflowPublicationRules.Token(row, expected);
            WorkflowCatalogRules.CheckTransition(row.State, state);

            if (state == WorkflowVersionState.Published)
                await WorkflowCatalogRules.CheckArtifactsAsync(row, artifacts ?? this, cancellationToken);

            if (row.State == state)
                return row;

            if (state == WorkflowVersionState.Retired)
            {
                var callers = versions
                    .Where(caller => (caller.Dependencies ?? []).Any(edge => edge.Name == name && edge.Version == version)).ToArray();

                WorkflowCatalogRules.CheckRetirement(row, callers, null);

                var pairs = callers.Where(caller => WorkflowCatalogRules.HasChildDependency(caller, name, version))
                    .Select(caller => new WorkflowVersionReference(caller.Name, caller.Version)).ToArray();

                if (pairs.Length != 0)
                {
                    var parent = await connection.QuerySingleOrDefaultAsync<UnfinishedCallerRow>(new CommandDefinition(
                        RequiredSql(Statement.CatalogUnfinishedCallers), new { Callers = JsonSerializer.Serialize(pairs) }, transaction,
                        cancellationToken: cancellationToken));

                    WorkflowCatalogRules.CheckRetirement(row, callers,
                        parent == null ? null : new WorkflowVersionReference(parent.Name, parent.Version));
                }
            }

            if (state == WorkflowVersionState.Published)
                foreach (var dependency in row.Dependencies ?? [])
                    if ((dependency.Name, dependency.Version) != (name, version))
                        WorkflowCatalogRules.CheckStartable(
                            versions.SingleOrDefault(child => child.Name == dependency.Name && child.Version == dependency.Version),
                            dependency.Name, dependency.Version);

            var revision = await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);
            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);
            var saved = row with { State = state, LifecycleRevision = revision, StateChangedAtUtc = now, StateChangedBy = actor };
            var parameters = new DynamicParameters(VersionParameters(saved));
            parameters.Add("ExpectedRevision", expected.Revision);

            if (await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogVersionState), parameters, transaction,
                    cancellationToken: cancellationToken)) != 1)
                throw new CatalogPreconditionException("The workflow lifecycle revision changed");

            return saved;
        }, cancellationToken: cancellationToken);
    }

    private async Task CheckCatalogStartsAsync(DbConnection connection, DbTransaction transaction,
        CheckpointCommit commit, bool newInstance, CancellationToken cancellationToken = default)
    {
        if (!newInstance && commit.ChildStarts.Count == 0)
            return;

        var starts = commit.ChildStarts.Select(child => JsonSerializer.Deserialize<WorkflowInstance>(child.Payload)!).ToList();

        if (newInstance)
            starts.Add(commit.ReadTrunk());

        foreach (var start in starts)
        {
            WorkflowCatalogRules.ValidateNameWhitespace(start.WorkflowName);

            // Low-level commits may store instances without a catalog registration. Engine starts register first.
            var row = (await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(RequiredSql(Statement.CatalogVersion),
                    new { Name = start.WorkflowName, Version = start.WorkflowVersion }, transaction, cancellationToken: cancellationToken)))
                ?.Value;

            if (row != null)
                WorkflowCatalogRules.CheckStartable(row, start.WorkflowName, start.WorkflowVersion);

            WorkflowCatalogRules.CheckInstancePin(row, start);
        }
    }

    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var digest = ArtifactDigest.Of(bytes);

        await CatalogConnectionAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.ArtifactPut), WithTimes(new
                        { Digest = digest.Value, Kind = kind.ToString(), Content = bytes },
                    ("Now", await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken))),
                transaction, cancellationToken: cancellationToken));

            return true;
        }, Operation.Artifact, cancellationToken: cancellationToken);

        return digest;
    }

    public async ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);

        var content = await CatalogConnectionAsync((connection, transaction) =>
            connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(RequiredSql(Statement.ArtifactRead),
                new { Digest = digest.Value }, transaction, cancellationToken: cancellationToken)), cancellationToken: cancellationToken);

        return content == null ? null : new MemoryStream(content, writable: false);
    }

    public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);

        return await CatalogConnectionAsync((connection, transaction) =>
                    connection.ExecuteScalarAsync<int>(new CommandDefinition(RequiredSql(Statement.ArtifactExists),
                        new { Digest = digest.Value }, transaction, cancellationToken: cancellationToken)),
                cancellationToken: cancellationToken) !=
            0;
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
        }, Operation.Artifact, cancellationToken: cancellationToken);
    }

    public async Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versions = await GetWorkflowVersionsAsync(cancellationToken: cancellationToken);

            var pins = await CatalogConnectionAsync(async (connection, transaction) =>
                (await connection.QueryAsync<string>(new CommandDefinition(RequiredSql(Statement.CatalogInstanceDigests),
                    transaction: transaction, cancellationToken: cancellationToken)))
                .Select(value => new ArtifactDigest(value)).ToArray(), cancellationToken: cancellationToken);

            var prepared = await WorkflowCatalogRules.ReadOrphanPinsAsync(versions, pins, artifacts, cancellationToken);

            var result = await CatalogWriteAsync<int?>(async (connection, transaction) =>
            {
                var references = WorkflowCatalogRules.RetentionReferences(
                    await ReadVersionsAsync(connection, transaction, cancellationToken: cancellationToken),
                    (await connection.QueryAsync<string>(new CommandDefinition(RequiredSql(Statement.CatalogInstanceDigests),
                        transaction: transaction, cancellationToken: cancellationToken))).Select(value => new ArtifactDigest(value))
                    .ToArray(),
                    (await connection.QueryAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackages),
                        new { Id = (string?)null }, transaction, cancellationToken: cancellationToken))).Select(row => row.Value).ToArray(),
                    prepared,
                    await ReadTemplatesAsync(connection, transaction, cancellationToken: cancellationToken));

                if (references == null)
                    return null;

                var cutoff = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken) - retention;

                return await artifacts.PurgeUnreferencedAsync(references, cutoff, batchSize, cancellationToken);
            }, cancellationToken: cancellationToken);

            if (result.HasValue)
                return result.Value;
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

        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.Purge, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            // Purge is a multi-statement atomic write on every provider; Artifact may autocommit.
            await using var transaction = await BeginTransactionAsync(connection, Operation.Purge, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Artifact purge requires a transaction");

            var removed = await CollectAsync(connection, transaction);
            await transaction.CommitAsync(cancellationToken: cancellationToken);

            return removed;
        }, cancellationToken: cancellationToken);

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