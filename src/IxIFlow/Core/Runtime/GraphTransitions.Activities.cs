namespace IxIFlow.Core.Runtime;

internal static partial class GraphTransitions
{
    private static void Fork(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState parent, WorkflowStep arm, WorkflowStep graph, StateMachineState definition,
        ScopePosition state, int cap)
    {
        var branches = definition.Transitions.Where(t => t.Steps[0].TriggerKind == TriggerKind.Activity)
            .Select(t => (Scope: scopes.Id(t.Steps[0])[..^2] + "/run", Kind: FrameKind.Run)).ToList();
        if (definition.ListenSteps.Count > 0)
            branches.Add((state.ScopeId + "/listen", FrameKind.Listen));
        // Admission precedes snapshots, join creation and reply movement, including in nested forks.
        ContinuationTransitions.Fork(scopes, arm, checkpoint, parent, cap, branches);
        var join = checkpoint.Joins.Single(j => j.ParentContinuationId == parent.Id);
        var listener = checkpoint.BranchesOf(join).SingleOrDefault(c => c.Stack[0].Kind == FrameKind.Listen);
        if (listener != null) MoveReplies(checkpoint, parent, listener, state.ActivationId);
        checkpoint.Trace.Graph(TraceEntryType.StateArmed, arm, parent, scopes.Id(graph), state.Node!,
            parent.Stack[^2].Graph!.Visits, count: definition.Transitions.Count);
    }

    private static (ContinuationState Parent, ScopePosition State, StateMachineState Definition) ListenerOwner(
        WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState listener)
    {
        var join = checkpoint.Joins.Single(j => j.Id == listener.ParentJoinId);
        var parent = checkpoint.ContinuationById(join.ParentContinuationId)!;
        var state = parent.Stack[^1];
        var owner = parent.Stack[^2];
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        return (parent, state, graph.GraphStates.Single(s => s.Key == state.Node));
    }

    public static void Listen(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep listen, IServiceProvider services, DateTime now, DateTime storeNow)
    {
        checkpoint.Trace.Record(TraceEntryType.StepEntered, listen, continuation);
        var (_, state, definition) = ListenerOwner(scopes, checkpoint, continuation);
        var frame = continuation.Stack[^1];
        if (continuation.AcceptedWait is { } accepted)
        {
            continuation.AcceptedWait = null;
            var trigger = scopes.Step(accepted.StepId);
            var fired = frame.Fired ??= new GraphFired { Trigger = accepted.StepId, Event = accepted.Event };
            if (trigger.TriggerKind is TriggerKind.Event or TriggerKind.Reply)
            {
                var value = accepted.Event?.Read(services) ?? throw new WorkflowEngineException(
                    WorkflowErrorKind.InvalidState, "A listener requires its accepted payload");
                WorkflowValueBinding.ApplyEventOutputs(trigger, value, checkpoint.DataOf(continuation),
                    ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
                continuation.SetPrevious(value);
            }
            else
            {
                continuation.Previous = frame.EntryPrevious;
                continuation.RuntimePrevious = frame.RuntimeEntryPrevious;
            }
            fired.Trigger = accepted.StepId;
            fired.Event = accepted.Event;
            frame.NextStepIndex++;
            return;
        }
        foreach (var transition in definition.Transitions.Where(t => t.Steps[0].TriggerKind == TriggerKind.Reply))
            if (checkpoint.BoundReplies.FirstOrDefault(r => r.ContinuationId == continuation.Id &&
                r.ScopeActivationId == state.ActivationId && r.StepId == scopes.Id(transition.Steps[0])) is { } answer)
            {
                checkpoint.BoundReplies.Remove(answer);
                checkpoint.TouchOwner(answer.ContinuationId);
                frame.Fired = new GraphFired { Trigger = answer.StepId, Event = answer.Event, Reply = answer };
                continuation.AcceptedWait = new AcceptedWaitState
                { StepId = answer.StepId, Event = answer.Event, EventId = answer.EventId, WaitId = answer.WaitId };
                TriggerFired(checkpoint, continuation);
                return;
            }
        OpenWaits(scopes, checkpoint, continuation, definition, state.ActivationId, services, now, storeNow);
        continuation.Status = ContinuationStatus.Waiting;
    }

    public static void Fire(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep fire)
    {
        checkpoint.Trace.Record(TraceEntryType.StepEntered, fire, continuation);
        var frame = continuation.Stack[^1];
        if (frame.Kind == FrameKind.Run)
            frame.Fired = new GraphFired
            {
                Trigger = frame.ScopeId[..^"/run".Length] + "/0",
                Event = continuation.RuntimePrevious != null
                    ? SerializedValue.From(continuation.RuntimePrevious) : continuation.Previous
            };
        if (frame.Fired == null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A Fire pick has no fired trigger");
        frame.NextStepIndex++;
    }

    // Called only after the Arm join has passed the durable winner/completion guards.
    public static void BranchFinished(ExecutionCheckpoint checkpoint, ParallelJoinState join, ScopePosition frame)
    {
        var parent = checkpoint.ContinuationById(join.ParentContinuationId)!;
        parent.Stack[^2].Graph!.Fired = frame.Fired ?? throw new WorkflowEngineException(
            WorkflowErrorKind.InvalidState, "An Arm branch completed without Fire");
        checkpoint.Touch(parent);
    }

    public static void RetainReply(ContinuationState listener, WaitState wait, SerializedValue value, string eventId)
    {
        if (listener.Stack.LastOrDefault() is not { Kind: FrameKind.Listen } frame) return;
        frame.Fired = new GraphFired
        {
            Trigger = wait.StepId, Event = value,
            Reply = new BoundReplyState
            {
                WaitId = wait.Id, EventId = eventId, ContinuationId = listener.Id, StepId = wait.StepId,
                ScopeActivationId = wait.ScopeActivationId!, InvocationId = wait.InvocationId,
                Subject = wait.Subject, Event = value
            }
        };
    }

    // Storage ownership changes; request identity and logical state-activation lifetime do not.
    private static void MoveReplies(ExecutionCheckpoint checkpoint, ContinuationState source,
        ContinuationState target, string activation)
    {
        foreach (var wait in checkpoint.Waits.Where(w => w.IsReply && w.ContinuationId == source.Id &&
                     w.ScopeActivationId == activation))
            wait.ContinuationId = target.Id;
        foreach (var reply in checkpoint.BoundReplies.Where(r => r.ContinuationId == source.Id &&
                     r.ScopeActivationId == activation))
            reply.ContinuationId = target.Id;
        checkpoint.TouchOwner(source.Id);
        checkpoint.TouchOwner(target.Id);
    }

    public static void ReturnReplies(ExecutionCheckpoint checkpoint, ContinuationState listener,
        ScopePosition frame, bool restoreReceipt)
    {
        var join = checkpoint.Joins.Single(j => j.Id == listener.ParentJoinId);
        var parent = checkpoint.ContinuationById(join.ParentContinuationId)!;
        MoveReplies(checkpoint, listener, parent, parent.Stack[^1].ActivationId);
        if (restoreReceipt)
        {
            if (frame.Fired?.Reply is { } receipt && checkpoint.BoundReplies.All(r => r.WaitId != receipt.WaitId))
            {
                receipt.ContinuationId = parent.Id;
                checkpoint.BoundReplies.Add(receipt);
            }
            listener.AcceptedWait = null;
            frame.Fired = null;
        }
        checkpoint.TouchOwner(listener.Id);
        checkpoint.TouchOwner(parent.Id);
    }
}
