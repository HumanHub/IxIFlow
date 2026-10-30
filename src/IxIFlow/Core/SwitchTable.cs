using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>A switch selector contract and its ordered case/default scopes for authoring tools.</summary>
public sealed record SwitchTableEntry(string CatalogId, string ValueType, IReadOnlyList<SwitchCaseEntry> Cases);

/// <summary>Canonical label state is exposed in the catalog, never in runtime selection facts.</summary>
public sealed record SwitchCaseEntry(int Order, bool IsDefault, string? LabelType, string? LabelSignature, string ScopeId);

public sealed class SwitchTable : IReadOnlyList<SwitchTableEntry>
{
    private readonly SwitchTableEntry[] _entries;

    internal SwitchTable(WorkflowScopeCatalog catalog) => _entries = catalog.AllSteps
        .Where(pair => pair.Value.StepType == WorkflowStepType.Switch)
        .Select(pair => new SwitchTableEntry(pair.Key, WorkflowTypeIdentity.StableName(pair.Value.SwitchValueType),
            Array.AsReadOnly(pair.Value.OutcomeBranches.Select((arm, index) => new SwitchCaseEntry(index, arm.IsDefault,
                arm.IsDefault ? null : WorkflowTypeIdentity.StableName(arm.Value?.GetType()),
                arm.IsDefault ? null : SwitchLabelSignature.Of(arm.Value), catalog.OutcomeScope(pair.Value, index))).ToArray())))
        .ToArray();

    public int Count => _entries.Length;
    public SwitchTableEntry this[int index] => _entries[index];
    public IEnumerator<SwitchTableEntry> GetEnumerator() => ((IEnumerable<SwitchTableEntry>)_entries).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}