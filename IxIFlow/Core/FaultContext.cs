namespace IxIFlow.Core;

/// <summary>The saved value made available to a catch handler.</summary>
public sealed class FaultContext<TWorkflowData, TFault, TPreviousStepData>
    : WorkflowContext<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
    public TFault Fault { get; set; } = default!;
}

/// <summary>Marker used by a catch handler that does not request exception data.</summary>
public sealed class EmptyFault;
