namespace IxIFlow.Core.Runtime;

/// <summary>
/// A run's inbox and waits-table work since its last commit (D-32, D-33): the events it decided, how
/// far it read, and the waits it opened and closed. The next commit carries all of it, so the store
/// applies the decisions together with the checkpoint that results from them.
/// </summary>
internal sealed class InboxProgress
{
    private HashSet<string> _committedWaits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WaitCloseReason> _closures = new(StringComparer.Ordinal);
    private readonly List<EventSettlement> _settlements = [];

    /// <summary>The highest inbox sequence the run has read, or null before its first read.</summary>
    public long? ReadThrough { get; private set; }

    /// <summary>
    /// True until the run has read the inbox, and again when a commit reply says events arrived after
    /// the last read.
    /// </summary>
    public bool MayHaveUnread { get; private set; } = true;

    /// <summary>Events decided since the last commit.</summary>
    public IReadOnlyList<EventSettlement> Settlements => _settlements;

    public static InboxProgress Committed(IEnumerable<WaitState> waits)
    {
        var progress = new InboxProgress();
        progress._committedWaits = waits.Where(wait => wait.TableKind != null).Select(wait => wait.Id)
            .ToHashSet(StringComparer.Ordinal);
        return progress;
    }

    public bool IsDecided(string eventId) =>
        _settlements.Any(settlement => string.Equals(settlement.EventId, eventId, StringComparison.Ordinal));

    public void Settle(EventSettlement settlement) => _settlements.Add(settlement);

    public void Read(long through)
    {
        ReadThrough = through;
        MayHaveUnread = false;
    }

    public void Closed(string waitId, WaitCloseReason reason) => _closures.TryAdd(waitId, reason);

    /// <summary>Why a wait this run closed since its last commit closed, if it did.</summary>
    public WaitCloseReason? ClosureOf(string waitId) =>
        _closures.TryGetValue(waitId, out var reason) ? reason : null;

    /// <summary>The waits the next commit opens and closes, against what was last committed.</summary>
    public (IReadOnlyList<OpenedWait> Opened, IReadOnlyList<ClosedWait> Closed) WaitChanges(
        IReadOnlyList<WaitState> waits)
    {
        var current = waits.Where(wait => wait.TableKind != null).ToArray();
        var currentIds = current.Select(wait => wait.Id).ToHashSet(StringComparer.Ordinal);
        var opened = current.Where(wait => !_committedWaits.Contains(wait.Id))
            .Select(wait => new OpenedWait(wait.Id, wait.TableKind!.Value, wait.Key, TypeName(wait.EventType),
                wait.DeadlineUtc))
            .ToArray();
        // A wait removed without a recorded reason went with its scope.
        var closed = _committedWaits.Where(id => !currentIds.Contains(id))
            .Order(StringComparer.Ordinal)
            .Select(id => new ClosedWait(id, _closures.GetValueOrDefault(id, WaitCloseReason.Cancelled)))
            .ToArray();
        return (opened, closed);
    }

    /// <summary>Records a commit the store acknowledged: what it carried is now the store's.</summary>
    public void Acknowledged(IReadOnlyList<WaitState> waits, bool unreadEvents)
    {
        _committedWaits = waits.Where(wait => wait.TableKind != null).Select(wait => wait.Id)
            .ToHashSet(StringComparer.Ordinal);
        _closures.Clear();
        _settlements.Clear();
        if (ReadThrough != null && unreadEvents)
            MayHaveUnread = true;
    }

    // The waits table lists a wait's event type without an assembly version, as discovery shows it.
    private static string TypeName(string savedType) =>
        WorkflowTypeIdentity.Resolve(savedType) is { } type ? WorkflowTypeIdentity.StableName(type) : savedType;
}
