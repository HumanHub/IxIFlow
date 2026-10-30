using System.ComponentModel;

namespace IxIFlow.Dsl.Documents;

[Description("A flowchart of keyed bodies and ordered first-match edges.")]
[DocumentKind("flowchart")]
public sealed record FlowchartStepDocument : WorkflowStepDocument
{
    [Description("The key of the first node.")]
    [DocumentRequired]
    public string Start { get; init; } = "";

    [Description("The nodes, addressed by ordinal key rather than declaration position.")]
    [DocumentRequired]
    public List<FlowchartNodeDocument> Nodes { get; init; } = [];
}

[Description("A keyed flowchart body, optional switch selector and ordered edges.")]
public sealed record FlowchartNodeDocument : DocumentNode
{
    [Description("The unique nonempty node key, without slashes; end is reserved.")]
    [DocumentRequired]
    public string Key { get; init; } = "";

    [Description("The display name, excluded from structure identity.")]
    public string? Name { get; init; }

    [Description("The node body, whose last value is available to its decision.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];

    [Description("A pure selector matched against case labels, instead of when guards.")]
    public ExpressionDocument? Switch { get; init; }

    [Description("Edges in first-match order; an unguarded fallback must be last.")]
    public List<FlowchartEdgeDocument> Transitions { get; init; } = [];
}

[Description("A guarded edge, a switch case, or the last fallback edge.")]
public sealed record FlowchartEdgeDocument : DocumentNode
{
    [Description("A pure Boolean guard, available only without switch.")]
    public ExpressionDocument? When { get; init; }

    [Description("A scalar literal or build-time expression matched against the node's switch value.")]
    public object? Case { get; init; }

    [Description(
        "Overrides the inferred fallback flag: true selects Otherwise, false selects Always. Used to preserve the fluent edge's exact structure.")]
    public bool? Default { get; init; }

    [Description("The destination key; end or null completes the graph.")]
    public string? To { get; init; }
}

[Description("A state machine with keyed entry and exit bodies and ordered triggers.")]
[DocumentKind("stateMachine")]
public sealed record StateMachineStepDocument : WorkflowStepDocument
{
    [Description("The key of the first state.")]
    [DocumentRequired]
    public string Start { get; init; } = "";

    [Description("The states, addressed by ordinal key rather than declaration position.")]
    [DocumentRequired]
    public List<StateDocument> States { get; init; } = [];
}

[Description("A state with entry, exit and transitions, or a final state with entry only.")]
public sealed record StateDocument : DocumentNode
{
    [Description("The unique nonempty state key, without slashes; end is reserved.")]
    [DocumentRequired]
    public string Key { get; init; } = "";

    [Description("The display name, excluded from structure identity.")]
    public string? Name { get; init; }

    [Description("Whether completing entry completes the state machine.")]
    public bool Final { get; init; }

    [Description("Steps run once on each state visit.")]
    public List<WorkflowStepDocument> Entry { get; init; } = [];

    [Description(
        "Steps run after selection and before the transition action. An empty array declares an exit scope; omission declares none.")]
    public List<WorkflowStepDocument>? Exit { get; init; }

    [Description("Triggers in arbitration order.")]
    public List<StateTransitionDocument> Transitions { get; init; } = [];
}

[Description("A guarded destination with an optional transition action.")]
public record StateCaseDocument : DocumentNode
{
    [Description("A pure Boolean guard reading the fired value, or incoming value for timers and immediate transitions.")]
    public ExpressionDocument? When { get; init; }

    [Description("The destination key; end or null completes the machine.")]
    public string? To { get; init; }

    [Description("The action steps, run after exit with the fired value as previousStep.")]
    public List<WorkflowStepDocument> Steps { get; init; } = [];
}

[Description("One event, reply, duration or code-activity trigger, or an immediate transition; either a destination or ordered cases.")]
public sealed record StateTransitionDocument : StateCaseDocument
{
    [Description("The keyed event name; requires event.")]
    public string? On { get; init; }

    [Description("The name of a request directly in this state's entry; requires event.")]
    public string? Reply { get; init; }

    [Description("The event or reply CLR type or catalog key.")]
    public string? Event { get; init; }

    [Description("A positive invariant TimeSpan string or a pure expression returning TimeSpan.")]
    public object? After { get; init; }

    [Description("One code activity to race against the other triggers.")]
    public string? Activity { get; init; }

    [Description("The activity contract version, when supplied.")]
    public string ActivityVersion { get; init; } = "";

    [Description("An intake predicate for event and reply triggers, reading event and workflow.")]
    public ExpressionDocument? Matches { get; init; }

    [Description("The suggested event or reply payload when armed.")]
    public object? DefaultPayload { get; init; }

    [Description("Inputs from workflow and the graph's incoming previousStep.")]
    public List<InputMappingDocument> Input { get; init; } = [];

    [Description("Outputs from the event, reply or completed activity into workflow data.")]
    public List<OutputMappingDocument> Output { get; init; } = [];

    [Description("Ordered guarded alternatives instead of a direct to, when and steps.")]
    public List<StateCaseDocument>? Cases { get; init; }
}

[Description("An explicit destination ending a timeout path that leaves a node or non-final state entry.")]
[DocumentKind("transition")]
public sealed record TransitionStepDocument : WorkflowStepDocument
{
    [Description("The destination key; end or null completes the graph.")]
    public string? To { get; init; }
}