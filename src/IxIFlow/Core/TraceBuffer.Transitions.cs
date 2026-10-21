using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

internal sealed partial class TraceBuffer
{
    public void Record(TraceEntryType kind, WorkflowStep step, ContinuationState continuation,
        int? index = null, string? scopeId = null, string? joinId = null)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (!Accepts(kind, now))
            return;
        Add(new ExecutionTraceEntry
        {
            Timestamp = now, EntryType = kind,
            Payload = new ControlFlowTrace(_scopes!.Id(step), step.StepType, continuation.Id, index, scopeId, joinId)
        });
    }

    public void Condition(WorkflowStep step, ContinuationState continuation, int index, string scopeId,
        ConditionPath path)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(TraceEntryType.ConditionTaken, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = TraceEntryType.ConditionTaken,
                Payload = new ConditionTrace(_scopes!.Id(step), step.StepType, continuation.Id, index, scopeId, path) });
    }

    public void Join(WorkflowStep step, ContinuationState continuation, string joinId, JoinPath path)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(TraceEntryType.JoinCompleted, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = TraceEntryType.JoinCompleted,
                Payload = new JoinTrace(_scopes!.Id(step), step.StepType, continuation.Id, joinId, path) });
    }

    public void Timeout(WorkflowStep step, ContinuationState continuation, string waitId, string? scopeId,
        bool replayed = false)
    {
        var kind = step.StepType == WorkflowStepType.Delay ? TraceEntryType.DelayFired : TraceEntryType.WaitTimedOut;
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = kind,
                Payload = new TimeoutTrace(_scopes!.Id(step), step.StepType, continuation.Id,
                    WaitReference.Of(_instanceId, waitId), scopeId, replayed) });
    }

    public void Retry(TraceEntryType kind, WorkflowStep step, ContinuationState continuation, int attempt,
        string? restartStepId = null, string? scopeId = null)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = kind,
                Payload = new RetryTrace(_scopes!.Id(step), step.StepType, continuation.Id, attempt,
                    restartStepId, scopeId, continuation.RetryAfterUtc) });
    }

    public void ItemStarted(WorkflowStep step, ContinuationState continuation, ScopePosition frame) =>
        Record(TraceEntryType.ForEachItemStarted, step, continuation, frame.ForEachIndex, frame.ScopeId,
            continuation.ParentJoinId);

    public void ItemEnded(ContinuationState continuation, ScopePosition frame, ItemEndCause cause = ItemEndCause.Completed)
    {
        if (frame.ForEachIndex is not { } index)
            return;
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(TraceEntryType.ForEachItemEnded, now))
        {
            var step = _scopes!.Step(frame.ScopeId[..^"/foreach".Length]);
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = TraceEntryType.ForEachItemEnded,
                FrameActivationId = frame.ActivationId,
                Payload = new ItemEndTrace(_scopes.Id(step), step.StepType, continuation.Id, index,
                    frame.ScopeId, continuation.ParentJoinId, cause) });
        }
    }

    public void Graph(TraceEntryType kind, WorkflowStep step, ContinuationState continuation,
        string graphId, string node, long visit, string? via = null, string? target = null,
        TransitionCause? cause = null, string? activationId = null, ItemEndCause? exitCause = null)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now))
            Add(new ExecutionTraceEntry
            {
                Timestamp = now, EntryType = kind, FrameActivationId = activationId,
                Payload = new GraphTrace(_scopes!.Id(step), continuation.Id, graphId, node, visit, via, target, cause, exitCause)
            });
    }

    public void DropFrames(ContinuationState continuation, int start, ItemEndCause cause)
    {
        for (var index = continuation.Stack.Count - 1; index >= start; index--)
        {
            var frame = continuation.Stack[index];
            ItemEnded(continuation, frame, cause);
            if (frame.Kind == FrameKind.Node && index > 0)
            {
                var owner = continuation.Stack[index - 1];
                var graph = _scopes!.Steps(owner.ScopeId)[owner.NextStepIndex];
                Graph(TraceEntryType.NodeExited, graph, continuation, _scopes.Id(graph), frame.Node!,
                    owner.Graph!.Visits, owner.Graph.Via, activationId: frame.ActivationId, exitCause: cause);
            }
        }
        continuation.Stack.RemoveRange(start, continuation.Stack.Count - start);
    }
}
