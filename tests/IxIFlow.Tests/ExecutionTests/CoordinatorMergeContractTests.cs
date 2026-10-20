using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "C-07")]
[Trait("Spec", "H-04")]
public sealed class CoordinatorMergeContractTests : IClassFixture<SqliteTestDatabase<CoordinatorMergeContractTests>>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TerminationFallbackClaimsOnlyOnAHostMeetingEveryDemand(bool sqlite, bool missingDefinition)
    {
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<CoordinatorMergeContractTests>.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
            var definition = Workflow.Create<Data>("PlacedTermination").Demand("gpu", "cuda").Step<Noop>().Build();
            await using var matching = Services(store, "cuda");
            await matching.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            await using var wrong = Services(store, missingDefinition ? "cuda" : "cpu");
            if (!missingDefinition)
                await wrong.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
            ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), scopes, DateTime.UtcNow);
            await store.SeedAsync(instance);
            var accepted = await store.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "stop" });
            Assert.True(accepted.Accepted);
            Assert.NotNull(accepted.Notice);
            Assert.True(accepted.Notice.Claimable);
            var demands = await store.GetWorkflowDemandsAsync(instance.InstanceId);
            Assert.Contains(new WorkflowDemand("gpu", "cuda"), demands);
            Assert.Equal(DemandSet.Create(demands).Id, accepted.Notice.DemandSetId);

            Assert.False(await wrong.GetRequiredService<WorkflowHost.InstanceOperations>().FinishTerminationAsync(instance.InstanceId));
            var unchanged = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            Assert.Equal(WorkflowStatus.Running, unchanged.Status);
            Assert.Equal(instance.Revision, unchanged.Revision);
            Assert.Empty(await store.GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(wrong)));
            Assert.Contains(await store.GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(matching)),
                candidate => candidate.InstanceId == instance.InstanceId);

            Assert.True(await matching.GetRequiredService<WorkflowHost.InstanceOperations>().FinishTerminationAsync(instance.InstanceId));
            var terminated = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            Assert.Equal(WorkflowStatus.Terminated, terminated.Status);
            Assert.Equal(instance.Revision + 1, terminated.Revision);
            Assert.Single(await store.GetTraceAsync(instance.InstanceId), entry => entry.Entry.EntryType == TraceEntryType.WorkflowTerminated);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, string gpu)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow(options => options.Capabilities["gpu"] = gpu);
        return services.BuildServiceProvider();
    }

    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
