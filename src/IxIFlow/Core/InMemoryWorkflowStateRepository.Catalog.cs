using System.Text.Json;

namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly Dictionary<(string, int), WorkflowDraft> _definitions = new();
    private readonly Dictionary<(string, int), WorkflowVersion> _versions = new();
    private readonly Dictionary<ArtifactDigest, (ArtifactKind Kind, byte[] Content, DateTime Created)> _artifacts = new();

    private static WorkflowVersion CopyVersion(WorkflowVersion row) =>
        JsonSerializer.Deserialize<WorkflowVersion>(JsonSerializer.Serialize(row))!;

    public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version)
    {
        WorkflowCatalogRules.ValidateName(name);
        lock (_claimLock) return Task.FromResult(_definitions.GetValueOrDefault((name, version)));
    }

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision)
    {
        WorkflowCatalogRules.ValidateName(definition.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(definition.Version, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        lock (_claimLock)
        {
            if (_versions.ContainsKey((definition.Name, definition.Version)))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A published version cannot be edited");
            var revision = _definitions.GetValueOrDefault((definition.Name, definition.Version))?.Revision ?? 0;
            if (revision != expectedRevision)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The definition revision changed");
            var saved = definition with { Revision = checked(revision + 1) };
            _definitions[(saved.Name, saved.Version)] = saved;
            return Task.FromResult(saved);
        }
    }

    public async Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore? artifacts = null)
    {
        await _catalogLock.WaitAsync();
        try
        {
            lock (_claimLock)
            {
                var key = (draftId.Name, draftId.Version);
                var existing = _versions.GetValueOrDefault(key);
                WorkflowCatalogRules.CheckPublication(draftId, version, _definitions.GetValueOrDefault(key), existing);
                if (existing != null) return CopyVersion(existing);
            }
            await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts ?? this);
            lock (_claimLock)
            {
                var key = (draftId.Name, draftId.Version);
                // Draft edits use the instance monitor rather than the catalog gate.
                WorkflowCatalogRules.CheckPublication(draftId, version, _definitions.GetValueOrDefault(key), null);
                foreach (var dependency in version.Dependencies ?? [])
                    WorkflowCatalogRules.CheckStartable(_versions.GetValueOrDefault((dependency.Name, dependency.Version)), dependency.Name, dependency.Version);
                _versions.Add(key, CopyVersion(version));
                _definitions.Remove(key);
                return CopyVersion(version);
            }
        }
        finally { _catalogLock.Release(); }
    }

    public async Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null)
    {
        WorkflowCatalogRules.Validate(version);
        await _catalogLock.WaitAsync();
        try
        {
            await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts ?? this);
            lock (_claimLock)
            {
                if (_versions.TryGetValue((version.Name, version.Version), out var existing))
                    WorkflowCatalogRules.CheckRegistration(existing, version);
                else
                {
                    if (version.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated)
                        foreach (var dependency in version.Dependencies ?? [])
                            if (_versions.TryGetValue((dependency.Name, dependency.Version), out var child))
                                WorkflowCatalogRules.CheckStartable(child, child.Name, child.Version);
                    _versions.Add((version.Name, version.Version), CopyVersion(version));
                }
            }
        }
        finally { _catalogLock.Release(); }
    }

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version)
    {
        WorkflowCatalogRules.ValidateName(name);
        lock (_claimLock) return Task.FromResult(_versions.TryGetValue((name, version), out var row) ? CopyVersion(row) : null);
    }

    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null)
    {
        if (name != null) WorkflowCatalogRules.ValidateName(name);
        lock (_claimLock) return Task.FromResult<IReadOnlyList<WorkflowVersion>>(_versions.Values
            .Where(row => name == null || row.Name == name).Select(CopyVersion).ToArray());
    }

    public async Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null)
    {
        WorkflowCatalogRules.ValidateName(name);
        await _catalogLock.WaitAsync();
        try
        {
            WorkflowVersion row;
            lock (_claimLock)
                row = _versions.GetValueOrDefault((name, version))
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow version not found");
            WorkflowCatalogRules.CheckTransition(row.State, state);
            if (state == WorkflowVersionState.Published)
                await WorkflowCatalogRules.CheckArtifactsAsync(row, artifacts ?? this);
            if (row.State == state) return;
            lock (_claimLock)
            {
                if (state == WorkflowVersionState.Retired)
                {
                    var versions = _versions.Values.ToArray();
                    var reference = new WorkflowVersionReference(name, version);
                    var callers = versions.Where(caller => (caller.Dependencies ?? []).Contains(reference))
                        .Select(caller => caller.Name).ToHashSet(StringComparer.Ordinal);
                    WorkflowCatalogRules.CheckRetirement(row, versions, _instances.Values.Where(instance =>
                        !WorkflowStoreRules.IsTerminal(instance.Status) && callers.Contains(instance.WorkflowName))
                        .Select(instance => new WorkflowVersionReference(instance.WorkflowName, instance.WorkflowVersion)));
                }
                if (state == WorkflowVersionState.Published)
                    foreach (var dependency in row.Dependencies ?? [])
                        if ((dependency.Name, dependency.Version) != (name, version))
                            WorkflowCatalogRules.CheckStartable(_versions.GetValueOrDefault((dependency.Name, dependency.Version)), dependency.Name, dependency.Version);
                _versions[(name, version)] = row with { State = state };
            }
        }
        finally { _catalogLock.Release(); }
    }

    // Called only for first writes and child insertions, while holding the same lock as retirement.
    private void CheckCatalogStart(WorkflowInstance instance)
    {
        WorkflowCatalogRules.ValidateNameWhitespace(instance.WorkflowName);
        if (_versions.TryGetValue((instance.WorkflowName, instance.WorkflowVersion), out var row))
            WorkflowCatalogRules.CheckStartable(row, instance.WorkflowName, instance.WorkflowVersion);
    }

    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var digest = ArtifactDigest.Of(bytes);
        lock (_claimLock)
        {
            var existing = _artifacts.GetValueOrDefault(digest);
            _artifacts[digest] = (existing.Content == null ? kind : existing.Kind, existing.Content ?? bytes, Now);
        }
        return digest;
    }

    public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        lock (_claimLock) return ValueTask.FromResult<Stream?>(_artifacts.TryGetValue(digest, out var artifact)
            ? new MemoryStream(artifact.Content, writable: false) : null);
    }

    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        lock (_claimLock) return ValueTask.FromResult(_artifacts.ContainsKey(digest));
    }

    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);
        lock (_claimLock) _artifacts.Remove(digest);
        return ValueTask.CompletedTask;
    }

    public async Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();
        await _catalogLock.WaitAsync(cancellationToken);
        try
        {
            IReadOnlySet<ArtifactDigest> references;
            DateTime cutoff;
            lock (_claimLock)
            {
                references = WorkflowCatalogRules.ArtifactReferences(_versions.Values);
                cutoff = Now - retention;
            }
            return await artifacts.PurgeUnreferencedAsync(references, cutoff, batchSize, cancellationToken);
        }
        finally { _catalogLock.Release(); }
    }

    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
        int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_claimLock)
        {
            var removed = _artifacts.Where(pair => pair.Value.Created < olderThanUtc && !references.Contains(pair.Key))
                .OrderBy(pair => pair.Value.Created).ThenBy(pair => pair.Key.Value, StringComparer.Ordinal)
                .Take(batchSize).Select(pair => pair.Key).ToArray();
            foreach (var digest in removed)
                if (_artifacts[digest].Created < olderThanUtc) _artifacts.Remove(digest);
            return ValueTask.FromResult(removed.Length);
        }
    }
}
