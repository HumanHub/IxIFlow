using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

/// <summary>Each branch changes its own copy. Sum adds their fee changes at the join.</summary>
public static class ParallelQuotes
{
    public static WorkflowDefinition Definition(string join = "all") =>
        new WorkflowBuilder<QuoteData, object>([], "ParallelQuotes-" + join)
            .Parallel(parallel =>
            {
                var branches = parallel
                .Do(branch => branch.Step<AddFee>(step => step
                    .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
                    .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount)))
                .Do(branch => branch.Step<AddFee>(step => step
                    .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
                    .Input(activity => activity.Fee).From(ctx => 5m)
                    .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount)))
                .Combine<decimal, Sum<decimal>>(data => data.Amount);
                if (join == "any")
                    branches.WaitAny();
                if (join == "conditional")
                    branches.WaitConditionally(completeWhenExpression: data => data.Amount >= 12.5m);
            })
            .Build();
}
