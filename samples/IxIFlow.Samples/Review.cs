using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class ReviewAnswer
{
    public string Customer { get; set; } = "";
}

/// <summary>Waits for a customer name, then invokes a pinned child that adds the fee.</summary>
public static class Review
{
    public static WorkflowDefinition Definition() =>
        new WorkflowBuilder<QuoteData, object>([], "Review")
            .Suspend<ReviewAnswer>("review", configure: wait => wait
                .Output(answer => answer.Customer).To(ctx => ctx.WorkflowData.Customer))
            .Invoke<QuoteData>("ReviewChild", 1, child => child
                .Input(data => data.Customer).From(ctx => ctx.WorkflowData.Customer)
                .Input(data => data.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(data => data.Amount).To(ctx => ctx.WorkflowData.Amount))
            .Build();

    public static WorkflowDefinition Child() =>
        Workflow.Create<QuoteData>("ReviewChild")
            .Step<AddFee>(step => step
                .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount))
            .Build();
}
