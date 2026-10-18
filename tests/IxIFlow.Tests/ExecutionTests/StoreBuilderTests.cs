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
public sealed class StoreBuilderTests : IClassFixture<SqliteTestDatabase<StoreBuilderTests>>
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
    public Task UseSqlServerKeepsInstancesInTheDatabase() => VerifyBuilderKeepsInstancesInTheDatabase<SqlWorkflowStateRepository>(builder => builder.UseSqlServer(SqlTestDatabase<StoreBuilderTests>.ConnectionString), () => new SqlWorkflowStateRepository(SqlTestDatabase<StoreBuilderTests>.ConnectionString), "StoredInSqlServer", expectWakeSource: false);

    [Fact]
    public void PostgreSqlIsRefusedAsASecondStoreChoice()
    {
        var builder = new ServiceCollection().AddLogging().AddIxIFlow().UseSqlServer("Server=unused");

        var error = Assert.Throws<InvalidOperationException>(() => builder.UsePostgreSql("Host=unused"));

        Assert.Contains("choose one store", error.Message);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task UsePostgreSqlKeepsInstancesInTheDatabase() => VerifyBuilderKeepsInstancesInTheDatabase<PostgreSqlWorkflowStateRepository>(builder => builder.UsePostgreSql(PostgreSqlTestDatabase<StoreBuilderTests>.ConnectionString), () => new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<StoreBuilderTests>.ConnectionString), "StoredInPostgreSql", expectWakeSource: true);

    public sealed class Data;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public Task UseSqliteKeepsInstancesInTheDatabase() => VerifyBuilderKeepsInstancesInTheDatabase<SqliteWorkflowStateRepository>(builder => builder.UseSqlite(SqliteTestDatabase<StoreBuilderTests>.ConnectionString), () => new SqliteWorkflowStateRepository(SqliteTestDatabase<StoreBuilderTests>.ConnectionString), "StoredInSqlite", expectWakeSource: false);


    private static async Task VerifyBuilderKeepsInstancesInTheDatabase<TStore>(Action<IIxIFlowBuilder> chooseStore, Func<IWorkflowStateRepository> reopen, string workflowName, bool expectWakeSource) where TStore : IWorkflowStateRepository
    {
        var services = new ServiceCollection().AddLogging();
        chooseStore(services.AddIxIFlow());
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>(workflowName).Step<Noop>().Build(), new Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        // PostgreSQL also registers the store as the host wake source (C-13).
        if (expectWakeSource)
            Assert.Same(provider.GetRequiredService<IWorkflowStateRepository>(),
                provider.GetRequiredService<IRunnableWakeSource>());
        Assert.IsType<TStore>(provider.GetRequiredService<IWorkflowStateRepository>());
        await provider.DisposeAsync();
        var repository = reopen();
        try
        {
            var saved = await repository.GetWorkflowInstanceAsync(result.InstanceId);
            Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        }
        finally
        {
            if (repository is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }


}
