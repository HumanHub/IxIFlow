using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Pending entries only. Acknowledgement clears them; requested results have a separate collector.</summary>
internal sealed partial class TraceBuffer
{
    private readonly List<ExecutionTraceEntry> _pending = [];
    private readonly List<ExecutionTraceEntry> _audits = [];
    private int _limit = ExecutionLimits.DefaultMaxTraceEntriesPerCommit;
    private TraceTruncation? _truncated;
    private TraceBudget? _budgetMarker;
    private bool _enabled = true;
    private TraceDetail _detail = TraceDetail.Full;
    private long? _budget;
    private ExecutionCheckpoint? _checkpoint;
    private long _acknowledgedCount;
    private bool _acknowledgedBudget;
    private DebugCheckpoint _acknowledgedDebug = new();
    private WorkflowScopeCatalog? _scopes;
    private string _instanceId = "";
    private TimeProvider _time = TimeProvider.System;

    public IReadOnlyList<ExecutionTraceEntry> Pending => _audits.Count == 0 ? _pending : [.. _pending, .. _audits];

    public void Configure(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
    }

    public void Configure(WorkflowInstance instance, ExecutionCheckpoint checkpoint, ExecutionLimits limits,
        TimeProvider? time, WorkflowScopeCatalog scopes)
    {
        Configure(limits.MaxTraceEntriesPerCommit);
        _instanceId = instance.InstanceId;
        _instance = instance;
        _enabled = instance.TracingEnabled;
        _detail = limits.TraceDetail;
        _budget = limits.MaxTraceEntriesPerInstance;
        if (!ReferenceEquals(_checkpoint, checkpoint))
        {
            _acknowledgedCount = checkpoint.TraceEntriesRecorded;
            _acknowledgedBudget = checkpoint.TraceBudgetExhausted;
            _acknowledgedReplayInitialized = checkpoint.TraceReplayInitialized;
            _acknowledgedDebug = checkpoint.Debug;
            if (!_origin && checkpoint.TraceReplayInitialized && checkpoint.StructureHash == scopes.StructureHash &&
                _enabled && _detail == TraceDetail.Full && !checkpoint.TraceBudgetExhausted)
            {
                // Establish the saved baseline before recovery reactivates continuations.
                _positions = InspectionProjection.Threads(instance, checkpoint, scopes,
                    (time ?? _time).GetUtcNow().UtcDateTime).ToDictionary(t => t.ContinuationId, StringComparer.Ordinal);
                _acknowledgedPositions = new(_positions, StringComparer.Ordinal);
                _origin = _acknowledgedOrigin = true;
            }
        }
        _checkpoint = checkpoint;
        _scopes = scopes;
        if (time != null)
            _time = time;
    }

    public bool Accepts(TraceEntryType kind, DateTime timestamp)
    {
        if (!_enabled || (_detail == TraceDetail.Activities || _checkpoint?.TraceBudgetExhausted == true) &&
            IsControlFlow(kind))
            return false;
        if (_budget is { } budget && _checkpoint is { } checkpoint)
        {
            if (!checkpoint.TraceBudgetExhausted && checkpoint.TraceEntriesRecorded >= budget)
                ReachedBudget(budget, timestamp);
            if (checkpoint.TraceBudgetExhausted && IsControlFlow(kind))
                return false;
        }
        return HasCapacity();
    }

    private void ReachedBudget(long budget, DateTime timestamp)
    {
        _checkpoint!.TraceBudgetExhausted = true;
        _budgetMarker = new TraceBudget(budget);
        var marker = new ExecutionTraceEntry
        {
            Timestamp = timestamp, EntryType = TraceEntryType.TraceBudgetReached, Payload = _budgetMarker
        };
        if (_pending.Count == _limit)
        {
            _budgetMarker.DroppedCount = _truncated?.DroppedCount ?? 1;
            _truncated = null;
            _pending[^1] = marker;
        }
        else
            _pending.Add(marker);
    }

    private bool HasCapacity()
    {
        if (_truncated != null)
        {
            _truncated.DroppedCount++;
            return false;
        }
        if (_pending.Count < _limit)
            return true;
        if (_budgetMarker != null)
        {
            _budgetMarker.DroppedCount++;
            return false;
        }
        _truncated = new TraceTruncation(2);
        _pending[^1] = new ExecutionTraceEntry
        {
            Timestamp = _pending[^1].Timestamp, EntryType = TraceEntryType.Truncated, Payload = _truncated
        };
        return false;
    }

    // Mandatory audit records bypass all tracing switches and capacity limits.
    public void AddAudit(DataEditedTrace audit, DateTime timestamp) => _audits.Add(new ExecutionTraceEntry
    {
        EntryType = TraceEntryType.DataEdited, Timestamp = timestamp, Payload = audit
    });

    public void Add(ExecutionTraceEntry entry)
    {
        CapturePositions(entry.Timestamp);
        var (stepId, continuationId) = entry.Payload switch
        {
            GraphTrace p => (p.StepId, p.ContinuationId),
            ControlFlowTrace p => (p.StepId, p.ContinuationId),
            ConditionTrace p => (p.StepId, p.ContinuationId),
            JoinTrace p => (p.StepId, p.ContinuationId),
            ItemEndTrace p => (p.StepId, p.ContinuationId),
            TimeoutTrace p => (p.StepId, p.ContinuationId),
            RetryTrace p => (p.StepId, p.ContinuationId),
            _ => (entry.StepId, entry.ContinuationId)
        };
        entry.StepId ??= stepId;
        entry.ContinuationId ??= continuationId;
        entry.FrameActivationId ??= _checkpoint?.Continuations.FirstOrDefault(c => c.Id == continuationId)
            ?.Stack.LastOrDefault()?.ActivationId;
        if (Accepts(entry.EntryType, entry.Timestamp))
        {
            _pending.Add(entry);
            if (_budget is { } budget && _checkpoint != null && !_checkpoint.TraceBudgetExhausted)
            {
                _checkpoint.TraceEntriesRecorded++;
                if (_checkpoint.TraceEntriesRecorded >= budget)
                    ReachedBudget(budget, entry.Timestamp);
            }
        }
    }

    public static bool IsControlFlow(TraceEntryType kind) => kind is
        TraceEntryType.StepEntered or TraceEntryType.ConditionTaken or TraceEntryType.LoopIteration or
        TraceEntryType.LoopCompleted or TraceEntryType.BranchStarted or TraceEntryType.JoinCompleted or
        TraceEntryType.CatchEntered or TraceEntryType.FinallyEntered or TraceEntryType.ForEachItemStarted or
        TraceEntryType.ForEachItemEnded or TraceEntryType.CompensationStarted or TraceEntryType.CompensationCompleted or
        TraceEntryType.CompensationFailed or TraceEntryType.WaitTimedOut or TraceEntryType.DelayFired or
        TraceEntryType.SagaRetryStarted or TraceEntryType.StepRetryScheduled or
        TraceEntryType.ReplayOrigin or TraceEntryType.ThreadPosition or TraceEntryType.NodeEntered or
        TraceEntryType.NodeExited or TraceEntryType.TransitionTaken or TraceEntryType.GraphCompleted or
        TraceEntryType.StateEntered or TraceEntryType.ExitEntered or TraceEntryType.ActionEntered;

    public void Committed()
    {
        if (_checkpoint != null)
        {
            _acknowledgedCount = _checkpoint.TraceEntriesRecorded;
            _acknowledgedBudget = _checkpoint.TraceBudgetExhausted;
            _acknowledgedReplayInitialized = _checkpoint.TraceReplayInitialized;
            _acknowledgedDebug = _checkpoint.Debug;
        }
        _pending.Clear();
        _audits.Clear();
        _truncated = null;
        _budgetMarker = null;
        _acknowledgedPositions = new(_positions, StringComparer.Ordinal);
        _acknowledgedOrigin = _origin;
    }

    public void DiscardPending()
    {
        _positions = new(_acknowledgedPositions, StringComparer.Ordinal);
        _origin = _acknowledgedOrigin;
        if (_checkpoint != null)
        {
            _checkpoint.TraceEntriesRecorded = _acknowledgedCount;
            _checkpoint.TraceBudgetExhausted = _acknowledgedBudget;
            _checkpoint.TraceReplayInitialized = _acknowledgedReplayInitialized;
            _checkpoint.Debug = _acknowledgedDebug;
        }
        _pending.Clear();
        _audits.Clear();
        _truncated = null;
        _budgetMarker = null;
    }
}
