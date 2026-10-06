using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class WorkflowDelayTests
{
    [Fact]
    public async Task WaitAllJoinsAnImmediateBranchAndADelayedBranch()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var definition = Workflow.Create<DelayData>("ParallelDelay")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Delay(TimeSpan.FromMilliseconds(300)))
                .Do(branch => branch.Step<NoopActivity>(_ => { })))
            .Step<MarkActivity>(_ => { })
            .Build();

        var started = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new DelayData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(0, Assert.IsType<DelayData>(started.WorkflowData).Runs);

        await Task.Delay(500);
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);

        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<DelayData>(
            saved.WorkflowDataJson)!.Runs);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task DueDelayRecoversFromSqlOnAnotherHost()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var repository = new SqlWorkflowStateRepository(connectionString);
        var definition = Workflow.Create<DelayData>("SqlDurableDelay")
            .Step<NoopActivity>()
            .Delay(TimeSpan.FromSeconds(1))
            .Step<MarkActivity>(_ => { })
            .Build();
        string? instanceId = null;
        try
        {
            using (var firstHost = Services(repository))
            {
                var started = await firstHost.GetRequiredService<IWorkflowEngine>()
                    .ExecuteWorkflowAsync(definition, new DelayData());
                Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
                instanceId = started.InstanceId;
            }

            using var secondHost = Services(new SqlWorkflowStateRepository(connectionString));
            await secondHost.GetRequiredService<IWorkflowVersionRegistry>()
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
            if (instanceId != null)
                await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    public async Task DelayParksWithoutAResumeEventAndCompletesAfterRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<DelayData>("DurableDelay")
            .Step<NoopActivity>()
            .Delay(TimeSpan.FromMilliseconds(500))
            .Step<MarkActivity>(_ => { })
            .Build();
        string instanceId;
        using (var firstProvider = Services(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new DelayData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal(0, Assert.IsType<DelayData>(started.WorkflowData).Runs);
            instanceId = started.InstanceId;
        }

        using var secondProvider = Services(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var rejected = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "delay", new object());
        Assert.False(rejected.EventAccepted);
        await Task.Delay(650);
        using var scanner = new WorkflowRecoveryService(
            secondProvider.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);

        var saved = await repository.GetWorkflowInstanceAsync(instanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<DelayData>(
            saved.WorkflowDataJson)!.Runs);
    }

    [Fact]
    public async Task ExecutionTimeoutStopsAWaitingDelay()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var definition = Workflow.Create<DelayData>("TimedDelay")
            .Step<NoopActivity>()
            .Delay(TimeSpan.FromSeconds(1))
            .Step<MarkActivity>(_ => { })
            .Build();
        var started = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new DelayData(),
                new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(500) });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        await Task.Delay(650);
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);

        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.TimedOut, saved!.Status);
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<DelayData>(
            saved.WorkflowDataJson)!.Runs);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddSingleton(repository);
        services.AddTransient<NoopActivity>();
        services.AddTransient<MarkActivity>();
        return services.BuildServiceProvider();
    }

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
}
