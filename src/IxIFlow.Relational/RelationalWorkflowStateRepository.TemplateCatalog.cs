using System.Data.Common;
using System.Text;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Templates;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    public async Task<PackageHolders> GetPackageHoldersAsync(string id, string version, CatalogScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        id = WorkflowPackageRules.NormalizeId(id);
        version = WorkflowPackageRules.NormalizeVersion(version);
        ArgumentNullException.ThrowIfNull(scope);

        while (true)
        {
            var versions = await GetWorkflowVersionsAsync(cancellationToken: cancellationToken);

            var pins = await CatalogConnectionAsync(async (connection, transaction) =>
                    (await connection.QueryAsync<string>(new CommandDefinition(RequiredSql(Statement.CatalogInstanceDigests),
                        transaction: transaction, cancellationToken: cancellationToken))).Select(item => new ArtifactDigest(item))
                    .ToArray(),
                cancellationToken: cancellationToken);

            var orphans = await WorkflowCatalogRules.ReadOrphanPinsAsync(versions, pins, CatalogArtifacts ?? this, cancellationToken);

            var result = await CatalogWriteAsync<PackageHolders?>(async (connection, transaction) =>
            {
                var current = await ReadVersionsAsync(connection, transaction, cancellationToken: cancellationToken);

                var currentPins = (await connection.QueryAsync<string>(new CommandDefinition(RequiredSql(Statement.CatalogInstanceDigests),
                    transaction: transaction, cancellationToken: cancellationToken))).Select(item => new ArtifactDigest(item)).ToArray();

                if (currentPins.Any(pin => !current.Any(item => item.ArtifactDigest == pin) && !orphans.ContainsKey(pin)))
                    return null;

                return PackageHolderRules.Read(id, version, scope, current,
                    await ReadTemplatesAsync(connection, transaction, cancellationToken: cancellationToken),
                    (await connection.QueryAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackages),
                        new { Id = (string?)null }, transaction, cancellationToken: cancellationToken))).Select(item => item.Value)
                    .ToArray(), currentPins, orphans);
            }, protectsStarts: false, cancellationToken: cancellationToken);

            if (result != null)
                return result;
        }
    }

    private sealed class TemplateRow
    {
        public string Key { get; set; } = "";
        public int Version { get; set; }
        public string Engine { get; set; } = "";
        public string Format { get; set; } = "";
        public string ModelType { get; set; } = "";
        public string ModelTypeId { get; set; } = "";
        public string ModelPackagesJson { get; set; } = "";
        public string CulturesJson { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string SearchText { get; set; } = "";
        public Guid? PublishedDraftGeneration { get; set; }

        public object? PublishedDraftGenerationValue
        {
            set => PublishedDraftGeneration = ReadOptionalGuid(value);
        }

        public long? PublishedDraftRevision { get; set; }
        public DateTime PublishedAtUtc { get; set; }
        public string? PublishedBy { get; set; }
        public string MetadataJson { get; set; } = "";
        public long CatalogRevision { get; set; }

        public TemplateVersionEntry Value
        {
            get
            {
                TemplateVersion content;

                try
                {
                    content = TemplateJson.Deserialize(Encoding.UTF8.GetBytes(MetadataJson));
                }
                catch (Exception error) when (error is JsonException or ArgumentException or FormatException)
                {
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                        "Template metadata is not valid immutable content", error);
                }

                if (content.Key != Key || content.Version != Version || content.Engine != Engine || content.Format.ToString() != Format ||
                    content.ModelType != ModelType || content.ModelTypeId != ModelTypeId || content.DisplayName != DisplayName ||
                    content.Description != Description ||
                    JsonSerializer.Serialize(content.ModelPackages) != ModelPackagesJson ||
                    JsonSerializer.Serialize(content.Variants.Select(item => item.Culture)) != CulturesJson ||
                    CatalogActivityRules.Search(Key, DisplayName ?? "") != SearchText ||
                    Encoding.UTF8.GetString(TemplateJson.Serialize(content)) != MetadataJson ||
                    PublishedDraftGeneration.HasValue != PublishedDraftRevision.HasValue || PublishedDraftGeneration == Guid.Empty ||
                    PublishedDraftRevision is < 1 || CatalogRevision < 1)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                        "Template columns differ from immutable content or receipt");

                return new(content,
                    PublishedDraftGeneration is { } generation
                        ? new(WorkflowDraftKinds.Template, Key, Version, generation, PublishedDraftRevision!.Value)
                        : null,
                    Utc(PublishedAtUtc), PublishedBy);
            }
        }
    }

    private static readonly (string Column, string Property)[] TemplateColumns =
    [
        ("key", "Key"), ("version", "Version"), ("engine", "Engine"), ("format", "Format"), ("model_type", "ModelType"),
        ("model_type_id", "ModelTypeId"),
        ("model_packages_json", "ModelPackagesJson"), ("cultures_json", "CulturesJson"), ("display_name", "DisplayName"),
        ("description", "Description"),
        ("search_text", "SearchText"), ("published_draft_generation", "PublishedDraftGeneration"),
        ("published_draft_revision", "PublishedDraftRevision"),
        ("published_at_utc", "PublishedAtUtc"), ("published_by", "PublishedBy"), ("metadata_json", "MetadataJson"),
        ("catalog_revision", "CatalogRevision")
    ];

    private string TemplateSelect => "SELECT " + string.Join(", ",
            TemplateColumns.Select(item => CatalogColumn(item.Column) + " AS " + CatalogColumn(ReadAlias(item.Property)))) +
        " FROM " + CatalogTable("template_versions");

    private async Task<TemplateVersionEntry?> ReadTemplateAsync(DbConnection connection, DbTransaction? transaction, string key,
        int version, CancellationToken cancellationToken = default) =>
        (await connection.QuerySingleOrDefaultAsync<TemplateRow>(new CommandDefinition(
            TemplateSelect + " WHERE " + CatalogColumn("key") + "=@Key AND " + CatalogColumn("version") + "=@Version",
            new { Key = key, Version = version }, transaction, cancellationToken: cancellationToken)))?.Value;

    private async Task<IReadOnlyList<TemplateVersionEntry>> ReadTemplatesAsync(DbConnection connection, DbTransaction? transaction,
        string? key = null, CancellationToken cancellationToken = default) =>
        (await connection.QueryAsync<TemplateRow>(new CommandDefinition(TemplateSelect +
            (key == null ? "" : " WHERE " + CatalogColumn("key") + "=@Key") +
            " ORDER BY " + CatalogColumn("key") + ", " + CatalogColumn("version"), new { Key = key }, transaction,
            cancellationToken: cancellationToken))).Select(row => row.Value).ToArray();

    public Task<TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TemplateCatalogRules.Key(key, version);

        return CatalogConnectionAsync(
            (connection, transaction) => ReadTemplateAsync(connection, transaction, key, version, cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<TemplateVersionEntry>> GetTemplateVersionsAsync(string? key = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (key != null)
            TemplateCatalogRules.Key(key, 1);

        return CatalogConnectionAsync(
            (connection, transaction) => ReadTemplatesAsync(connection, transaction, key, cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);
    }

    public Task<TemplateVersionEntry> RegisterTemplateVersionAsync(TemplateVersion version, IArtifactStore artifacts, string? actor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return SaveTemplateAsync(version, artifacts, actor, null, cancellationToken: cancellationToken);
    }

    public Task<TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId, TemplateVersion version,
        IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(version);
        TemplateCatalogRules.Publication(draftId, version);

        return SaveTemplateAsync(version, artifacts, actor, draftId, cancellationToken: cancellationToken);
    }

    private Task<TemplateVersionEntry> SaveTemplateAsync(TemplateVersion version, IArtifactStore artifacts, string? actor,
        WorkflowDraftId? draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(artifacts);
        WorkflowPublicationRules.Actor(actor);

        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var existing = await ReadTemplateAsync(connection, transaction, version.Key, version.Version,
                cancellationToken: cancellationToken);

            if (existing != null)
            {
                if (draft == null ? existing.Version.IsSameAs(version) : existing.PublishedDraft == draft)
                    return existing;

                throw new TemplateVersionConflictException(version.Reference);
            }

            if (draft != null &&
                (await connection.QuerySingleOrDefaultAsync<DraftRow>(new CommandDefinition(RequiredSql(Statement.CatalogDefinition), draft,
                    transaction, cancellationToken: cancellationToken)))?.Value.Id != draft)
                throw new CatalogPreconditionException("The definition identity changed");

            await TemplateCatalogRules.CheckPackagesAsync(version, async (id, value) =>
                    (await connection.QuerySingleOrDefaultAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackage),
                        new { Id = id, Version = value }, transaction, cancellationToken: cancellationToken)))?.Value,
                artifacts, cancellationToken);

            var revision = await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);
            var now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

            var parameters = WithTimes(new
            {
                version.Key, version.Version, version.Engine, Format = version.Format.ToString(), version.ModelType, version.ModelTypeId,
                ModelPackagesJson = JsonSerializer.Serialize(version.ModelPackages),
                CulturesJson = JsonSerializer.Serialize(version.Variants.Select(item => item.Culture)),
                version.DisplayName, version.Description, SearchText = CatalogActivityRules.Search(version.Key, version.DisplayName ?? ""),
                PublishedDraftGeneration = draft?.Generation, PublishedDraftRevision = draft?.Revision, PublishedBy = actor,
                MetadataJson = Encoding.UTF8.GetString(TemplateJson.Serialize(version)), CatalogRevision = revision
            }, ("PublishedAtUtc", now));

            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO " + CatalogTable("template_versions") + " (" +
                string.Join(", ", TemplateColumns.Select(item => CatalogColumn(item.Column))) +
                ") VALUES (" + string.Join(", ", TemplateColumns.Select(item => "@" + item.Property)) + ")", parameters, transaction,
                cancellationToken: cancellationToken));

            if (draft != null && await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogDefinitionDelete), draft,
                    transaction, cancellationToken: cancellationToken)) != 1)
                throw new CatalogPreconditionException("The definition identity changed");

            return new TemplateVersionEntry(version, draft, now, actor);
        }, protectsStarts: false, cancellationToken: cancellationToken);
    }
}