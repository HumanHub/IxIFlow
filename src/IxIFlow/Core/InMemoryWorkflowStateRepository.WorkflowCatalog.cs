namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    public Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filter = CatalogWorkflowRules.Filter(query);
        var after = CatalogPaging.Decode(query.After, filter, 1, query.Limit);
        var text = CatalogActivityRules.Text(query.Text);

        lock (_claimLock)
        {
            var rows = _versions.Values.Where(row => query.Scope.Allows(WorkflowDraftKinds.Workflow, row.Name) &&
                    (after == null || CatalogPaging.CompareDraftNames(row.Name, after[0]) > 0) &&
                    (text == null || CatalogActivityRules.Search(row.Name, row.Descriptor.DisplayName)
                        .Contains(text, StringComparison.Ordinal)) &&
                    (query.Tag == null || row.Descriptor.Tags.Contains(query.Tag, StringComparer.Ordinal)) &&
                    (query.Category == null || row.Descriptor.Category == query.Category) &&
                    (query.State == null || row.State == query.State))
                .GroupBy(row => row.Name).OrderBy(group => group.Key, CatalogPaging.DraftNames).Take(query.Limit + 1)
                .Select(group => CatalogWorkflowRules.Summarize(group.Select(CatalogWorkflowRules.Summary))).ToArray();

            return Task.FromResult(new CatalogPage<CatalogWorkflow>(rows.Take(query.Limit).ToArray(), rows.Length > query.Limit
                ? CatalogPaging.Encode(filter, rows[query.Limit - 1].Name)
                : null, _catalogRevision));
        }
    }

    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateName(name);
        ArgumentNullException.ThrowIfNull(query.Scope);
        var filter = CatalogPaging.Hash(new { query.Scope.Hash, name, Kind = "workflow-versions" });
        var after = CatalogWorkflowRules.AfterVersion(CatalogPaging.Decode(query.After, filter, 1, query.Limit));

        lock (_claimLock)
        {
            var rows = _versions.Values.Where(row => row.Name == name && query.Scope.Allows(WorkflowDraftKinds.Workflow, row.Name) &&
                    (after == null || row.Version > after)).OrderBy(row => row.Version).Take(query.Limit + 1)
                .Select(CatalogWorkflowRules.Summary).ToArray();

            return Task.FromResult(new CatalogPage<CatalogWorkflowVersion>(rows.Take(query.Limit).ToArray(), rows.Length > query.Limit
                ? CatalogPaging.Encode(filter, rows[query.Limit - 1].Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : null, _catalogRevision));
        }
    }

    public async Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateName(name);
        ArgumentNullException.ThrowIfNull(scope);

        if (!scope.Allows(WorkflowDraftKinds.Workflow, name))
            return null;

        var row = await GetWorkflowVersionAsync(name, version, cancellationToken: cancellationToken);

        return row == null ? null : await CatalogWorkflowRules.DetailAsync(row, scope, CatalogArtifacts ?? this);
    }
}