namespace IxIFlow.Core.Runtime;

/// <summary>
/// A run's inbox and waits-table work since its last commit: the events it decided, how
/// far it read, and the waits it opened, armed and closed. The next commit carries all of it, so the
/// store applies the decisions together with the checkpoint that results from them.
/// </summary>
internal sealed class InboxProgress
{
    private HashSet<string> _committedWaits = new(StringComparer.Ordinal);
    // Committed waits whose timeout the store has, so it stamped their deadline (D-35).
    private HashSet<string> _committedTimeouts = new(StringComparer.Ordinal);
    private HashSet<string> _committedSuggestions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WaitCloseReason> _closures = new(StringComparer.Ordinal);
    private readonly List<EventSettlement> _settlements = [];
    private HashSet<string> _due = new(StringComparer.Ordinal);

    /// <summary>The highest inbox sequence the run has read, or null before its first read.</summary>
    public long? ReadThrough { get; private set; }

    /// <summary>
    /// True until the run has read the inbox, and again when a commit reply says events arrived after
    /// the last read.
    /// </summary>
    public bool MayHaveUnread { get; private set; } = true;

    /// <summary>Events decided since the last commit.</summary>
    public IReadOnlyList<EventSettlement> Settlements => _settlements;

    public static InboxProgress Committed(ExecutionCheckpoint checkpoint)
    {
        var progress = new InboxProgress();
        progress.Acknowledged(checkpoint, false);
        return progress;
    }

    public bool IsDecided(string eventId) =>
        _settlements.Any(settlement => string.Equals(settlement.EventId, eventId, StringComparison.Ordinal));

    public void Settle(EventSettlement settlement) => _settlements.Add(settlement);

    /// <summary>
    /// Makes the next pass read the inbox although no commit reply reported an arrival: the host saw
    /// the inbox sequence rise, or a deadline may be due and the store says which, together with the
    /// events received before it.
    /// </summary>
    public void ExpectArrivals() => MayHaveUnread = true;

    public void Read(long through)
    {
        ReadThrough = through;
        MayHaveUnread = false;
    }

    /// <summary>Records the waits the store found due at its time when the run last read the inbox.</summary>
    public void Due(IEnumerable<string> waitIds) => _due = waitIds.ToHashSet(StringComparer.Ordinal);

    /// <summary>True when the store found the wait due at the run's last read.</summary>
    public bool IsDue(string waitId) => _due.Contains(waitId);

    public void Closed(string waitId, WaitCloseReason reason) => _closures.TryAdd(waitId, reason);

    /// <summary>Why a wait this run closed since its last commit closed, if it did.</summary>
    public WaitCloseReason? ClosureOf(string waitId) =>
        _closures.TryGetValue(waitId, out var reason) ? reason : null;

    /// <summary>The waits the next commit opens, arms and closes, against what was last committed.</summary>
    public (IReadOnlyList<OpenedWait> Opened, IReadOnlyList<ArmedWait> Armed, IReadOnlyList<ClosedWait> Closed)
        WaitChanges(ExecutionCheckpoint checkpoint)
    {
        var current = Rows(checkpoint).ToArray();
        var currentIds = current.Select(wait => wait.WaitId).ToHashSet(StringComparer.Ordinal);
        var opened = current.Where(wait => !_committedWaits.Contains(wait.WaitId)).ToArray();
        var armed = current.Where(wait => _committedWaits.Contains(wait.WaitId) &&
                (wait.Timeout != null && !_committedTimeouts.Contains(wait.WaitId) ||
                 wait.DefaultPayloadJson != null && !_committedSuggestions.Contains(wait.WaitId)))
            .Select(wait => new ArmedWait(wait.WaitId,
                _committedTimeouts.Contains(wait.WaitId) ? null : wait.Timeout,
                _committedSuggestions.Contains(wait.WaitId) ? null : wait.DefaultPayloadJson))
            .ToArray();
        // A wait removed without a recorded reason went with its scope.
        var closed = _committedWaits.Where(id => !currentIds.Contains(id))
            .Order(StringComparer.Ordinal)
            .Select(id => new ClosedWait(id, _closures.GetValueOrDefault(id, WaitCloseReason.Cancelled)))
            .ToArray();
        return (opened, armed, closed);
    }

    /// <summary>Records a commit the store acknowledged: what it carried is now the store's.</summary>
    public void Acknowledged(ExecutionCheckpoint checkpoint, bool unreadEvents)
    {
        var rows = Rows(checkpoint).ToArray();
        _committedWaits = rows.Select(wait => wait.WaitId).ToHashSet(StringComparer.Ordinal);
        _committedTimeouts = rows.Where(wait => wait.Timeout != null).Select(wait => wait.WaitId)
            .ToHashSet(StringComparer.Ordinal);
        _committedSuggestions = rows.Where(wait => wait.DefaultPayloadJson != null).Select(wait => wait.WaitId)
            .ToHashSet(StringComparer.Ordinal);
        _closures.Clear();
        _settlements.Clear();
        if (ReadThrough != null && unreadEvents)
            MayHaveUnread = true;
    }

    // The waits-table rows a checkpoint stands for: its waits, and its execution deadline while that can
    // still end the run.
    private static IEnumerable<OpenedWait> Rows(ExecutionCheckpoint checkpoint)
    {
        foreach (var wait in checkpoint.Waits)
            yield return new OpenedWait(wait.Id, wait.TableKind, wait.Key, TypeName(wait.EventType), wait.Timeout,
                wait.DefaultPayloadJson);
        if (checkpoint.ExecutionTimerOpen)
            yield return new OpenedWait(checkpoint.ExecutionTimerId!, WaitKind.Execution, "execution", "",
                checkpoint.ExecutionTimeout);
    }

    // The waits table lists a wait's event type without an assembly version, as discovery shows it.
    private static string TypeName(string savedType) =>
        savedType.Length == 0 ? savedType
        : WorkflowTypeIdentity.Resolve(savedType) is { } type ? WorkflowTypeIdentity.StableName(type) : savedType;
}
