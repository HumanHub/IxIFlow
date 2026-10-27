using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Templates;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private static CatalogTemplate TemplateSummary(TemplateRow row, CatalogScope scope)
    {
        try
        {
            if (!Enum.TryParse<TemplateFormat>(row.Format, out var format) || !Enum.IsDefined(format))
                throw new ArgumentException("Invalid template format");
            var packages = JsonSerializer.Deserialize<WorkflowPackageReference[]>(row.ModelPackagesJson)
                ?? throw new JsonException();
            var cultures = JsonSerializer.Deserialize<string[]>(row.CulturesJson) ?? throw new JsonException();
            // Validate the portable projections with the content model's rules, without reading variant content.
            var projected = new TemplateVersion(row.Key, row.Version, row.Engine, format, row.ModelType, row.ModelTypeId,
                packages, row.DisplayName, row.Description, cultures.Select(culture => new TemplateVariant(culture, "")).ToArray());
            if (projected.Format.ToString() != row.Format || JsonSerializer.Serialize(projected.ModelPackages) != row.ModelPackagesJson ||
                JsonSerializer.Serialize(projected.Variants.Select(item => item.Culture)) != row.CulturesJson ||
                CatalogActivityRules.Search(row.Key, row.DisplayName ?? "") != row.SearchText || row.CatalogRevision < 1)
                throw new ArgumentException("Noncanonical template projections");
            return CatalogTemplateRules.Summary(projected, scope);
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Invalid template catalog projections", error);
        }
    }

    public Task<CatalogPage<CatalogTemplate>> ListTemplatesAsync(CatalogTemplateQuery query) => TemplatePageAsync(query, null);

    public Task<CatalogPage<CatalogTemplate>> ListTemplateVersionsAsync(string key, CatalogVersionQuery query)
    {
        TemplateCatalogRules.Key(key, 1);
        return TemplatePageAsync(new(query.Scope, After: query.After, Limit: query.Limit), key);
    }

    private Task<CatalogPage<CatalogTemplate>> TemplatePageAsync(CatalogTemplateQuery query, string? key)
    {
        var filter = CatalogTemplateRules.Filter(query, key);
        var after = CatalogTemplateRules.After(query, filter, key);
        var text = CatalogActivityRules.Text(query.Text);
        var engine = CatalogTemplateRules.Engine(query.Engine);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var parameters = new DynamicParameters(new { Limit = query.Limit + 1 });
            var keyColumn = CatalogColumn("key");
            var versionColumn = CatalogColumn("version");
            var predicates = new List<string> { NameScope(query.Scope, WorkflowDraftKinds.Template, keyColumn, parameters, "templateScope") };
            if (key != null) { parameters.Add("Key", key); predicates.Add(CatalogEqual(keyColumn, "@Key")); }
            if (engine != null) { parameters.Add("Engine", engine); predicates.Add(CatalogEqual(CatalogColumn("engine"), "@Engine")); }
            if (text != null) { parameters.Add("Text", LiteralLike(text)); predicates.Add(CatalogColumn("search_text") + " LIKE @Text ESCAPE '\\'"); }
            if (after.Key != null)
            {
                parameters.Add("AfterKey", after.Key); parameters.Add("AfterVersion", after.Version);
                predicates.Add("(" + CatalogOrder(keyColumn) + ">" + CatalogOrder("@AfterKey") + " OR (" + CatalogEqual(keyColumn, "@AfterKey") +
                    " AND " + versionColumn + ">@AfterVersion))");
            }
            var columns = TemplateColumns.Where(item => item.Column != "metadata_json");
            var sql = "SELECT " + string.Join(", ", columns.Select(item => CatalogColumn(item.Column) + " AS " + CatalogColumn(item.Property))) +
                " FROM " + CatalogTable("template_versions") + " WHERE " + string.Join(" AND ", predicates) +
                " ORDER BY " + CatalogOrder(keyColumn) + ", " + versionColumn + CatalogTake;
            var rows = (await connection.QueryAsync<TemplateRow>(sql, parameters, transaction)).ToArray();
            var items = rows.Take(query.Limit).Select(row => TemplateSummary(row, query.Scope)).ToArray();
            return new CatalogPage<CatalogTemplate>(items, rows.Length > query.Limit
                ? CatalogTemplateRules.Cursor(filter, items[^1], key != null) : null, await ReadCatalogRevisionAsync(connection, transaction));
        }, protectsStarts: false);
    }

    public Task<CatalogTemplateDetail?> GetTemplateDetailAsync(string key, int version, CatalogScope scope)
    {
        TemplateCatalogRules.Key(key, version);
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.Allows(WorkflowDraftKinds.Template, key)) return Task.FromResult<CatalogTemplateDetail?>(null);
        return CatalogWriteAsync<CatalogTemplateDetail?>(async (connection, transaction) =>
        {
            var row = await ReadTemplateAsync(connection, transaction, key, version);
            return row == null ? null : new(CatalogTemplateRules.Summary(row.Version, scope), row.PublishedDraft, row.PublishedAtUtc, row.PublishedBy);
        }, protectsStarts: false);
    }

    public Task<IReadOnlyList<CatalogStepDescriptor>> ListStepDescriptorsAsync(CatalogScope scope) => Task.FromResult(CatalogTemplateRules.Steps(scope));
}
