using System.Globalization;
using IxIFlow.Dsl.Documents;

namespace IxIFlow.Dsl.Compilation;

public sealed partial class WorkflowDocumentValidator
{
    private async Task ValidateGraphAsync(WorkflowStepDocument step, string path, WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics, IWorkflowValidationContext context, CancellationToken token,
        IReadOnlyCollection<string> aliases)
    {
        var roots = new[] { "workflow", "previousStep" }.Concat(aliases).ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var nodes = step is FlowchartStepDocument flow ? flow.Nodes.Select(n => n.Key) :
            ((StateMachineStepDocument)step).States.Select(s => s.Key);
        var collection = step is FlowchartStepDocument ? ".nodes" : ".states";
        var index = 0;
        foreach (var key in nodes)
        {
            if (string.IsNullOrEmpty(key) || key.IndexOf('/') >= 0 || key == "end" || !keys.Add(key))
                Invalid("GRAPH001", $"Invalid or duplicate graph key '{key}'", $"{path}{collection}[{index}].key", diagnostics);
            index++;
        }
        var start = step is FlowchartStepDocument chart ? chart.Start : ((StateMachineStepDocument)step).Start;
        if (!keys.Contains(start)) Invalid("GRAPH002", "Start must name a node or state in this graph", path + ".start", diagnostics);

        if (step is FlowchartStepDocument graph)
        {
            for (var i = 0; i < graph.Nodes.Count; i++)
            {
                var node = graph.Nodes[i];
                var prefix = $"{path}.nodes[{i}]";
                ValidateExpression(node.Switch, diagnostics, prefix + ".switch", roots, document.Code);
                var labels = new HashSet<object?>();
                for (var j = 0; j < node.Transitions.Count; j++)
                {
                    var edge = node.Transitions[j];
                    var edgePath = $"{prefix}.transitions[{j}]";
                    GraphTarget(edge, edge.To, keys, edgePath, diagnostics);
                    ValidateExpression(edge.When, diagnostics, edgePath + ".when", roots, document.Code);
                    var hasCase = Present(edge, "case", edge.Case != null);
                    if (edge.When != null && (node.Switch != null || hasCase) || hasCase && node.Switch == null)
                        Invalid("GRAPH004", "Use when edges or switch/case edges, never both", edgePath, diagnostics);
                    if (edge.Default != null && (edge.When != null || hasCase) || node.Switch != null && edge.Default == false)
                        Invalid("GRAPH004", "Default applies only to an unguarded edge; a switch fallback must be default", edgePath, diagnostics);
                    if (edge.Case is ExpressionDocument label)
                        ValidateExpression(label, diagnostics, edgePath + ".case", [], document.Code);
                    else if (hasCase && edge.Case is not (null or string or bool or int or long or double or decimal))
                        Invalid("GRAPH004", "A case must be a scalar literal or build-time expression", edgePath + ".case", diagnostics);
                    if (hasCase && !labels.Add(edge.Case))
                        Invalid("GRAPH005", "A switch cannot repeat a case label", edgePath + ".case", diagnostics);
                    if (!hasCase && edge.When == null && j != node.Transitions.Count - 1)
                        Invalid("GRAPH005", "A fallback or unconditional edge must be last", edgePath, diagnostics);
                }
            }
            return;
        }

        var machine = (StateMachineStepDocument)step;
        for (var i = 0; i < machine.States.Count; i++)
        {
            var state = machine.States[i];
            var prefix = $"{path}.states[{i}]";
            if (state.Final && (state.Exit != null || state.Transitions.Count > 0))
                Invalid("GRAPH008", "A final state has only an entry body", prefix, diagnostics);
            var waits = state.Transitions.Any(t => t.On != null || t.Reply != null || t.After != null || t.Activity != null);
            if (!state.Final && !waits && !state.Transitions.Any(t => (t.Cases ?? [t]).Any(c => c.When == null)))
                Invalid("GRAPH009", "A non-final state needs a waiting trigger or an unconditional immediate transition", prefix, diagnostics);
            var triggerKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < state.Transitions.Count; j++)
            {
                var transition = state.Transitions[j];
                var triggerPath = $"{prefix}.transitions[{j}]";
                var forms = new[] { Present(transition, "on", transition.On != null), Present(transition, "reply", transition.Reply != null),
                    Present(transition, "after", transition.After != null), Present(transition, "activity", transition.Activity != null) };
                if (forms.Count(value => value) > 1)
                    Invalid("GRAPH006", "A transition has exactly one trigger form, or none for immediate", triggerPath, diagnostics);
                var isEvent = forms[0] || forms[1];
                var key = transition.On ?? transition.Reply;
                if (isEvent)
                {
                    if (string.IsNullOrWhiteSpace(key) || !triggerKeys.Add(key!))
                        Invalid("GRAPH006", "Event and reply keys must be nonblank and unique within a state; use cases", triggerPath, diagnostics);
                    if (string.IsNullOrWhiteSpace(transition.Event) || !await context.HasEventAsync(transition.Event!, token))
                        Invalid("GRAPH006", "Event and reply triggers require a resolved event type", triggerPath + ".event", diagnostics);
                }
                else if (transition.Event != null || transition.Matches != null || transition.DefaultPayload != null ||
                    transition.Activity == null && (transition.Input.Count > 0 || transition.Output.Count > 0))
                    Invalid("GRAPH006", "Only event/reply triggers have event, matches and defaultPayload; mappings also allow activity triggers", triggerPath, diagnostics);
                if (forms[3])
                {
                    var activity = string.IsNullOrWhiteSpace(transition.Activity) ? null : await context.FindActivityAsync(transition.Activity!, token);
                    if (string.IsNullOrWhiteSpace(transition.Activity) || activity == null)
                        Invalid("GRAPH006", "An activity trigger requires a resolved code activity", triggerPath + ".activity", diagnostics);
                    else ValidateActivityDependency(transition.ActivityVersion, activity, document, diagnostics, triggerPath);
                }
                else if (transition.ActivityVersion.Length > 0)
                    Invalid("GRAPH006", "activityVersion requires an activity trigger", triggerPath + ".activityVersion", diagnostics);
                if (transition.After is string duration) Duration(duration, true, triggerPath + ".after", diagnostics);
                else if (transition.After is ExpressionDocument expression)
                    ValidateExpression(expression, diagnostics, triggerPath + ".after", roots, document.Code);
                else if (forms[2])
                    Invalid("GRAPH006", "After must be a positive duration string or a TimeSpan expression", triggerPath + ".after", diagnostics);
                ValidateExpression(transition.Matches, diagnostics, triggerPath + ".matches", new[] { "workflow", "event" }.Concat(aliases).ToArray(), document.Code);
                ValidateInputMappings(transition.Input, diagnostics, triggerPath + ".input", false, aliases, document.Code);
                ValidateOutputMappings(transition.Output, diagnostics, triggerPath + ".output");
                var cases = transition.Cases ?? [transition];
                if (cases.Count == 0 || transition.Cases != null &&
                    (Present(transition, "to", transition.To != null) || Present(transition, "when", transition.When != null) ||
                     Present(transition, "steps", transition.Steps.Count > 0)))
                    Invalid("GRAPH007", "Use a direct to with optional when/steps, or nonempty cases, never both", triggerPath, diagnostics);
                for (var k = 0; k < cases.Count; k++)
                {
                    var alternative = cases[k];
                    var casePath = transition.Cases == null ? triggerPath : $"{triggerPath}.cases[{k}]";
                    GraphTarget(alternative, alternative.To, keys, casePath, diagnostics);
                    ValidateExpression(alternative.When, diagnostics, casePath + ".when", roots, document.Code);
                    if (alternative.When == null && k != cases.Count - 1)
                        Invalid("GRAPH005", "An unguarded alternative must be last", casePath, diagnostics);
                }
                if (transition.Reply != null)
                {
                    if (cases.LastOrDefault()?.When != null)
                        Invalid("GRAPH010", "A reply trigger must end with an unguarded alternative", triggerPath, diagnostics);
                    var requests = state.Entry.OfType<RequestStepDocument>().Where(r => r.Key == transition.Reply).ToArray();
                    if (requests.Length != 1)
                        Invalid("GRAPH010", "A reply trigger names exactly one request directly in this state's entry", triggerPath + ".reply", diagnostics);
                }
                if (!forms.Any(value => value) && cases.Any(c => c.When == null) && (waits || j != state.Transitions.Count - 1))
                    Invalid("GRAPH009", "An unconditional immediate transition must be last and cannot accompany waiting triggers", triggerPath, diagnostics);
            }
        }
    }

    private static bool Present(DocumentNode node, string member, bool hasValue) => node.Members.ContainsKey(member) || hasValue;

    private static void GraphTarget(DocumentNode node, string? target, HashSet<string> keys, string path, List<WorkflowDiagnostic> diagnostics)
    {
        if (!Present(node, "to", target != null))
            Invalid("GRAPH003", "A graph edge requires to (use end or null to complete)", path + ".to", diagnostics);
        else if (target != null && target != "end" && !keys.Contains(target))
            Invalid("GRAPH003", $"Unknown graph destination '{target}'", path + ".to", diagnostics);
    }

    // Placement follows lexical scopes. Only direct timeout children of a graph body
    // leave that body; nested ordinary sequences retain their ordinary timeout behavior.
    private static void ValidateGraphPlacement(IEnumerable<WorkflowStepDocument> source, string path, WorkflowDocument document,
        List<WorkflowDiagnostic> diagnostics, HashSet<string>? targets, bool body, bool timeout, HashSet<string> templates)
    {
        var steps = source.ToArray();
        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            var stepPath = $"{path}[{i}]";
            if (step is ParallelStepDocument { Branches: null } or FlowchartStepDocument { Nodes: null } or StateMachineStepDocument { States: null })
                continue; // Required-member validation already reports these absent collections.
            if (step is TransitionStepDocument transition)
            {
                if (!timeout || targets == null || i != steps.Length - 1)
                    Invalid("GRAPH011", "An explicit transition must end a timeout path leaving a node or non-final state entry", stepPath, diagnostics);
                else GraphTarget(transition, transition.To, targets, stepPath, diagnostics);
            }
            if (step is FlowchartStepDocument or StateMachineStepDocument)
            {
                var keys = new HashSet<string>(step is FlowchartStepDocument chart ? chart.Nodes.Select(n => n.Key) :
                    ((StateMachineStepDocument)step).States.Select(s => s.Key), StringComparer.Ordinal);
                foreach (var child in step.GraphChildScopes())
                {
                    ValidateGraphPlacement(child.Steps, stepPath + child.Path, document, diagnostics,
                        child.Entry ? keys : null, child.Entry, false, templates);
                }
                continue;
            }
            if (step is StepReferenceStepDocument reference && document.Steps.TryGetValue(reference.Ref, out var template) && templates.Add(reference.Ref))
            {
                ValidateGraphPlacement(template.Steps, stepPath + ".steps", document, diagnostics, targets, false, false, templates);
                templates.Remove(reference.Ref);
            }
            foreach (var child in step.ChildScopes())
            {
                var leaves = (body || timeout) && child.Kind == "timeout";
                if (leaves && child.Steps.Count > 0 && child.Steps.Last() is not TransitionStepDocument)
                    Invalid("GRAPH012", "Every timeout path leaving a graph body must end with an explicit transition", stepPath + ".onTimeout", diagnostics);
                ValidateGraphPlacement(child.Steps, ChildPath(step, stepPath, child.Kind, child.Key), document, diagnostics, targets, false, leaves, templates);
            }
        }
    }

    private static string ChildPath(WorkflowStepDocument step, string path, string kind, string? key) => kind switch
    {
        "timeout" => path + ".onTimeout",
        "sequence" or "loop" or "foreach" => path + ".steps",
        "branch" => $"{path}.branches[{key}].steps",
        "catch" => $"{path}.{(step is SagaStepDocument ? "onError" : "catches")}[{key}].steps",
        "outcome" => step is OutcomeStepDocument outcome && int.Parse(key!, CultureInfo.InvariantCulture) < outcome.Branches.Count
            ? $"{path}.branches[{key}].steps" : path + ".default",
        _ => path + "." + kind
    };
}
