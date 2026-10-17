namespace IxIFlow.Core;

/// <summary>
/// A run's trace entries (G-01): all it recorded, for its result, and those its commits have not yet carried. An
/// entry that no commit carries, because the progress it describes was not saved, is dropped with it.
/// </summary>
internal sealed class TraceBuffer
{
    private readonly List<ExecutionTraceEntry> _entries = [];
    private int _committed;

    /// <summary>Every entry this run recorded and kept.</summary>
    public IReadOnlyList<ExecutionTraceEntry> Run => _entries;

    /// <summary>The entries the next commit carries.</summary>
    public IReadOnlyList<ExecutionTraceEntry> Pending => _entries.Skip(_committed).ToArray();

    public void Add(ExecutionTraceEntry entry) => _entries.Add(entry);

    /// <summary>The commit that carried the pending entries was applied.</summary>
    public void Committed() => _committed = _entries.Count;

    /// <summary>The progress since the last commit was not saved; neither are its entries.</summary>
    public void DiscardPending() => _entries.RemoveRange(_committed, _entries.Count - _committed);
}
