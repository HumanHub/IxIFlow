using System.Collections.Concurrent;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public enum WorkflowVersionState { Draft, Published, Deprecated, Retired }

public sealed record WorkflowVersionReference(string Name, int Version);

/// <summary>Immutable version metadata. Only State may change after registration.</summary>
public sealed record WorkflowVersion(string Name, int Version, string StructureHash,
    WorkflowVersionState State = WorkflowVersionState.Published, ArtifactDigest? ArtifactDigest = null,
    IReadOnlyList<WorkflowVersionReference>? Dependencies = null,
    IReadOnlyList<ArtifactDigest>? ArtifactReferences = null,
    long? PublishedDraftRevision = null, string? ManifestJson = null);

/// <summary>Editable source, independently revisioned from immutable workflow versions.</summary>
public sealed record WorkflowDraft(string Name, int Version, string Kind, string Source, long Revision = 0)
{
    public WorkflowDraftId Id => new(Name, Version, Revision);
    public WorkflowVersionState State => WorkflowVersionState.Draft;
}

/// <summary>Identifies the exact source revision being published, including on retries after deletion.</summary>
public sealed record WorkflowDraftId(string Name, int Version, long Revision);

/// <summary>Durable catalog operations, shared with the instance commit protocol.</summary>
public interface IWorkflowCatalogStore
{
    Task RegisterPackageAsync(WorkflowPackage package, IArtifactStore artifacts);
    Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null);
    Task<WorkflowPackage?> GetPackageAsync(string id, string version);
    Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit);
    Task<WorkflowDraft?> GetDefinitionAsync(string name, int version);
    Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision);
    Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null);
    Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version);
    Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null);
    Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null);
    /// <summary>Publishes a version atomically against the draft's revision and deletes the draft.</summary>
    Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore? artifacts = null);
    /// <summary>Collects old artifacts under the catalog/start lock, retaining catalog references and saved instance pins.</summary>
    Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistent version catalog and the code definitions this host can execute.</summary>
public interface IWorkflowCatalog : IWorkflowCatalogStore, IWorkflowDefinitionSource
{
    Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version);
    /// <summary>Monotonically increases after a local catalog mutation or executable registration.</summary>
    long DefinitionRevision { get; }
    Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync();
    Task RegisterWorkflowAsync(WorkflowDefinition definition);
    Task<string?> GetWorkflowStructureHashAsync(string name, int version);
    Task<WorkflowVersion?> GetLatestWorkflowVersionAsync(string name);
    Task RetireWorkflowVersionAsync(string name, int version);
}

public sealed class WorkflowCatalog : IWorkflowCatalog, IDisposable
{
    private readonly IWorkflowCatalogStore _store;
    private readonly ConcurrentDictionary<(string, int), WorkflowDefinition> _definitions = new();
    private readonly ConcurrentDictionary<(string, int), WorkflowVersion> _registrations = new();
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private long _definitionRevision;
    private readonly IArtifactStore? _artifacts;
    private readonly WorkflowBundleLoader? _bundles;

    public long DefinitionRevision => Interlocked.Read(ref _definitionRevision);

    public WorkflowCatalog() : this(new InMemoryWorkflowStateRepository()) { }
    public WorkflowCatalog(IEnumerable<WorkflowDefinition> definitions)
        : this(new InMemoryWorkflowStateRepository(), definitions) { }

    public WorkflowCatalog(IWorkflowCatalogStore store, IEnumerable<WorkflowDefinition>? definitions = null,
        IArtifactStore? artifacts = null, WorkflowBundleOptions? bundles = null)
    {
        _store = store;
        _artifacts = artifacts;
        if (bundles != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bundles.CacheDirectory);
            ArgumentOutOfRangeException.ThrowIfNegative(bundles.MaxCachedBundles);
            bundles.ReadLimits.Validate();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bundles.FetchTimeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bundles.CacheLockTimeout, TimeSpan.Zero);
            _bundles = new(artifacts ?? throw new ArgumentException("Bundle loading requires an artifact store"), bundles);
        }
        foreach (var definition in definitions ?? [])
            RegisterWorkflowAsync(definition).GetAwaiter().GetResult();
    }

    public async Task RegisterWorkflowAsync(WorkflowDefinition definition)
    {
        WorkflowDefinitionValidator.Validate(definition);
        var scopes = new WorkflowScopeCatalog(definition);
        var dependencies = scopes.AllSteps.Select(pair => pair.Value)
            .Where(step => step.WorkflowName != null && step.WorkflowVersion != null)
            .Select(step => new WorkflowVersionReference(step.WorkflowName!, step.WorkflowVersion!.Value))
            .Distinct().OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Version).ToArray();
        var registration = new WorkflowVersion(definition.Name, definition.Version, scopes.StructureHash,
            Dependencies: dependencies);
        await _registrationLock.WaitAsync();
        try
        {
            var key = (definition.Name, definition.Version);
            if (_registrations.TryGetValue(key, out var existing))
                WorkflowCatalogRules.CheckRegistration(existing, registration);
            else
            {
                await _store.RegisterWorkflowVersionAsync(registration, _artifacts);
                _registrations.TryAdd(key, registration);
                _definitions.TryAdd(key, definition);
                Interlocked.Increment(ref _definitionRevision);
            }
        }
        finally { _registrationLock.Release(); }
    }

    public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync() =>
        Task.FromResult<IReadOnlyList<WorkflowDefinition>>(_definitions.Values.ToArray());

    public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version)
    {
        WorkflowCatalogRules.ValidateName(name);
        return Task.FromResult(_definitions.GetValueOrDefault((name, version)));
    }

    public async Task<WorkflowDefinitionLease> AcquireWorkflowDefinitionAsync(string name, int version, ArtifactDigest? bundleDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateName(name);
        if (bundleDigest == null)
            return new(await GetWorkflowDefinitionAsync(name, version)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Workflow definition not found: {name} v{version}; it is unavailable on this host"));
        if (_bundles == null)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "This host has no bundle cache configured");
        var lease = await _bundles.AcquireAsync(bundleDigest.Value, cancellationToken);
        if (lease.Definition.Name == name && lease.Definition.Version == version) return lease;
        lease.Dispose();
        throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Bundle identity differs from the saved workflow identity");
    }

    public void Dispose() => _bundles?.Dispose();

    public async Task<WorkflowDefinitionLease> ResolveWorkflowDefinitionAsync(string name, int version, CancellationToken cancellationToken = default) =>
        await AcquireWorkflowDefinitionAsync(name, version, (await _store.GetWorkflowVersionAsync(name, version))?.ArtifactDigest, cancellationToken);

    public async Task<string?> GetWorkflowStructureHashAsync(string name, int version) =>
        _registrations.TryGetValue((name, version), out var local) ? local.StructureHash
            : (await _store.GetWorkflowVersionAsync(name, version))?.StructureHash;

    public async Task<WorkflowVersion?> GetLatestWorkflowVersionAsync(string name) =>
        (await _store.GetWorkflowVersionsAsync(name)).Where(row => row.State == WorkflowVersionState.Published)
        .MaxBy(row => row.Version);

    public Task RetireWorkflowVersionAsync(string name, int version) =>
        SetWorkflowVersionStateAsync(name, version, WorkflowVersionState.Retired);
    public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) => _store.GetDefinitionAsync(name, version);
    public Task RegisterPackageAsync(WorkflowPackage package, IArtifactStore artifacts) => _store.RegisterPackageAsync(package, artifacts);
    public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null) => _store.GetPackagesAsync(id);
    public Task<WorkflowPackage?> GetPackageAsync(string id, string version) => _store.GetPackageAsync(id, version);
    public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit) => _store.GetPackagePageAsync(id, afterVersion, limit);
    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) =>
        _store.SaveDefinitionAsync(definition, expectedRevision);
    public async Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null)
    {
        await _store.RegisterWorkflowVersionAsync(version, artifacts ?? _artifacts);
        Interlocked.Increment(ref _definitionRevision);
    }
    public async Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore? artifacts = null)
    {
        var published = await _store.PublishDefinitionAsync(draftId, version, artifacts ?? _artifacts);
        Interlocked.Increment(ref _definitionRevision);
        return published;
    }
    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) => _store.GetWorkflowVersionAsync(name, version);
    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => _store.GetWorkflowVersionsAsync(name);
    public async Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null)
    {
        await _store.SetWorkflowVersionStateAsync(name, version, state, artifacts ?? _artifacts);
        Interlocked.Increment(ref _definitionRevision);
    }
    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default) => _store.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
}

/// <summary>Rules used under each store's catalog/start lock.</summary>
internal static class WorkflowCatalogRules
{
    public static void CheckPublication(WorkflowDraftId id, WorkflowVersion version, WorkflowDraft? draft, WorkflowVersion? existing)
    {
        Validate(version);
        if (id.Revision < 1 || version.Name != id.Name || version.Version != id.Version ||
            version.PublishedDraftRevision != id.Revision || version.State != WorkflowVersionState.Published ||
            version.ArtifactDigest == null || string.IsNullOrWhiteSpace(version.ManifestJson))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid draft publication metadata");
        if (existing != null && existing.PublishedDraftRevision == id.Revision)
        {
            // A revision is the idempotency key: another publisher may have won using
            // a different compiler/reference snapshot. Return its immutable receipt.
            return;
        }
        if (draft?.Revision != id.Revision || existing != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The definition revision changed or was already published");
    }

    public static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateNameWhitespace(name);
    }

    // The raw instance protocol also permits unnamed instances; any supplied name must be exact.
    public static void ValidateNameWhitespace(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length != name.Trim().Length)
            throw new ArgumentException("A workflow name cannot have leading or trailing whitespace", nameof(name));
    }

    public static async Task CheckArtifactsAsync(WorkflowVersion row, IArtifactStore artifacts)
    {
        foreach (var digest in ArtifactReferences([row]))
            if (!await artifacts.ExistsAsync(digest))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Workflow {row.Name} v{row.Version} references missing artifact {digest}");
    }

    public static void Validate(WorkflowVersion row)
    {
        ValidateName(row.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(row.Version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.StructureHash);
        if (!Enum.IsDefined(row.State)) throw new ArgumentOutOfRangeException(nameof(row.State));
        foreach (var digest in (row.ArtifactReferences ?? []).Concat(row.ArtifactDigest is { } artifact ? [artifact] : []))
            _ = new ArtifactDigest(digest.Value);
        foreach (var dependency in row.Dependencies ?? [])
        {
            ValidateName(dependency.Name);
            ArgumentOutOfRangeException.ThrowIfLessThan(dependency.Version, 1);
        }
    }

    public static void CheckRegistration(WorkflowVersion existing, WorkflowVersion loaded)
    {
        if (existing.StructureHash != loaded.StructureHash)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow {loaded.Name} v{loaded.Version} is already registered with structure hash " +
                $"'{existing.StructureHash}'; loaded structure hash '{loaded.StructureHash}' differs");
        if (existing.ArtifactDigest != loaded.ArtifactDigest || existing.PublishedDraftRevision != loaded.PublishedDraftRevision ||
            existing.ManifestJson != loaded.ManifestJson ||
            !(existing.Dependencies ?? []).SequenceEqual(loaded.Dependencies ?? []) ||
            !(existing.ArtifactReferences ?? []).SequenceEqual(loaded.ArtifactReferences ?? []))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow version metadata is immutable");
    }

    public static void CheckTransition(WorkflowVersionState from, WorkflowVersionState to)
    {
        if (!Enum.IsDefined(to)) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        if ((from, to) is (WorkflowVersionState.Draft, WorkflowVersionState.Published or WorkflowVersionState.Retired)
            or (WorkflowVersionState.Published, WorkflowVersionState.Deprecated or WorkflowVersionState.Retired)
            or (WorkflowVersionState.Deprecated, WorkflowVersionState.Retired)) return;
        throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, $"Cannot change workflow version from {from} to {to}");
    }

    public static void CheckStartable(WorkflowVersion? row, string name, int version)
    {
        ValidateName(name);
        if (row == null || row.State is WorkflowVersionState.Draft or WorkflowVersionState.Retired)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                row?.State == WorkflowVersionState.Retired
                    ? $"Workflow {name} v{version} is deactivated (Retired)"
                    : $"Workflow {name} v{version} is unavailable for new starts ({row?.State.ToString() ?? "missing"})");
    }

    public static void CheckInstancePin(WorkflowVersion? version, WorkflowInstance instance)
    {
        if (version?.ArtifactDigest != instance.BundleDigest)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "The instance bundle pin differs from its immutable catalog version");
    }

    public static void CheckRetirement(WorkflowVersion target, IReadOnlyList<WorkflowVersion> callers,
        WorkflowVersionReference? unfinishedCaller)
    {
        // Published and Deprecated callers both keep their pinned dependencies startable.
        var caller = callers.FirstOrDefault(row => (row.Name, row.Version) != (target.Name, target.Version) &&
            row.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated);
        if (caller != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Workflow {target.Name} v{target.Version} is required by caller {caller.Name} v{caller.Version}");
        if (unfinishedCaller != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Workflow {target.Name} v{target.Version} has unfinished parent instances of {unfinishedCaller.Name} v{unfinishedCaller.Version}");
    }

    public static IReadOnlySet<ArtifactDigest> ArtifactReferences(IEnumerable<WorkflowVersion> rows) =>
        rows.SelectMany(row => (row.ArtifactReferences ?? []).Concat(row.ArtifactDigest is { } digest ? [digest] : [])
            .Concat(row.ManifestJson == null ? [] : System.Text.Json.JsonSerializer.Deserialize<WorkflowBundleManifest>(row.ManifestJson)!
                .ActivityPackages.Select(package => package.Digest)))
            .ToHashSet();

    internal sealed record OrphanPin(IReadOnlyList<ArtifactDigest> Packages, string? Error = null);

    internal static async Task<IReadOnlyDictionary<ArtifactDigest, OrphanPin>> ReadOrphanPinsAsync(IReadOnlyList<WorkflowVersion> versions,
        IReadOnlyList<ArtifactDigest> instancePins, IArtifactStore artifacts, CancellationToken token)
    {
        var catalogPins = versions.Where(row => row.ArtifactDigest != null).Select(row => row.ArtifactDigest!.Value).ToHashSet();
        var result = new Dictionary<ArtifactDigest, OrphanPin>();
        foreach (var digest in instancePins.Distinct().Where(pin => !catalogPins.Contains(pin)))
        {
            try
            {
                await using var archive = await artifacts.OpenAsync(digest, token)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Artifact unavailable");
                var bundle = await WorkflowBundleArchive.ReadAsync(archive, digest, token);
                result.Add(digest, new(bundle.Manifest.ActivityPackages.Select(package => package.Digest).ToArray()));
            }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
            {
                result.Add(digest, new([], error is WorkflowEngineException engine ? engine.Kind.ToString() : "Unreadable"));
            }
        }
        return result;
    }

    // Called under the catalog lock after re-reading versions and pins. A newly orphaned pin
    // asks the caller to retry preparation outside the lock; no artifact I/O happens here.
    internal static IReadOnlySet<ArtifactDigest>? RetentionReferences(IReadOnlyList<WorkflowVersion> versions,
        IReadOnlyList<ArtifactDigest> instancePins, IReadOnlyList<WorkflowPackage> packages,
        IReadOnlyDictionary<ArtifactDigest, OrphanPin> prepared)
    {
        var references = ArtifactReferences(versions).Concat(instancePins).ToHashSet();
        var catalogPins = versions.Where(row => row.ArtifactDigest != null).Select(row => row.ArtifactDigest!.Value).ToHashSet();
        foreach (var digest in instancePins.Distinct().Where(pin => !catalogPins.Contains(pin)))
        {
            if (!prepared.TryGetValue(digest, out var pin)) return null;
            references.UnionWith(pin.Packages);
            if (pin.Error != null)
            {
                // Unknown dependencies cannot be collected safely. Preserve package identities'
                // content, but still collect unrelated abandoned bundles. Do not stop the pass.
                references.UnionWith(packages.Select(package => package.Digest));
                Runtime.Telemetry.ArtifactRetentionUnreadablePins.Add(1, new KeyValuePair<string, object?>("reason", pin.Error));
                System.Diagnostics.Trace.TraceWarning($"Artifact retention: unreadable orphan instance pin {digest} ({pin.Error}); package content retained conservatively");
            }
        }
        return references;
    }
}
