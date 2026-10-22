using IxIFlow.Core.Runtime;
namespace IxIFlow.Core;

/// <summary>A step's stable catalog address, author identity and source call site.</summary>
public sealed record StepTableEntry(string CatalogId, string AuthorId, string Name,
    WorkflowStepType Kind, string ScopePath, string? SourceLocation)
{
    public AssignmentDescriptor? Assignment { get; init; }
}

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
            { Assignment = pair.Value.Assignment is { } assignment ?
                new(assignment.TargetPath!, WorkflowTypeIdentity.StableName(assignment.ValueType)) : null }).ToArray();
    }
    public int Count => _entries.Length;
    public StepTableEntry this[int index] => _entries[index];
    public IEnumerator<StepTableEntry> GetEnumerator() => ((IEnumerable<StepTableEntry>)_entries).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
