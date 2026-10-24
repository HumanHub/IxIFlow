using IxIFlow.Core.Runtime;
namespace IxIFlow.Core;

/// <summary>A step's stable catalog address, author identity and source call site.</summary>
public sealed record StepTableEntry(string CatalogId, string AuthorId, string Name,
    WorkflowStepType Kind, string ScopePath, string? SourceLocation)
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
    internal StepTable(WorkflowScopeCatalog catalog)
    {
        _entries = catalog.AllSteps.Select(pair => new StepTableEntry(
            pair.Key, catalog.AuthorId(pair.Value), pair.Value.Name, pair.Value.StepType,
            pair.Key[..pair.Key.LastIndexOf('/')], pair.Value.SourceLocation)
            {
                CancellationScope = pair.Value.StepType == WorkflowStepType.CancellationScope ?
                    new(catalog.SequenceScope(pair.Value), catalog.CancellationScope(pair.Value)) : null,
                Assignment = pair.Value.Assignment is { } assignment ?
                    new(assignment.TargetPath!, WorkflowTypeIdentity.StableName(assignment.ValueType)) : null,
                ExceptionType = pair.Value.StepType == WorkflowStepType.Throw ? WorkflowTypeIdentity.StableName(pair.Value.ExceptionType) : null,
                RethrowOwnerId = pair.Value.RethrowOwnerId
            }).ToArray();
    }
    public int Count => _entries.Length;
    public StepTableEntry this[int index] => _entries[index];
    public IEnumerator<StepTableEntry> GetEnumerator() => ((IEnumerable<StepTableEntry>)_entries).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
