using System.Collections.Concurrent;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

public enum WorkflowVersionState { Draft, Published, Deprecated, Retired }

public sealed record WorkflowVersionReference(string Name, int Version);

public enum WorkflowDependencyKind { Child }

public sealed record WorkflowVersionDependency(string Name, int Version, WorkflowDependencyKind Kind = WorkflowDependencyKind.Child);

/// <summary>A child invocation and its authoring position, retained for catalog validation.</summary>
public sealed record WorkflowVersionInvocation(string Name, int Version, string Step, string SourceLocation);

/// <summary>Version identity and contracts are immutable. Code presentation refreshes at registration.</summary>
public sealed record WorkflowVersion(string Name, int Version, string StructureHash,
    string ContractJson, string ContractHash,
    [property: System.Text.Json.Serialization.JsonRequired] WorkflowDescriptor Descriptor,
    [property: System.Text.Json.Serialization.JsonRequired] IReadOnlyList<WorkflowVersionInvocation> Invocations,
    WorkflowVersionState State = WorkflowVersionState.Published, ArtifactDigest? ArtifactDigest = null,
    IReadOnlyList<WorkflowVersionDependency>? Dependencies = null,
    IReadOnlyList<ArtifactDigest>? ArtifactReferences = null,
    WorkflowDraftId? PublishedDraft = null, string? ManifestJson = null)
{
    public DateTime? PublishedAtUtc { get; init; }
    public string? PublishedBy { get; init; }
    public DateTime? StateChangedAtUtc { get; init; }
    public string? StateChangedBy { get; init; }
    public long LifecycleRevision { get; init; }

    public bool Equals(WorkflowVersion? other) => ReferenceEquals(this, other) || other != null &&
        Name == other.Name && Version == other.Version && StructureHash == other.StructureHash &&
        ContractJson == other.ContractJson && ContractHash == other.ContractHash && Descriptor == other.Descriptor && State == other.State &&
        ArtifactDigest == other.ArtifactDigest && PublishedDraft == other.PublishedDraft &&
        ManifestJson == other.ManifestJson && SameValues(Invocations, other.Invocations) &&
        SameValues(Dependencies, other.Dependencies) && SameValues(ArtifactReferences, other.ArtifactReferences);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name); hash.Add(Version); hash.Add(StructureHash);
        hash.Add(ContractJson); hash.Add(ContractHash); hash.Add(Descriptor); hash.Add(State);
        hash.Add(ArtifactDigest); hash.Add(PublishedDraft); hash.Add(ManifestJson);
        AddValues(ref hash, Invocations);
        AddValues(ref hash, Dependencies);
        AddValues(ref hash, ArtifactReferences);
        return hash.ToHashCode();
    }

    private static bool SameValues<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) =>
        ReferenceEquals(left, right) || left != null && right != null && left.SequenceEqual(right);

    private static void AddValues<T>(ref HashCode hash, IReadOnlyList<T>? values)
    {
        hash.Add(values?.Count);
        if (values != null)
            foreach (var value in values) hash.Add(value);
    }
}

/// <summary>Current rows changed after a revision, and the catalog revision observed atomically with them.
/// Rows may have changed more than once; the latest row is returned once. Versions are never deleted.
/// </summary>
public sealed record WorkflowCatalogChanges(long Revision, IReadOnlyList<WorkflowVersion> Versions);

/// <summary>Editable source, independently revisioned from immutable workflow versions.</summary>
public sealed record WorkflowDraft(string Name, int Version, string Kind, string Source, long Revision = 0)
{
    public Guid Generation { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public string? UpdatedBy { get; init; }
    public WorkflowDraftId Id => new(Kind, Name, Version, Generation, Revision);
    public WorkflowVersionState State => WorkflowVersionState.Draft;
}

/// <summary>Durable catalog operations, shared with the instance commit protocol.</summary>
public interface IWorkflowCatalogStore
{
    /// <summary>The artifact source used for current catalog availability; null uses the store's own artifacts.</summary>
    IArtifactStore? CatalogArtifacts { get; set; }
    /// <summary>Lists installed metadata variants and their live host marks, optionally filtered by normalized id.</summary>
    Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null);
    Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query);
    Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query);
    Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope);
    Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query);
    Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query);
    Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope);
    Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query);
    Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query);
    Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope);
    Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query);
    Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null);
    Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest, PackageActivities activities, IArtifactStore artifacts, string? actor = null);
    Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit);
    Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity, int limit);
    Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null);
    Task<WorkflowPackage?> GetPackageAsync(string id, string version);
    Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit);
    Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version);
    Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null);
    Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null);
    Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version);
    Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null);
    /// <summary>Reads the committed store-wide version revision without reading version metadata.</summary>
    Task<long> GetCatalogRevisionAsync();
    /// <summary>Returns current rows changed after <paramref name="afterRevision"/> and a consistent revision watermark.
    /// Every version mutation must stamp its row and advance the revision in the same transaction.
    /// A concurrent write is included or remains visible to a subsequent read from the returned revision.
    /// </summary>
    Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision);
    Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null);
    /// <summary>Publishes a version atomically against the draft's complete identity and deletes the draft.</summary>
    Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts, string? actor = null);
    Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected);
    Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query);
    Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope);
    /// <summary>Joins/starts publication, or returns the abandoned stale attempt when its draft was superseded.</summary>
    Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter);
    Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId);
    Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome);
    Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope);
    Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query);
    Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default);
    /// <summary>Collects old artifacts under the catalog/start lock, retaining catalog references and saved instance pins.</summary>
    Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistent version catalog and the code definitions this host can execute.</summary>
public interface IWorkflowCatalog : IWorkflowCatalogStore, IWorkflowDefinitionSource
{
    Task<IReadOnlyList<CatalogHost>> GetWorkflowVersionHostsAsync(string name, int version, CatalogScope scope);
    Task<IReadOnlyList<CatalogHost>> GetPackageHostsAsync(string id, string version, string? build, CatalogScope scope);
    Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version);
    /// <summary>Monotonically increases after a local catalog mutation or executable registration.</summary>
    long DefinitionRevision { get; }
    Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync();
    Task RegisterWorkflowAsync(WorkflowDefinition definition);
    Task<string?> GetWorkflowStructureHashAsync(string name, int version);
    Task<WorkflowVersion?> GetLatestWorkflowVersionAsync(string name);
    Task RetireWorkflowVersionAsync(string name, int version, CatalogMutationToken expected, string? actor = null);
}

public sealed partial class WorkflowCatalog : IWorkflowCatalog, IDisposable
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
        IArtifactStore? artifacts = null, WorkflowBundleOptions? bundles = null, ILogger<WorkflowCatalog>? logger = null)
    {
        _store = store;
        _artifacts = artifacts;
        var artifactSource = artifacts is RepositoryArtifactStore repositoryArtifacts ? repositoryArtifacts.Repository : artifacts;
        if (artifactSource != null && !ReferenceEquals(artifactSource, store)) store.CatalogArtifacts = artifacts;
        if (bundles != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bundles.CacheDirectory);
            ArgumentOutOfRangeException.ThrowIfNegative(bundles.MaxCachedBundles);
            bundles.ReadLimits.Validate();
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bundles.FetchTimeout, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bundles.CacheLockTimeout, TimeSpan.Zero);
            _bundles = new(artifacts ?? throw new ArgumentException("Bundle loading requires an artifact store"), bundles, logger);
        }
        foreach (var definition in definitions ?? [])
            RegisterWorkflowAsync(definition).GetAwaiter().GetResult();
    }

    public async Task RegisterWorkflowAsync(WorkflowDefinition definition)
    {
        WorkflowDefinitionValidator.Validate(definition);
        var scopes = new WorkflowScopeCatalog(definition);
        var dependencies = WorkflowDefinitionValidator.GetDependencies(definition);
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        var registration = new WorkflowVersion(definition.Name, definition.Version, scopes.StructureHash,
            contract.ToJson(), contract.Hash, WorkflowDescriptor.Of(definition), Dependencies: dependencies,
            Invocations: WorkflowDefinitionValidator.GetInvocations(definition));
        await _registrationLock.WaitAsync();
        try
        {
            var key = (definition.Name, definition.Version);
            if (_registrations.TryGetValue(key, out var existing))
            {
                WorkflowCatalogRules.CheckRegistration(existing, registration);
                // Starts register the same definition repeatedly; only a changed presentation
                // refreshes the stored row, so an unchanged start never takes the catalog lock.
                if (existing.Descriptor != registration.Descriptor)
                {
                    await _store.RegisterWorkflowVersionAsync(registration, _artifacts);
                    _registrations[key] = registration;
                }
            }
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
        try
        {
            if (lease.Definition.Name != name || lease.Definition.Version != version)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Bundle identity differs from the saved workflow identity");
            var registered = await _store.GetWorkflowVersionAsync(name, version)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Bundle has no registered workflow version");
            WorkflowCatalogRules.CheckBundleDefinition(registered, lease.Definition, WorkflowErrorKind.ArtifactMismatch);
            return lease;
        }
        catch { lease.Dispose(); throw; }
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

    public Task RetireWorkflowVersionAsync(string name, int version, CatalogMutationToken expected, string? actor = null) =>
        SetWorkflowVersionStateAsync(name, version, WorkflowVersionState.Retired, expected, actor: actor);
    public Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version) => _store.GetDefinitionAsync(kind, name, version);
    public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null) => _store.GetInstalledPackagesAsync(id);
    public Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query) => _store.ListActivitiesAsync(query);
    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query) => _store.ListActivityVersionsAsync(key, query);
    public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope) => _store.GetActivityAsync(key, version, provider, scope);
    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query) => _store.ListPackagesAsync(query);
    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query) => _store.ListPackageVersionsAsync(id, query);
    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope) => _store.GetPackageDetailAsync(id, version, scope);
    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query) => _store.GetPackageActivitiesAsync(id, version, query);
    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit) => _store.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit);
    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => _store.RegisterPackageAsync(package, activities, artifacts, actor);
    public Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => _store.CompletePackageExtractionAsync(id, version, expectedDigest, activities, artifacts, actor);
    public IArtifactStore? CatalogArtifacts { get => _store.CatalogArtifacts; set => _store.CatalogArtifacts = value; }
    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity, int limit) => _store.GetPackageExtractionPageAsync(extraction, afterIdentity, limit);
    public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null) => _store.GetPackagesAsync(id);
    public Task<WorkflowPackage?> GetPackageAsync(string id, string version) => _store.GetPackageAsync(id, version);
    public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit) => _store.GetPackagePageAsync(id, afterVersion, limit);
    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null) =>
        _store.SaveDefinitionAsync(definition, expected, actor);
    public async Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null)
    {
        await _store.RegisterWorkflowVersionAsync(version, artifacts ?? _artifacts);
        Interlocked.Increment(ref _definitionRevision);
    }
    public async Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts, string? actor = null)
    {
        var published = await _store.PublishDefinitionAsync(draftId, version, artifacts, actor);
        Interlocked.Increment(ref _definitionRevision);
        return published;
    }
    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) => _store.GetWorkflowVersionAsync(name, version);
    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => _store.GetWorkflowVersionsAsync(name);
    public Task<long> GetCatalogRevisionAsync() => _store.GetCatalogRevisionAsync();
    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision) => _store.GetWorkflowVersionChangesAsync(afterRevision);
    public async Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null)
    {
        var row = await _store.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts ?? _artifacts, actor);
        Interlocked.Increment(ref _definitionRevision);
        return row;
    }
    public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected) => _store.DeleteDefinitionAsync(expected);
    public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query) => _store.GetDefinitionsAsync(query);
    public Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query) => _store.ListWorkflowsAsync(query);
    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query) => _store.ListWorkflowVersionsAsync(name, query);
    public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope) => _store.GetWorkflowVersionDetailAsync(name, version, scope);
    public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope) => _store.GetRetirementBlockersAsync(name, version, scope);
    public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter) => _store.BeginPublicationAsync(draftId, hostId, actor, staleAfter);
    public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId) => _store.HeartbeatPublicationAsync(id, hostId);
    public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome) => _store.CompletePublicationAsync(id, hostId, outcome);
    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope) => _store.GetPublicationAsync(id, scope);
    public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query) => _store.GetPublicationsAsync(query);
    public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => _store.PurgePublicationsAsync(retention, batchSize, cancellationToken);
    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default) => _store.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
}

/// <summary>Rules used under each store's catalog/start lock.</summary>
internal static class WorkflowCatalogRules
{
    internal static void CheckManifestInvocations(WorkflowVersion row)
    {
        if (row.ManifestJson == null) return;
        try
        {
            using var manifest = System.Text.Json.JsonDocument.Parse(row.ManifestJson);
            if (manifest.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                manifest.RootElement.TryGetProperty("Invocations", out var inventory) &&
                (inventory.ValueKind != System.Text.Json.JsonValueKind.Array || row.Invocations == null ||
                 !SameInvocations(row.Invocations, System.Text.Json.JsonSerializer.Deserialize<WorkflowVersionInvocation[]>(inventory)!)))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                    $"Workflow '{row.Name}' v{row.Version} invocation inventory differs from its bundle manifest");
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Workflow invocation inventory manifest is invalid", error);
        }
    }

    internal static void CheckBundleDefinition(WorkflowVersion row, WorkflowDefinition definition, WorkflowErrorKind kind)
    {
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        if (row.ContractJson != contract.ToJson() || row.ContractHash != contract.Hash)
            throw new WorkflowEngineException(kind,
                $"Workflow '{row.Name}' v{row.Version} contract differs from its bundle definition");
        if (!SameInvocations(row.Invocations, WorkflowDefinitionValidator.GetInvocations(definition)) ||
            !(row.Dependencies ?? []).SequenceEqual(WorkflowDefinitionValidator.GetDependencies(definition)))
            throw new WorkflowEngineException(kind,
                $"Workflow '{row.Name}' v{row.Version} invocation inventory or dependencies differ from its bundle definition");
    }

    internal static bool SameInvocations(IReadOnlyList<WorkflowVersionInvocation>? left, IReadOnlyList<WorkflowVersionInvocation>? right) =>
        ValidInvocations(left) && ValidInvocations(right) &&
        left!.Select(invocation => (invocation.Name, invocation.Version, invocation.Step))
            .SequenceEqual(right!.Select(invocation => (invocation.Name, invocation.Version, invocation.Step)));

    private static bool ValidInvocations(IReadOnlyList<WorkflowVersionInvocation>? invocations) =>
        invocations != null && invocations.All(invocation => invocation != null && invocation.Version > 0 &&
            !string.IsNullOrWhiteSpace(invocation.Name) && !string.IsNullOrWhiteSpace(invocation.Step) &&
            !string.IsNullOrWhiteSpace(invocation.SourceLocation));

    public static void CheckInvocations(WorkflowVersion candidate, IEnumerable<WorkflowVersion> registered)
    {
        var dependencies = (candidate.Dependencies ?? []).Select(dependency => (dependency.Name, dependency.Version)).ToHashSet();
        if (!candidate.Invocations.Select(invocation => (invocation.Name, invocation.Version)).ToHashSet().IsSubsetOf(dependencies))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow invocation metadata must refer to declared dependencies");
        var versions = registered.Where(row => (row.Name, row.Version) != (candidate.Name, candidate.Version))
            .Append(candidate).ToDictionary(row => (row.Name, row.Version));
        foreach (var caller in versions.Values)
            foreach (var invocation in caller.Invocations)
            {
                // Only edges made resolvable by this registration need to be checked.
                if ((caller.Name, caller.Version) != (candidate.Name, candidate.Version) &&
                    (invocation.Name, invocation.Version) != (candidate.Name, candidate.Version)) continue;
                if (versions.TryGetValue((invocation.Name, invocation.Version), out var callee) &&
                    WorkflowContract.Parse(callee.ContractJson).RequiredScopes.Count != 0)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"{invocation.SourceLocation}: Workflow '{caller.Name}' v{caller.Version}, step '{invocation.Step}' " +
                        $"cannot invoke '{callee.Name}' v{callee.Version}: an independent child cannot supply required scopes");
            }
    }

    public static void CheckPublication(WorkflowDraftId id, WorkflowVersion version, WorkflowDraft? draft, WorkflowVersion? existing)
    {
        CheckPublicationArguments(id, version);
        if (existing != null && existing.PublishedDraft == id)
        {
            // The complete draft identity is the idempotency key: another publisher may have won using
            // a different compiler/reference snapshot. Return its immutable receipt.
            return;
        }
        if (existing != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The workflow identity was already published");
        if (draft?.Id != id)
            throw new CatalogPreconditionException("The definition identity changed");
    }

    public static void CheckPublicationArguments(WorkflowDraftId id, WorkflowVersion version)
    {
        WorkflowPublicationRules.Id(id);
        Validate(version);
        if (id.Kind != WorkflowDraftKinds.Workflow || id.Revision < 1 || version.Name != id.Name || version.Version != id.Version ||
            version.PublishedDraft != id || version.State != WorkflowVersionState.Published ||
            version.ArtifactDigest == null || string.IsNullOrWhiteSpace(version.ManifestJson))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid draft publication metadata");
    }

    public static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateNameWhitespace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, 200);
    }

    // The raw instance protocol also permits unnamed instances; any supplied name must be exact.
    public static void ValidateNameWhitespace(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ValidateUtf16(name);
        if (name.Length != name.Trim().Length)
            throw new ArgumentException("A workflow name cannot have leading or trailing whitespace", nameof(name));
    }

    internal static void ValidateUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[i + 1]))
                throw new ArgumentException("Catalog names and prefixes must contain well-formed UTF-16", nameof(value));
            i++;
        }
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
        WorkflowDescriptor.Validate(row.Descriptor);
        ArgumentOutOfRangeException.ThrowIfLessThan(row.Version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.StructureHash);
        try
        {
            var contract = WorkflowContract.Parse(row.ContractJson);
            if (contract.ToJson() != row.ContractJson || contract.Hash != row.ContractHash)
                throw new ArgumentException("Contract JSON or hash is not canonical");
        }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        { throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow version requires a canonical contract and matching contract hash", error); }
        if (!Enum.IsDefined(row.State)) throw new ArgumentOutOfRangeException(nameof(row.State));
        foreach (var digest in (row.ArtifactReferences ?? []).Concat(row.ArtifactDigest is { } artifact ? [artifact] : []))
            _ = new ArtifactDigest(digest.Value);
        foreach (var dependency in row.Dependencies ?? [])
        {
            ValidateName(dependency.Name);
            ArgumentOutOfRangeException.ThrowIfLessThan(dependency.Version, 1);
            if (!Enum.IsDefined(dependency.Kind)) throw new ArgumentOutOfRangeException(nameof(dependency.Kind));
        }
        if (!ValidInvocations(row.Invocations))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow version requires complete invocation metadata (use an empty list when there are no invocations)");
        foreach (var invocation in row.Invocations)
        {
            ValidateName(invocation.Name);
            ArgumentOutOfRangeException.ThrowIfLessThan(invocation.Version, 1);
            ArgumentException.ThrowIfNullOrWhiteSpace(invocation.Step);
            ArgumentException.ThrowIfNullOrWhiteSpace(invocation.SourceLocation);
        }
    }

    public static void CheckRegistration(WorkflowVersion existing, WorkflowVersion loaded)
    {
        if (existing.StructureHash != loaded.StructureHash)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow {loaded.Name} v{loaded.Version} is already registered with structure hash " +
                $"'{existing.StructureHash}'; loaded structure hash '{loaded.StructureHash}' differs");
        // Source positions are presentation metadata. Each invocation's callee and
        // step identity remain immutable even when the caller's hash is supplied by metadata.
        if (existing.ArtifactDigest != loaded.ArtifactDigest || existing.PublishedDraft != loaded.PublishedDraft ||
            existing.ManifestJson != loaded.ManifestJson ||
            existing.Descriptor.DataType != loaded.Descriptor.DataType || existing.Descriptor.DataTypeName != loaded.Descriptor.DataTypeName ||
            (existing.ArtifactDigest != null && existing.Descriptor != loaded.Descriptor) ||
            existing.ContractJson != loaded.ContractJson || existing.ContractHash != loaded.ContractHash ||
            !SameInvocations(existing.Invocations, loaded.Invocations) ||
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
            .Concat(row.ManifestJson == null ? [] : System.Text.Json.JsonSerializer.Deserialize<ArtifactManifestReferences>(row.ManifestJson)!
                .ActivityPackages.Select(package => package.Digest)))
            .ToHashSet();

    // Retention needs only artifact references. Executable manifest validation belongs to loading.
    private sealed record ArtifactManifestReferences
    {
        public IReadOnlyList<WorkflowPackagePin> ActivityPackages { get; init; } = [];
    }

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
