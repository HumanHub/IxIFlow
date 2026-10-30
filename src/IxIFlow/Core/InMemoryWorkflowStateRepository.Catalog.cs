using System.Text.Json;

namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly Dictionary<(string, string, int), WorkflowDraft> _definitions = new();
    private readonly HashSet<Guid> _draftGenerations = [];
    private readonly Dictionary<(string, int), WorkflowVersion> _versions = new();
    private readonly Dictionary<(string, int), long> _versionRevisions = new();
    private long _catalogRevision;
    private readonly Dictionary<(string, string), WorkflowPackage> _packages = new();
    private readonly Dictionary<ArtifactDigest, (ArtifactKind Kind, byte[] Content, DateTime Created)> _artifacts = new();

    private static WorkflowVersion CopyVersion(WorkflowVersion row) =>
        JsonSerializer.Deserialize<WorkflowVersion>(JsonSerializer.Serialize(row))!;

    public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);

        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<WorkflowPackage>>(_packages.Values
                .Where(package => id == null || package.Id == id.ToLowerInvariant()).Select(WorkflowPackageRules.Copy).ToArray());
    }

    public Task<WorkflowPackage?> GetPackageAsync(string id, string version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);
        WorkflowCatalogRules.ValidateIdentifier(version);

        lock (_claimLock)
            return Task.FromResult(_packages.TryGetValue((id.ToLowerInvariant(), version), out var row)
                ? WorkflowPackageRules.Copy(row)
                : null);
    }

    public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);
        WorkflowCatalogRules.ValidateIdentifier(afterVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<WorkflowPackage>>(_packages.Values
                .Where(row => row.Id == id.ToLowerInvariant() &&
                    (afterVersion == null || string.CompareOrdinal(row.Version, afterVersion) > 0))
                .OrderBy(row => row.Version, StringComparer.Ordinal).Take(limit).Select(WorkflowPackageRules.Copy).ToArray());
    }

    private readonly Dictionary<(string, string), PackageActivities> _packageActivities = [];

    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackageIndexRules.Registration(package, actor);

        return AcceptPackageAsync(package, activities, artifacts, actor, false, null, cancellationToken: cancellationToken);
    }

    public async Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Actor(actor);
        var package = await GetPackageAsync(id, version, cancellationToken: cancellationToken);

        if (package == null || package.Digest != expectedDigest)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package extraction identity changed");

        return await AcceptPackageAsync(package, activities, artifacts, actor, true, null, cancellationToken: cancellationToken);
    }

    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackageIndexRules.Publication(draftId, package, actor);

        return AcceptPackageAsync(package, activities, artifacts, actor, false, draftId, cancellationToken: cancellationToken);
    }

    public async Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn,
        CatalogMutationToken expected, string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);
        WorkflowCatalogRules.ValidateIdentifier(version);
        id = WorkflowPackageRules.NormalizeId(id);
        version = WorkflowPackageRules.NormalizeVersion(version);
        WorkflowPublicationRules.Actor(actor);
        ArgumentNullException.ThrowIfNull(expected);
        await _catalogLock.WaitAsync(cancellationToken);

        try
        {
            lock (_claimLock)
            {
                var row = _packages.GetValueOrDefault((id, version))
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Package version not found");

                PackageIndexRules.Token(row, expected);
                PackageIndexRules.Withdrawal(withdrawn);

                if (row.Withdrawn)
                    return WorkflowPackageRules.Copy(row);

                var revision = checked(_catalogRevision + 1);
                row = row with { Withdrawn = true, WithdrawnAtUtc = Now, WithdrawnBy = actor, LifecycleRevision = revision };
                _packages[(id, version)] = row;
                _catalogRevision = revision;

                return WorkflowPackageRules.Copy(row);
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private async Task<PackageRegistration> AcceptPackageAsync(WorkflowPackage package, PackageActivities activities,
        IArtifactStore artifacts, string? actor, bool completion, WorkflowDraftId? draft, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPackageRules.Validate(package);
        package = WorkflowPackageRules.Copy(package);
        activities = PackageIndexRules.Snapshot(package, activities);
        await _catalogLock.WaitAsync(cancellationToken);

        try
        {
            lock (_claimLock)
            {
                var existing = _packages.GetValueOrDefault((package.Id, package.Version));

                if (draft != null && PackageIndexRules.PublicationWinner(draft, existing,
                        _definitions.GetValueOrDefault((draft.Kind, draft.Name, draft.Version))))
                    return new(WorkflowPackageRules.Copy(existing!),
                        PackageIndexRules.Snapshot(existing!, _packageActivities[(package.Id, package.Version)]), false);

                WorkflowPackageRules.CheckRegistration(existing, package);
            }

            if (draft != null)
                PackageIndexRules.PublicationActivities(draft, activities);

            await PackageIndexRules.CheckArtifactsAsync(package, activities, artifacts, cancellationToken);

            lock (_claimLock)
            {
                var key = (package.Id, package.Version);
                var existing = _packages.GetValueOrDefault(key);

                if (draft != null && PackageIndexRules.PublicationWinner(draft, existing,
                        _definitions.GetValueOrDefault((draft.Kind, draft.Name, draft.Version))))
                    return new(WorkflowPackageRules.Copy(existing!), PackageIndexRules.Snapshot(existing!, _packageActivities[key]), false);

                if (completion && existing == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package disappeared");

                WorkflowPackageRules.CheckRegistration(existing, package);
                var restoring = PackageIndexRules.Restoring(existing, package, completion);

                if (existing?.Withdrawn == true && !restoring)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package is withdrawn");

                foreach (var reference in restoring ? Array.Empty<WorkflowPackageReference>() : activities.Closure ?? [])
                    if (_packages.TryGetValue((reference.Id, reference.Version), out var dependency) &&
                        (dependency.Digest != reference.Digest || dependency.Withdrawn))
                        throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Package closure identity changed");

                var prior = _packageActivities.GetValueOrDefault(key);
                var merged = prior == null ? activities : PackageIndexRules.Merge(prior, activities);

                if (existing == null || restoring || existing.Origin < package.Origin || !PackageIndexRules.Equal(prior!, merged))
                {
                    var revision = checked(_catalogRevision + 1);
                    var now = Now;
                    var stored = PackageIndexRules.Stamp(package, existing, merged, now, actor, revision);

                    if (draft != null)
                        stored = stored with { PublishedDraft = draft, PublishedAtUtc = now, PublishedBy = actor };

                    if (restoring)
                        stored = stored with { Withdrawn = false, WithdrawnAtUtc = null, WithdrawnBy = null };

                    _packages[key] = WorkflowPackageRules.Copy(stored);
                    _packageActivities[key] = merged;

                    if (draft != null)
                        _definitions.Remove((draft.Kind, draft.Name, draft.Version));

                    _catalogRevision = revision;
                }

                var saved = _packages[key];

                return new(WorkflowPackageRules.Copy(saved), PackageIndexRules.Snapshot(saved, _packageActivities[key]), existing == null);
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity,
        int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        WorkflowCatalogRules.ValidateIdentifier(afterIdentity);

        if (!Enum.IsDefined(extraction))
            throw new ArgumentException("Invalid extraction state");

        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<WorkflowPackage>>(_packages.Values
                .Where(row => _packageActivities[(row.Id, row.Version)].Extraction == extraction &&
                    (afterIdentity == null || string.CompareOrdinal(row.Id + "@" + row.Version, afterIdentity) > 0))
                .OrderBy(row => row.Id + "@" + row.Version, StringComparer.Ordinal).Take(limit).Select(WorkflowPackageRules.Copy)
                .ToArray());
    }

    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(afterIdentity);
        missingPackageId = WorkflowPackageRules.NormalizeId(missingPackageId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);

        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<WorkflowPackage>>(_packages.Values
                .Where(row => _packageActivities[(row.Id, row.Version)].Extraction == PackageExtraction.Pending &&
                    _packageActivities[(row.Id, row.Version)].MissingPackageIds.Contains(missingPackageId, StringComparer.Ordinal) &&
                    (afterIdentity == null || string.CompareOrdinal(row.Id + "@" + row.Version, afterIdentity) > 0))
                .OrderBy(row => row.Id + "@" + row.Version, StringComparer.Ordinal).Take(limit).Select(WorkflowPackageRules.Copy)
                .ToArray());
    }

    public Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Key(kind, name, version);

        lock (_claimLock)
            return Task.FromResult(_definitions.GetValueOrDefault((kind, name, version)));
    }

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Save(definition, expected, actor);

        lock (_claimLock)
        {
            if (definition.Kind == WorkflowDraftKinds.Template && _templates.ContainsKey((definition.Name, definition.Version)) ||
                definition.Kind == WorkflowDraftKinds.Workflow && _versions.ContainsKey((definition.Name, definition.Version)) ||
                definition.Kind == WorkflowDraftKinds.Activity && _packages.ContainsKey((definition.Name,
                    definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0.0")))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A published identity cannot be edited");

            var key = (definition.Kind, definition.Name, definition.Version);
            var current = _definitions.GetValueOrDefault(key);

            if (expected == null ? current != null : current?.Id != expected)
                throw new CatalogPreconditionException("The definition identity changed");

            var generation = expected?.Generation ?? Guid.NewGuid();

            if (expected == null)
                while (!_draftGenerations.Add(generation))
                    generation = Guid.NewGuid();

            var saved = definition with
            {
                Generation = generation, Revision = checked((expected?.Revision ?? 0) + 1), UpdatedAtUtc = Now, UpdatedBy = actor
            };

            _definitions[key] = saved;
            _catalogRevision = checked(_catalogRevision + 1);

            return Task.FromResult(saved);
        }
    }

    public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Id(expected);

        lock (_claimLock)
        {
            var key = (expected.Kind, expected.Name, expected.Version);

            if (_definitions.GetValueOrDefault(key)?.Id != expected)
                throw new CatalogPreconditionException("The definition identity changed");

            _definitions.Remove(key);
            _catalogRevision = checked(_catalogRevision + 1);

            return Task.FromResult(true);
        }
    }

    public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Query(query);
        var filter = CatalogPaging.Hash(new { query.Scope.Hash, query.Kind, query.NamePrefix });
        var key = CatalogPaging.Decode(query.After, filter, 3, query.Limit);

        int? afterVersion = key == null ? null :
            int.TryParse(key[2], out var parsed) ? parsed : throw new ArgumentException("Invalid draft cursor");

        lock (_claimLock)
        {
            var rows = _definitions.Values.Where(row => query.Scope.Allows(row.Kind, row.Name) &&
                    (query.Kind == null || row.Kind == query.Kind) &&
                    (query.NamePrefix == null || row.Name.StartsWith(query.NamePrefix, StringComparison.Ordinal)))
                .OrderBy(row => row.Kind, StringComparer.Ordinal).ThenBy(row => row.Name, CatalogPaging.DraftNames)
                .ThenBy(row => row.Version)
                .Where(row => key == null || string.CompareOrdinal(row.Kind, key[0]) > 0 || row.Kind == key[0] &&
                    (CatalogPaging.CompareDraftNames(row.Name, key[1]) > 0 || row.Name == key[1] && row.Version > afterVersion))
                .Take(query.Limit + 1).ToArray();

            var page = rows.Take(query.Limit).Select(row => new WorkflowDraftSummary(row.Kind, row.Name, row.Version, row.Generation,
                row.Revision, row.Source.Length, row.UpdatedAtUtc!.Value, row.UpdatedBy)).ToArray();

            var last = page.LastOrDefault();

            return Task.FromResult(new CatalogPage<WorkflowDraftSummary>(page, rows.Length > query.Limit && last != null
                ? CatalogPaging.Encode(filter, last.Kind, last.Name,
                    last.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : null, _catalogRevision));
        }
    }

    public async Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowPublicationRules.Actor(actor);
        ArgumentNullException.ThrowIfNull(artifacts);
        await _catalogLock.WaitAsync(cancellationToken);

        try
        {
            lock (_claimLock)
            {
                var key = (draftId.Name, draftId.Version);
                var existing = _versions.GetValueOrDefault(key);

                WorkflowCatalogRules.CheckPublication(draftId, version,
                    _definitions.GetValueOrDefault((draftId.Kind, draftId.Name, draftId.Version)), existing);

                if (existing != null)
                    return CopyVersion(existing);
            }

            await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts, cancellationToken);

            lock (_claimLock)
            {
                var key = (draftId.Name, draftId.Version);

                // Draft edits use the instance monitor rather than the catalog gate.
                WorkflowCatalogRules.CheckPublication(draftId, version,
                    _definitions.GetValueOrDefault((draftId.Kind, draftId.Name, draftId.Version)), null);

                WorkflowCatalogRules.CheckInvocations(version, _versions.Values);
                WorkflowCatalogRules.CheckAuthoringPackages(version, _packages.Values, _versions.Values);

                foreach (var dependency in version.Dependencies ?? [])
                    WorkflowCatalogRules.CheckStartable(_versions.GetValueOrDefault((dependency.Name, dependency.Version)), dependency.Name,
                        dependency.Version);

                var revision = checked(_catalogRevision + 1);

                var copy = CopyVersion(version) with
                {
                    PublishedAtUtc = Now, PublishedBy = actor,
                    StateChangedAtUtc = Now, StateChangedBy = actor, LifecycleRevision = revision
                };

                _versions.Add(key, copy);
                _versionRevisions[key] = _catalogRevision = revision;
                _definitions.Remove((draftId.Kind, draftId.Name, draftId.Version));

                return CopyVersion(copy);
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public async Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.Validate(version);
        await _catalogLock.WaitAsync(cancellationToken);

        try
        {
            await WorkflowCatalogRules.CheckArtifactsAsync(version, artifacts ?? this, cancellationToken);

            lock (_claimLock)
            {
                if (_versions.TryGetValue((version.Name, version.Version), out var existing))
                    WorkflowCatalogRules.CheckRegistration(existing, version);

                WorkflowCatalogRules.CheckInvocations(version, _versions.Values);

                if (existing != null && existing.ArtifactDigest == null && existing.Descriptor != version.Descriptor)
                {
                    var key = (version.Name, version.Version);
                    _versions[key] = CopyVersion(existing with { Descriptor = version.Descriptor });
                    _versionRevisions[key] = _catalogRevision = checked(_catalogRevision + 1);
                }

                if (existing == null)
                {
                    if (version.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated)
                        foreach (var dependency in version.Dependencies ?? [])
                            if (_versions.TryGetValue((dependency.Name, dependency.Version), out var child))
                                WorkflowCatalogRules.CheckStartable(child, child.Name, child.Version);

                    var revision = checked(_catalogRevision + 1);

                    var copy = CopyVersion(version) with
                    {
                        PublishedDraft = null, PublishedAtUtc = null, PublishedBy = null,
                        StateChangedAtUtc = Now, StateChangedBy = null, LifecycleRevision = revision
                    };

                    _versions.Add((version.Name, version.Version), copy);
                    _versionRevisions[(version.Name, version.Version)] = _catalogRevision = revision;
                }
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateName(name);

        lock (_claimLock)
            return Task.FromResult(_versions.TryGetValue((name, version), out var row) ? CopyVersion(row) : null);
    }

    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (name != null)
            WorkflowCatalogRules.ValidateName(name);

        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<WorkflowVersion>>(_versions.Values
                .Where(row => name == null || row.Name == name).Select(CopyVersion).ToArray());
    }

    public Task<long> GetCatalogRevisionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_claimLock)
            return Task.FromResult(_catalogRevision);
    }

    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);

        // Copy rows and capture the watermark under the same monitor as every version mutation.
        lock (_claimLock)
            return Task.FromResult(new WorkflowCatalogChanges(_catalogRevision,
                _versionRevisions.Where(pair => pair.Value > afterRevision)
                    .Select(pair => CopyVersion(_versions[pair.Key])).ToArray()));
    }

    public async Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state,
        CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateName(name);
        WorkflowPublicationRules.Actor(actor);
        await _catalogLock.WaitAsync(cancellationToken);

        try
        {
            WorkflowVersion row;

            lock (_claimLock)
                row = _versions.GetValueOrDefault((name, version))
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow version not found");

            WorkflowPublicationRules.Token(row, expected);
            WorkflowCatalogRules.CheckTransition(row.State, state);

            if (state == WorkflowVersionState.Published)
                await WorkflowCatalogRules.CheckArtifactsAsync(row, artifacts ?? this, cancellationToken);

            lock (_claimLock)
            {
                row = _versions[(name, version)];
                WorkflowPublicationRules.Token(row, expected);

                if (row.State == state)
                    return CopyVersion(row);

                if (state == WorkflowVersionState.Retired)
                {
                    var callers = _versions.Values.Where(caller =>
                        (caller.Dependencies ?? []).Any(edge => edge.Name == name && edge.Version == version)).ToArray();

                    WorkflowCatalogRules.CheckRetirement(row, callers, null);

                    var pairs = callers.Where(caller => WorkflowCatalogRules.HasChildDependency(caller, name, version))
                        .Select(caller => new WorkflowVersionReference(caller.Name, caller.Version)).ToHashSet();

                    WorkflowCatalogRules.CheckRetirement(row, callers,
                        FindUnfinishedCaller(_instances.Select(entry => entry.Value), pairs));
                }

                if (state == WorkflowVersionState.Published)
                    foreach (var dependency in row.Dependencies ?? [])
                        if ((dependency.Name, dependency.Version) != (name, version))
                            WorkflowCatalogRules.CheckStartable(_versions.GetValueOrDefault((dependency.Name, dependency.Version)),
                                dependency.Name, dependency.Version);

                var revision = checked(_catalogRevision + 1);

                _versions[(name, version)] = row with
                {
                    State = state, LifecycleRevision = revision, StateChangedAtUtc = Now, StateChangedBy = actor
                };

                _versionRevisions[(name, version)] = _catalogRevision = revision;

                return CopyVersion(_versions[(name, version)]);
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    internal static WorkflowVersionReference? FindUnfinishedCaller(IEnumerable<WorkflowInstance> instances,
        IReadOnlySet<WorkflowVersionReference> callers)
    {
        if (callers.Count == 0)
            return null;

        foreach (var instance in instances)
        {
            if (WorkflowStoreRules.IsTerminal(instance.Status))
                continue;

            var pair = new WorkflowVersionReference(instance.WorkflowName, instance.WorkflowVersion);

            if (callers.Contains(pair))
                return pair;
        }

        return null;
    }

    // Called only for first writes and child insertions, while holding the same lock as retirement.
    private void CheckCatalogStart(WorkflowInstance instance)
    {
        WorkflowCatalogRules.ValidateNameWhitespace(instance.WorkflowName);

        if (_versions.TryGetValue((instance.WorkflowName, instance.WorkflowVersion), out var row))
            WorkflowCatalogRules.CheckStartable(row, instance.WorkflowName, instance.WorkflowVersion);

        WorkflowCatalogRules.CheckInstancePin(row, instance);
    }

    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));

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

        lock (_claimLock)
            return ValueTask.FromResult<Stream?>(_artifacts.TryGetValue(digest, out var artifact)
                ? new MemoryStream(artifact.Content, writable: false)
                : null);
    }

    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);

        lock (_claimLock)
            return ValueTask.FromResult(_artifacts.ContainsKey(digest));
    }

    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = new ArtifactDigest(digest.Value);

        lock (_claimLock)
            _artifacts.Remove(digest);

        return ValueTask.CompletedTask;
    }

    public async Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            WorkflowVersion[] versions;
            ArtifactDigest[] pins;

            lock (_claimLock)
            {
                versions = _versions.Values.Select(CopyVersion).ToArray();

                pins = _instances.Values.Where(instance => instance.BundleDigest != null).Select(instance => instance.BundleDigest!.Value)
                    .Distinct().ToArray();
            }

            var prepared = await WorkflowCatalogRules.ReadOrphanPinsAsync(versions, pins, artifacts, cancellationToken);
            await _catalogLock.WaitAsync(cancellationToken);

            try
            {
                IReadOnlySet<ArtifactDigest>? references;
                DateTime cutoff;

                lock (_claimLock)
                {
                    references = WorkflowCatalogRules.RetentionReferences(_versions.Values.ToArray(),
                        _instances.Values.Where(instance => instance.BundleDigest != null).Select(instance => instance.BundleDigest!.Value)
                            .Distinct().ToArray(),
                        _packages.Values.ToArray(), prepared, _templates.Values.ToArray());

                    cutoff = Now - retention;
                }

                if (references == null)
                    continue;

                return await artifacts.PurgeUnreferencedAsync(references, cutoff, batchSize, cancellationToken);
            }
            finally
            {
                _catalogLock.Release();
            }
        }
    }

    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
        int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_claimLock)
        {
            var removed = 0;

            // Dictionary removal does not invalidate enumeration. The monitor excludes refreshes.
            foreach (var (digest, artifact) in _artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (artifact.Created >= olderThanUtc || references.Contains(digest))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                _artifacts.Remove(digest);

                if (++removed == batchSize)
                    break;
            }

            return ValueTask.FromResult(removed);
        }
    }
}