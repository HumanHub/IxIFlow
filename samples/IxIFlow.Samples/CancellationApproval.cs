using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class CancellationApprovalData { public string State { get; set; } = "pending"; }
public sealed class CancellationApprovalSignal;

/// <summary>Withdraws an unfinished approval when its workflow is cancelled.</summary>
public static class CancellationApproval
{
    public static WorkflowDefinition Definition() => Workflow.Create<CancellationApprovalData>("CancellationApproval")
        .CancellationScope(body => body.WaitFor<CancellationApprovalSignal>("approval")
            .Assign(c => c.WorkflowData.State, c => "approved"),
            onCancelled => onCancelled.Step<Label>(s => s.Input(a => a.Value).From(c => "withdrawn")
                .Output(a => a.Value).To(c => c.WorkflowData.State))).Build();
}
