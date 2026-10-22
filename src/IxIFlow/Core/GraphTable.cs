using System.Globalization;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>A graph's topology and designer metadata, addressed by executable catalog ids.</summary>
public sealed record GraphTableEntry(string CatalogId, WorkflowStepType Kind, string Start,
    IReadOnlyList<GraphNodeEntry> Nodes, IReadOnlyList<GraphTransitionEntry> Transitions);

/// <summary>A keyed node or state and its entry, body and optional exit scope.</summary>
public sealed record GraphNodeEntry(string Key, string EnterStepId, string Name, bool Final,
    string? SourceLocation, string BodyScopeId, string? ExitScopeId);

/// <summary>An ordered flowchart edge or state trigger with ordered cases.</summary>
public sealed record GraphTransitionEntry(string? TriggerStepId, string From, TransitionCause Kind,
    string? Key, string? EventType, string? After, string? ActivityType, IReadOnlyList<GraphCaseEntry> Cases);

/// <summary>A destination and its executable edge, guard, immutable JSON label snapshot and action metadata.</summary>
public sealed record GraphCaseEntry(string EdgeStepId, string? To, int Order, bool Guarded,
    object? Label, bool HasAction, string? SourceLocation);

/// <summary>The flowcharts and state machines in one definition, including nested graphs.</summary>
public sealed class GraphTable : IReadOnlyList<GraphTableEntry>
{
    private readonly GraphTableEntry[] _entries;
    internal GraphTable(WorkflowScopeCatalog catalog)
    {
        _entries = catalog.AllSteps.Where(pair => pair.Value.StepType is WorkflowStepType.Flowchart or WorkflowStepType.StateMachine)
            .Select(pair => Build(pair.Key, pair.Value)).ToArray();

        GraphTableEntry Build(string id, WorkflowStep graph)
        {
            var nodes = new List<GraphNodeEntry>();
            var transitions = new List<GraphTransitionEntry>();
            foreach (var node in graph.GraphNodes)
            {
                var scope = catalog.NodeScope(graph, node.Key);
                nodes.Add(new(node.Key, catalog.Id(node.Steps[0]), node.Name, false,
                    node.Steps[0].SourceLocation, scope, null));
                for (var i = 0; i < node.Edges.Count; i++)
                {
                    var edge = node.Edges[i][0];
                    var kind = edge.IsDefault ? TransitionCause.Default : edge.ConditionExpression != null ? TransitionCause.When :
                        node.Steps[^1].OutcomeExpression != null ? TransitionCause.Case : TransitionCause.Always;
                    transitions.Add(new(null, node.Key, kind, null, null, null, null, Array.AsReadOnly(new[] { Case(node.Edges[i], i) })));
                }
                Explicit(node.Key, node.Steps);
            }
            foreach (var state in graph.GraphStates)
            {
                nodes.Add(new(state.Key, catalog.Id(state.Steps[0]), state.Name, state.Final,
                    state.Steps[0].SourceLocation, catalog.StateScope(graph, state.Key),
                    state.ExitSteps.Count == 0 ? null : catalog.StateExitScope(graph, state.Key)));
                foreach (var transition in state.Transitions)
                {
                    var trigger = transition.Steps[0];
                    var kind = trigger.TriggerKind switch
                    {
                        TriggerKind.Event => TransitionCause.Event, TriggerKind.Reply => TransitionCause.Reply,
                        TriggerKind.Timer => TransitionCause.Timer, TriggerKind.Activity => TransitionCause.Activity,
                        _ => TransitionCause.Immediate
                    };
                    transitions.Add(new(catalog.Id(trigger), state.Key, kind,
                        trigger.StepMetadata.GetValueOrDefault("WaitKey") as string,
                        trigger.ResumeEventType == null ? null : WorkflowTypeIdentity.StableName(trigger.ResumeEventType),
                        trigger.DelayDuration?.ToString("c", CultureInfo.InvariantCulture) ?? trigger.AfterExpression?.ToString(),
                        trigger.ActivityType == null ? null : WorkflowTypeIdentity.StableName(trigger.ActivityType),
                        Array.AsReadOnly(transition.Cases.Select((body, order) => Case(body, order)).ToArray())));
                }
                Explicit(state.Key, state.Steps);
            }
            return new(id, graph.StepType, graph.GraphStart!, nodes.AsReadOnly(), transitions.AsReadOnly());

            void Explicit(string from, IEnumerable<WorkflowStep> body)
            {
                foreach (var step in body)
                {
                    if (step.StepType is WorkflowStepType.Flowchart or WorkflowStepType.StateMachine) continue;
                    if (step.StepType == WorkflowStepType.Edge && step.Explicit)
                        transitions.Add(new(null, from, TransitionCause.Explicit, null, null, null, null, Array.AsReadOnly(new[] { Case([step], 0) })));
                    foreach (var child in step.ChildScopes().Where(scope => scope.Kind != "inline")) Explicit(from, child.Steps);
                }
            }
        }

        GraphCaseEntry Case(IReadOnlyList<WorkflowStep> body, int order)
        {
            var edge = body[0];
            return new(catalog.Id(edge), edge.Target, order, edge.ConditionExpression != null,
                edge.EdgeLabel == null ? null : WorkflowJson.SerializeToElement(edge.EdgeLabel), body.Count > 1, edge.SourceLocation);
        }
    }

    public int Count => _entries.Length;
    public GraphTableEntry this[int index] => _entries[index];
    public IEnumerator<GraphTableEntry> GetEnumerator() => ((IEnumerable<GraphTableEntry>)_entries).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
