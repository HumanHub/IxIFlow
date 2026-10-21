using System.Text.Json;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>A detached, read-only reconstruction. Sequence is authoritative across host and store clocks.</summary>
public sealed record ReplayPosition(long Sequence, DateTime? Timestamp, TraceClock? Clock, long? Revision,
    WorkflowStatus? Status, bool IsComplete, IReadOnlyList<string> IncompleteReasons,
    IReadOnlyList<InspectionThread> Threads, bool IsCheckpoint)
{
    /// <summary>Only an explicitly attached checkpoint at this exact revision can supply values.</summary>
    public IReadOnlyList<InspectionData>? Data { get; init; }
}

/// <summary>Folds recorded positions without invoking activities, expressions or data combiners.</summary>
public sealed class TraceReplay
{
    private readonly ReplayPosition[] _positions;
    private readonly string?[] _stops;
    private readonly HashSet<string> _catalog;
    private readonly HashSet<string> _breakpoints = new(StringComparer.Ordinal);
    private int _index;
    private readonly Dictionary<long, IReadOnlyList<InspectionData>> _data = [];
    private string? _structureHash;
    private string? _instanceId;
    private TraceReplay(ReplayPosition[] positions, string?[] stops, HashSet<string> catalog)
        => (_positions, _stops, _catalog) = (positions, stops, catalog);

    // Return a detached graph so modifying a response cannot change subsequent cursor reads.
    public ReplayPosition Current
    {
        get
        {
            var position = _positions[_index];
            if (position.IsCheckpoint && position.Revision is { } revision && _data.TryGetValue(revision, out var data))
                position = position with { Data = data };
            return InspectionPrivacy.Clean(position, []);
        }
    }
    /// <summary>Attaches values only to the matching committed revision; values never come from the trace.</summary>
    public void AttachCheckpoint(InstanceInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (inspection.StructureHash != _structureHash || inspection.Summary.InstanceId != _instanceId)
            throw new ArgumentException("The checkpoint belongs to another replay", nameof(inspection));
        var boundary = _positions.SingleOrDefault(p => p.IsCheckpoint && p.Revision == inspection.Revision);
        if (boundary == null || JsonSerializer.Serialize(boundary.Threads) != JsonSerializer.Serialize(inspection.Threads))
            throw new ArgumentException("The checkpoint does not match a recorded revision and position", nameof(inspection));
        _data[inspection.Revision] = InspectionPrivacy.Clean(inspection.Data, []);
    }
    public bool CanStepForward => _index + 1 < _positions.Length;
    /// <summary>The recorded executable step occurrence at this cursor position, if any.</summary>
    public string? CurrentStepId => _stops[_index];
    public bool CanStepBackward => _index > 0;
    public bool StepForward() { if (!CanStepForward) return false; _index++; return true; }
    public bool StepBackward() { if (!CanStepBackward) return false; _index--; return true; }
    public void Seek(long sequence)
    {
        var index = Array.FindIndex(_positions, p => p.Sequence == sequence);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        _index = index;
    }
    public void SetBreakpoints(IEnumerable<string> catalogStepIds)
    {
        var ids = catalogStepIds.ToArray();
        if (ids.Any(id => !_catalog.Contains(id))) throw new ArgumentException("Unknown catalog step ID", nameof(catalogStepIds));
        _breakpoints.Clear(); _breakpoints.UnionWith(ids);
    }
    /// <summary>Moves at least once, stopping at a recorded step occurrence or the end.</summary>
    public bool Continue(bool backwards = false)
    {
        var moved = false;
        while (backwards ? StepBackward() : StepForward())
        {
            moved = true;
            if (_stops[_index] is { } step && _breakpoints.Contains(step)) break;
        }
        return moved;
    }
    public static TraceReplay Load(WorkflowDefinition definition, IEnumerable<StoredTraceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(entries);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        // Own inputs. Neither caller mutation nor a subsequent execution can change this replay.
        var history = InspectionPrivacy.Clean(entries.ToArray(), []);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var threads = new Dictionary<string, InspectionThread>(StringComparer.Ordinal);
        var positions = new List<ReplayPosition>(); var stops = new List<string?>();
        long previous = 0; long? revision = null; WorkflowStatus? status = null;
        var origin = false; string? instanceId = null;
        long? commitRevision = null; long? commitEnd = null;
        var commitComplete = false;
        foreach (var stored in history)
        {
            if (stored.Sequence <= previous) throw new ArgumentException("Trace sequences must be strictly increasing", nameof(entries));
            if (stored.Sequence != previous + 1) reasons.Add("Missing trace sequences");
            previous = stored.Sequence;
            if (stored.CommitEndSequence is { } end)
            {
                if (stored.Revision <= 0 || end < stored.Sequence || stored.CommitStatus == null)
                    throw new ArgumentException("Invalid commit trace metadata", nameof(entries));
                if (commitRevision != stored.Revision)
                {
                    if (commitEnd != null && !commitComplete)
                        reasons.Add("The trace ends before a commit boundary");
                    // Trace-free commits have no event sequence. A missing traced commit leaves a sequence gap.
                    if (commitRevision != null && stored.Revision < commitRevision)
                        throw new ArgumentException("Commit revisions must not move backwards", nameof(entries));
                    commitRevision = stored.Revision; commitEnd = end; commitComplete = false;
                }
                else if (commitEnd != end)
                    throw new ArgumentException("Inconsistent commit sequence range", nameof(entries));
            }
            var entry = stored.Entry; var checkpoint = false;
            switch (entry.Payload)
            {
                case ReplayOriginTrace start:
                    RequireHash(start.StructureHash);
                    if (instanceId != null && instanceId != start.InstanceId)
                        throw new ArgumentException("Entries belong to different instances", nameof(entries));
                    instanceId = start.InstanceId;
                    threads.Clear(); foreach (var thread in start.Threads) threads.Add(thread.ContinuationId, thread);
                    if (!origin && (stored.Sequence != 1 || start.BaseRevision > 1)) reasons.Add("History starts after execution began");
                    if (start.Detail != TraceDetail.Full) reasons.Add("Control flow detail was omitted");
                    origin = true;
                    break;
                case ThreadPositionTrace position:
                    if (position.Thread == null) threads.Remove(position.ContinuationId);
                    else threads[position.ContinuationId] = position.Thread;
                    break;
            }
            if (stored.CommitEndSequence == stored.Sequence)
            {
                revision = stored.Revision; status = stored.CommitStatus; checkpoint = true;
                commitComplete = true;
            }
            if (entry.EntryType is TraceEntryType.Truncated or TraceEntryType.TraceBudgetReached)
                reasons.Add("Trace recording limit omitted history");
            var problems = reasons.Concat(origin ? [] : new[] { "Replay origin is missing" }).ToArray();
            positions.Add(new(stored.Sequence, entry.Timestamp, entry.Clock, revision, status,
                problems.Length == 0, problems, threads.Values.OrderBy(t => t.Sequence).ToArray(), checkpoint));
            stops.Add(entry.EntryType is TraceEntryType.StepEntered or TraceEntryType.ActivityStarted or TraceEntryType.ConditionTaken or
                TraceEntryType.LoopIteration or TraceEntryType.ForEachItemStarted or TraceEntryType.NodeEntered or
                TraceEntryType.StateEntered or TraceEntryType.ExitEntered or TraceEntryType.ActionEntered ? entry.StepId : null);
        }
        if (positions.Count == 0)
        {
            positions.Add(new(0, null, null, null, null, false, ["Trace history is empty"], [], false)); stops.Add(null);
        }
        // Only the store's declared end sequence proves that a complete commit was supplied.
        if (!positions.Any(p => p.IsCheckpoint))
            for (var i = 0; i < positions.Count; i++)
                positions[i] = positions[i] with { IsComplete = false,
                    IncompleteReasons = [.. positions[i].IncompleteReasons, "No commit boundary is available"] };
        var lastBoundary = positions.FindLastIndex(p => p.IsCheckpoint);
        for (var i = lastBoundary + 1; i < positions.Count; i++)
            if (history.Length > i && history[i].CommitEndSequence != null)
                positions[i] = positions[i] with { IsComplete = false,
                    IncompleteReasons = [.. positions[i].IncompleteReasons, "The trace ends before a commit boundary"] };
        return new(positions.ToArray(), stops.ToArray(), definition.StepTable.Select(s => s.CatalogId).ToHashSet(StringComparer.Ordinal))
            { _structureHash = scopes.StructureHash, _instanceId = instanceId };
        void RequireHash(string actual)
        {
            if (actual != scopes.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Replay workflow structure differs from the recorded structure hash");
        }
    }
}
