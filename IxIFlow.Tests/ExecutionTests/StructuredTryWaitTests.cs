using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public class StructuredTryWaitTests
{
    [Fact]
    public async Task WaitInsideTry_ContinuesThroughFinallyWithoutEnteringCatch()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<TryData>("WaitInTry")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval")
                .Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body)))
            .Catch<InvalidOperationException>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Exception.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.After)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, Data(completed).Body);
        Assert.Equal(1, Data(completed).Finally);
        Assert.Equal(1, Data(completed).After);
        Assert.Null(Data(completed).Error);
    }

    [Fact]
    public async Task FailureAfterWait_EntersMatchingCatchThenFinally()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<TryData>("FailureAfterWait")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowActivity>(_ => { }))
            .Catch<InvalidOperationException>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Exception.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.After)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("boom", Data(completed).Error);
        Assert.Equal(1, Data(completed).Finally);
        Assert.Equal(1, Data(completed).After);
    }

    [Fact]
    public async Task WaitInsideCatch_ResumesTheHandlerAndRunsFinally()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = CatchWaitWorkflow();

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal("boom", Data(started).Error);
        Assert.Equal(0, Data(started).Finally);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "review", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, Data(completed).Catch);
        Assert.Equal(1, Data(completed).Finally);
    }

    [Fact]
    public async Task WaitInsideCatch_RestoresItsPhaseAndExceptionAfterProviderRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var engine = firstProvider.GetRequiredService<IWorkflowEngine>();
            var started = await engine.ExecuteWorkflowAsync(CatchWaitWorkflow(), new TryData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(CatchWaitWorkflow());
        var completed = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "review", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("boom", Data(completed).Error);
        Assert.Equal(1, Data(completed).Catch);
        Assert.Equal(1, Data(completed).Finally);
    }

    [Fact]
    public async Task ParallelFailureIsCaughtByTheEnclosingTry()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("ParallelFailureInTry")
            .Step<StartActivity>()
            .Try(body => body.Parallel(parallel => parallel
                .Do(branch => branch.Step<ThrowActivity>(_ => { }))
                .Do(branch => branch.WaitFor<Approval>("approval"))))
            .Catch<InvalidOperationException>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Exception.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("boom", Data(result).Error);
        Assert.Equal(1, Data(result).Finally);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(result.InstanceId);
        Assert.DoesNotContain(saved!.ExecutionSnapshot!.Pointers, pointer => pointer.Status == "Waiting");
    }

    [Fact]
    public async Task WaitAny_RunsLosingBranchFinallyBeforeContinuing()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("WaitAnyCleanup")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("loser"))
                    .Finally(body => body.Step<CountActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body))))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Finally);
        Assert.Equal(2, Data(result).After);
    }

    [Fact]
    public async Task WaitInsideFinally_CompletesBeforeTheNextStep()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<TryData>("WaitInFinally")
            .Step<StartActivity>()
            .Try(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body)))
            .Finally(body => body.WaitFor<Approval>("audit")
                .Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.After)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(1, Data(started).Body);
        Assert.Equal(0, Data(started).After);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "audit", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, Data(completed).Finally);
        Assert.Equal(1, Data(completed).After);
    }

    [Fact]
    public async Task UnhandledFailureRunsFinallyAndFaults()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("UnhandledFailure")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowActivity>(_ => { }))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, Data(result).Finally);
        Assert.Contains("boom", result.ErrorMessage);
    }

    private static WorkflowDefinition CatchWaitWorkflow() => Workflow.Create<TryData>("WaitInCatch")
        .Step<StartActivity>()
        .Try(body => body.Step<ThrowActivity>(_ => { }))
        .Catch<InvalidOperationException>(body => body
            .Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Exception.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
            .WaitFor<Approval>("review")
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Catch)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch)))
        .Finally(body => body.Step<CountActivity>(setup => setup
            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
        .Build();

    private static ServiceProvider CreateServices(IWorkflowStateRepository? repository = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        if (repository != null)
            services.AddSingleton(repository);
        return services.BuildServiceProvider();
    }

    private static TryData Data(WorkflowExecutionResult result) => Assert.IsType<TryData>(result.WorkflowData);

    public sealed class TryData
    {
        public int Body { get; set; }
        public int Catch { get; set; }
        public int Finally { get; set; }
        public int After { get; set; }
        public string? Error { get; set; }
    }

    public sealed class Approval;

    public sealed class StartActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class ThrowActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    public sealed class CountActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count + 1;
            return Task.CompletedTask;
        }
    }

    public sealed class ReadErrorActivity : IAsyncActivity
    {
        public string Message { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
