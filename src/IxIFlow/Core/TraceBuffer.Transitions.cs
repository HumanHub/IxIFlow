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

    public void CancellationHandler(TraceEntryType kind, WorkflowStep step, ContinuationState continuation,
        CancellationScopeState state, string handlerScope)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now)) Add(new ExecutionTraceEntry { Timestamp = now, EntryType = kind,
            Payload = new CancellationHandlerTrace(_scopes!.Id(step), continuation.Id, _scopes.SequenceScope(step),
                handlerScope, state.ActivationId, state.Cause!.Value) });
    }

    public void Fault(WorkflowStep step, ContinuationState continuation, SerializedException error, bool rethrow,
        WorkflowFaultLocation current)
    {
        var kind = rethrow ? TraceEntryType.FaultRethrown : TraceEntryType.FaultRaised;
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now)) Add(new ExecutionTraceEntry { Timestamp = now, EntryType = kind,
            Payload = new FaultTrace(_scopes!.Id(step), step.StepType, continuation.Id, error.Type,
                error.OriginalFaultLocation, current)
                { DeclaredExceptionType = step.StepType == WorkflowStepType.Throw ? WorkflowTypeIdentity.SavedName(step.ExceptionType!) : null } });
    }

    public void Condition(WorkflowStep step, ContinuationState continuation, int index, string scopeId,
        ConditionPath path)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(TraceEntryType.ConditionTaken, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = TraceEntryType.ConditionTaken,
                Payload = new ConditionTrace(_scopes!.Id(step), step.StepType, continuation.Id, index, scopeId, path) });
    }

    public void Assigned(WorkflowStep step, ContinuationState continuation)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(TraceEntryType.DataAssigned, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = TraceEntryType.DataAssigned,
                Payload = new DataAssignedTrace(_scopes!.Id(step), continuation.Id, step.Assignment!.TargetPath!) });
    }

    public void Switch(WorkflowStep step, ContinuationState continuation, SwitchPath path, int? index, string? scopeId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(TraceEntryType.SwitchSelected, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = TraceEntryType.SwitchSelected,
                Payload = new SwitchTrace(_scopes!.Id(step), continuation.Id, path, index, scopeId) });
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
        var kind = step.StepType == WorkflowStepType.Trigger ? TraceEntryType.TimerFired :
            step.StepType == WorkflowStepType.Delay ? TraceEntryType.DelayFired : TraceEntryType.WaitTimedOut;
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = kind,
                Payload = new TimeoutTrace(_scopes!.Id(step), step.StepType, continuation.Id,
                    WaitReference.Of(_instanceId, waitId), scopeId, replayed) });
    }

    public void Retry(TraceEntryType kind, WorkflowStep step, ContinuationState continuation, int attempt,
        string? restartStepId = null, string? scopeId = null, InvocationRecordState? invocation = null)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now))
            Add(new ExecutionTraceEntry { Timestamp = now, EntryType = kind,
                Payload = new RetryTrace(_scopes!.Id(step), step.StepType, continuation.Id, attempt,
                    restartStepId, scopeId, continuation.RetryAfterUtc)
                { Invocation = invocation == null ? null : InvocationRecords.Facts(invocation, invocation.Attempts[^1]) } });
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
        TransitionCause? cause = null, string? activationId = null, ItemEndCause? exitCause = null, string? triggerId = null, int? count = null)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now))
            Add(new ExecutionTraceEntry
            {
                Timestamp = now, EntryType = kind, FrameActivationId = activationId,
                Payload = new GraphTrace(_scopes!.Id(step), continuation.Id, graphId, node, visit, via, target, cause, exitCause) { Trigger = triggerId, Count = count }
            });
    }

    public void Inline(TraceEntryType kind, WorkflowStep step, ContinuationState continuation,
        ScopePosition frame, ItemEndCause? cause = null)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(kind, now)) Add(new ExecutionTraceEntry
        {
            Timestamp = now, EntryType = kind, FrameActivationId = frame.ActivationId,
            Payload = new InlineTrace(_scopes!.Id(step), continuation.Id, frame.ScopeId, frame.ActivationId, cause)
        });
    }

    public void InlineLeaving(ContinuationState continuation, ScopePosition frame, ItemEndCause cause)
    {
        if (frame.Kind != FrameKind.Inline) return;
        Inline(TraceEntryType.InlineExited, _scopes!.Step(frame.ScopeId[..^"/inline".Length]), continuation, frame, cause);
    }

    public void ScopeInterrupted(ScopeFrameState entry)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (Accepts(TraceEntryType.ScopeClosed, now)) Add(new ExecutionTraceEntry
        {
            Timestamp = now, EntryType = TraceEntryType.ScopeClosed, StepId = entry.StepId,
            ContinuationId = entry.ContinuationId, FrameActivationId = entry.Id,
            Payload = new ScopeTrace(entry.StepId, WorkflowStepType.Scope, entry.ContinuationId, entry.Id,
                false, entry.RecordedOpens, entry.CloseReason, ScopeTraceCause.Interrupted) { InvocationId = entry.InvocationId }
        });
    }

    public void DropFrames(ContinuationState continuation, int start, ItemEndCause cause, PendingFailure? failure = null)
    {
        continuation.AuthorizedDispatch = null;
        for (var index = continuation.Stack.Count - 1; index >= start; index--)
        {
            var frame = continuation.Stack[index];
            if (frame.Kind == FrameKind.Scope)
                ScopeRuntime.RecordClose(_checkpoint!, continuation, frame.ActivationId,
                    cause == ItemEndCause.Cancelled ? ScopeCloseReason.Cancelled : ScopeCloseReason.Failed, failure);
            ItemEnded(continuation, frame, cause);
            InlineLeaving(continuation, frame, cause);
            if (frame.Kind is FrameKind.Node or FrameKind.State && index > 0)
            {
                var owner = continuation.Stack[index - 1];
                var graph = _scopes!.Steps(owner.ScopeId)[owner.NextStepIndex];
                if (frame.Kind == FrameKind.State) GraphTransitions.CloseState(_checkpoint!, continuation, frame);
                Graph(frame.Kind == FrameKind.Node ? TraceEntryType.NodeExited : TraceEntryType.StateExited, graph, continuation, _scopes.Id(graph), frame.Node!,
                    owner.Graph!.Visits, owner.Graph.Via, activationId: frame.ActivationId, exitCause: cause);
            }
        }
        continuation.Stack.RemoveRange(start, continuation.Stack.Count - start);
    }
}
