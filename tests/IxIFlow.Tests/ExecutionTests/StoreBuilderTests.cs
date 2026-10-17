using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The store is chosen on the builder <c>AddIxIFlow</c> returns; without a choice, instances live in memory (D-21).
/// </summary>
[Trait("Feature", "registration")]
[Trait("Spec", "C-11")]
public sealed class StoreBuilderTests
{
    [Fact]
    public void WithoutAChoiceInstancesLiveInMemory()
    {
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();

        Assert.IsType<InMemoryWorkflowStateRepository>(provider.GetRequiredService<IWorkflowStateRepository>());
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task UseSqlServerKeepsInstancesInTheDatabase()
    {
        var connectionString = SqlTestDatabase<StoreBuilderTests>.ConnectionString;
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow().UseSqlServer(connectionString);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("StoredInSqlServer").Step<Noop>().Build(), new Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.IsType<SqlWorkflowStateRepository>(provider.GetRequiredService<IWorkflowStateRepository>());
        var saved = await new SqlWorkflowStateRepository(connectionString).GetWorkflowInstanceAsync(result.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
    }

    public sealed class Data;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
