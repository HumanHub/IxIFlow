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

    // Two store choices would silently end on the last; the second is refused.
    [Fact]
    public void ASecondStoreChoiceIsRefused()
    {
        var builder = new ServiceCollection().AddLogging()
            .AddIxIFlow(options => options.UseCustomStateRepository = typeof(InMemoryWorkflowStateRepository));

        var error = Assert.Throws<InvalidOperationException>(() => builder.UseSqlServer("Server=unused"));

        Assert.Contains("choose one store", error.Message);
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

    [Fact]
    public void PostgreSqlIsRefusedAsASecondStoreChoice()
    {
        var builder = new ServiceCollection().AddLogging().AddIxIFlow().UseSqlServer("Server=unused");

        var error = Assert.Throws<InvalidOperationException>(() => builder.UsePostgreSql("Host=unused"));

        Assert.Contains("choose one store", error.Message);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task UsePostgreSqlKeepsInstancesInTheDatabase()
    {
        var connectionString = PostgreSqlTestDatabase<StoreBuilderTests>.ConnectionString;
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow().UsePostgreSql(connectionString);
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("StoredInPostgreSql").Step<Noop>().Build(), new Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        // The store also wakes the host when an instance becomes runnable (C-13).
        Assert.Same(provider.GetRequiredService<IWorkflowStateRepository>(),
            provider.GetRequiredService<IRunnableWakeSource>());
        Assert.IsType<PostgreSqlWorkflowStateRepository>(provider.GetRequiredService<IWorkflowStateRepository>());
        var saved = await new PostgreSqlWorkflowStateRepository(connectionString).GetWorkflowInstanceAsync(result.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
    }

    public sealed class Data;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
