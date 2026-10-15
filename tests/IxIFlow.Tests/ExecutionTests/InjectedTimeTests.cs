using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Timers follow the injected TimeProvider, so they can be tested without real waiting (spec A-06).
/// </summary>
[Trait("Feature", "timers")]
[Trait("Spec", "A-06")]
public sealed class InjectedTimeTests
{
    [Fact]
    public async Task DelayCompletesOnlyWhenInjectedTimeReachesItsDeadline()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var definition = Workflow.Create<TimeData>("InjectedTimeDelay")
            .Step<NoopActivity>()
            .Delay(TimeSpan.FromHours(1))
            .Step<MarkActivity>(step => step
                .Input(activity => activity.Value).From(_ => "after-delay")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))
            .Build();

        var started = await Engine(services).ExecuteWorkflowAsync(definition, new TimeData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        time.Advance(TimeSpan.FromMinutes(59));
        await RecoverOnceAsync(services, time);
        Assert.Equal(WorkflowStatus.Suspended, (await InstanceAsync(services, started.InstanceId)).Status);

        time.Advance(TimeSpan.FromMinutes(2));
        await RecoverOnceAsync(services, time);
        var saved = await InstanceAsync(services, started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        Assert.Contains("after-delay", saved.WorkflowDataJson);
    }

    [Fact]
    public async Task WaitTimeoutPathRunsOnlyWhenInjectedTimePassesItsDeadline()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var definition = Workflow.Create<TimeData>("InjectedTimeWaitTimeout")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMinutes(10))
                .OnTimeout(path => path.Step<MarkActivity>(step => step
                    .Input(activity => activity.Value).From(_ => "timeout")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
            .Build();

        var started = await Engine(services).ExecuteWorkflowAsync(definition, new TimeData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        time.Advance(TimeSpan.FromMinutes(9));
        await RecoverOnceAsync(services, time);
        Assert.Equal(WorkflowStatus.Suspended, (await InstanceAsync(services, started.InstanceId)).Status);

        time.Advance(TimeSpan.FromMinutes(2));
        await RecoverOnceAsync(services, time);
        var saved = await InstanceAsync(services, started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        Assert.Contains("timeout", saved.WorkflowDataJson);
    }

    [Fact]
    public async Task ExecutionTimeoutAppliesWhenInjectedTimePassesIt()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var definition = Workflow.Create<TimeData>("InjectedTimeExecutionTimeout")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Build();

        var started = await Engine(services).ExecuteWorkflowAsync(definition, new TimeData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromHours(1) });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        time.Advance(TimeSpan.FromMinutes(30));
        await RecoverOnceAsync(services, time);
        Assert.Equal(WorkflowStatus.Suspended, (await InstanceAsync(services, started.InstanceId)).Status);

        time.Advance(TimeSpan.FromMinutes(31));
        await RecoverOnceAsync(services, time);
        Assert.Equal(WorkflowStatus.TimedOut, (await InstanceAsync(services, started.InstanceId)).Status);
    }

    private static ServiceProvider Services(TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        services.AddTransient<MarkActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowEngine Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowEngine>();

    private static async Task<WorkflowInstance> InstanceAsync(IServiceProvider services, string instanceId) =>
        (await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(instanceId))!;

    private static async Task RecoverOnceAsync(IServiceProvider services, TimeProvider time)
    {
        using var scanner = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IWorkflowStateRepository>(), NullLogger<WorkflowRecoveryService>.Instance,
            time);
        await scanner.RecoverOnceAsync(CancellationToken.None);
    }

    public sealed class TimeData
    {
        public string Outcome { get; set; } = "";
    }

    public sealed class Approval;

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class MarkActivity : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
