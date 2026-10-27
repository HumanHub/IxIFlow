namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
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
        lock (_claimLock)
        {
            var rows = _templates.Values.Select(entry => entry.Version).Where(row =>
                query.Scope.Allows(WorkflowDraftKinds.Template, row.Key) && (key == null || row.Key == key) &&
                (text == null || CatalogActivityRules.Search(row.Key, row.DisplayName ?? "").Contains(text, StringComparison.Ordinal)) &&
                (engine == null || row.Engine == engine) &&
                (after.Key == null || StringComparer.Ordinal.Compare(row.Key, after.Key) > 0 || row.Key == after.Key && row.Version > after.Version))
                .OrderBy(row => row.Key, StringComparer.Ordinal).ThenBy(row => row.Version).Take(query.Limit + 1)
                .Select(row => CatalogTemplateRules.Summary(row, query.Scope)).ToArray();
            return Task.FromResult(new CatalogPage<CatalogTemplate>(rows.Take(query.Limit).ToArray(), rows.Length > query.Limit
                ? CatalogTemplateRules.Cursor(filter, rows[query.Limit - 1], key != null) : null, _catalogRevision));
        }
    }

    public Task<CatalogTemplateDetail?> GetTemplateDetailAsync(string key, int version, CatalogScope scope)
    {
        TemplateCatalogRules.Key(key, version);
        ArgumentNullException.ThrowIfNull(scope);
        lock (_claimLock)
        {
            var row = scope.Allows(WorkflowDraftKinds.Template, key) ? _templates.GetValueOrDefault((key, version)) : null;
            return Task.FromResult(row == null ? null : new CatalogTemplateDetail(CatalogTemplateRules.Summary(row.Version, scope),
                row.PublishedDraft, row.PublishedAtUtc, row.PublishedBy));
        }
    }

    public Task<IReadOnlyList<CatalogStepDescriptor>> ListStepDescriptorsAsync(CatalogScope scope) => Task.FromResult(CatalogTemplateRules.Steps(scope));
}
