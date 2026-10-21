namespace IxIFlow.Core.Runtime;

internal sealed record GraphFrameInfo(string Name, string Phase, long Visit, string? Via)
{
    public static GraphFrameInfo? Of(WorkflowScopeCatalog scopes, ContinuationState continuation, int index)
    {
        var frame = continuation.Stack[index];
        if (frame.Kind is not (FrameKind.Node or FrameKind.Edge)) return null;
        var owner = continuation.Stack.Take(index).Last(item => item.Graph != null);
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var state = owner.Graph!;
        var node = graph.GraphNodes.Single(node => node.Key == state.Node);
        var name = graph.Name + " / " + node.Name;
        var phase = frame.Kind == FrameKind.Edge
            ? frame.NextStepIndex == scopes.Steps(frame.ScopeId).Count ? "EdgeChosen" : "EdgePushed"
            : frame.NextStepIndex == node.Steps.Count ? "Leaving" :
                frame.NextStepIndex == node.Steps.Count - 1 ? "Deciding" : "Running";
        if (frame.Kind == FrameKind.Edge) name += " / to " + frame.ScopeId[(frame.ScopeId.LastIndexOf('/') + 1)..];
        else name += $" (visit {state.Visits})";
        return new(name, phase, state.Visits, state.Via);
    }
}
