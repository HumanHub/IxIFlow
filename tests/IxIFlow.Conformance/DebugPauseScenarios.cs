using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed class DebugPauseScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ContinueTakesAFreshLeaseOnAnotherHost(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Data>("ConformancePause").Step<Noop>().WaitFor<Go>("go").Build();
        var firstRepository = store.NewRepository();
        var first = Services(firstRepository, registry, "first");
        string id;
        try
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Paused,
                (await first.GetRequiredService<IWorkflowOperations>().PauseAsync(id, "first operator")).Status);
            var commands = first.GetRequiredService<IWorkflowStateRepository>();
            Assert.Equal(DebugCommandStatus.StaleGeneration,
                (await commands.RequestDebugCommandAsync(id, new("stale", DebugCommandKind.Continue, 0, "stale actor"))).Status);
            var row = await first.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(id);
            Assert.Null(row!.RunnableAtUtc);
            var eventResult = await client.ResumeAsync(id, "go", new Go(), "held-event");
            Assert.Equal(WorkflowExecutionStatus.Paused, eventResult.Status);
            Assert.Equal(EventOutcome.Pending, eventResult.Event!.Outcome);
        }
        finally
        {
            // SQLite has one owner: hand its file to a new host after the old one has shut down.
            await first.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await first.DisposeAsync();
            if (firstRepository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
        await using var second = Services(store.NewRepository(), registry, "second");
        var continued = await second.GetRequiredService<IWorkflowOperations>().ContinueAsync(id, "second operator");
        Assert.Equal(WorkflowExecutionStatus.Success, continued.Status);
        var repository = second.GetRequiredService<IWorkflowStateRepository>();
        Assert.Equal(EventOutcome.Consumed, (await repository.GetEventAsync(id, "held-event"))!.Outcome);
        var debug = repository;
        Assert.All((await debug.GetDebugStatesAsync([id]))[id].Commands, command => Assert.NotNull(command.AcknowledgedRevision));
        var trace = await repository.GetTraceAsync(id);
        Assert.Equal("second operator", Assert.IsType<DebugCommandTrace>(trace.Single(e => e.Entry.EntryType == TraceEntryType.Continued).Entry.Payload).Actor);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository, WorkflowCatalog registry, string hostName)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => { options.Debugging = true; options.HostName = hostName; });
        services.AddSingleton(repository);
        services.AddSingleton<IWorkflowCatalog>(registry);
        return services.BuildServiceProvider();
    }
    public sealed class Data;
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
