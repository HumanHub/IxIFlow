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
                        "An explicit transition must end a timeout path leaving a node or non-final state");

                Target(graph, step);
            }

            if (step.StepType is WorkflowStepType.Flowchart or WorkflowStepType.StateMachine)
            {
                if (step.StepType == WorkflowStepType.Flowchart)
                    Graph(step);
                else
                    Machine(step);

                continue;
            }

            foreach (var scope in step.ChildScopes().Where(scope => scope.Kind != "inline"))
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
            var labels = new List<object?>();

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

                if (decision.OutcomeSelector != null && !edge.IsDefault &&
                    labels.Any(label => decision.SwitchEquals!(label, edge.EdgeLabel)))
                    throw WorkflowBuildError.Invalid(edge, "SWITCH003: a switch cannot repeat a case label");

                if (decision.OutcomeSelector != null && !edge.IsDefault)
                    labels.Add(edge.EdgeLabel);
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

    private static void Machine(WorkflowStep graph)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var state in graph.GraphStates)
            if (string.IsNullOrEmpty(state.Key) || state.Key.Contains('/') || state.Key == "end" || !keys.Add(state.Key))
                throw WorkflowBuildError.Invalid(graph, $"Invalid or duplicate state key '{state.Key}'");

        if (graph.GraphStart == null || !keys.Contains(graph.GraphStart))
            throw WorkflowBuildError.Invalid(graph, "A state machine requires a start key naming one of its states");

        foreach (var state in graph.GraphStates)
        {
            if (state.Steps.Count == 0 || state.Steps[0].StepType != WorkflowStepType.Enter ||
                !state.Final && state.Steps[^1].StepType != WorkflowStepType.Arm)
                throw WorkflowBuildError.Invalid(graph, $"State '{state.Key}' requires Enter and a non-final state requires Arm");

            if (state.Final && (state.Transitions.Count != 0 || state.ExitSteps.Count != 0 ||
                    state.Steps.Any(step => step.StepType == WorkflowStepType.Arm)))
                throw WorkflowBuildError.Invalid(graph, $"Final state '{state.Key}' has only an entry body");

            if (!state.Final && state.Transitions.All(t => t.Steps[0].TriggerKind == TriggerKind.Immediate) &&
                !state.Transitions.Any(t => t.Cases.Any(c => c[0].CompiledCondition == null)))
                throw WorkflowBuildError.Invalid(graph, $"State '{state.Key}' has only guarded immediate transitions and cannot wait");

            var author = graph.Id + "/" + state.Key;
            state.Steps[0].Id = author + "/enter";
            state.Steps[0].Name = state.Name;

            if (!state.Final)
                state.Steps[^1].Id = author + "/arm";

            if (state.ExitSteps.Count > 0)
            {
                if (state.ExitSteps[0].StepType != WorkflowStepType.Enter)
                    throw WorkflowBuildError.Invalid(graph, "An exit body requires Enter");

                state.ExitSteps[0].Id = author + "/exit/enter";
            }

            var triggerKeys = new HashSet<string>(StringComparer.Ordinal);

            for (var index = 0; index < state.Transitions.Count; index++)
            {
                var transition = state.Transitions[index];

                if (transition.Steps is not [{ StepType: WorkflowStepType.Trigger } trigger] || transition.Cases.Count == 0)
                    throw WorkflowBuildError.Invalid(graph, "A transition requires one trigger and at least one alternative");

                trigger.Id = author + "/on/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (trigger.TriggerKind == TriggerKind.Activity)
                {
                    if (trigger.ActivityType == null || !typeof(IAsyncActivity).IsAssignableFrom(trigger.ActivityType) ||
                        trigger.ActivityType.GetInterfaces().Any(t =>
                            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IRequestActivity<>)) ||
                        transition.RunSteps is not
                            [{ StepType: WorkflowStepType.Activity } activity, { StepType: WorkflowStepType.Fire } fire] ||
                        activity.ActivityType != trigger.ActivityType)
                        throw WorkflowBuildError.Invalid(trigger,
                            "An activity trigger requires one code activity, cannot be a request, and ends with Fire");

                    activity.Id = trigger.Id + "/run";
                    fire.Id = trigger.Id + "/run/fire";
                    Body(transition.RunSteps, null, false);
                }
                else if (transition.RunSteps.Count != 0 || trigger.ActivityType != null)
                    throw WorkflowBuildError.Invalid(trigger, "Only activity triggers have a run branch");

                if (!Enum.IsDefined(trigger.TriggerKind) || trigger.WaitTimeout != null || trigger.TimeoutSteps.Count > 0)
                    throw WorkflowBuildError.Invalid(trigger, "A trigger has one native form and no timeout branch; use After");

                if (trigger.TriggerKind is TriggerKind.Event or TriggerKind.Reply &&
                    (trigger.ResumeEventType == null || trigger.StepMetadata.GetValueOrDefault("WaitKey") is not string { Length: > 0 }))
                    throw WorkflowBuildError.Invalid(trigger, "An event or reply trigger needs a key and event type");

                if (trigger.TriggerKind is TriggerKind.Event or TriggerKind.Reply &&
                    !triggerKeys.Add((string)trigger.StepMetadata["WaitKey"]))
                    throw WorkflowBuildError.Invalid(trigger,
                        $"Duplicate trigger key '{trigger.StepMetadata["WaitKey"]}' in state '{state.Key}'; use ordered cases under one trigger");

                if (trigger.TriggerKind is not (TriggerKind.Event or TriggerKind.Reply) &&
                    (trigger.ResumeEventType != null || trigger.StepMetadata.ContainsKey("WaitKey") ||
                        trigger.TriggerKind != TriggerKind.Activity &&
                        (trigger.InputMappings.Count > 0 || trigger.OutputMappings.Count > 0) ||
                        trigger.DefaultPayload != null || trigger.CompiledCondition != null))
                    throw WorkflowBuildError.Invalid(trigger,
                        "Only event and reply triggers have intake predicates, suggestions and mappings");

                if (trigger.TriggerKind != TriggerKind.Timer &&
                    (trigger.DelayDuration != null || trigger.AfterExpression != null || trigger.After != null))
                    throw WorkflowBuildError.Invalid(trigger, "Only timer triggers have an After duration");

                if ((trigger.TriggerKind == TriggerKind.Reply) != (trigger.ReplyToRequest != null))
                    throw WorkflowBuildError.Invalid(trigger, "Only reply triggers name a request");

                if (trigger.TriggerKind == TriggerKind.Timer &&
                    ((trigger.DelayDuration != null) == (trigger.AfterExpression != null) ||
                        trigger.DelayDuration is { } duration && duration <= TimeSpan.Zero ||
                        trigger.AfterExpression != null && trigger.After == null))
                    throw WorkflowBuildError.Invalid(trigger, "After requires one positive duration or duration expression");

                for (var alternative = 0; alternative < transition.Cases.Count; alternative++)
                {
                    var body = transition.Cases[alternative];

                    if (body.FirstOrDefault() is not { StepType: WorkflowStepType.Edge, Explicit: false } edge)
                        throw WorkflowBuildError.Invalid(trigger, "A transition alternative starts with an Edge");

                    edge.Id = trigger.Id + "/case/" + alternative.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Target(graph, edge);

                    if (edge.CompiledCondition == null && alternative != transition.Cases.Count - 1)
                        throw WorkflowBuildError.Invalid(edge, "An unguarded alternative must be last");

                    Body(body, null, false);
                }

                if (trigger.TriggerKind == TriggerKind.Reply && transition.Cases[^1][0].CompiledCondition != null)
                    throw WorkflowBuildError.Invalid(trigger, "A reply trigger must end with an unguarded alternative");

                if (trigger.TriggerKind == TriggerKind.Immediate && transition.Cases.Any(c => c[0].CompiledCondition == null) &&
                    index != state.Transitions.Count - 1)
                    throw WorkflowBuildError.Invalid(trigger, "An unconditional immediate transition must be last");

                if (trigger.TriggerKind == TriggerKind.Immediate && transition.Cases.Any(c => c[0].CompiledCondition == null) &&
                    state.Transitions.Any(t => t.Steps[0].TriggerKind != TriggerKind.Immediate))
                    throw WorkflowBuildError.Invalid(trigger,
                        "An unconditional immediate transition cannot be combined with event, reply or timer triggers, or an activity trigger");
            }

            var listener = state.Transitions.Any(t => t.Steps[0].TriggerKind == TriggerKind.Activity) &&
                state.Transitions.Any(t => t.Steps[0].TriggerKind is TriggerKind.Event or TriggerKind.Reply or TriggerKind.Timer);

            if (listener)
            {
                if (state.ListenSteps is not [{ StepType: WorkflowStepType.Listen } listen, { StepType: WorkflowStepType.Fire } fire])
                    throw WorkflowBuildError.Invalid(graph, "A mixed activity-trigger state requires Listen and Fire");

                listen.Id = author + "/listen";
                fire.Id = author + "/listen/fire";
            }
            else if (state.ListenSteps.Count != 0)
                throw WorkflowBuildError.Invalid(graph, "A listener requires both activity and wait triggers");

            foreach (var exit in Exits(state.Steps))
            {
                if (!state.Final && exit.LastOrDefault() is not { StepType: WorkflowStepType.Edge, Explicit: true })
                    throw WorkflowBuildError.Invalid(graph,
                        $"Every timeout path leaving state '{state.Key}' must end with an explicit transition");
            }

            Body(state.Steps, state.Final ? null : graph, !state.Final);
            Body(state.ExitSteps, null, false);
        }
    }

    private static IEnumerable<IReadOnlyList<WorkflowStep>> Exits(IReadOnlyList<WorkflowStep> body) =>
        body.SelectMany(step => step.ChildScopes().Where(scope => scope.Kind == "timeout" && scope.Steps.Count > 0))
            .SelectMany(scope => Exits(scope.Steps).Prepend(scope.Steps));

    private static void Target(WorkflowStep graph, WorkflowStep edge)
    {
        if (edge.Target != null && !(graph.StepType == WorkflowStepType.Flowchart
                ? graph.GraphNodes.Any(node => node.Key == edge.Target)
                : graph.GraphStates.Any(state => state.Key == edge.Target)))
            throw WorkflowBuildError.Invalid(edge, $"Unknown graph destination '{edge.Target}'");
    }
}