namespace IxIFlow.Core.Runtime;

internal static partial class GraphTransitions
{
    public static void Enter(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition owner, WorkflowStep graph)
    {
        owner.Graph = new GraphState { Node = graph.GraphStart!, Visits = 1, PassEntries = 1 };
        Push(scopes, checkpoint, continuation, owner, graph);
    }

    private static void Push(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition owner, WorkflowStep graph)
    {
        var frame = new ScopePosition
        {
            ScopeId = graph.StepType == WorkflowStepType.Flowchart
                ? scopes.NodeScope(graph, owner.Graph!.Node) : scopes.StateScope(graph, owner.Graph!.Node),
            Kind = graph.StepType == WorkflowStepType.Flowchart ? FrameKind.Node : FrameKind.State, Node = owner.Graph.Node,
            EntryPrevious = continuation.Previous, RuntimeEntryPrevious = continuation.RuntimePrevious,
            RestorePreviousOnExit = true
        };
        continuation.Stack.Add(frame);
        SteppingPolicy.Entered(checkpoint, continuation, scopes.Id(graph), frame);
    }

    public static void Decide(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep decision, IServiceProvider services)
    {
        var frame = continuation.Stack[^1];
        var owner = continuation.Stack[^2];
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        if (owner.Graph!.Pending != null)
        {
            frame.NextStepIndex++;
            return;
        }
        var context = WorkflowValueBinding.EvaluationContext(decision, checkpoint.DataOf(continuation),
            continuation.GetPrevious(services), ForEachTransitions.ItemsOf(scopes, checkpoint, continuation), TryScopeTransitions.CatchFault(checkpoint, continuation, services, decision, scopes));
        var value = decision.OutcomeSelector?.Invoke(context);
        var node = graph.GraphNodes.Single(node => node.Key == frame.Node);
        for (var index = 0; index < node.Edges.Count; index++)
        {
            var edge = node.Edges[index][0];
            if (!edge.IsDefault && (decision.OutcomeSelector != null
                    ? !decision.SwitchEquals!(edge.EdgeLabel, value)
                    : edge.CompiledCondition != null && !edge.CompiledCondition(context))) continue;
            var selected = new ScopePosition
            {
                ScopeId = scopes.EdgeScope(graph, frame.Node!, index), Kind = FrameKind.Edge,
                EntryPrevious = continuation.Previous, RuntimeEntryPrevious = continuation.RuntimePrevious,
                RestorePreviousOnExit = true
            };
            continuation.Stack.Add(selected);
            SteppingPolicy.Entered(checkpoint, continuation, scopes.Id(graph), selected);
            return;
        }
        Choose(scopes, checkpoint, continuation, decision, null, TransitionCause.NoTransition);
        frame.NextStepIndex++;
    }

    public static void Edge(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep edge)
    {
        if (continuation.Stack[^1].Kind == FrameKind.Case)
        {
            TraceEntry(scopes, checkpoint, continuation, edge, TraceEntryType.ActionEntered);
            continuation.Stack[^1].NextStepIndex++;
            return;
        }
        checkpoint.Trace.Record(TraceEntryType.StepEntered, edge, continuation);
        var cause = edge.Explicit ? TransitionCause.Explicit : edge.IsDefault ? TransitionCause.Default :
            edge.CompiledCondition != null ? TransitionCause.When :
            continuation.Stack.Count >= 3 && scopes.Steps(continuation.Stack[^2].ScopeId)[^1].OutcomeSelector != null
                ? TransitionCause.Case : TransitionCause.Always;
        Choose(scopes, checkpoint, continuation, edge, edge.Target, cause);
        continuation.Stack[^1].NextStepIndex++;
    }

    private static void Choose(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep via, string? target, TransitionCause cause, WorkflowStep? trigger = null, SerializedValue? value = null)
    {
        var owner = continuation.Stack.Last(frame => frame.Graph != null);
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        owner.Graph!.Pending = new GraphPending
        {
            Via = scopes.Id(via), Target = target, Cause = cause,
            Trigger = trigger == null ? null : scopes.Id(trigger), Event = value
        };
        if (graph.StepType == WorkflowStepType.StateMachine)
        {
            var state = continuation.Stack[continuation.Stack.IndexOf(owner) + 1];
            CloseState(checkpoint, continuation, state);
            Restore(continuation, state);
        }
        checkpoint.Trace.Graph(TraceEntryType.TransitionTaken, via, continuation, scopes.Id(graph),
            owner.Graph.Node, owner.Graph.Visits, scopes.Id(via), target, cause,
            triggerId: trigger == null ? null : scopes.Id(trigger));
    }

    public static void CheckExit(ContinuationState continuation, ScopePosition popped, int limit)
    {
        if (popped.Kind is not (FrameKind.Node or FrameKind.State)) return;
        var graph = continuation.Stack[^2].Graph!;
        if (graph.Pending?.Target != null && graph.PassEntries >= limit)
            throw new GraphTransitionLimitExceededException(limit);
    }

    public static bool Exit(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ScopePosition parent,
        ScopePosition popped, WorkflowStep current, ContinuationState continuation)
    {
        if (popped.Kind == FrameKind.Edge)
        {
            parent.NextStepIndex++;
            return true;
        }
        if (popped.Kind == FrameKind.StateExit)
        {
            Transition(scopes, checkpoint, continuation, parent);
            return true;
        }
        if (popped.Kind == FrameKind.Case)
        {
            parent.NextStepIndex++;
            return true;
        }
        if (popped.Kind == FrameKind.Transition)
        {
            parent.NextStepIndex = scopes.Steps(parent.ScopeId).Count;
            return true;
        }
        if (popped.Kind is not (FrameKind.Node or FrameKind.State)) return false;
        var state = parent.Graph!;
        var pending = state.Pending;
        if (pending == null && !(popped.Kind == FrameKind.State && current.GraphStates.Single(s => s.Key == popped.Node).Final))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A non-final graph body left without choosing a transition");
        var cause = pending?.Cause ?? TransitionCause.Final;
        checkpoint.Trace.Graph(popped.Kind == FrameKind.Node ? TraceEntryType.NodeExited : TraceEntryType.StateExited,
            current, continuation, scopes.Id(current), popped.Node!, state.Visits,
            pending?.Via, pending?.Target, cause, popped.ActivationId);
        if (pending?.Target != null)
        {
            state.Node = pending.Target;
            state.Visits++;
            state.PassEntries++;
            state.Via = pending.Via;
            state.Pending = null;
            Push(scopes, checkpoint, continuation, parent, current);
        }
        else
        {
            checkpoint.Trace.Graph(TraceEntryType.GraphCompleted, current, continuation, scopes.Id(current),
                popped.Node!, state.Visits, pending?.Via, null, cause);
            parent.Graph = null;
            parent.NextStepIndex++;
        }
        return true;
    }
}
