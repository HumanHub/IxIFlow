using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>A step's stable catalog address, author identity and source call site.</summary>
public sealed record StepTableEntry(
    string CatalogId,
    string AuthorId,
    string Name,
    WorkflowStepType Kind,
    string ScopePath,
    string? SourceLocation)
{
    public CancellationScopeDescriptor? CancellationScope { get; init; }
    public AssignmentDescriptor? Assignment { get; init; }
    public string? ExceptionType { get; init; }
    public string? RethrowOwnerId { get; init; }
}

/// <summary>The distinct body and cleanup scopes of a cancellation wrapper.</summary>
public sealed record CancellationScopeDescriptor(string BodyScopeId, string HandlerScopeId);

/// <summary>A structural destination and its declared value type.</summary>
public sealed record AssignmentDescriptor(string Target, string ValueType);

/// <summary>The steps of one executable definition, including nested scopes.</summary>
public sealed class StepTable : System.Collections.Generic.IReadOnlyList<StepTableEntry>
{
    private readonly StepTableEntry[] _entries;
    private StepTable(StepTableEntry[] entries) => _entries = entries;

    // Published bodies retain their original PDB/#line document names. Resolve those
    // names by the nearest graft owner, including nested and repeated callee sites.
    // Keep authored step locations intact: invocation inventories verify those bytes.
    internal StepTable InBundle(IReadOnlyList<WorkflowInlineWorkflow> inlineWorkflows)
    {
        var sites = inlineWorkflows.SelectMany(pin => pin.Sites.Select(site => (Pin: pin, Prefix: site + "/inline/")))
            .OrderByDescending(site => site.Prefix.Length).ToArray();

        return new(_entries.Select(entry =>
        {
            var source = entry.SourceLocation;
            var colon = source?.LastIndexOf(':') ?? -1;

            if (colon <= 0)
                return entry;

            var path = source![..colon];

            if (path is not (WorkflowBundleArchive.YamlPath or WorkflowBundleArchive.SourcePath))
                return entry;

            var owner = sites.FirstOrDefault(site => entry.CatalogId.StartsWith(site.Prefix, StringComparison.Ordinal)).Pin;

            return owner?.BundleDigest == null
                ? entry
                : entry with
                {
                    SourceLocation = WorkflowBundleIdentity.InlineSourceDirectory(owner.Name, owner.Version) + source
                };
        }).ToArray());
    }

    internal StepTable(WorkflowScopeCatalog catalog)
    {
        _entries = catalog.AllSteps.Select(pair => new StepTableEntry(
            pair.Key, catalog.AuthorId(pair.Value), pair.Value.Name, pair.Value.StepType,
            pair.Key[..pair.Key.LastIndexOf('/')], pair.Value.SourceLocation)
        {
            CancellationScope = pair.Value.StepType == WorkflowStepType.CancellationScope
                ? new(catalog.SequenceScope(pair.Value), catalog.CancellationScope(pair.Value))
                : null,
            Assignment = pair.Value.Assignment is { } assignment
                ? new(assignment.TargetPath!, WorkflowTypeIdentity.StableName(assignment.ValueType))
                : null,
            ExceptionType =
                pair.Value.StepType == WorkflowStepType.Throw ? WorkflowTypeIdentity.StableName(pair.Value.ExceptionType) : null,
            RethrowOwnerId = pair.Value.RethrowOwnerId
        }).ToArray();
    }

    public int Count => _entries.Length;
    public StepTableEntry this[int index] => _entries[index];
    public IEnumerator<StepTableEntry> GetEnumerator() => ((IEnumerable<StepTableEntry>)_entries).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}