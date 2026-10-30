using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "E-04")]
public sealed class Slice4ParallelTests
{
    [Theory]
    [InlineData("left", 11)]
    [InlineData("right", 22)]
    public async Task WaitAnyMergesOnlyTheWinningBranchAtRuntime(string winner, int expected)
    {
        await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>();

        var definition = new WorkflowBuilder<Data, object>([], "WinnerMerge")
            .Parallel(parallel => parallel.WaitAny()
                .Do(left => left.Assign(c => c.WorkflowData.Value, _ => 11).WaitFor<string>("left"))
                .Do(right => right.Assign(c => c.WorkflowData.Value, _ => 22).WaitFor<string>("right")))
            .Build();

        var waiting = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        Assert.Equal(0, Assert.IsType<Data>(waiting.WorkflowData).Value);
        var completed = await client.ResumeAsync(waiting.InstanceId, winner, "go");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(expected, Assert.IsType<Data>(completed.WorkflowData).Value);
        Assert.Empty(await client.GetOpenWaitsAsync(new(InstanceId: waiting.InstanceId)));
    }

    public sealed class Data
    {
        public int Value { get; set; }
    }
}