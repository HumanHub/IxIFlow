using IxIFlow.Templates;

namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<(string, int), TemplateVersionEntry> _templates = [];

    public async Task<PackageHolders> GetPackageHoldersAsync(string id, string version, CatalogScope scope)
    {
        id = WorkflowPackageRules.NormalizeId(id);
        version = WorkflowPackageRules.NormalizeVersion(version);
        ArgumentNullException.ThrowIfNull(scope);
        while (true)
        {
            WorkflowVersion[] versions;
            ArtifactDigest[] pins;
            lock (_claimLock)
            {
                versions = _versions.Values.ToArray();
                pins = _instances.Values.Where(item => item.BundleDigest != null).Select(item => item.BundleDigest!.Value).Distinct().ToArray();
            }
            var orphans = await WorkflowCatalogRules.ReadOrphanPinsAsync(versions, pins, CatalogArtifacts ?? this, default);
            lock (_claimLock)
            {
                var currentPins = _instances.Values.Where(item => item.BundleDigest != null).Select(item => item.BundleDigest!.Value).Distinct().ToArray();
                if (currentPins.Any(pin => !_versions.Values.Any(item => item.ArtifactDigest == pin) && !orphans.ContainsKey(pin))) continue;
                return PackageHolderRules.Read(id, version, scope, _versions.Values.ToArray(), _templates.Values.ToArray(), _packages.Values.ToArray(), currentPins, orphans);
            }
        }
    }

    public Task<TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version)
    {
        TemplateCatalogRules.Key(key, version);
        lock (_claimLock) return Task.FromResult(_templates.GetValueOrDefault((key, version)));
    }

    public Task<IReadOnlyList<TemplateVersionEntry>> GetTemplateVersionsAsync(string? key = null)
    {
        if (key != null) TemplateCatalogRules.Key(key, 1);
        lock (_claimLock) return Task.FromResult<IReadOnlyList<TemplateVersionEntry>>(_templates.Values
            .Where(row => key == null || row.Version.Key == key).OrderBy(row => row.Version.Key, StringComparer.Ordinal)
            .ThenBy(row => row.Version.Version).ToArray());
    }

    public Task<TemplateVersionEntry> RegisterTemplateVersionAsync(TemplateVersion version, IArtifactStore artifacts, string? actor = null) =>
        SaveTemplateAsync(version, artifacts, actor, null);

    public Task<TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId, TemplateVersion version,
        IArtifactStore artifacts, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        TemplateCatalogRules.Publication(draftId, version);
        return SaveTemplateAsync(version, artifacts, actor, draftId);
    }

    private async Task<TemplateVersionEntry> SaveTemplateAsync(TemplateVersion version, IArtifactStore artifacts, string? actor, WorkflowDraftId? draft)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(artifacts);
        WorkflowPublicationRules.Actor(actor);
        await _catalogLock.WaitAsync();
        try
        {
            lock (_claimLock)
            {
                if (_templates.TryGetValue((version.Key, version.Version), out var existing))
                {
                    if (draft == null ? existing.Version.IsSameAs(version) : existing.PublishedDraft == draft) return existing;
                    throw new TemplateVersionConflictException(version.Reference);
                }
                if (draft != null && _definitions.GetValueOrDefault((draft.Kind, draft.Name, draft.Version))?.Id != draft)
                    throw new CatalogPreconditionException("The definition identity changed");
            }
            await TemplateCatalogRules.CheckPackagesAsync(version, GetPackageAsync, artifacts);
            lock (_claimLock)
            {
                if (draft != null && _definitions.GetValueOrDefault((draft.Kind, draft.Name, draft.Version))?.Id != draft)
                    throw new CatalogPreconditionException("The definition identity changed");
                var entry = new TemplateVersionEntry(version, draft, Now, actor);
                _templates.Add((version.Key, version.Version), entry);
                if (draft != null) _definitions.Remove((draft.Kind, draft.Name, draft.Version));
                _catalogRevision = checked(_catalogRevision + 1);
                return entry;
            }
        }
        finally { _catalogLock.Release(); }
    }
}
