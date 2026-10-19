using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed class OperationsScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OperatorsSearchInspectWaitsAndTerminateThroughTheHost(string profile)
    {
        await using var profileStore = await StoreProfiles.CreateAsync(profile);
        var services = new ServiceCollection();
        services.AddLogging(); services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        services.AddSingleton(profileStore.NewRepository());
        services.AddTransient<Begin>();
        await using var host = services.BuildServiceProvider();
        var client = host.GetRequiredService<IWorkflowClient>();
        var operations = host.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("OperationsConformance").Step<Begin>().WaitFor<Approval>("approve").Build();
        var start = await client.RunAsync(definition, new Data());
        Assert.Equal(start.InstanceId, Assert.Single((await operations.SearchAsync(new InstanceQuery(WorkflowName: definition.Name))).Items).InstanceId);
        Assert.Equal(WorkflowStatus.Suspended, (await operations.GetAsync(start.InstanceId))!.Status);
        Assert.Equal("approve", Assert.Single(await operations.GetWaitsAsync([start.InstanceId])).Key);
        var first = await operations.GetTraceAsync(start.InstanceId, limit: 1);
        var next = await operations.GetTraceAsync(start.InstanceId, first[0].Sequence, 1);
        Assert.True(next[0].Sequence > first[0].Sequence);
        var stopped = await operations.TerminateAsync(start.InstanceId, "operator", actor: "conformance");
        Assert.Equal(WorkflowStatus.Terminated, stopped.Status);
        Assert.Empty(await operations.GetWaitsAsync([start.InstanceId]));
        Assert.Equal(WorkflowExecutionStatus.Terminated, (await client.AwaitAsync(start.InstanceId, WorkflowAwaitCondition.Finished)).Status);
    }

    public sealed class Data;
    public sealed class Approval;
    public sealed class Begin : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
