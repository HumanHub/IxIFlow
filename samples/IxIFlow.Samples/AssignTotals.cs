using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class AssignmentTotalsData
{
    public decimal Price { get; set; } = 12.5m;
    public int Quantity { get; set; } = 3;
    public decimal Total { get; set; }
}

/// <summary>Calculates workflow data without creating an activity.</summary>
[Workflow("AssignTotals")]
public sealed class AssignTotals : IWorkflow<AssignmentTotalsData>
{
    public static WorkflowDefinition Definition()
    {
        var builder = Workflow.Create<AssignmentTotalsData>("AssignTotals");
        new AssignTotals().Build(builder);

        return builder.Build();
    }

    // snippet: assign-totals
    public void Build(IWorkflowBuilder<AssignmentTotalsData> builder) =>
        builder.Assign(ctx => ctx.WorkflowData.Total,
                ctx => ctx.WorkflowData.Price * ctx.WorkflowData.Quantity)
            .WithId("total");

    // end-snippet: assign-totals
}