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
        ? "[" + (name == "csharp_type_name" ? "CSharpTypeName" : string.Concat(name.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]))) + "]"
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
        .Select(item => CatalogColumn(item.Column) + " AS " + (CatalogSqlServer ? "[" + item.Property + "]" : item.Property))) +
        (installed ? ", " + CatalogColumn("fingerprint") + " AS Fingerprint" : "") + " FROM " + CatalogTable(installed ? "installed_activities" : "package_activities");

    private Task<int> InsertActivityAsync(DbConnection connection, DbTransaction transaction, ActivityDescriptor item, long revision, string? fingerprint = null)
    {
        var columns = ActivityColumns.Concat(fingerprint == null ? [] : new[] { ("fingerprint", "Fingerprint") }).ToArray();
        return connection.ExecuteAsync("INSERT INTO " + CatalogTable(fingerprint == null ? "package_activities" : "installed_activities") +
            " (" + string.Join(", ", columns.Select(column => CatalogColumn(column.Item1))) + ") VALUES (" +
            string.Join(", ", columns.Select(column => "@" + column.Item2)) + ")", new
            {
                item.PackageId, item.PackageVersion, item.Key, item.Version, item.MetadataTypeName, item.CSharpTypeName,
                DisplayName = item.Name, item.Category, item.Icon, SearchText = CatalogActivityRules.Search(item.Key, item.Name),
                item.ContractHash, DescriptorJson = item.ToJson(), CatalogRevision = revision, Fingerprint = fingerprint
            }, transaction);
    }

    private async Task<InstalledActivityPackage> ReadInstalledPackageAsync(DbConnection connection, DbTransaction? transaction, InstalledPackageRow row)
    {
        var activities = (await connection.QueryAsync<ActivityRow>(ActivitySelect(true) + " WHERE " + CatalogColumn("package_id") + "=@Id AND " +
            CatalogColumn("package_version") + "=@Version AND " + CatalogColumn("fingerprint") + "=@Fingerprint", row, transaction)).ToArray();
        if (activities.Any(item => item.CatalogRevision != row.CatalogRevision))
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Installed activity revision differs from its package");
        return InstalledPackageRules.Snapshot(new(row.Id, row.Version, row.Fingerprint, activities.Select(item => item.Value).ToArray()));
    }

    private async Task<PackageActivities> ReadPackageActivitiesAsync(DbConnection connection, DbTransaction? transaction, PackageRow row)
    {
        var activities = (await connection.QueryAsync<ActivityRow>(ActivitySelect(false) + " WHERE " + CatalogColumn("package_id") + "=@Id AND " +
            CatalogColumn("package_version") + "=@Version", row, transaction)).ToArray();
        var missing = (await connection.QueryAsync<string>("SELECT " + CatalogColumn("missing_package_id") + " FROM " +
            CatalogTable("package_pending_dependencies") + " WHERE " + CatalogColumn("package_id") + "=@Id AND " + CatalogColumn("package_version") + "=@Version", row, transaction)).ToArray();
        if (activities.Any(item => item.CatalogRevision != row.CatalogRevision))
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Activity revision differs from its package");
        return PackageIndexRules.Snapshot(row.Value, new(activities.Select(item => item.Value).ToArray(), Enum.Parse<PackageExtraction>(row.Extraction),
            row.ExtractionDetail, row.ClosureJson == null ? null : JsonSerializer.Deserialize<WorkflowPackageReference[]>(row.ClosureJson), missing));
    }

    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null)
    {
        PackageIndexRules.Registration(package, actor);
        return AcceptPackageAsync(package, activities, artifacts, actor, false);
    }

    public async Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null)
    {
        WorkflowPublicationRules.Actor(actor);
        var package = await GetPackageAsync(id, version);
        if (package == null || package.Digest != expectedDigest)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package extraction identity changed");
        return await AcceptPackageAsync(package, activities, artifacts, actor, true);
    }

    private Task<PackageRegistration> AcceptPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor, bool completion)
    {
        package = WorkflowPackageRules.Copy(package);
        activities = PackageIndexRules.Snapshot(package, activities);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var prior = await connection.QuerySingleOrDefaultAsync<PackageRow>(RequiredSql(Statement.CatalogPackage), new { package.Id, package.Version }, transaction);
            var existing = prior?.Value;
            if (completion && existing == null) throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package disappeared");
            WorkflowPackageRules.CheckRegistration(existing, package);
            await PackageIndexRules.CheckArtifactsAsync(package, activities, artifacts);
            if (existing?.Withdrawn == true) throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package is withdrawn");
            foreach (var reference in activities.Closure ?? [])
            {
                var dependency = (await connection.QuerySingleOrDefaultAsync<PackageRow>(RequiredSql(Statement.CatalogPackage), reference, transaction))?.Value;
                if (dependency != null && (dependency.Digest != reference.Digest || dependency.Withdrawn))
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package closure identity changed");
            }
            var oldActivities = prior == null ? null : await ReadPackageActivitiesAsync(connection, transaction, prior);
            var merged = oldActivities == null ? activities : PackageIndexRules.Merge(oldActivities, activities);
            if (existing != null && existing.Origin >= package.Origin && PackageIndexRules.Equal(oldActivities!, merged))
                return new PackageRegistration(existing, oldActivities!, false);
            var revision = await AdvanceCatalogRevisionAsync(connection, transaction);
            var saved = PackageIndexRules.Stamp(package, existing, merged, await OutboxNowAsync(connection, transaction), actor, revision);
            var parameters = PackageParameters(saved, merged, revision);
            if (existing == null)
                await connection.ExecuteAsync(RequiredSql(Statement.CatalogPackageInsert), parameters, transaction);
            else
            {
                var columns = PackageColumns.Where(item => item.Property is not ("Id" or "Version" or "Digest"));
                await connection.ExecuteAsync("UPDATE " + CatalogTable("packages") + " SET " + string.Join(", ", columns.Select(item => CatalogColumn(item.Column) + "=@" + item.Property)) +
                    " WHERE " + CatalogColumn("id") + "=@Id AND " + CatalogColumn("version") + "=@Version AND " + CatalogColumn("digest") + "=@Digest", parameters, transaction);
                foreach (var table in new[] { "package_activities", "package_pending_dependencies" })
                    await connection.ExecuteAsync("DELETE FROM " + CatalogTable(table) + " WHERE " + CatalogColumn("package_id") + "=@Id AND " + CatalogColumn("package_version") + "=@Version", package, transaction);
            }
            foreach (var item in merged.Activities) await InsertActivityAsync(connection, transaction, item, revision);
            foreach (var missing in merged.MissingPackageIds)
                await connection.ExecuteAsync("INSERT INTO " + CatalogTable("package_pending_dependencies") + " (" + string.Join(", ", new[] { "package_id", "package_version", "missing_package_id" }.Select(CatalogColumn)) +
                    ") VALUES (@Id,@Version,@Missing)", new { package.Id, package.Version, Missing = missing }, transaction);
            return new PackageRegistration(saved, merged, existing == null);
        }, protectsStarts: false);
    }

    private static readonly (string Column, string Property)[] PackageColumns =
    [
        ("id", "Id"), ("version", "Version"), ("digest", "Digest"), ("metadata_json", "MetadataJson"), ("dependency_groups_json", "DependencyGroupsJson"),
        ("origin", "Origin"), ("extraction", "Extraction"), ("extraction_detail", "ExtractionDetail"), ("closure_json", "ClosureJson"),
        ("withdrawn", "Withdrawn"), ("withdrawn_at_utc", "WithdrawnAtUtc"), ("withdrawn_by", "WithdrawnBy"), ("registered_at_utc", "RegisteredAtUtc"),
        ("registered_by", "RegisteredBy"), ("published_draft_generation", "PublishedDraftGeneration"), ("published_draft_revision", "PublishedDraftRevision"),
        ("published_draft_name", "PublishedDraftName"), ("published_draft_version", "PublishedDraftVersion"), ("published_at_utc", "PublishedAtUtc"),
        ("published_by", "PublishedBy"), ("lifecycle_revision", "LifecycleRevision"), ("catalog_revision", "CatalogRevision")
    ];

    private object PackageParameters(WorkflowPackage row, PackageActivities activities, long revision) => WithTimes(new
    {
        row.Id, row.Version, Digest = row.Digest.Value, MetadataJson = JsonSerializer.Serialize(row), DependencyGroupsJson = JsonSerializer.Serialize(row.DependencyGroups),
        Origin = row.Origin.ToString(), Extraction = activities.Extraction.ToString(), ExtractionDetail = activities.Detail,
        ClosureJson = row.Closure == null ? null : JsonSerializer.Serialize(row.Closure), row.Withdrawn, row.WithdrawnBy, row.RegisteredBy,
        PublishedDraftGeneration = row.PublishedDraft?.Generation, PublishedDraftRevision = row.PublishedDraft?.Revision,
        PublishedDraftName = row.PublishedDraft?.Name, PublishedDraftVersion = row.PublishedDraft?.Version,
        row.PublishedBy, row.LifecycleRevision, CatalogRevision = revision
    }, ("RegisteredAtUtc", row.RegisteredAtUtc), ("WithdrawnAtUtc", row.WithdrawnAtUtc), ("PublishedAtUtc", row.PublishedAtUtc));

    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        if (!Enum.IsDefined(extraction)) throw new ArgumentException("Invalid extraction state");
        return CatalogConnectionAsync<IReadOnlyList<WorkflowPackage>>(async (connection, transaction) =>
        {
            var identity = CatalogColumn("id") + (CatalogSqlServer ? "+'@'+" : "||'@'||") + CatalogColumn("version");
            var sql = "SELECT " + string.Join(", ", PackageColumns.Select(item => CatalogColumn(item.Column) + " AS " + (CatalogSqlServer ? "[" + item.Property + "]" : item.Property))) + " FROM " + CatalogTable("packages") +
                " WHERE " + CatalogColumn("extraction") + "=@Extraction AND (@AfterIdentity IS NULL OR " + identity + ">@AfterIdentity) ORDER BY " + identity + CatalogTake;
            return (await connection.QueryAsync<PackageRow>(sql, new { Extraction = extraction.ToString(), AfterIdentity = afterIdentity, Limit = limit }, transaction)).Select(row => row.Value).ToArray();
        });
    }

    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit)
    {
        missingPackageId = WorkflowPackageRules.NormalizeId(missingPackageId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        return CatalogConnectionAsync<IReadOnlyList<WorkflowPackage>>(async (connection, transaction) =>
        {
            var identity = "p." + CatalogColumn("id") + (CatalogSqlServer ? "+'@'+p." : "||'@'||p.") + CatalogColumn("version");
            var sql = "SELECT " + string.Join(", ", PackageColumns.Select(item => "p." + CatalogColumn(item.Column) + " AS " + item.Property)) +
                " FROM " + CatalogTable("packages") + " p JOIN " + CatalogTable("package_pending_dependencies") + " d ON d." + CatalogColumn("package_id") + "=p." + CatalogColumn("id") +
                " AND d." + CatalogColumn("package_version") + "=p." + CatalogColumn("version") + " WHERE d." + CatalogColumn("missing_package_id") + "=@MissingPackageId" +
                " AND p." + CatalogColumn("extraction") + "='Pending' AND (@AfterIdentity IS NULL OR " + identity + ">@AfterIdentity) ORDER BY " + identity + CatalogTake;
            return (await connection.QueryAsync<PackageRow>(sql, new { MissingPackageId = missingPackageId, AfterIdentity = afterIdentity, Limit = limit }, transaction)).Select(row => row.Value).ToArray();
        });
    }
}
