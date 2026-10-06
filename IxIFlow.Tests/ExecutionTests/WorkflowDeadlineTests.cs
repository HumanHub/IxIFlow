using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class WorkflowDeadlineTests
{
    [Fact]
    public async Task ApprovalBeforeDeadlineRunsOnlyTheReceivedPath()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(ApprovalWorkflow("ApprovedInTime"),
            new ApprovalData());

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval",
            new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("received", Assert.IsType<ApprovalData>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task WaitDeadlineRunsTimeoutPathAndSkipsReceivedPath()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(ApprovalWorkflow("ApprovalTimedOut"),
            new ApprovalData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        await Task.Delay(160);
        var result = await engine.RecoverWorkflowAsync(started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("timeout", Assert.IsType<ApprovalData>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task LateApprovalCannotBeatItsCommittedDeadline()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(ApprovalWorkflow("LateApproval"),
            new ApprovalData());
        await Task.Delay(160);

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval",
            new Approval());

        Assert.False(result.EventAccepted);
        Assert.Equal("timeout", Assert.IsType<ApprovalData>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task DueWaitDoesNotDiscardAnEventForAnotherBranch()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("ParallelDeadlineAndApproval")
            .Step<SeedActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<Approval>("first", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMilliseconds(80))
                    .OnTimeout(path => path.Step<MarkActivity>(step => step
                        .Input(activity => activity.Value).From(_ => "timeout")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))))
                .Do(branch => branch.WaitFor<Approval>("second")
                    .Step<AfterActivity>(step => step
                        .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After))))
            .Build();
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData());
        await Task.Delay(160);

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "second", new Approval());

        Assert.True(result.EventAccepted);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ApprovalData>(result.WorkflowData);
        Assert.Equal("timeout", data.Outcome);
        Assert.True(data.After);
    }

    [Fact]
    public async Task InstanceDeadlineIncludesTimeParkedAtWait()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("WholeInstanceTimeout")
            .Step<SeedActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(80) });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        await Task.Delay(160);
        var result = await engine.RecoverWorkflowAsync(started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task InstanceDeadlineInterruptsAnActiveActivity()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var blocking = services.GetRequiredService<BlockingProbe>();
        var definition = Workflow.Create<ApprovalData>("ActiveExecutionTimeout")
            .Step<BlockingActivity>()
            .Build();

        var execution = engine.ExecuteWorkflowAsync(definition, new ApprovalData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(500) });
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
        Assert.Equal(WorkflowStatus.TimedOut,
            (await services.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(result.InstanceId))!.Status);
    }

    [Fact]
    public async Task LocalRecoveryServiceWakesAnOverdueWait()
    {
        using var services = Services();
        Assert.Contains(services.GetServices<IHostedService>(),
            service => service is WorkflowRecoveryService);
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.ExecuteWorkflowAsync(ApprovalWorkflow("ScannedApproval"),
            new ApprovalData());
        await Task.Delay(160);
        Assert.Contains(await repository.GetWorkflowsRequiringRecoveryAsync(),
            instance => instance.InstanceId == started.InstanceId);
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);

        await scanner.RecoverOnceAsync(CancellationToken.None);

        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Equal("timeout", JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!.Outcome);
    }

    [Fact]
    public async Task TimeoutExitsOnlyItsContainingSequence()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ApprovalData>("NestedWaitTimeout")
            .Step<SeedActivity>()
            .Sequence(sequence => sequence
                .WaitFor<Approval>("approval", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMilliseconds(80))
                    .OnTimeout(path => path.Step<MarkActivity>(step => step
                        .Input(activity => activity.Value).From(_ => "timeout")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
                .Step<MarkActivity>(step => step
                    .Input(activity => activity.Value).From(_ => "received")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Step<AfterActivity>(step => step
                .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After))
            .Build();
        var started = await engine.ExecuteWorkflowAsync(definition, new ApprovalData());
        await Task.Delay(160);

        var result = await engine.RecoverWorkflowAsync(started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ApprovalData>(result.WorkflowData);
        Assert.Equal("timeout", data.Outcome);
        Assert.True(data.After);
    }

    [Fact]
    public async Task WholeInstanceTimeoutRunsFinally()
    {
        using var services = Services();
        var blocking = services.GetRequiredService<BlockingProbe>();
        var definition = Workflow.Create<ApprovalData>("TimeoutFinally")
            .Step<SeedActivity>()
            .Try(body => body.Step<BlockingActivity>(_ => { }))
            .Finally(body => body.Step<AfterActivity>(step => step
                .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After)))
            .Build();

        var execution = services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new ApprovalData(),
                new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(500) });
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
        Assert.True(Assert.IsType<ApprovalData>(result.WorkflowData).After);
    }

    private static WorkflowDefinition ApprovalWorkflow(string name) =>
        Workflow.Create<ApprovalData>(name)
            .Step<SeedActivity>()
            .WaitFor<Approval>("approval", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMilliseconds(80))
                .OnTimeout(path => path.Step<MarkActivity>(step => step
                    .Input(activity => activity.Value).From(_ => "timeout")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
            .Step<MarkActivity>(step => step
                .Input(activity => activity.Value).From(_ => "received")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))
            .Build();

    private static ServiceProvider Services()
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddTransient<SeedActivity>();
        registrations.AddTransient<MarkActivity>();
        registrations.AddSingleton<BlockingProbe>();
        registrations.AddTransient<BlockingActivity>();
        registrations.AddTransient<AfterActivity>();
        registrations.AddIxIFlow();
        return registrations.BuildServiceProvider();
    }

    public sealed class ApprovalData
    {
        public string Outcome { get; set; } = "";
        public bool After { get; set; }
    }

    public sealed class Approval;

    public sealed class SeedActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class MarkActivity : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class BlockingActivity : IAsyncActivity
    {
        private readonly BlockingProbe _probe;

        public BlockingActivity(BlockingProbe probe) => _probe = probe;

        public async Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            _probe.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    public sealed class BlockingProbe
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class AfterActivity : IAsyncActivity
    {
        public bool Done { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Done = true;
            return Task.CompletedTask;
        }
    }
}
