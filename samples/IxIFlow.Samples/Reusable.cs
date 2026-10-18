using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

/// <summary>The YAML version reuses local activity and step templates and a C# expression asset.</summary>
public static class Reusable
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<QuoteData>("Reusable")
            .Step<AddFee>(step => step
                .Input(activity => activity.Fee).From(ctx => 1)
                .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
                .Input(activity => activity.Amount).From(ctx => System.Math.Abs(ctx.WorkflowData.Amount))
                .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount))
            .Step<StampTime>(step => step
                .Input(activity => activity.At).From(ctx => ctx.UtcNow)
                .Output(activity => activity.At).To(ctx => ctx.WorkflowData.PricedAt))
            .Sequence(sequence => sequence.Step<Label>(step => step
                .Input(activity => activity.Value).From(ctx => (string)("quoted"))
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Build();
}
