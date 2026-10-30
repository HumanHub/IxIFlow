using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    internal virtual bool CatalogSqlServer => false;

    private string CatalogTable(string name) => CatalogSqlServer
        ? "dbo.IxIFlow" + string.Concat(name.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]))
        : "ixiflow_" + name;

    private string CatalogColumn(string name) => CatalogSqlServer
        ? "[" + (name == "csharp_type_name"
            ? "CSharpTypeName"
            : string.Concat(name.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]))) + "]"
        : "\"" + name + "\"";

    private string CatalogTake => CatalogSqlServer ? " OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY" : " LIMIT @Limit";

    private sealed class InstalledPackageRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public long CatalogRevision { get; set; }
    }

    private class ActivityRow
    {
        public string PackageId { get; set; } = "";
        public string PackageVersion { get; set; } = "";
        public string Key { get; set; } = "";
        public string Version { get; set; } = "";
        public string MetadataTypeName { get; set; } = "";
        public string CSharpTypeName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Category { get; set; } = "";
        public string? Icon { get; set; }
        public string SearchText { get; set; } = "";
        public string ContractHash { get; set; } = "";
        public string DescriptorJson { get; set; } = "";
        public string? Fingerprint { get; set; }
        public long CatalogRevision { get; set; }

        public ActivityDescriptor Value
        {
            get
            {
                var item = ActivityDescriptor.Parse(DescriptorJson);

                if (item.PackageId != PackageId || item.PackageVersion != PackageVersion || item.Key != Key || item.Version != Version ||
                    item.MetadataTypeName != MetadataTypeName || item.CSharpTypeName != CSharpTypeName || item.Name != DisplayName ||
                    item.Category != Category || item.Icon != Icon || item.ContractHash != ContractHash ||
                    CatalogActivityRules.Search(item.Key, item.Name) != SearchText || item.ToJson() != DescriptorJson)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Activity columns differ from their descriptor");

                return item;
            }
        }
    }

    private static readonly (string Column, string Property)[] ActivityColumns =
    [
        ("package_id", "PackageId"), ("package_version", "PackageVersion"), ("key", "Key"), ("version", "Version"),
        ("metadata_type_name", "MetadataTypeName"), ("csharp_type_name", "CSharpTypeName"), ("display_name", "DisplayName"),
        ("category", "Category"), ("icon", "Icon"), ("search_text", "SearchText"), ("contract_hash", "ContractHash"),
        ("descriptor_json", "DescriptorJson"), ("catalog_revision", "CatalogRevision")
    ];

    private string ActivitySelect(bool installed) => "SELECT " + string.Join(", ", ActivityColumns
            .Select(item =>
                CatalogColumn(item.Column) + " AS " +
                (CatalogSqlServer ? "[" + ReadAlias(item.Property) + "]" : ReadAlias(item.Property)))) +
        (installed ? ", " + CatalogColumn("fingerprint") + " AS Fingerprint" : "") + " FROM " +
        CatalogTable(installed ? "installed_activities" : "package_activities");

    private Task<int> InsertActivityAsync(DbConnection connection, DbTransaction transaction, ActivityDescriptor item, long revision,
        string? fingerprint = null, CancellationToken cancellationToken = default)
    {
        var columns = ActivityColumns.Concat(fingerprint == null ? [] : new[] { ("fingerprint", "Fingerprint") }).ToArray();

        return connection.ExecuteAsync(new CommandDefinition("INSERT INTO " +
            CatalogTable(fingerprint == null ? "package_activities" : "installed_activities") +
            " (" + string.Join(", ", columns.Select(column => CatalogColumn(column.Item1))) + ") VALUES (" +
            string.Join(", ", columns.Select(column => "@" + column.Item2)) + ")", new
            {
                item.PackageId, item.PackageVersion, item.Key, item.Version, item.MetadataTypeName, item.CSharpTypeName,
                DisplayName = item.Name, item.Category, item.Icon, SearchText = CatalogActivityRules.Search(item.Key, item.Name),
                item.ContractHash, DescriptorJson = item.ToJson(), CatalogRevision = revision, Fingerprint = fingerprint
            }, transaction, cancellationToken: cancellationToken));
    }

    private async Task<InstalledActivityPackage> ReadInstalledPackageAsync(DbConnection connection, DbTransaction? transaction,
        InstalledPackageRow row, CancellationToken cancellationToken = default)
    {
        var activities = (await connection.QueryAsync<ActivityRow>(new CommandDefinition(ActivitySelect(true) + " WHERE " +
            CatalogColumn("package_id") + "=@Id AND " +
            CatalogColumn("package_version") + "=@Version AND " + CatalogColumn("fingerprint") + "=@Fingerprint", row, transaction,
            cancellationToken: cancellationToken))).ToArray();

        if (activities.Any(item => item.CatalogRevision != row.CatalogRevision))
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Installed activity revision differs from its package");

        return InstalledPackageRules.Snapshot(new(row.Id, row.Version, row.Fingerprint, activities.Select(item => item.Value).ToArray()));
    }

    private async Task<PackageActivities> ReadPackageActivitiesAsync(DbConnection connection, DbTransaction? transaction, PackageRow row,
        CancellationToken cancellationToken = default)
    {
        var activities = (await connection.QueryAsync<ActivityRow>(new CommandDefinition(ActivitySelect(false) + " WHERE " +
            CatalogColumn("package_id") + "=@Id AND " +
            CatalogColumn("package_version") + "=@Version", row, transaction, cancellationToken: cancellationToken))).ToArray();

        var missing = (await connection.QueryAsync<string>(new CommandDefinition("SELECT " + CatalogColumn("missing_package_id") +
            " FROM " +
            CatalogTable("package_pending_dependencies") + " WHERE " + CatalogColumn("package_id") + "=@Id AND " +
            CatalogColumn("package_version") + "=@Version", row, transaction, cancellationToken: cancellationToken))).ToArray();

        if (activities.Any(item => item.CatalogRevision != row.CatalogRevision))
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Activity revision differs from its package");

        return PackageIndexRules.Snapshot(row.Value, new(activities.Select(item => item.Value).ToArray(),
            Enum.Parse<PackageExtraction>(row.Extraction),
            row.ExtractionDetail, row.ClosureJson == null ? null : JsonSerializer.Deserialize<WorkflowPackageReference[]>(row.ClosureJson),
            missing));
    }

    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackageIndexRules.Registration(package, actor);

        return AcceptPackageAsync(package, activities, artifacts, actor, false, null, cancellationToken: cancellationToken);
    }

    public async Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Actor(actor);
        var package = await GetPackageAsync(id, version, cancellationToken: cancellationToken);

        if (package == null || package.Digest != expectedDigest)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package extraction identity changed");

        return await AcceptPackageAsync(package, activities, artifacts, actor, true, null, cancellationToken: cancellationToken);
    }

    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackageIndexRules.Publication(draftId, package, actor);

        return AcceptPackageAsync(package, activities, artifacts, actor, false, draftId, cancellationToken: cancellationToken);
    }

    private Task<PackageRow?> ReadPackageRowAsync(DbConnection connection, DbTransaction? transaction, string id, string version,
        CancellationToken cancellationToken = default) =>
        connection.QuerySingleOrDefaultAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackage),
            new { Id = id, Version = version }, transaction, cancellationToken: cancellationToken));

    public Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn,
        CatalogMutationToken expected, string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);
        WorkflowCatalogRules.ValidateIdentifier(version);
        id = WorkflowPackageRules.NormalizeId(id);
        version = WorkflowPackageRules.NormalizeVersion(version);
        WorkflowPublicationRules.Actor(actor);
        ArgumentNullException.ThrowIfNull(expected);

        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var prior = await ReadPackageRowAsync(connection, transaction, id, version, cancellationToken: cancellationToken)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Package version not found");

            var row = prior.Value;
            PackageIndexRules.Token(row, expected);
            PackageIndexRules.Withdrawal(withdrawn);

            if (row.Withdrawn)
                return row;

            var activities = await ReadPackageActivitiesAsync(connection, transaction, prior, cancellationToken: cancellationToken);
            var revision = await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);

            var saved = row with
            {
                Withdrawn = true, WithdrawnAtUtc = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken),
                WithdrawnBy = actor, LifecycleRevision = revision
            };

            var parameters = new DynamicParameters(PackageParameters(saved, activities, revision));
            parameters.Add("ExpectedRevision", expected.Revision);
            var columns = PackageColumns.Where(item => item.Property is not ("Id" or "Version" or "Digest"));

            var affected = await connection.ExecuteAsync(new CommandDefinition("UPDATE " + CatalogTable("packages") + " SET " +
                string.Join(", ", columns.Select(item => CatalogColumn(item.Column) + "=@" + item.Property)) +
                " WHERE " + CatalogColumn("id") + "=@Id AND " + CatalogColumn("version") + "=@Version AND " +
                CatalogColumn("lifecycle_revision") + "=@ExpectedRevision", parameters, transaction, cancellationToken: cancellationToken));

            if (affected != 1)
                throw new CatalogPreconditionException("The package lifecycle revision changed");

            await connection.ExecuteAsync(new CommandDefinition("UPDATE " + CatalogTable("package_activities") + " SET " +
                CatalogColumn("catalog_revision") + "=@Revision WHERE " +
                CatalogColumn("package_id") + "=@Id AND " + CatalogColumn("package_version") + "=@Version",
                new { Id = id, Version = version, Revision = revision }, transaction, cancellationToken: cancellationToken));

            return saved;
        }, protectsStarts: false, cancellationToken: cancellationToken);
    }

    private async Task<PackageRegistration> AcceptPackageAsync(WorkflowPackage package, PackageActivities activities,
        IArtifactStore artifacts, string? actor, bool completion, WorkflowDraftId? draft, CancellationToken cancellationToken = default)
    {
        WorkflowPackageRules.Validate(package);
        package = WorkflowPackageRules.Copy(package);
        activities = PackageIndexRules.Snapshot(package, activities);

        // Preparation is outside the write transaction so draft edits and a competing
        // publisher can win while artifact I/O is awaited. Only the final snapshot decides.
        if (draft != null && (await GetPackageAsync(package.Id, package.Version, cancellationToken: cancellationToken)) == null &&
            (await GetDefinitionAsync(draft.Kind, draft.Name, draft.Version, cancellationToken: cancellationToken))?.Id == draft)
        {
            try
            {
                await PackageIndexRules.CheckArtifactsAsync(package, activities, artifacts, cancellationToken);
            }
            catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.ArtifactUnavailable)
            {
            }
        }

        return await CatalogWriteAsync(async (connection, transaction) =>
        {
            var prior = await connection.QuerySingleOrDefaultAsync<PackageRow>(new CommandDefinition(RequiredSql(Statement.CatalogPackage),
                new { package.Id, package.Version }, transaction, cancellationToken: cancellationToken));

            var existing = prior?.Value;

            if (draft != null)
            {
                var current = (await connection.QuerySingleOrDefaultAsync<DraftRow>(
                    new CommandDefinition(RequiredSql(Statement.CatalogDefinition), draft, transaction,
                        cancellationToken: cancellationToken)))?.Value;

                if (PackageIndexRules.PublicationWinner(draft, existing, current))
                    return new PackageRegistration(existing!,
                        await ReadPackageActivitiesAsync(connection, transaction, prior!, cancellationToken: cancellationToken), false);

                PackageIndexRules.PublicationActivities(draft, activities);
            }

            if (completion && existing == null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package disappeared");

            WorkflowPackageRules.CheckRegistration(existing, package);
            await PackageIndexRules.CheckArtifactsAsync(package, activities, artifacts, cancellationToken);
            var restoring = PackageIndexRules.Restoring(existing, package, completion);

            if (existing?.Withdrawn == true && !restoring)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package is withdrawn");

            foreach (var reference in restoring ? Array.Empty<WorkflowPackageReference>() : activities.Closure ?? [])
            {
                var dependency = (await connection.QuerySingleOrDefaultAsync<PackageRow>(
                    new CommandDefinition(RequiredSql(Statement.CatalogPackage), reference, transaction,
                        cancellationToken: cancellationToken)))?.Value;

                if (dependency != null && (dependency.Digest != reference.Digest || dependency.Withdrawn))
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package closure identity changed");
            }

            var oldActivities = prior == null
                ? null
                : await ReadPackageActivitiesAsync(connection, transaction, prior, cancellationToken: cancellationToken);

            var merged = oldActivities == null ? activities : PackageIndexRules.Merge(oldActivities, activities);

            if (existing != null && !restoring && existing.Origin >= package.Origin && PackageIndexRules.Equal(oldActivities!, merged))
                return new PackageRegistration(existing, oldActivities!, false);

            var revision = await AdvanceCatalogRevisionAsync(connection, transaction, cancellationToken: cancellationToken);

            var saved = PackageIndexRules.Stamp(package, existing, merged,
                await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken), actor, revision);

            if (draft != null)
                saved = saved with { PublishedDraft = draft, PublishedAtUtc = saved.RegisteredAtUtc, PublishedBy = actor };

            if (restoring)
                saved = saved with { Withdrawn = false, WithdrawnAtUtc = null, WithdrawnBy = null };

            var parameters = PackageParameters(saved, merged, revision);

            if (existing == null)
                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogPackageInsert), parameters, transaction,
                    cancellationToken: cancellationToken));
            else
            {
                var columns = PackageColumns.Where(item => item.Property is not ("Id" or "Version" or "Digest"));

                await connection.ExecuteAsync(new CommandDefinition("UPDATE " + CatalogTable("packages") + " SET " +
                    string.Join(", ", columns.Select(item => CatalogColumn(item.Column) + "=@" + item.Property)) +
                    " WHERE " + CatalogColumn("id") + "=@Id AND " + CatalogColumn("version") + "=@Version AND " + CatalogColumn("digest") +
                    "=@Digest", parameters, transaction, cancellationToken: cancellationToken));

                foreach (var table in new[] { "package_activities", "package_pending_dependencies" })
                    await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM " + CatalogTable(table) + " WHERE " + CatalogColumn("package_id") + "=@Id AND " +
                        CatalogColumn("package_version") + "=@Version", package, transaction, cancellationToken: cancellationToken));
            }

            foreach (var item in merged.Activities)
                await InsertActivityAsync(connection, transaction, item, revision, cancellationToken: cancellationToken);

            foreach (var missing in merged.MissingPackageIds)
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO " + CatalogTable("package_pending_dependencies") + " (" +
                    string.Join(", ", new[] { "package_id", "package_version", "missing_package_id" }.Select(CatalogColumn)) +
                    ") VALUES (@Id,@Version,@Missing)", new { package.Id, package.Version, Missing = missing }, transaction,
                    cancellationToken: cancellationToken));

            if (draft != null && await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.CatalogDefinitionDelete), draft,
                    transaction, cancellationToken: cancellationToken)) != 1)
                throw new CatalogPreconditionException("The definition identity changed");

            return new PackageRegistration(saved, merged, existing == null);
        }, protectsStarts: false, cancellationToken: cancellationToken);
    }

    private static readonly (string Column, string Property)[] PackageColumns =
    [
        ("id", "Id"), ("version", "Version"), ("digest", "Digest"), ("metadata_json", "MetadataJson"),
        ("dependency_groups_json", "DependencyGroupsJson"),
        ("origin", "Origin"), ("extraction", "Extraction"), ("extraction_detail", "ExtractionDetail"), ("closure_json", "ClosureJson"),
        ("withdrawn", "Withdrawn"), ("withdrawn_at_utc", "WithdrawnAtUtc"), ("withdrawn_by", "WithdrawnBy"),
        ("registered_at_utc", "RegisteredAtUtc"),
        ("registered_by", "RegisteredBy"), ("published_draft_generation", "PublishedDraftGeneration"),
        ("published_draft_revision", "PublishedDraftRevision"),
        ("published_draft_name", "PublishedDraftName"), ("published_draft_version", "PublishedDraftVersion"),
        ("published_at_utc", "PublishedAtUtc"),
        ("published_by", "PublishedBy"), ("lifecycle_revision", "LifecycleRevision"), ("catalog_revision", "CatalogRevision")
    ];

    private object PackageParameters(WorkflowPackage row, PackageActivities activities, long revision) => WithTimes(new
    {
        row.Id, row.Version, Digest = row.Digest.Value, MetadataJson = JsonSerializer.Serialize(row),
        DependencyGroupsJson = JsonSerializer.Serialize(row.DependencyGroups),
        Origin = row.Origin.ToString(), Extraction = activities.Extraction.ToString(), ExtractionDetail = activities.Detail,
        ClosureJson = row.Closure == null ? null : JsonSerializer.Serialize(row.Closure), row.Withdrawn, row.WithdrawnBy, row.RegisteredBy,
        PublishedDraftGeneration = row.PublishedDraft?.Generation, PublishedDraftRevision = row.PublishedDraft?.Revision,
        PublishedDraftName = row.PublishedDraft?.Name, PublishedDraftVersion = row.PublishedDraft?.Version,
        row.PublishedBy, row.LifecycleRevision, CatalogRevision = revision
    }, ("RegisteredAtUtc", row.RegisteredAtUtc), ("WithdrawnAtUtc", row.WithdrawnAtUtc), ("PublishedAtUtc", row.PublishedAtUtc));

    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity,
        int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        WorkflowCatalogRules.ValidateIdentifier(afterIdentity);

        if (!Enum.IsDefined(extraction))
            throw new ArgumentException("Invalid extraction state");

        return CatalogConnectionAsync<IReadOnlyList<WorkflowPackage>>(async (connection, transaction) =>
        {
            var identity = CatalogColumn("id") + (CatalogSqlServer ? "+'@'+" : "||'@'||") + CatalogColumn("version");

            var sql = "SELECT " +
                string.Join(", ",
                    PackageColumns.Select(item =>
                        CatalogColumn(item.Column) + " AS " +
                        (CatalogSqlServer ? "[" + ReadAlias(item.Property) + "]" : ReadAlias(item.Property)))) + " FROM " +
                CatalogTable("packages") +
                " WHERE " + CatalogColumn("extraction") + "=@Extraction AND (@AfterIdentity IS NULL OR " + identity +
                ">@AfterIdentity) ORDER BY " + identity + CatalogTake;

            return (await connection.QueryAsync<PackageRow>(new CommandDefinition(sql,
                new { Extraction = extraction.ToString(), AfterIdentity = afterIdentity, Limit = limit }, transaction,
                cancellationToken: cancellationToken))).Select(row => row.Value).ToArray();
        }, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(afterIdentity);
        missingPackageId = WorkflowPackageRules.NormalizeId(missingPackageId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);

        return CatalogConnectionAsync<IReadOnlyList<WorkflowPackage>>(async (connection, transaction) =>
        {
            var identity = "p." + CatalogColumn("id") + (CatalogSqlServer ? "+'@'+p." : "||'@'||p.") + CatalogColumn("version");

            var sql = "SELECT " + string.Join(", ",
                    PackageColumns.Select(item => "p." + CatalogColumn(item.Column) + " AS " + ReadAlias(item.Property))) +
                " FROM " + CatalogTable("packages") + " p JOIN " + CatalogTable("package_pending_dependencies") + " d ON d." +
                CatalogColumn("package_id") + "=p." + CatalogColumn("id") +
                " AND d." + CatalogColumn("package_version") + "=p." + CatalogColumn("version") + " WHERE d." +
                CatalogColumn("missing_package_id") + "=@MissingPackageId" +
                " AND p." + CatalogColumn("extraction") + "='Pending' AND (@AfterIdentity IS NULL OR " + identity +
                ">@AfterIdentity) ORDER BY " + identity + CatalogTake;

            return (await connection.QueryAsync<PackageRow>(new CommandDefinition(sql,
                new { MissingPackageId = missingPackageId, AfterIdentity = afterIdentity, Limit = limit }, transaction,
                cancellationToken: cancellationToken))).Select(row => row.Value).ToArray();
        }, cancellationToken: cancellationToken);
    }
}