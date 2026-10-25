using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    /// <summary>The artifact source used for current catalog availability, when files replace database artifacts.</summary>
    public IArtifactStore? CatalogArtifacts { get; set; }

    private string NameScope(CatalogScope scope, string kind, string column, DynamicParameters parameters, string prefix)
    {
        var names = scope.For(kind);
        if (names.Names == null && names.Prefixes == null) return "1=1";
        var terms = new List<string>();
        if (PayloadSqlDialect == PayloadDialect.PostgreSql && names.Names?.Count > 0)
        {
            parameters.Add(prefix + "Names", names.Names.ToArray());
            terms.Add(column + " = ANY(@" + prefix + "Names)");
        }
        else foreach (var name in names.Names ?? [])
        {
            var parameter = prefix + terms.Count;
            parameters.Add(parameter, name);
            terms.Add(CatalogEqual(column, "@" + parameter));
        }
        foreach (var value in names.Prefixes ?? [])
        {
            var parameter = prefix + terms.Count;
            parameters.Add(parameter, value);
            parameters.Add(parameter + "Length", CatalogPrefixLength(value));
            terms.Add(CatalogEqual("SUBSTRING(" + column + ",1,@" + parameter + "Length)", "@" + parameter));
        }
        return "(" + (terms.Count == 0 ? "1=0" : string.Join(" OR ", terms)) + ")";
    }

    private static string LiteralLike(string value) => "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]") + "%";

    // Match the UTF-8 binary key order used by SQLite/PostgreSQL and the existing draft pages.
    private string CatalogOrder(string column) => CatalogSqlServer
        ? "CONVERT(VARBINARY(MAX), CONVERT(VARCHAR(MAX), " + column + " COLLATE Latin1_General_100_CI_AS_SC_UTF8) COLLATE Latin1_General_100_CI_AS_SC_UTF8)"
        : column;

    private string CatalogEqual(string left, string right) => CatalogOrder(left) + "=" + CatalogOrder(right);

    private string AfterKey(string[] columns, string[]? after, DynamicParameters parameters)
    {
        if (after == null) return "1=1";
        var terms = new List<string>();
        for (var index = 0; index < columns.Length; index++)
        {
            parameters.Add("after" + index, after[index]);
            terms.Add("(" + string.Join(" AND ", Enumerable.Range(0, index).Select(part => CatalogEqual(columns[part], "@after" + part))
                .Append(CatalogOrder(columns[index]) + ">" + CatalogOrder("@after" + index))) + ")");
        }
        return "(" + string.Join(" OR ", terms) + ")";
    }

    private sealed class ActivityCatalogRow : ActivityRow
    {
        public string Origin { get; set; } = "";
        public string Build { get; set; } = "";
        public bool Withdrawn { get; set; }
        public CatalogActivity Activity => new(Value, new(PackageId, PackageVersion, Enum.Parse<ActivityOrigin>(Origin), Build), Withdrawn, false);
    }

    private string ActivityUnion()
    {
        var columns = string.Join(", ", ActivityColumns.Select(item => "a." + CatalogColumn(item.Column) + " AS " + (CatalogSqlServer ? "[" + item.Property + "]" : item.Property)));
        var joined = "a." + CatalogColumn("package_id") + "=p." + CatalogColumn("id") + " AND a." + CatalogColumn("package_version") + "=p." + CatalogColumn("version");
        return "SELECT " + columns + ", p." + CatalogColumn("origin") + " AS Origin, p." + CatalogColumn("digest") + " AS Build, p." + CatalogColumn("withdrawn") + " AS Withdrawn FROM " +
            CatalogTable("package_activities") + " a JOIN " + CatalogTable("packages") + " p ON " + joined +
            " UNION ALL SELECT " + columns + ", 'Local' AS Origin, a." + CatalogColumn("fingerprint") + " AS Build, " +
            (CatalogSqlServer ? "CAST(0 AS BIT)" : "FALSE") + " AS Withdrawn FROM " + CatalogTable("installed_activities") + " a";
    }

    public Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query) => ActivityPageAsync(query, null, null);
    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query) =>
        ActivityPageAsync(new(query.Scope, After: query.After, Limit: query.Limit), key, null);
    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query) =>
        ActivityPageAsync(new(query.Scope, PackageId: WorkflowPackageRules.NormalizeId(id), After: query.After, Limit: query.Limit), null, version);

    private Task<CatalogPage<CatalogActivity>> ActivityPageAsync(CatalogActivityQuery query, string? activityKey, string? packageVersion)
    {
        ArgumentNullException.ThrowIfNull(query.Scope);
        WorkflowCatalogRules.ValidateText(activityKey);
        WorkflowCatalogRules.ValidateText(packageVersion);
        var text = CatalogActivityRules.Text(query.Text);
        var id = query.PackageId == null ? null : WorkflowPackageRules.NormalizeId(query.PackageId);
        if (query.Origin is { } origin && !Enum.IsDefined(origin)) throw new ArgumentException("Invalid activity origin");
        var filter = CatalogPaging.Hash(new { query.Scope.Hash, Text = text, query.Category, PackageId = id, query.Origin, query.Withdrawn, query.ArtifactPresent, activityKey, packageVersion });
        var initialAfter = CatalogPaging.Decode(query.After, filter, 5, query.Limit);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var page = new List<CatalogActivity>();
            var after = initialAfter;
            while (page.Count <= query.Limit)
            {
                var parameters = new DynamicParameters(new { Limit = Math.Min(query.Limit + 1, 128) });
                string C(string name) => CatalogSqlServer ? "[" + name + "]" : name.ToLowerInvariant();
                var predicates = new List<string>
                {
                    NameScope(query.Scope, WorkflowDraftKinds.Activity, C("Key"), parameters, "activityScope"),
                    NameScope(query.Scope, "package", C("PackageId"), parameters, "packageScope")
                };
                void Equal(string column, string name, object? value)
                { if (value != null) { parameters.Add(name, value); predicates.Add(value is string ? CatalogEqual(column, "@" + name) : column + "=@" + name); } }
                Equal(C("Key"), "ActivityKey", activityKey);
                Equal(C("PackageId"), "PackageId", id);
                Equal(C("PackageVersion"), "PackageVersion", packageVersion);
                Equal(C("Category"), "Category", query.Category);
                Equal(C("Origin"), "Origin", query.Origin?.ToString());
                Equal(C("Withdrawn"), "Withdrawn", query.Withdrawn);
                if (text != null) { parameters.Add("Text", LiteralLike(text)); predicates.Add(C("SearchText") + " LIKE @Text ESCAPE '\\'"); }
                var keyColumns = new[] { "Key", "Version", "PackageId", "PackageVersion", "Build" }.Select(C).ToArray();
                predicates.Add(AfterKey(keyColumns, after, parameters));
                var sql = "SELECT * FROM (" + ActivityUnion() + ") activities WHERE " + string.Join(" AND ", predicates) + " ORDER BY " + string.Join(", ", keyColumns.Select(CatalogOrder)) + CatalogTake;
                var rows = (await connection.QueryAsync<ActivityCatalogRow>(sql, parameters, transaction)).ToArray();
                foreach (var row in rows)
                {
                    var activity = row.Activity;
                    var present = activity.Provider.Origin != ActivityOrigin.Local && await (CatalogArtifacts ?? this).ExistsAsync(new(activity.Provider.Build));
                    if (query.ArtifactPresent is { } required && present != required) continue;
                    page.Add(activity with { ArtifactPresent = present });
                    if (page.Count > query.Limit) break;
                }
                if (rows.Length < Math.Min(query.Limit + 1, 128) || page.Count > query.Limit) break;
                after = CatalogActivityRules.Key(rows[^1].Activity);
            }
            return new CatalogPage<CatalogActivity>(page.Take(query.Limit).ToArray(), page.Count > query.Limit
                ? CatalogPaging.Encode(filter, CatalogActivityRules.Key(page[query.Limit - 1])) : null, await ReadCatalogRevisionAsync(connection, transaction));
        }, protectsStarts: false);
    }

    public async Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope)
    {
        WorkflowCatalogRules.ValidateText(key);
        WorkflowCatalogRules.ValidateText(version);
        provider = provider with { PackageId = WorkflowPackageRules.NormalizeId(provider.PackageId) };
        if (!scope.Allows(WorkflowDraftKinds.Activity, key) || !scope.Allows("package", provider.PackageId)) return null;
        var row = await CatalogWriteAsync(async (connection, transaction) =>
        {
            var sql = "SELECT * FROM (" + ActivityUnion() + ") activities WHERE " + string.Join(" AND ",
                new[] { "Key", "Version", "PackageId", "PackageVersion", "Build" }.Select(name =>
                    CatalogEqual(CatalogSqlServer ? "[" + name + "]" : name.ToLowerInvariant(), "@" + name)));
            var result = await connection.QuerySingleOrDefaultAsync<ActivityCatalogRow>(sql, new { Key = key, Version = version, provider.PackageId, provider.PackageVersion, provider.Build }, transaction);
            if (result == null) return null;
            var activity = result.Activity;
            return activity with { ArtifactPresent = activity.Provider.Origin != ActivityOrigin.Local && await (CatalogArtifacts ?? this).ExistsAsync(new(activity.Provider.Build)) };
        }, protectsStarts: false);
        if (row == null) return null;
        // Registry always precedes catalog locks; never request it while holding a catalog transaction.
        var hosts = row.Provider.Origin == ActivityOrigin.Local ? await CatalogLocalHosts.ReadAsync(this, this, provider.PackageId, provider.PackageVersion, provider.Build, scope) : [];
        return new(row, hosts);
    }

    private sealed class PackageSummaryRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Digest { get; set; } = "";
        public string Origin { get; set; } = "";
        public string Extraction { get; set; } = "";
        public string? ExtractionDetail { get; set; }
        public bool Withdrawn { get; set; }
        public long LifecycleRevision { get; set; }
        public int ActivityCount { get; set; }
        public CatalogPackage Summary(bool present) => new(Id, Version, new(Digest), Enum.Parse<WorkflowPackageOrigin>(Origin),
            Enum.Parse<PackageExtraction>(Extraction), ExtractionDetail, ActivityCount, Withdrawn, present, LifecycleRevision);
    }

    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query) => PackagePageAsync(query, null);
    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query) =>
        PackagePageAsync(new(query.Scope, After: query.After, Limit: query.Limit), WorkflowPackageRules.NormalizeId(id));

    private Task<CatalogPage<CatalogPackage>> PackagePageAsync(CatalogPackageQuery query, string? id)
    {
        ArgumentNullException.ThrowIfNull(query.Scope);
        var text = CatalogActivityRules.Text(query.Text);
        if (query.Origin is { } origin && !Enum.IsDefined(origin)) throw new ArgumentException("Invalid package origin");
        var filter = CatalogPaging.Hash(new { query.Scope.Hash, Text = text, query.Origin, query.Withdrawn, query.ArtifactPresent, id });
        var initialAfter = CatalogPaging.Decode(query.After, filter, 2, query.Limit);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var page = new List<CatalogPackage>();
            var after = initialAfter;
            while (page.Count <= query.Limit)
            {
                var parameters = new DynamicParameters(new { Limit = Math.Min(query.Limit + 1, 128) });
                string C(string name) => "p." + CatalogColumn(name);
                var predicates = new List<string> { NameScope(query.Scope, "package", C("id"), parameters, "packageScope") };
                void Equal(string column, string name, object? value)
                { if (value != null) { parameters.Add(name, value); predicates.Add(value is string ? CatalogEqual(column, "@" + name) : column + "=@" + name); } }
                Equal(C("id"), "Id", id);
                Equal(C("origin"), "Origin", query.Origin?.ToString());
                Equal(C("withdrawn"), "Withdrawn", query.Withdrawn);
                if (text != null) { parameters.Add("Text", LiteralLike(text)); predicates.Add(C("id") + " LIKE @Text ESCAPE '\\'"); }
                predicates.Add(AfterKey([C("id"), C("version")], after, parameters));
                var count = "(SELECT count(*) FROM " + CatalogTable("package_activities") + " a WHERE a." + CatalogColumn("package_id") + "=" + C("id") +
                    " AND a." + CatalogColumn("package_version") + "=" + C("version") + " AND " + NameScope(query.Scope, WorkflowDraftKinds.Activity, "a." + CatalogColumn("key"), parameters, "activityScope") + ") AS ActivityCount";
                var columns = PackageColumns.Where(item => item.Property is "Id" or "Version" or "Digest" or "Origin" or "Extraction" or "ExtractionDetail" or "Withdrawn" or "LifecycleRevision");
                var sql = "SELECT " + string.Join(", ", columns.Select(item => C(item.Column) + " AS " + item.Property).Append(count)) + " FROM " + CatalogTable("packages") +
                    " p WHERE " + string.Join(" AND ", predicates) + " ORDER BY " + CatalogOrder(C("id")) + ", " + CatalogOrder(C("version")) + CatalogTake;
                var rows = (await connection.QueryAsync<PackageSummaryRow>(sql, parameters, transaction)).ToArray();
                foreach (var row in rows)
                {
                    var present = await (CatalogArtifacts ?? this).ExistsAsync(new(row.Digest));
                    if (query.ArtifactPresent is { } required && present != required) continue;
                    page.Add(row.Summary(present));
                    if (page.Count > query.Limit) break;
                }
                if (rows.Length < Math.Min(query.Limit + 1, 128) || page.Count > query.Limit) break;
                after = [rows[^1].Id, rows[^1].Version];
            }
            return new CatalogPage<CatalogPackage>(page.Take(query.Limit).ToArray(), page.Count > query.Limit
                ? CatalogPaging.Encode(filter, page[query.Limit - 1].Id, page[query.Limit - 1].Version) : null, await ReadCatalogRevisionAsync(connection, transaction));
        }, protectsStarts: false);
    }

    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope)
    {
        WorkflowCatalogRules.ValidateText(version);
        id = WorkflowPackageRules.NormalizeId(id);
        if (!scope.Allows("package", id)) return Task.FromResult<CatalogPackageDetail?>(null);
        return CatalogWriteAsync<CatalogPackageDetail?>(async (connection, transaction) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<PackageRow>(RequiredSql(Statement.CatalogPackage), new { Id = id, Version = version }, transaction);
            if (row == null) return null;
            var package = row.Value;
            var activities = CatalogActivityRules.Scoped(await ReadPackageActivitiesAsync(connection, transaction, row), scope);
            var summary = new CatalogPackage(package.Id, package.Version, package.Digest, package.Origin, activities.Extraction, activities.Detail, activities.Activities.Count,
                package.Withdrawn, await (CatalogArtifacts ?? this).ExistsAsync(package.Digest), package.LifecycleRevision);
            return new(summary, activities, package.PublishedDraft, package.RegisteredAtUtc, package.RegisteredBy, package.WithdrawnAtUtc, package.WithdrawnBy);
        }, protectsStarts: false);
    }
}
