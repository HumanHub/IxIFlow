using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "timers")]
public sealed class WorkflowDelayTests : IClassFixture<SqliteTestDatabase<WorkflowDelayTests>>
{
    [Fact]
    public async Task WaitAllJoinsAnImmediateBranchAndADelayedBranch()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        using var services = Services(repository, time);
        var definition = Workflow.Create<DelayData>("ParallelDelay")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Delay(TimeSpan.FromMinutes(1)))
                .Do(branch => branch.Step<NoopActivity>()))
            .Step<MarkActivity>()
            .Build();

        var started = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new DelayData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(0, Assert.IsType<DelayData>(started.WorkflowData).Runs);

        time.Advance(TimeSpan.FromMinutes(2));
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance, time);
        await scanner.RecoverOnceAsync(CancellationToken.None);
        await FinishedAsync(services, started.InstanceId);

        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<DelayData>(
            saved.WorkflowDataJson)!.Runs);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task DueDelayRecoversFromSqlOnAnotherHost() => VerifyDueDelayRecoversOnAnotherHost(() => new SqlWorkflowStateRepository(SqlTestDatabase<WorkflowDelayTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task DueDelayRecoversFromPostgreSqlOnAnotherHost() => VerifyDueDelayRecoversOnAnotherHost(() => new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<WorkflowDelayTests>.ConnectionString));

    [Fact]
    public async Task DelayParksWithoutAResumeEventAndCompletesAfterRestart()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var definition = Workflow.Create<DelayData>("DurableDelay")
            .Step<NoopActivity>()
            .Delay(TimeSpan.FromMinutes(1))
            .Step<MarkActivity>()
            .Build();
        string instanceId;
        using (var firstProvider = Services(repository, time))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new DelayData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal(0, Assert.IsType<DelayData>(started.WorkflowData).Runs);
            instanceId = started.InstanceId;
        }

        using var secondProvider = Services(repository, time);
        await secondProvider.GetRequiredService<IWorkflowCatalog>()
            .RegisterWorkflowAsync(definition);
        var rejected = await secondProvider.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, "delay", new object());
        EventAssert.Rejected(rejected, EventRejection.NoOpenWait);
        time.Advance(TimeSpan.FromMinutes(2));
        using var scanner = new WorkflowRecoveryService(
            secondProvider.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance, time);
        await scanner.RecoverOnceAsync(CancellationToken.None);

        var saved = await repository.GetWorkflowInstanceAsync(instanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<DelayData>(
            saved.WorkflowDataJson)!.Runs);
    }

    [Fact]
    public async Task ExecutionTimeoutStopsAWaitingDelay()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        using var services = Services(repository, time);
        var definition = Workflow.Create<DelayData>("TimedDelay")
            .Step<NoopActivity>()
            .Delay(TimeSpan.FromMinutes(2))
            .Step<MarkActivity>()
            .Build();
        var started = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new DelayData(),
                new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMinutes(1) });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        time.Advance(TimeSpan.FromSeconds(90));
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance, time);
        await scanner.RecoverOnceAsync(CancellationToken.None);
        await FinishedAsync(services, started.InstanceId);

        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.TimedOut, saved!.Status);
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<DelayData>(
            saved.WorkflowDataJson)!.Runs);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (time != null)
            services.AddSingleton(time);
        services.AddIxIFlow();
        services.AddSingleton(repository);
        services.AddTransient<NoopActivity>();
        services.AddTransient<MarkActivity>();
        return services.BuildServiceProvider();
    }

    // The host's own timer wakes the instance as the clock moves, so the scan may find it already taken;
    // either way it finishes.
    private static Task<WorkflowExecutionResult> FinishedAsync(IServiceProvider services, string instanceId) =>
        services.GetRequiredService<IWorkflowClient>()
            .AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

    public sealed class DelayData { public int Runs { get; set; } }

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class MarkActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            ((DelayData)context.WorkflowData).Runs++;
            return Task.CompletedTask;
        }
    }
    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public Task DueDelayRecoversFromSqliteOnAnotherHost() => VerifyDueDelayRecoversOnAnotherHost(() => new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowDelayTests>.ConnectionString));

    private static async Task VerifyDueDelayRecoversOnAnotherHost(Func<IWorkflowStateRepository> newRepository)
    {
        var repository = newRepository();
        var definition = Workflow.Create<DelayData>("SqlDurableDelay")
            .Step<NoopActivity>()
            .Delay(TimeSpan.FromSeconds(1))
            .Step<MarkActivity>()
            .Build();
        string? instanceId = null;
        try
        {
            await using (var firstHost = Services(repository))
            {
                var started = await firstHost.GetRequiredService<IWorkflowClient>()
                    .RunAsync(definition, new DelayData());
                Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
                instanceId = started.InstanceId;
            }

            if (repository is IAsyncDisposable firstOwner)
                await firstOwner.DisposeAsync();
            repository = newRepository();
            await using var secondHost = Services(repository);
            await secondHost.GetRequiredService<IWorkflowCatalog>()
                .RegisterWorkflowAsync(definition);
            await Task.Delay(1200);
            using var scanner = new WorkflowRecoveryService(
                secondHost.GetRequiredService<IServiceScopeFactory>(),
                secondHost.GetRequiredService<IWorkflowStateRepository>(),
                NullLogger<WorkflowRecoveryService>.Instance);
            await scanner.RecoverOnceAsync(CancellationToken.None);

            var saved = await repository.GetWorkflowInstanceAsync(instanceId);
            Assert.Equal(WorkflowStatus.Completed, saved!.Status);
            Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<DelayData>(
                saved.WorkflowDataJson)!.Runs);
        }
        finally
        {
            try
            {
                if (instanceId != null)
                    await repository.DeleteWorkflowInstanceAsync(instanceId);
            }
            finally
            {
                if (repository is IAsyncDisposable owner)
                    await owner.DisposeAsync();
            }
        }
    }


}
