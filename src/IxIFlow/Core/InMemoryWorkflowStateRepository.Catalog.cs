using System.Text.Json;

namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<(string, int), WorkflowDraft> _definitions = new();
    private readonly Dictionary<(string, int), WorkflowVersion> _versions = new();
    private readonly Dictionary<ArtifactDigest, (ArtifactKind Kind, byte[] Content, DateTime Created)> _artifacts = new();

    private static WorkflowVersion CopyVersion(WorkflowVersion row) =>
        JsonSerializer.Deserialize<WorkflowVersion>(JsonSerializer.Serialize(row))!;

    public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version)
    {
        lock (_claimLock) return Task.FromResult(_definitions.GetValueOrDefault((name, version)));
    }

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
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

    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version)
    {
        lock (_claimLock)
        {
            var key = (draftId.Name, draftId.Version);
            var existing = _versions.GetValueOrDefault(key);
            WorkflowCatalogRules.CheckPublication(draftId, version, _definitions.GetValueOrDefault(key), existing);
            if (existing != null) return Task.FromResult(CopyVersion(existing));
            foreach (var dependency in version.Dependencies ?? [])
                WorkflowCatalogRules.CheckStartable(_versions.GetValueOrDefault((dependency.Name, dependency.Version)), dependency.Name, dependency.Version);
            _versions.Add(key, CopyVersion(version));
            _definitions.Remove(key);
            return Task.FromResult(CopyVersion(version));
        }
    }

    public Task RegisterWorkflowVersionAsync(WorkflowVersion version)
    {
        WorkflowCatalogRules.Validate(version);
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
        return Task.CompletedTask;
    }

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version)
    {
        lock (_claimLock) return Task.FromResult(_versions.TryGetValue((name, version), out var row) ? CopyVersion(row) : null);
    }

    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null)
    {
        lock (_claimLock) return Task.FromResult<IReadOnlyList<WorkflowVersion>>(_versions.Values
            .Where(row => name == null || row.Name == name).Select(CopyVersion).ToArray());
    }

    public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state)
    {
        lock (_claimLock)
        {
            var row = _versions.GetValueOrDefault((name, version))
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow version not found");
            WorkflowCatalogRules.CheckTransition(row.State, state);
            if (row.State == state) return Task.CompletedTask;
            if (state == WorkflowVersionState.Retired)
                WorkflowCatalogRules.CheckRetirement(row, _versions.Values.ToArray(), _instances.Values);
            if (state == WorkflowVersionState.Published)
                foreach (var dependency in row.Dependencies ?? [])
                    if ((dependency.Name, dependency.Version) != (name, version))
                        WorkflowCatalogRules.CheckStartable(_versions.GetValueOrDefault((dependency.Name, dependency.Version)), dependency.Name, dependency.Version);
            _versions[(name, version)] = row with { State = state };
        }
        return Task.CompletedTask;
    }

    // Called only for first writes and child insertions, while holding the same lock as retirement.
    private void CheckCatalogStart(WorkflowInstance instance)
    {
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
        lock (_claimLock) _artifacts.TryAdd(digest, (kind, bytes, Now));
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

    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();
        artifacts = RepositoryArtifactStore.Unwrap(artifacts);
        lock (_claimLock)
        {
            var references = WorkflowCatalogRules.ArtifactReferences(_versions.Values);
            var cutoff = Now - retention;
            if (!ReferenceEquals(artifacts, this))
                return Task.FromResult(((IArtifactRetentionStore)artifacts)
                    .PurgeUnreferencedAsync(references, cutoff, batchSize, cancellationToken).AsTask().GetAwaiter().GetResult());
            var removed = _artifacts.Where(pair => pair.Value.Created < cutoff && !references.Contains(pair.Key))
                .Take(batchSize).Select(pair => pair.Key).ToArray();
            foreach (var digest in removed) _artifacts.Remove(digest);
            return Task.FromResult(removed.Length);
        }
    }
}
