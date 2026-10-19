using System.Text.Json;
using System.Text.Json.Nodes;

namespace IxIFlow.Core;

/// <summary>A retry deadline and the store time from which it is frozen.</summary>
public sealed record FrozenRetry(DateTime DeadlineUtc, DateTime FrozenFromUtc);

/// <summary>Store-owned timer state for one debug session.</summary>
public sealed record TimerFreeze(DateTime StartedAtUtc,
    IReadOnlyDictionary<string, DateTime> WaitAnchors,
    IReadOnlyDictionary<string, FrozenRetry> RetryAnchors)
{
    public IReadOnlyList<string> ExpiredWaits { get; init; } = [];
    public IReadOnlyDictionary<string, DateTime> ExpiredRetries { get; init; } = new Dictionary<string, DateTime>();
}

/// <summary>Authoritative timer state returned by a timer commit, including on receipt replay.</summary>
public sealed record TimerCommitResult(DebugCheckpoint Debug,
    IReadOnlyDictionary<string, DateTime> Deadlines,
    IReadOnlyDictionary<string, DateTime> RetryDeadlines);

/// <summary>The store-clock audit of a freeze or shift. Shift is the whole session duration.</summary>
public sealed record TimersTrace(string Actor, string CommandId, DateTime FrozenAtUtc, TimeSpan Shift) : TracePayload;

// These rules run under the instance lock, after receipt/fencing checks, before runnable-time calculation.
internal static class WorkflowTimerRules
{
    internal static bool Frozen(WorkflowInstance instance) =>
        instance.ExecutionStateJson.Contains("\"Timers\"", StringComparison.Ordinal) && WorkflowDebugRules.Checkpoint(instance).Timers != null;

    internal static bool Changes(WorkflowInstance? previous, WorkflowInstance next, IReadOnlyList<DebugCommand> commands) =>
        previous != null && Frozen(previous) || commands.Any(c => c.AcknowledgedRevision == null &&
            c.Order > (previous == null ? 0 : WorkflowDebugRules.Checkpoint(previous).AppliedOrder) &&
            c.Order <= WorkflowDebugRules.Checkpoint(next).AppliedOrder &&
            c.Kind is DebugCommandKind.FreezeTimers or DebugCommandKind.UnfreezeTimers);

    internal static TimerCommitResult Read(WorkflowInstance instance, IReadOnlyDictionary<string, StoredJson> parts,
        IEnumerable<StoredWait> waits) => new(WorkflowDebugRules.Checkpoint(instance),
            waits.Where(w => w.IsOpen && w.DeadlineUtc != null).ToDictionary(w => w.WaitId, w => w.DeadlineUtc!.Value),
            RetryNodes(instance.ExecutionStateJson, parts).Where(p => p.Node["RetryAfterUtc"] != null)
                .ToDictionary(p => p.Node["Id"]!.GetValue<string>(), p => p.Node["RetryAfterUtc"]!.GetValue<DateTime>()));

    internal static TimerCommitResult Apply(WorkflowInstance? previous, WorkflowInstance next,
        Dictionary<string, StoredJson> parts, Dictionary<string, StoredWait> waits,
        IReadOnlyList<DebugCommand> commands, DateTime now, List<string> trace)
    {
        var before = previous == null ? new DebugCheckpoint() : WorkflowDebugRules.Checkpoint(previous);
        var after = WorkflowDebugRules.Checkpoint(next);
        var freeze = before.Timers;
        var applied = commands.Where(c => c.Order > before.AppliedOrder && c.Order <= after.AppliedOrder).ToArray();
        var freezing = applied.LastOrDefault(c => c.Kind == DebugCommandKind.FreezeTimers);
        if (freezing != null && freeze == null)
        {
            freeze = new(now, new Dictionary<string, DateTime>(), new Dictionary<string, FrozenRetry>())
            {
                ExpiredWaits = waits.Values.Where(w => w.IsOpen && w.DeadlineUtc <= now).Select(w => w.WaitId).ToArray(),
                ExpiredRetries = RetryNodes(next.ExecutionStateJson, parts).Where(p => p.Node["RetryAfterUtc"] != null &&
                        p.Node["RetryAfterUtc"]!.GetValue<DateTime>() <= now)
                    .ToDictionary(p => p.Node["Id"]!.GetValue<string>(), p => p.Node["RetryAfterUtc"]!.GetValue<DateTime>())
            };
            Audit(TraceEntryType.TimersFrozen, freezing, now, TimeSpan.Zero);
        }
        if (freeze != null)
        {
            // Exclude only timers already due at the start. A zero-duration timer opened later
            // still shifts by its remaining freeze, becoming due exactly when the freeze clears.
            var anchors = freeze.WaitAnchors.Where(a => waits.TryGetValue(a.Key, out var w) && w.IsOpen && w.DeadlineUtc != null)
                .ToDictionary(a => a.Key, a => a.Value);
            var expiredWaits = freeze.ExpiredWaits.Where(id => waits.TryGetValue(id, out var w) && w.IsOpen).ToHashSet(StringComparer.Ordinal);
            foreach (var wait in waits.Values.Where(w => w.IsOpen && w.DeadlineUtc != null && !expiredWaits.Contains(w.WaitId)))
                anchors.TryAdd(wait.WaitId, now);
            var retries = new Dictionary<string, FrozenRetry>();
            var expiredRetries = new Dictionary<string, DateTime>();
            foreach (var (_, node) in RetryNodes(next.ExecutionStateJson, parts))
            {
                if (node["RetryAfterUtc"] == null) continue;
                var id = node["Id"]!.GetValue<string>();
                var deadline = node["RetryAfterUtc"]!.GetValue<DateTime>();
                if (freeze.ExpiredRetries.TryGetValue(id, out var expired) && expired == deadline)
                    expiredRetries[id] = expired;
                else if (freeze.RetryAnchors.TryGetValue(id, out var kept) && kept.DeadlineUtc == deadline)
                    retries[id] = kept;
                else retries[id] = new(deadline, now);
            }
            freeze = freeze with { WaitAnchors = anchors, RetryAnchors = retries,
                ExpiredWaits = expiredWaits.ToArray(), ExpiredRetries = expiredRetries };
            var thaw = applied.LastOrDefault(c => c.Kind is DebugCommandKind.UnfreezeTimers or DebugCommandKind.Continue);
            if (thaw != null)
            {
                foreach (var (id, anchor) in anchors)
                    waits[id] = waits[id] with { DeadlineUtc = waits[id].DeadlineUtc + (now - anchor) };
                ShiftRetries(next, parts, retries.ToDictionary(p => p.Key, p => p.Value.DeadlineUtc + (now - p.Value.FrozenFromUtc)));
                Audit(TraceEntryType.TimersShifted, thaw, freeze.StartedAtUtc, now - freeze.StartedAtUtc);
                freeze = null;
            }
        }
        SetDebug(next, after with { Timers = freeze });
        return Read(next, parts, waits.Values);

        void Audit(TraceEntryType type, DebugCommand command, DateTime start, TimeSpan shift) => trace.Add(
            WorkflowStoreRules.SerializeTrace(new ExecutionTraceEntry { Timestamp = now, Clock = TraceClock.Store,
                EntryType = type, Payload = new TimersTrace(command.Actor, command.Id, start, shift) }));
    }

    internal static void SetDebug(WorkflowInstance instance, DebugCheckpoint debug)
    {
        var state = JsonNode.Parse(instance.ExecutionStateJson)!;
        state["Debug"] = JsonSerializer.SerializeToNode(debug);
        instance.ExecutionStateJson = state.ToJsonString();
    }

    // Retry timers live in the root trunk or a continuation part. Never visit workflow values.
    private static IEnumerable<(string? Part, JsonNode Node)> RetryNodes(string trunk, IReadOnlyDictionary<string, StoredJson> parts)
    {
        var state = JsonNode.Parse(trunk)!;
        foreach (var node in state["Continuations"]?.AsArray() ?? [])
            if (node != null) yield return (null, node);
        foreach (var (key, json) in parts.Where(p => p.Key.StartsWith("c/", StringComparison.Ordinal)))
            yield return (key, JsonNode.Parse(json.InlineText)!["Continuation"]!);
    }

    internal static void ShiftRetries(WorkflowInstance instance, Dictionary<string, StoredJson> parts,
        IReadOnlyDictionary<string, DateTime> deadlines)
    {
        var state = JsonNode.Parse(instance.ExecutionStateJson)!;
        foreach (var node in state["Continuations"]?.AsArray() ?? [])
            if (node != null && deadlines.TryGetValue(node["Id"]!.GetValue<string>(), out var deadline))
                node["RetryAfterUtc"] = deadline;
        instance.ExecutionStateJson = state.ToJsonString();
        foreach (var key in parts.Keys.Where(k => k.StartsWith("c/", StringComparison.Ordinal)).ToArray())
        {
            var part = JsonNode.Parse(parts[key].InlineText)!;
            var node = part["Continuation"]!;
            if (!deadlines.TryGetValue(node["Id"]!.GetValue<string>(), out var deadline)) continue;
            node["RetryAfterUtc"] = deadline;
            parts[key] = StoredJson.Inline(part.ToJsonString());
        }
    }
}
