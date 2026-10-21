using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public static class Flowcharts
{
    // snippet: stock-flowchart
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Order>("FulfilOrder")
            .Flowchart(f => f.Start("check")
                .Node("check", n => n.Step<CheckStock>(), edges => edges
                    .When(ctx => ctx.PreviousStep.InStock, to: "ship")
                    .Otherwise(to: "backorder"))
                .Node("ship", n => n.Step<Ship>())
                .Node("backorder", n => n.Delay(TimeSpan.FromDays(1)),
                    edges => edges.Always("check")))
            .Build();
    // end-snippet: stock-flowchart

    public sealed class Order
    {
        public bool StockAvailable { get; set; } = true;
        public bool Shipped { get; set; }
    }

    public sealed class CheckStock : IAsyncActivity
    {
        public bool InStock { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            InStock = ((Order)context.WorkflowData).StockAvailable;
            return Task.CompletedTask;
        }
    }

    public sealed class Ship : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((Order)context.WorkflowData).Shipped = true;
            return Task.CompletedTask;
        }
    }
}
