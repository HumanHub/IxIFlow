using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.Generators.Tests;

public class FlowData
{
    public int Value { get; set; }
    public int Result { get; set; }
    public string? Note { get; set; }
    public List<int> Values { get; set; } = [];
    public List<ItemResult<int>> Results { get; set; } = [];
}

public sealed class DerivedFlowData : FlowData;

public sealed class Answer
{
    public int Value { get; set; }
}

[WorkflowActivity("test.multiply")]
public sealed class Multiply : IAsyncActivity
{
    [WorkflowInput(Required = true)]
    public int Value { get; set; }
    [WorkflowInput(Default = "2")]
    public int Factor { get; set; } = 2;
    [WorkflowOutput]
    public int Result { get; private set; }
    public string? Internal { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Result = Value * Factor;
        return Task.CompletedTask;
    }
}
