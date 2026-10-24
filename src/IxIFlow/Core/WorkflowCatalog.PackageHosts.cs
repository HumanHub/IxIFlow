namespace IxIFlow.Core;

public sealed partial class WorkflowCatalog
{
    public async Task<IReadOnlyList<CatalogHost>> GetWorkflowVersionHostsAsync(string name, int version, CatalogScope scope)
    {
        WorkflowCatalogRules.ValidateName(name);
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.Allows(WorkflowDraftKinds.Workflow, name)) return [];
        var row = await _store.GetWorkflowVersionAsync(name, version);
        if (row == null || _store is not IWorkflowStateRepository registry) return [];
        var key = $"workflow:{name}@{version}";
        return (await registry.GetLiveMembersAsync()).Where(member => member.Kind == MemberKind.Host &&
            member.Capabilities.TryGetValue(key, out var hash) && hash == row.StructureHash)
            .OrderBy(member => member.Id).Select(member => new CatalogHost(member.Id, member.Name, member.HeartbeatAtUtc, member.EngineVersion)).ToArray();
    }

    public Task<IReadOnlyList<CatalogHost>> GetPackageHostsAsync(string id, string version, string? build, CatalogScope scope) =>
        _store is IWorkflowStateRepository registry
            ? CatalogLocalHosts.ReadAsync(_store, registry, WorkflowPackageRules.NormalizeId(id), version, build, scope)
            : Task.FromResult<IReadOnlyList<CatalogHost>>([]);
}
