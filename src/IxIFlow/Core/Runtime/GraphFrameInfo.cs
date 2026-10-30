namespace IxIFlow.Core.Runtime;

internal sealed record GraphFrameInfo(string Name, string Phase, long Visit, string? Via)
{
    public string GraphId { get; init; } = "";
    public string Node { get; init; } = "";
    public InspectionGraphChoice? Pending { get; init; }

    public static GraphFrameInfo? Of(WorkflowScopeCatalog scopes, ContinuationState continuation, int index,
        ExecutionCheckpoint? checkpoint = null)
    {
        var frame = continuation.Stack[index];

        if (frame.Kind is FrameKind.Run or FrameKind.Listen)
        {
            var split = frame.ScopeId.LastIndexOf("/state/", StringComparison.Ordinal);
            var graphStep = scopes.Step(frame.ScopeId[..split]);
            var path = frame.ScopeId[(split + "/state/".Length)..];
            var key = path[..path.IndexOf('/')];
            var stateName = graphStep.GraphStates.Single(s => s.Key == key).Name;
            var join = checkpoint?.Joins.SingleOrDefault(j => j.Id == continuation.ParentJoinId);
            var parent = join == null ? null : checkpoint!.ContinuationById(join.ParentContinuationId);

            var branchOwner = parent?.Stack.LastOrDefault(f => f.Graph != null &&
                scopes.Id(scopes.Steps(f.ScopeId)[f.NextStepIndex]) == scopes.Id(graphStep));

            var branchState = branchOwner?.Graph;

            return new(graphStep.Name + " / " + stateName + " / " + PathName(path[(key.Length + 1)..]),
                    frame.Fired != null || continuation.AcceptedWait != null ? "Fired" :
                    frame.Kind == FrameKind.Run ? "Running" : "Armed", branchState?.Visits ?? 0, branchState?.Via)
                { GraphId = scopes.Id(graphStep), Node = key, Pending = Choice(branchState?.Pending) };
        }

        if (frame.Kind is not (FrameKind.Node or FrameKind.Edge or FrameKind.State or FrameKind.StateExit or FrameKind.Transition
            or FrameKind.Case))
            return null;

        var owner = continuation.Stack.Take(index).Last(item => item.Graph != null);
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var state = owner.Graph!;

        if (graph.StepType == WorkflowStepType.StateMachine)
        {
            var definition = graph.GraphStates.Single(s => s.Key == state.Node);
            var stateFrame = continuation.Stack[continuation.Stack.IndexOf(owner) + 1];

            var statePhase = frame.Kind switch
            {
                FrameKind.StateExit => "Exiting",
                FrameKind.Transition => frame.NextStepIndex == scopes.Steps(frame.ScopeId).Count ? "TransitionDone" : "TransitionChosen",
                FrameKind.Case => "Action",
                _ => definition.Final ? "FinalEntry" :
                    frame.NextStepIndex == definition.Steps.Count ? "Leaving" :
                    frame.NextStepIndex < definition.Steps.Count - 1 ? "Running" :
                    continuation.Status == ContinuationStatus.Joining ? state.Fired != null ? "Settling" : "Armed" :
                    continuation.Status == ContinuationStatus.Waiting ? "Armed" :
                    continuation.AcceptedWait != null || state.Fired != null ? "Fired" : "Ready"
            };

            var suffix = frame.Kind == FrameKind.State
                ? $" (visit {state.Visits})"
                : " / " + PathName(frame.ScopeId[(stateFrame.ScopeId.Length + 1)..]);

            return new(graph.Name + " / " + definition.Name + suffix, statePhase, state.Visits, state.Via)
                { GraphId = scopes.Id(graph), Node = state.Node, Pending = Choice(state.Pending) };
        }

        var node = graph.GraphNodes.Single(node => node.Key == state.Node);
        var name = graph.Name + " / " + node.Name;

        var phase = frame.Kind == FrameKind.Edge
            ? frame.NextStepIndex == scopes.Steps(frame.ScopeId).Count ? "EdgeChosen" : "EdgePushed"
            : frame.NextStepIndex == node.Steps.Count
                ? "Leaving"
                : frame.NextStepIndex == node.Steps.Count - 1
                    ? "Deciding"
                    : "Running";

        if (frame.Kind == FrameKind.Edge)
            name += " / to " + frame.ScopeId[(frame.ScopeId.LastIndexOf('/') + 1)..];
        else
            name += $" (visit {state.Visits})";

        return new(name, phase, state.Visits, state.Via)
            { GraphId = scopes.Id(graph), Node = state.Node, Pending = Choice(state.Pending) };
    }

    private static InspectionGraphChoice? Choice(GraphPending? pending) =>
        pending == null ? null : new(pending.Via, pending.Trigger, pending.Target, pending.Cause.ToString());

    private static string PathName(string path) => path.Replace("/", " / ", StringComparison.Ordinal)
        .Replace("on / ", "on ", StringComparison.Ordinal).Replace("case / ", "case ", StringComparison.Ordinal);
}