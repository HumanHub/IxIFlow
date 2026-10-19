using System.Collections.Concurrent;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public enum WorkflowVersionState { Draft, Published, Deprecated, Retired }

public sealed record WorkflowVersionReference(string Name, int Version);

/// <summary>Immutable version metadata. Only State may change after registration.</summary>
public sealed record WorkflowVersion(string Name, int Version, string StructureHash,
    WorkflowVersionState State = WorkflowVersionState.Published, ArtifactDigest? ArtifactDigest = null,
    IReadOnlyList<WorkflowVersionReference>? Dependencies = null,
    IReadOnlyList<ArtifactDigest>? ArtifactReferences = null);

/// <summary>Editable source, independently revisioned from immutable workflow versions.</summary>
public sealed record WorkflowDraft(string Name, int Version, string Kind, string Source, long Revision = 0);

/// <summary>Durable catalog operations, shared with the instance commit protocol.</summary>
public interface IWorkflowCatalogStore
{
    Task<WorkflowDraft?> GetDefinitionAsync(string name, int version);
    Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision);
    Task RegisterWorkflowVersionAsync(WorkflowVersion version);
    Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version);
    Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null);
    Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state);
    /// <summary>Collects old artifacts while catalog references cannot change. No instance names an artifact yet.</summary>
    Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>Persistent version catalog and the code definitions this host can execute.</summary>
public interface IWorkflowCatalog : IWorkflowCatalogStore
{
    Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync();
    Task RegisterWorkflowAsync(WorkflowDefinition definition);
    Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version);
    Task<string?> GetWorkflowStructureHashAsync(string name, int version);
    Task<WorkflowVersion?> GetLatestWorkflowVersionAsync(string name);
    Task RetireWorkflowVersionAsync(string name, int version);
}

public sealed class WorkflowCatalog : IWorkflowCatalog
{
    private readonly IWorkflowCatalogStore _store;
    private readonly ConcurrentDictionary<(string, int), WorkflowDefinition> _definitions = new();

    public WorkflowCatalog() : this(new InMemoryWorkflowStateRepository()) { }
    public WorkflowCatalog(IEnumerable<WorkflowDefinition> definitions)
        : this(new InMemoryWorkflowStateRepository(), definitions) { }

    public WorkflowCatalog(IWorkflowCatalogStore store, IEnumerable<WorkflowDefinition>? definitions = null)
    {
        _store = store;
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
        await _store.RegisterWorkflowVersionAsync(new(definition.Name, definition.Version, scopes.StructureHash,
            Dependencies: dependencies));
        _definitions.TryAdd((definition.Name, definition.Version), definition);
    }

    public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync() =>
        Task.FromResult<IReadOnlyList<WorkflowDefinition>>(_definitions.Values.ToArray());

    public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version) =>
        Task.FromResult(_definitions.GetValueOrDefault((name, version)));

    public async Task<string?> GetWorkflowStructureHashAsync(string name, int version) =>
        (await _store.GetWorkflowVersionAsync(name, version))?.StructureHash;

    public async Task<WorkflowVersion?> GetLatestWorkflowVersionAsync(string name) =>
        (await _store.GetWorkflowVersionsAsync(name)).Where(row => row.State == WorkflowVersionState.Published)
        .MaxBy(row => row.Version);

    public Task RetireWorkflowVersionAsync(string name, int version) =>
        SetWorkflowVersionStateAsync(name, version, WorkflowVersionState.Retired);
    public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) => _store.GetDefinitionAsync(name, version);
    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) =>
        _store.SaveDefinitionAsync(definition, expectedRevision);
    public Task RegisterWorkflowVersionAsync(WorkflowVersion version) => _store.RegisterWorkflowVersionAsync(version);
    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) => _store.GetWorkflowVersionAsync(name, version);
    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => _store.GetWorkflowVersionsAsync(name);
    public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state) =>
        _store.SetWorkflowVersionStateAsync(name, version, state);
    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default) => _store.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
}

/// <summary>Rules used under each store's catalog/start lock.</summary>
internal static class WorkflowCatalogRules
{
    public static void Validate(WorkflowVersion row)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(row.Version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.StructureHash);
        if (!Enum.IsDefined(row.State)) throw new ArgumentOutOfRangeException(nameof(row.State));
        foreach (var digest in (row.ArtifactReferences ?? []).Concat(row.ArtifactDigest is { } artifact ? [artifact] : []))
            _ = new ArtifactDigest(digest.Value);
        foreach (var dependency in row.Dependencies ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dependency.Name);
            ArgumentOutOfRangeException.ThrowIfLessThan(dependency.Version, 1);
        }
    }

    public static void CheckRegistration(WorkflowVersion existing, WorkflowVersion loaded)
    {
        if (existing.StructureHash != loaded.StructureHash)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow {loaded.Name} v{loaded.Version} is already registered with structure hash " +
                $"'{existing.StructureHash}'; loaded structure hash '{loaded.StructureHash}' differs");
        if (existing.ArtifactDigest != loaded.ArtifactDigest ||
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
        if (row == null || row.State is WorkflowVersionState.Draft or WorkflowVersionState.Retired)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                row?.State == WorkflowVersionState.Retired
                    ? $"Workflow {name} v{version} is deactivated (Retired)"
                    : $"Workflow {name} v{version} is unavailable for new starts ({row?.State.ToString() ?? "missing"})");
    }

    public static void CheckRetirement(WorkflowVersion target, IReadOnlyList<WorkflowVersion> versions,
        IEnumerable<WorkflowInstance> instances)
    {
        var reference = new WorkflowVersionReference(target.Name, target.Version);
        var callers = versions.Where(row => (row.Dependencies ?? []).Contains(reference)).ToArray();
        if (callers.Any(row => (row.Name, row.Version) != (target.Name, target.Version) &&
                row.State is WorkflowVersionState.Published or WorkflowVersionState.Deprecated) ||
            instances.Any(instance => !WorkflowStoreRules.IsTerminal(instance.Status) &&
                callers.Any(row => row.Name == instance.WorkflowName && row.Version == instance.WorkflowVersion)))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Workflow {target.Name} v{target.Version} is required by a caller or unfinished parent");
    }

    public static IReadOnlySet<ArtifactDigest> ArtifactReferences(IEnumerable<WorkflowVersion> rows) =>
        rows.SelectMany(row => (row.ArtifactReferences ?? []).Concat(row.ArtifactDigest is { } digest ? [digest] : []))
            .ToHashSet();
}
