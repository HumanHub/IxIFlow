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
    public async Task WaitAny_RunsNestedFinallyBlocksBeforeContinuing()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("NestedWaitAnyCleanup")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(outer => outer
                        .Try(inner => inner.WaitFor<Approval>("loser"))
                        .Finally(inner => inner.Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                    .Finally(outer => outer.Step<CountActivity>(setup => setup
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
        Assert.Equal(2, Data(result).Finally);
        Assert.Equal(3, Data(result).After);
    }

    [Fact]
    public async Task WaitAny_CleanupCanWaitBeforeTheParentContinues()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<TryData>("WaitAnyCleanupWait")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("loser"))
                    .Finally(body => body.WaitFor<Approval>("cleanup")
                        .Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body))))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(0, Data(started).After);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "cleanup", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, Data(completed).Finally);
        Assert.Equal(2, Data(completed).After);
    }

    [Fact]
    public async Task WaitAny_DoesNotDiscardAFinallyAlreadyWaiting()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<TryData>("ExistingFinallyWait")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.Step<StartActivity>(_ => { }))
                    .Finally(body => body.WaitFor<Approval>("audit")
                        .Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.WaitFor<Approval>("winner")))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var winner = await engine.ResumeWorkflowAsync(started.InstanceId, "winner", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Suspended, winner.Status);
        Assert.Equal(0, Data(winner).After);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "audit", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, Data(completed).Finally);
        Assert.Equal(2, Data(completed).After);
    }

    [Fact]
    public async Task WaitAny_FinishesParallelWorkInsideLosingBranchFinally()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("ParallelCleanup")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("loser"))
                    .Finally(body => body
                        .Parallel(cleanup => cleanup
                            .Do(first => first.Step<CountActivity>(setup => setup
                                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
                            .Do(second => second.Step<CountActivity>(setup => setup
                                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Catch)
                                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch))))
                        .Step<CountActivity>(setup => setup
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
        Assert.Equal(2, Data(result).Finally);
        Assert.Equal(1, Data(result).Catch);
        Assert.Equal(3, Data(result).After);
    }

    [Fact]
    public async Task WaitAny_FinishesNestedTryAndFollowingStepsInsideLosingBranchFinally()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("NestedTryCleanup")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("loser"))
                    .Finally(body => body
                        .Try(inner => inner.Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
                        .Finally(inner => inner.Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
                        .Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.Step<StartActivity>(_ => { })))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(3, Data(result).Finally);
        Assert.Equal(4, Data(result).After);
    }

    [Fact]
    public async Task WaitAny_FailingLosingBranchFinallyDoesNotRetryItsActivity()
    {
        using var services = CreateServicesWithCleanupProbe();
        var definition = Workflow.Create<TryData>("FailingCleanup")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("loser"))
                    .Finally(body => body.Step<ThrowOnceCleanupActivity>(_ => { })))
                .Do(branch => branch.Step<StartActivity>(_ => { })))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(result.InstanceId);
        Assert.True(result.ErrorMessage?.Contains("cleanup failed") == true,
            $"{result.ErrorMessage}\n{saved?.LastErrorStackTrace}");
        Assert.Equal(1, services.GetRequiredService<CleanupAttemptProbe>().Attempts);
    }

    [Fact]
    public async Task ExceptionWithoutRestorableConstructorStillEntersCatchAndFinally()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<TryData>("OpaqueCatch")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowOpaqueActivity>(_ => { }))
            .Catch<OpaqueApprovalException>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Exception.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("opaque 17", Data(result).Error);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task OpaqueExceptionCannotParkInsideCatchAndStillRunsFinally()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("OpaqueCatchWait")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowOpaqueActivity>(_ => { }))
            .Catch<OpaqueApprovalException>(body => body
                .Step<StartActivity>(_ => { }).WaitFor<Approval>("review"))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("cannot be restored after a wait", result.ErrorMessage);
        Assert.Equal(1, Data(result).Finally);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(result.InstanceId);
        Assert.Empty(saved!.ExecutionSnapshot!.Pointers);
    }

    [Fact]
    public async Task OpaqueInnerExceptionCannotParkInsideCatch()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("OpaqueInnerCatchWait")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowOuterOpaqueActivity>(_ => { }))
            .Catch<InvalidOperationException>(body => body
                .Step<StartActivity>(_ => { }).WaitFor<Approval>("review"))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("cannot be restored after a wait", result.ErrorMessage);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task OpaqueParallelFailureKeepsItsMessageAfterSiblingCleanup()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("OpaqueParallelFailure")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<ThrowOpaqueActivity>(_ => { }))
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("approval"))
                    .Finally(body => body.Step<CountActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal("opaque 17", result.ErrorMessage);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task CallerCancellationRunsFinallyBeforeCancellingInstance()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<CancellationProbe>();
        collection.AddTransient<WaitForCancellationActivity>();
        collection.AddIxIFlow();
        using var services = collection.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var definition = Workflow.Create<TryData>("CallerCancellationCleanup")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("begin")
                .Step<WaitForCancellationActivity>(_ => { }))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var running = engine.ResumeWorkflowAsync(started.InstanceId, "begin", new Approval(), cancellation.Token);
        await services.GetRequiredService<CancellationProbe>().Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task CallerCancellationCanWaitForFinallyBeforeCancellingInstance()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<CancellationProbe>();
        collection.AddTransient<WaitForCancellationActivity>();
        collection.AddIxIFlow();
        using var services = collection.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var definition = Workflow.Create<TryData>("CallerCancellationWaitCleanup")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("begin")
                .Step<WaitForCancellationActivity>(_ => { }))
            .Finally(body => body.WaitFor<Approval>("cleanup")
                .Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        var resuming = engine.ResumeWorkflowAsync(started.InstanceId, "begin", new Approval(), cancellation.Token);
        await services.GetRequiredService<CancellationProbe>().Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var cleaning = await resuming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowExecutionStatus.Suspended, cleaning.Status);
        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "cleanup", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Cancelled, completed.Status);
        Assert.Equal(1, Data(completed).Finally);
    }

    [Fact]
    public async Task CallerCancellationDoesNotInterruptAnActiveFinallyActivity()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<ManualCleanupProbe>();
        collection.AddTransient<ManualCleanupActivity>();
        collection.AddIxIFlow();
        using var services = collection.BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var definition = Workflow.Create<TryData>("CallerCancellationDuringFinally")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("begin"))
            .Finally(body => body.Step<ManualCleanupActivity>(setup => setup
                .Output(activity => activity.Completed).To(ctx => ctx.WorkflowData.Finally)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        var resuming = engine.ResumeWorkflowAsync(started.InstanceId, "begin", new Approval(), cancellation.Token);
        var probe = services.GetRequiredService<ManualCleanupProbe>();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        probe.Release.SetResult();

        var result = await resuming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task UncaughtParallelFailureRunsSiblingFinallyBeforeFaulting()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("UncaughtParallelFailure")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<ThrowActivity>(_ => { }))
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("approval"))
                    .Finally(body => body.Step<CountActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal("boom", result.ErrorMessage);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task CancellationCleanupRunsItsOwnCatchAndFinally()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("HandledCleanupFailure")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("approval"))
                    .Finally(body => body
                        .Try(inner => inner.Step<ThrowActivity>(_ => { }))
                        .Catch<InvalidOperationException>(inner => inner.Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Catch)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch)))
                        .Finally(inner => inner.Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
                .Do(branch => branch.Step<StartActivity>(_ => { })))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.After)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Catch);
        Assert.Equal(1, Data(result).Finally);
        Assert.Equal(1, Data(result).After);
    }

    [Fact]
    public async Task FailureInParallelCleanupBypassesOuterForwardCatch()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("ParallelCleanupFailure")
            .Step<StartActivity>()
            .Try(body => body.Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(inner => inner.WaitFor<Approval>("approval"))
                    .Finally(inner => inner.Parallel(cleanup => cleanup
                        .Do(path => path.Step<ThrowActivity>(_ => { }))
                        .Do(path => path.Step<StartActivity>(_ => { })))))
                .Do(branch => branch.Step<StartActivity>(_ => { }))))
            .Catch<InvalidOperationException>(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Catch)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(0, Data(result).Catch);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task UncaughtParallelFailureCanWaitForSiblingCleanupBeforeFaulting()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<TryData>("UncaughtParallelCleanupWait")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<ThrowActivity>(_ => { }))
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("approval"))
                    .Finally(body => body.WaitFor<Approval>("cleanup")
                        .Step<CountActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "cleanup", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal("boom", result.ErrorMessage);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task ParallelActivityInsideCatchReceivesTheCaughtException()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<TryData>("ParallelInsideCatch")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowActivity>(_ => { }))
            .Catch<InvalidOperationException>(body => body
                .Step<StartActivity>(_ => { })
                .Parallel(parallel => parallel.Do(branch => branch.Step<ReadErrorActivity>(setup => setup
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))))
            .Build();
        var catchActivity = definition.Steps[1].CatchBlocks[0].SequenceSteps[1].ParallelBranches[0][0];
        catchActivity.InputMappings.Add(new PropertyMapping
        {
            TargetProperty = nameof(ReadErrorActivity.Message),
            Direction = PropertyMappingDirection.Input,
            SourceType = typeof(string),
            TargetType = typeof(string),
            SourceFunction = context =>
                ((CatchContext<TryData, InvalidOperationException, StartActivity>)context).Exception.Message
        });

        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("boom", Data(completed).Error);
    }

    [Fact]
    public async Task CatchRestoresCustomExceptionPropertiesAfterProviderRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var engine = firstProvider.GetRequiredService<IWorkflowEngine>();
            var started = await engine.ExecuteWorkflowAsync(CustomErrorWorkflow(), new TryData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(CustomErrorWorkflow());
        var completed = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "review", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("custom failure", Data(completed).Error);
        Assert.Equal(42, Data(completed).After);
    }

    [Fact]
    public async Task FailureInCatchRunsFinallyAndFaults()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("FailureInCatch")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowActivity>(_ => { }))
            .Catch<InvalidOperationException>(body => body
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Exception.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
                .Step<ThrowActivity>(_ => { }))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new TryData());
        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, Data(result).Finally);
        Assert.Equal("boom", Data(result).Error);
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

    private static WorkflowDefinition CustomErrorWorkflow()
    {
        var definition = Workflow.Create<TryData>("CustomCatchRecovery")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowCustomActivity>(_ => { }))
            .Catch<ApprovalException>(body => body
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Exception.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
                .WaitFor<Approval>("review")
                .Step<CaptureCodeActivity>(setup => setup
                    .Output(activity => activity.Code).To(ctx => ctx.WorkflowData.After)))
            .Build();
        var afterWait = definition.Steps[1].CatchBlocks[0].SequenceSteps[2];
        afterWait.InputMappings.Add(new PropertyMapping
        {
            TargetProperty = nameof(CaptureCodeActivity.Code),
            Direction = PropertyMappingDirection.Input,
            SourceType = typeof(int),
            TargetType = typeof(int),
            SourceFunction = context =>
                ((CatchContext<TryData, ApprovalException, Approval>)context).Exception.Code
        });
        return definition;
    }

    private static ServiceProvider CreateServices(IWorkflowStateRepository? repository = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        if (repository != null)
            services.AddSingleton(repository);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateServicesWithCleanupProbe()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<CleanupAttemptProbe>();
        services.AddTransient<ThrowOnceCleanupActivity>();
        services.AddIxIFlow();
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

    public sealed class ApprovalException(string message, int code) : Exception(message)
    {
        public int Code { get; } = code;
    }

    public sealed class ThrowCustomActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new ApprovalException("custom failure", 42);
    }

    public sealed class CaptureCodeActivity : IAsyncActivity
    {
        public int Code { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class CleanupAttemptProbe
    {
        public int Attempts { get; set; }
    }

    public sealed class ThrowOnceCleanupActivity(CleanupAttemptProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Attempts++;
            if (probe.Attempts == 1)
                throw new InvalidOperationException("cleanup failed");
            return Task.CompletedTask;
        }
    }

    public sealed class OpaqueApprovalException(int reason) : Exception($"opaque {reason}");

    public sealed class ThrowOpaqueActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new OpaqueApprovalException(17);
    }

    public sealed class ThrowOuterOpaqueActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("outer", new OpaqueApprovalException(17));
    }

    public sealed class CancellationProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class WaitForCancellationActivity(CancellationProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    public sealed class ManualCleanupProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ManualCleanupActivity(ManualCleanupProbe probe) : IAsyncActivity
    {
        public int Completed { get; private set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Started.SetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
            Completed = 1;
        }
    }
}
