using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "G-06")]
public sealed class SteppingScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnOrderedStepSurvivesRepositoryAndHostRecreation(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var registry = new WorkflowVersionRegistry();
        var definition = Workflow.Create<Data>("ConformanceStep").Step<Noop>().WaitFor<Go>("go")
            .Sequence(b => b.Step<Noop>().Step<Noop>()).WithId("target").Step<Noop>().Build();
        var repository = store.NewRepository();
        var first = Services(repository, registry);
        string id; BreakpointStop origin; long generation;
        try
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            await first.GetRequiredService<IWorkflowOperations>().SetBreakpointsAsync(id, [new("bp", "target")], "setter");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "go", new Go())).Status);
            var debug = WorkflowDebugRules.Checkpoint((await repository.GetWorkflowInstanceAsync(id))!);
            origin = debug.Stop!; generation = debug.PauseGeneration;
            Assert.Equal(DebugCommandStatus.Accepted, (await repository.RequestDebugCommandAsync(id,
                new("step", DebugCommandKind.Step, generation, "stepper",
                    ContinuationId: origin.BranchId, StepKind: DebugStepKind.Over))).Status);
        }
        finally
        {
            await first.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await first.DisposeAsync();
            if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
        await using var second = Services(store.NewRepository(), registry);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).Status);
        var secondRepository = second.GetRequiredService<IWorkflowStateRepository>();
        var saved = (await secondRepository.GetWorkflowInstanceAsync(id))!;
        var stepped = WorkflowDebugRules.Checkpoint(saved);
        Assert.Equal("Step", stepped.Stop!.Reason);
        Assert.Equal(origin.ActivationId, stepped.Stepping!.Origin.ActivationId);
        Assert.Equal(generation + 1, stepped.PauseGeneration);
        Assert.Null(saved.RunnableAtUtc);
        Assert.NotNull((await secondRepository.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == "step").AcknowledgedRevision);
        Assert.Equal(DebugCommandStatus.StaleGeneration, (await secondRepository.RequestDebugCommandAsync(id,
            new("stale", DebugCommandKind.Step, generation, "stale",
                ContinuationId: origin.BranchId, StepKind: DebugStepKind.Into))).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await second.GetRequiredService<IWorkflowOperations>().ContinueAsync(id, "continue")).Status);
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
