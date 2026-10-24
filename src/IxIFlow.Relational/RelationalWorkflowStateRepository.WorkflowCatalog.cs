using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private class WorkflowSummaryRow
    {
        public string Name { get; set; } = "";
        public int Version { get; set; }
        public string State { get; set; } = "";
        public string StructureHash { get; set; } = "";
        public string? ArtifactDigest { get; set; }
        public string ContractHash { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Category { get; set; }
        public string Tags { get; set; } = "";
        public string SearchText { get; set; } = "";
        public string DescriptorJson { get; set; } = "";
        public long LifecycleRevision { get; set; }
        public int VersionCount { get; set; }
        public int? LatestPublishedVersion { get; set; }
        public CatalogWorkflowVersion Summary
        {
            get
            {
                var descriptor = WorkflowDescriptor.Parse(DescriptorJson);
                if (descriptor.ToJson() != DescriptorJson || descriptor.DisplayName != DisplayName || descriptor.Category != Category ||
                    WorkflowDescriptor.TagProjection(descriptor) != Tags || CatalogActivityRules.Search(Name, DisplayName) != SearchText)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Workflow descriptor differs from its projections");
                return new(Name, Version, Enum.Parse<WorkflowVersionState>(State), StructureHash,
                    ArtifactDigest == null ? null : new(ArtifactDigest), ContractHash, descriptor, LifecycleRevision);
            }
        }
    }

    private static readonly (string Column, string Property)[] WorkflowSummaryColumns =
    [
        ("name", "Name"), ("version", "Version"), ("state", "State"), ("structure_hash", "StructureHash"),
        ("artifact_digest", "ArtifactDigest"), ("contract_hash", "ContractHash"), ("display_name", "DisplayName"),
        ("category", "Category"), ("tags", "Tags"), ("search_text", "SearchText"), ("descriptor_json", "DescriptorJson"),
        ("lifecycle_revision", "LifecycleRevision")
    ];
    private string WorkflowSummarySelect => string.Join(", ", WorkflowSummaryColumns.Select(item =>
        CatalogColumn(item.Column) + " AS " + (CatalogSqlServer ? "[" + item.Property + "]" : item.Property)));

    public Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query)
    {
        var filter = CatalogWorkflowRules.Filter(query);
        var after = CatalogPaging.Decode(query.After, filter, 1, query.Limit);
        var text = CatalogActivityRules.Text(query.Text);
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var parameters = new DynamicParameters(new { Limit = query.Limit + 1 });
            var predicates = new List<string> { NameScope(query.Scope, WorkflowDraftKinds.Workflow, CatalogColumn("name"), parameters, "workflowScope") };
            void Equal(string column, string parameter, string? value)
            { if (value != null) { parameters.Add(parameter, value); predicates.Add(CatalogEqual(CatalogColumn(column), "@" + parameter)); } }
            Equal("category", "Category", query.Category);
            Equal("state", "State", query.State?.ToString());
            if (text != null) { parameters.Add("Text", LiteralLike(text)); predicates.Add(CatalogColumn("search_text") + " LIKE @Text ESCAPE '\\'"); }
            if (query.Tag != null)
            {
                parameters.Add("Tag", " " + query.Tag + " ");
                var tags = CatalogColumn("tags");
                predicates.Add(CatalogSqlServer ? "CHARINDEX(@Tag, " + tags + " COLLATE Latin1_General_100_BIN2)>0" :
                    PayloadSqlDialect == PayloadDialect.PostgreSql ? "strpos(" + tags + ", @Tag)>0" : "instr(" + tags + ", @Tag)>0");
            }
            predicates.Add(AfterKey([CatalogColumn("name")], after, parameters));
            string A(string property) => CatalogSqlServer ? "[" + property + "]" : property.ToLowerInvariant();
            var name = A("Name"); var version = A("Version"); var state = A("State");
            var sql = "WITH filtered AS (SELECT " + WorkflowSummarySelect + " FROM " + CatalogTable("workflow_versions") + " WHERE " + string.Join(" AND ", predicates) +
                "), ranked AS (SELECT filtered.*, ROW_NUMBER() OVER (PARTITION BY " + name + " ORDER BY CASE WHEN " + state + "='Published' THEN 0 ELSE 1 END, " + version +
                " DESC) AS choice, COUNT(*) OVER (PARTITION BY " + name + ") AS VersionCount, MAX(CASE WHEN " + state + "='Published' THEN " + version +
                " END) OVER (PARTITION BY " + name + ") AS LatestPublishedVersion FROM filtered) SELECT * FROM ranked WHERE choice=1 ORDER BY " + CatalogOrder(name) + CatalogTake;
            var rows = (await connection.QueryAsync<WorkflowSummaryRow>(sql, parameters, transaction)).ToArray();
            var items = rows.Take(query.Limit).Select(row =>
            {
                var summary = row.Summary;
                return new CatalogWorkflow(row.Name, summary.Descriptor.DisplayName, summary.Descriptor.Category, summary.Descriptor.Tags,
                    summary.Descriptor.Icon, row.LatestPublishedVersion, row.VersionCount);
            }).ToArray();
            return new CatalogPage<CatalogWorkflow>(items, rows.Length > query.Limit ? CatalogPaging.Encode(filter, items[^1].Name) : null,
                await ReadCatalogRevisionAsync(connection, transaction));
        }, protectsStarts: false);
    }

    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query)
    {
        WorkflowCatalogRules.ValidateName(name);
        ArgumentNullException.ThrowIfNull(query.Scope);
        var filter = CatalogPaging.Hash(new { query.Scope.Hash, name, Kind = "workflow-versions" });
        var after = CatalogWorkflowRules.AfterVersion(CatalogPaging.Decode(query.After, filter, 1, query.Limit));
        return CatalogWriteAsync(async (connection, transaction) =>
        {
            var parameters = new DynamicParameters(new { Name = name, AfterVersion = after, Limit = query.Limit + 1 });
            var scope = NameScope(query.Scope, WorkflowDraftKinds.Workflow, CatalogColumn("name"), parameters, "workflowScope");
            var sql = "SELECT " + WorkflowSummarySelect + " FROM " + CatalogTable("workflow_versions") + " WHERE " + scope + " AND " +
                CatalogEqual(CatalogColumn("name"), "@Name") + " AND (@AfterVersion IS NULL OR " + CatalogColumn("version") + ">@AfterVersion) ORDER BY " + CatalogColumn("version") + CatalogTake;
            var rows = (await connection.QueryAsync<WorkflowSummaryRow>(sql, parameters, transaction)).Select(row => row.Summary).ToArray();
            return new CatalogPage<CatalogWorkflowVersion>(rows.Take(query.Limit).ToArray(), rows.Length > query.Limit
                ? CatalogPaging.Encode(filter, rows[query.Limit - 1].Version.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
                await ReadCatalogRevisionAsync(connection, transaction));
        }, protectsStarts: false);
    }

    public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope)
    {
        WorkflowCatalogRules.ValidateName(name);
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.Allows(WorkflowDraftKinds.Workflow, name)) return Task.FromResult<CatalogWorkflowVersionDetail?>(null);
        return CatalogWriteAsync<CatalogWorkflowVersionDetail?>(async (connection, transaction) =>
        {
            var row = (await connection.QuerySingleOrDefaultAsync<VersionRow>(RequiredSql(Statement.CatalogVersion), new { Name = name, Version = version }, transaction))?.Value;
            return row == null ? null : await CatalogWorkflowRules.DetailAsync(row, scope, CatalogArtifacts ?? this);
        }, protectsStarts: false);
    }
}
