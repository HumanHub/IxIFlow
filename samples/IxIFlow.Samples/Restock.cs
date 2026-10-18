using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class StockData
{
    public int Count { get; set; }
}

public sealed class AddStock : IAsyncActivity
{
    public int Count { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Count++;
        return Task.CompletedTask;
    }
}

/// <summary>Checks before restocking with WhileDo, then checks after one mandatory inspection with DoWhile.</summary>
public static class Restock
{
    public static WorkflowDefinition Definition() =>
        new WorkflowBuilder<StockData, object>([], "Restock")
            .WhileDo(conditionExpression: ctx => ctx.WorkflowData.Count < 2, body => body
                .Step<AddStock>(step => step
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Count)))
            .DoWhile(body => body.Step<AddStock>(step => step
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Count)),
                conditionExpression: ctx => ctx.WorkflowData.Count < 3)
            .Build();
}
