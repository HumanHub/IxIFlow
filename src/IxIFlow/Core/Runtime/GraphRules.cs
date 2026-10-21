namespace IxIFlow.Core.Runtime;

internal static class GraphRules
{
    public static void Validate(IReadOnlyList<WorkflowStep> steps) => Body(steps, null, false);

    private static void Body(IReadOnlyList<WorkflowStep> steps, WorkflowStep? graph, bool leavesNode)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.StepType == WorkflowStepType.Edge && step.Explicit)
            {
                if (graph == null || !leavesNode || index != steps.Count - 1)
                    throw WorkflowBuildError.Invalid(step,
                        "An explicit transition must end a timeout path leaving a flowchart node");
                Target(graph, step);
            }
            if (step.StepType == WorkflowStepType.Flowchart)
            {
                Graph(step);
                continue;
            }
            foreach (var scope in step.ChildScopes())
                Body(scope.Steps, graph, leavesNode && scope.Kind == "timeout");
        }
    }

    private static void Graph(WorkflowStep graph)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.GraphNodes)
            if (string.IsNullOrEmpty(node.Key) || node.Key.Contains('/') || node.Key == "end" || !keys.Add(node.Key))
                throw WorkflowBuildError.Invalid(graph, $"Invalid or duplicate flowchart node key '{node.Key}'");
        if (graph.GraphStart == null || !keys.Contains(graph.GraphStart))
            throw WorkflowBuildError.Invalid(graph, "A flowchart requires a start key naming one of its nodes");
        foreach (var node in graph.GraphNodes)
        {
            if (node.Steps.Count < 2 || node.Steps[0].StepType != WorkflowStepType.Enter ||
                node.Steps[^1].StepType != WorkflowStepType.Decision)
                throw WorkflowBuildError.Invalid(graph, $"Node '{node.Key}' requires Enter and Decision picks");
            var author = graph.Id + "/" + node.Key;
            node.Steps[0].Id = author + "/enter";
            node.Steps[0].Name = node.Name;
            var decision = node.Steps[^1];
            decision.Id = author + "/decision";
            var labels = new HashSet<object?>();
            for (var index = 0; index < node.Edges.Count; index++)
            {
                if (node.Edges[index] is not [{ StepType: WorkflowStepType.Edge, Explicit: false } edge])
                    throw WorkflowBuildError.Invalid(graph, $"Node '{node.Key}' has an invalid edge scope");
                edge.Id = author + "/to/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Target(graph, edge);
                if ((edge.IsDefault || decision.OutcomeSelector == null && edge.CompiledCondition == null) &&
                    index != node.Edges.Count - 1)
                    throw WorkflowBuildError.Invalid(edge, "A fallback or unconditional edge must be last");
                if (decision.OutcomeSelector != null && edge.CompiledCondition != null ||
                    decision.OutcomeSelector == null && edge.EdgeLabel != null)
                    throw WorkflowBuildError.Invalid(edge, "A node cannot mix guards and switch cases");
                if (decision.OutcomeSelector != null && !edge.IsDefault && !labels.Add(edge.EdgeLabel))
                    throw WorkflowBuildError.Invalid(edge, "A switch cannot repeat a case label");
            }
            foreach (var exit in Exits(node.Steps))
            {
                if (exit.LastOrDefault() is not { StepType: WorkflowStepType.Edge, Explicit: true })
                    throw WorkflowBuildError.Invalid(graph,
                        $"Every timeout path leaving node '{node.Key}' must end with an explicit transition");
                Body(exit, graph, true);
            }
            Body(node.Steps, graph, true);
        }
    }

    private static IEnumerable<IReadOnlyList<WorkflowStep>> Exits(IReadOnlyList<WorkflowStep> body) =>
        body.SelectMany(step => step.ChildScopes().Where(scope => scope.Kind == "timeout" && scope.Steps.Count > 0))
            .SelectMany(scope => Exits(scope.Steps).Prepend(scope.Steps));

    private static void Target(WorkflowStep graph, WorkflowStep edge)
    {
        if (edge.Target != null && !graph.GraphNodes.Any(node => node.Key == edge.Target))
            throw WorkflowBuildError.Invalid(edge, $"Unknown flowchart destination '{edge.Target}'");
    }
}
