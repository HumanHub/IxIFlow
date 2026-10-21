using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "control-flow")]
public sealed class ActivityTriggerExamples
{
    [Fact]
    public async Task DocumentedActivityTriggerMapsAndChoosesItsDestination()
    {
        var collection = new ServiceCollection().AddLogging(); collection.AddIxIFlow();
        await using var services = collection.BuildServiceProvider();
        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(ActivityTriggers.Definition(), new ActivityTriggers.Review());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<ActivityTriggers.Review>(result.WorkflowData).Approved);
    }
}
