namespace IxIFlow.Core;

public sealed partial class WorkflowCatalog
{
    public Task<IReadOnlyList<CatalogHost>> GetPackageHostsAsync(string id, string version, string? build, CatalogScope scope) =>
        _store is IWorkflowStateRepository registry
            ? CatalogLocalHosts.ReadAsync(_store, registry, WorkflowPackageRules.NormalizeId(id), version, build, scope)
            : Task.FromResult<IReadOnlyList<CatalogHost>>([]);
}
