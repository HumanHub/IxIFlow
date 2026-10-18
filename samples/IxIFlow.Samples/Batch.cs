using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class BatchData
{
    public List<int> Weights { get; set; } = [2, 3, 4];
    public List<ItemResult<int>> Results { get; set; } = [];
}

[WorkflowActivity("samples.weigh")]
public sealed class Weigh : IAsyncActivity
{
    [ActivityInput]
    public int Weight { get; set; }

    [ActivityOutput]
    public int Grams { get; private set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Grams = Weight * 1000;
        return Task.CompletedTask;
    }
}

/// <summary>Weighs parcels and keeps results in parcel order, including cancelled items.</summary>
public static class Batch
{
    public static WorkflowDefinition Definition(string join = "sequential") =>
        new WorkflowBuilder<BatchData, object>([], "Batch-" + join)
            .ForEach(itemsExpression: ctx => ctx.WorkflowData.Weights,
                (body, parcel) => body
                    .Step<Weigh>(step => step
                        .Input(activity => activity.Weight).From(ctx => ctx.Item(parcel)))
                    .Result(valueExpression: ctx => ctx.PreviousStep.Grams),
                setup =>
                {
                    setup.As("parcel");
                    if (join != "sequential")
                        setup.Parallel(maxConcurrency: 2);
                    if (join == "any")
                        setup.WaitAny();
                    if (join == "conditional")
                        setup.WaitConditionally<int>(completeWhenExpression: (data, results) => results.Count >= 2);
                    setup.Into(ctx => ctx.WorkflowData.Results);
                })
            .Build();
}
