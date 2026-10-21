namespace IxIFlow.Core;

/// <summary>A keyed state with entry, exit and ordered transition scopes.</summary>
public sealed class StateMachineState
{
    /// <summary>The ordinal structural key within its graph.</summary>
    public string Key { get; set; } = "";
    /// <summary>The display name, excluded from definition identity.</summary>
    public string Name { get; set; } = "";
    /// <summary>Whether completion of entry ends the machine.</summary>
    public bool Final { get; set; }
    /// <summary>Enter, authored entry steps and, for a non-final state, Arm.</summary>
    public List<WorkflowStep> Steps { get; set; } = [];
    /// <summary>Enter and authored exit steps, or empty when exit is absent.</summary>
    public List<WorkflowStep> ExitSteps { get; set; } = [];
    /// <summary>Triggers in arbitration order.</summary>
    public List<StateMachineTransition> Transitions { get; set; } = [];
    /// <summary>The shared wait branch of an activity-trigger state, when it also has waits.</summary>
    public List<WorkflowStep> ListenSteps { get; set; } = [];
}

/// <summary>One trigger pick and its first-match action scopes.</summary>
public sealed class StateMachineTransition
{
    /// <summary>The executable Trigger scope.</summary>
    public List<WorkflowStep> Steps { get; set; } = [];
    /// <summary>One scope per alternative: Edge followed by authored actions.</summary>
    public List<List<WorkflowStep>> Cases { get; set; } = [];
    /// <summary>The code activity and its Fire pick, for an activity trigger.</summary>
    public List<WorkflowStep> RunSteps { get; set; } = [];
}

/// <summary>The native ways a state can be woken.</summary>
public enum TriggerKind { Event, Reply, Timer, Immediate, Activity }
