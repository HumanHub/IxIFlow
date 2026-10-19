using System.Text.Json;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

internal sealed partial class TraceBuffer
{
    private WorkflowInstance? _instance;
    private Dictionary<string, InspectionThread> _positions = new(StringComparer.Ordinal);
    private Dictionary<string, InspectionThread> _acknowledgedPositions = new(StringComparer.Ordinal);
    private bool _capturing;
    private bool _origin;
    private bool _acknowledgedOrigin;
    private bool _acknowledgedReplayInitialized;

    public void CapturePositions(DateTime? timestamp = null)
    {
        if (_capturing || !_enabled || _detail != TraceDetail.Full || _checkpoint == null || _scopes == null || _instance == null ||
            _checkpoint.StructureHash != _scopes.StructureHash) return;
        _capturing = true;
        try
        {
            var now = timestamp ?? _time.GetUtcNow().UtcDateTime;
            if (!_origin)
            {
                _origin = true;
                var resumed = _checkpoint.TraceReplayInitialized;
                var initial = !_checkpoint.TraceBudgetExhausted
                    ? InspectionProjection.Threads(_instance, _checkpoint, _scopes, now) : [];
                if (!resumed)
                {
                    _checkpoint.TraceReplayInitialized = true;
                    Add(new() { Timestamp = now, EntryType = TraceEntryType.ReplayOrigin,
                        Payload = new ReplayOriginTrace(_instanceId, _checkpoint.StructureHash, _instance.Revision, _detail)
                            { Threads = initial } });
                }
                // Recovery starts from the saved position, already present in a complete trace.
                _positions = initial.ToDictionary(t => t.ContinuationId, StringComparer.Ordinal);
            }
            if (_detail != TraceDetail.Full || _checkpoint.TraceBudgetExhausted || _truncated != null) return;
            var current = InspectionProjection.Threads(_instance, _checkpoint, _scopes, now)
                .ToDictionary(t => t.ContinuationId, StringComparer.Ordinal);
            foreach (var id in _positions.Keys.Union(current.Keys).ToArray())
            {
                _positions.TryGetValue(id, out var before); current.TryGetValue(id, out var after);
                if (JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after)) continue;
                Add(new() { Timestamp = now, EntryType = TraceEntryType.ThreadPosition, ContinuationId = id,
                    FrameActivationId = after?.FrameId ?? before?.FrameId, StepId = after?.StepId ?? before?.StepId,
                    Payload = new ThreadPositionTrace(id, after) });
            }
            _positions = current;
        }
        finally { _capturing = false; }
    }

}
