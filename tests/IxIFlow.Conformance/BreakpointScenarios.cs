using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "G-04")]
public sealed class BreakpointScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task BreakpointsCountsAndSkipSurviveAHostHandoff(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var registry = new WorkflowVersionRegistry();
        var definition = Workflow.Create<Data>("ConformanceBreakpoint").Step<Noop>().WaitFor<Go>("go")
            .Step<Noop>().WithId("target").Build();
        var repository = store.NewRepository();
        var first = Services(repository, registry);
        string id;
        try
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            var ops = first.GetRequiredService<IWorkflowOperations>();
            await ops.SetBreakpointsAsync(id, [new("bp", "target")], "setter");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "go", new Go())).Status);
            Assert.Equal(1, Assert.Single(await ops.GetBreakpointsAsync(id)).HitCount);
            Assert.NotNull(WorkflowDebugRules.Checkpoint((await repository.GetWorkflowInstanceAsync(id))!).Stop);
            Assert.Null((await repository.GetWorkflowInstanceAsync(id))!.RunnableAtUtc);
        }
        finally
        {
            await first.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await first.DisposeAsync();
            if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
        await using var second = Services(store.NewRepository(), registry);
        var operations = second.GetRequiredService<IWorkflowOperations>();
        Assert.Equal(1, Assert.Single(await operations.GetBreakpointsAsync(id)).HitCount);
        Assert.Equal(WorkflowExecutionStatus.Success, (await operations.ContinueAsync(id, "continuer")).Status);
        Assert.Equal(1, Assert.Single(await operations.GetBreakpointsAsync(id)).HitCount);
        var secondRepository = second.GetRequiredService<IWorkflowStateRepository>();
        Assert.All((await secondRepository.GetDebugStatesAsync([id]))[id].Commands, c => Assert.NotNull(c.AcknowledgedRevision));
        Assert.Single(await secondRepository.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.BreakpointHit);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository, WorkflowVersionRegistry registry)
    {
        var services = new ServiceCollection().AddLogging(); services.AddIxIFlow(o => o.Debugging = true);
        services.AddSingleton(repository); services.AddSingleton<IWorkflowVersionRegistry>(registry);
        return services.BuildServiceProvider();
    }
    public sealed class Data;
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
