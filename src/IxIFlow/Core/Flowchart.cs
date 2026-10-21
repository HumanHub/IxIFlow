namespace IxIFlow.Core;

/// <summary>A keyed flowchart body and its ordered edge scopes.</summary>
public sealed class FlowchartNode
{
    /// <summary>The node's ordinal key within its graph.</summary>
    public string Key { get; set; } = "";
    /// <summary>The display name, excluded from definition identity.</summary>
    public string Name { get; set; } = "";
    /// <summary>The entry pick, authored body and trailing decision.</summary>
    public List<WorkflowStep> Steps { get; set; } = [];
    /// <summary>One scope per edge, in first-match order; each contains its edge pick.</summary>
    public List<List<WorkflowStep>> Edges { get; set; } = [];
}

/// <summary>The reason a graph chooses or completes a transition.</summary>
public enum TransitionCause
{
    /// <summary>A guard matched.</summary>
    When,
    /// <summary>A switch label matched.</summary>
    Case,
    /// <summary>The fallback edge matched.</summary>
    Default,
    /// <summary>An unconditional edge was selected.</summary>
    Always,
    /// <summary>No edge matched, so the graph ended.</summary>
    NoTransition,
    /// <summary>A timeout path explicitly selected its destination.</summary>
    Explicit
}
