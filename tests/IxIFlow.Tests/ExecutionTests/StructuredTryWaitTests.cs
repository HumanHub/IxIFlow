using System.Linq.Expressions;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "try")]
public class StructuredTryWaitTests
{
    public sealed class ApprovalFault
    {
        public string Message { get; set; } = "";
        public int Code { get; set; }
    }

    public sealed class ObjectDetailsFault
    {
        public object? Details { get; set; }
    }

    public sealed class MarkedDetailsFault
    {
        public ApiErrorDetails? Details { get; set; }
    }

    public sealed class NullableDetailsFault
    {
        public OptionalErrorDetails? Details { get; set; }
    }

    public sealed class MissingPropertyFault
    {
        public string Unknown { get; set; } = "";
    }

    public sealed class WrongCodeFault
    {
        public string Code { get; set; } = "";
    }

    public sealed class DelegateFault
    {
        public Action? Callback { get; set; }
    }

    [Fact]
    public async Task WaitInsideTry_ContinuesThroughFinallyWithoutEnteringCatch()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("WaitInTry")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval")
                .Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body)))
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.After)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var completed = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());
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
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("FailureAfterWait")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowActivity>())
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.After)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var completed = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("boom", Data(completed).Error);
        Assert.Equal(1, Data(completed).Finally);
        Assert.Equal(1, Data(completed).After);
    }

    [Fact]
    public async Task WaitInsideCatch_ResumesTheHandlerAndRunsFinally()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = CatchWaitWorkflow();

        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal("boom", Data(started).Error);
        Assert.Equal(0, Data(started).Finally);

        var completed = await engine.ResumeAsync(started.InstanceId, "review", new Approval());
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
            var engine = firstProvider.GetRequiredService<IWorkflowClient>();
            var started = await engine.RunAsync(CatchWaitWorkflow(), new TryData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(CatchWaitWorkflow());
        var completed = await secondProvider.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, "review", new Approval());

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
                .Do(branch => branch.Step<ThrowActivity>())
                .Do(branch => branch.WaitFor<Approval>("approval"))))
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("boom", Data(result).Error);
        Assert.Equal(1, Data(result).Finally);
        var snapshot = await services.GetRequiredService<IWorkflowClient>().GetSnapshotAsync(result.InstanceId);
        Assert.DoesNotContain(snapshot!.Pointers, pointer => pointer.Status == "Waiting");
    }

    [Fact]
    [Trait("Spec", "E-08")]
    public async Task WaitAny_RunsLosingBranchFinallyBeforeContinuing()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("WaitAnyCleanup")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("loser"))
                    .Finally(body => body.Step<LoggedCountActivity>(setup => setup
                        .Input(activity => activity.Name).From(_ => "Finally")
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.Step<LoggedCountActivity>(setup => setup
                    .Input(activity => activity.Name).From(_ => "Body")
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body))))
            .Step<LoggedCountActivity>(setup => setup
                .Input(activity => activity.Name).From(_ => "After")
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        // The loser's Finally ran before the next step; its write was discarded with the loser (D-14).
        Assert.Equal(["Body:1", "Finally:1", "After:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(result).Finally);
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
                        .Finally(inner => inner.Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                    .Finally(outer => outer.Step<LoggedCountActivity>(setup => setup
                        .Input(activity => activity.Name).From(_ => "Finally")
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.Step<LoggedCountActivity>(setup => setup
                    .Input(activity => activity.Name).From(_ => "Body")
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body))))
            .Step<LoggedCountActivity>(setup => setup
                .Input(activity => activity.Name).From(_ => "After")
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["Body:1", "Finally:1", "Finally:2", "After:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(result).Finally);
    }

    [Fact]
    public async Task WaitAny_CleanupCanWaitBeforeTheParentContinues()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("WaitAnyCleanupWait")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("loser"))
                    .Finally(body => body.WaitFor<Approval>("cleanup")
                        .Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.Step<LoggedCountActivity>(setup => setup
                    .Input(activity => activity.Name).From(_ => "Body")
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body))))
            .Step<LoggedCountActivity>(setup => setup
                .Input(activity => activity.Name).From(_ => "After")
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(0, Data(started).After);

        var completed = await engine.ResumeAsync(started.InstanceId, "cleanup", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(["Body:1", "Finally:1", "After:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(completed).Finally);
    }

    [Fact]
    public async Task WaitAny_DoesNotDiscardAFinallyAlreadyWaiting()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("ExistingFinallyWait")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.Step<StartActivity>())
                    .Finally(body => body.WaitFor<Approval>("audit")
                        .Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.WaitFor<Approval>("winner")))
            .Step<LoggedCountActivity>(setup => setup
                .Input(activity => activity.Name).From(_ => "After")
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var winner = await engine.ResumeAsync(started.InstanceId, "winner", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Suspended, winner.Status);
        Assert.Equal(0, Data(winner).After);

        var completed = await engine.ResumeAsync(started.InstanceId, "audit", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(["Finally:1", "After:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(completed).Finally);
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
                            .Do(first => first.Step<LoggedCountActivity>(setup => setup
                                .Input(activity => activity.Name).From(_ => "Finally")
                                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
                            .Do(second => second.Step<LoggedCountActivity>(setup => setup
                                .Input(activity => activity.Name).From(_ => "Catch")
                                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Catch)
                                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch))))
                        .Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.Step<LoggedCountActivity>(setup => setup
                    .Input(activity => activity.Name).From(_ => "Body")
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Body)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Body))))
            .Step<LoggedCountActivity>(setup => setup
                .Input(activity => activity.Name).From(_ => "After")
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        // The cleanup's own parallel merges into the loser's copy, so the step after it counts 2.
        var log = services.GetRequiredService<StepLog>().Entries;
        Assert.Equal("Body:1", log[0]);
        Assert.Equal(["Catch:1", "Finally:1"], log.Skip(1).Take(2).Order());
        Assert.Equal(["Finally:2", "After:1"], log.Skip(3));
        Assert.Equal((0, 0), (Data(result).Finally, Data(result).Catch));
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
                        .Try(inner => inner.Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
                        .Finally(inner => inner.Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
                        .Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
                .Do(branch => branch.Step<StartActivity>()))
            .Step<LoggedCountActivity>(setup => setup
                .Input(activity => activity.Name).From(_ => "After")
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["Finally:1", "Finally:2", "Finally:3", "After:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(result).Finally);
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
                    .Finally(body => body.Step<ThrowOnceCleanupActivity>()))
                .Do(branch => branch.Step<StartActivity>()))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

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
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("OpaqueCatch")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowOpaqueActivity>())
            .Catch<OpaqueApprovalException, MessageFault>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var result = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("opaque 17", Data(result).Error);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task OpaqueExceptionSurvivesWaitInFinallyAndStillFaultsWorkflow()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<TryData>("OpaqueFinallyWait")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowOpaqueActivity>())
            .Finally(body => body.WaitFor<Approval>("cleanup")
                .Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new TryData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var completed = await secondProvider.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, "cleanup", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, completed.Status);
        Assert.Contains("opaque 17", completed.ErrorMessage);
        Assert.Equal(1, Data(completed).Finally);
    }

    [Fact]
    public async Task OpaqueExceptionCanParkInsideCatchWhenNoPropertiesAreSelected()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("OpaqueCatchWait")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowOpaqueActivity>())
            .Catch<OpaqueApprovalException, MessageFault>(body => body
                .Step<StartActivity>().WaitFor<Approval>("review"))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var result = await engine.ResumeAsync(started.InstanceId, "review", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task OpaqueInnerExceptionCanParkInsideCatchWhenNotSelected()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("OpaqueInnerCatchWait")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowOuterOpaqueActivity>())
            .Catch<InvalidOperationException, MessageFault>(body => body
                .Step<StartActivity>().WaitFor<Approval>("review"))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var result = await engine.ResumeAsync(started.InstanceId, "review", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task OpaqueParallelFailureKeepsItsMessageAfterSiblingCleanup()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("OpaqueParallelFailure")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<ThrowOpaqueActivity>())
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("approval"))
                    .Finally(body => body.Step<LoggedCountActivity>(setup => setup
                        .Input(activity => activity.Name).From(_ => "Finally")
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal("opaque 17", result.ErrorMessage);
        // The sibling's Finally ran; a failed join merges nothing (D-14).
        Assert.Equal(["Finally:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(result).Finally);
    }

    [Fact]
    public async Task BusinessCancellationRunsFinallyBeforeCancellingInstance()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<CancellationProbe>();
        collection.AddTransient<WaitForCancellationActivity>();
        collection.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        using var services = collection.BuildServiceProvider();
        var definition = Workflow.Create<TryData>("CallerCancellationCleanup")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("begin")
                .Step<WaitForCancellationActivity>())
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var running = engine.ResumeAsync(started.InstanceId, "begin", new Approval());
        await services.GetRequiredService<CancellationProbe>().Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task RemoteCancellationOfWaitRunsFinallyAndSkipsFollowingStep()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("RemoteCancellationCleanup")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval"))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.After)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();
        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await engine.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn" });

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(1, Data(result).Finally);
        Assert.Equal(0, Data(result).After);
    }

    [Fact]
    public async Task RemoteCancellationCanWaitForFinallyBeforeEnding()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("RemoteCancellationWaitCleanup")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval"))
            .Finally(body => body.WaitFor<Approval>("cleanup")
                .Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();
        var started = await engine.RunAsync(definition, new TryData());

        var cleaning = await engine.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, cleaning.Status);
        var completed = await engine.ResumeAsync(started.InstanceId, "cleanup", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Cancelled, completed.Status);
        Assert.Equal(1, Data(completed).Finally);
    }

    [Fact]
    public async Task BusinessCancellationCanWaitForFinallyBeforeCancellingInstance()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<CancellationProbe>();
        collection.AddTransient<WaitForCancellationActivity>();
        collection.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        using var services = collection.BuildServiceProvider();
        var definition = Workflow.Create<TryData>("CallerCancellationWaitCleanup")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("begin")
                .Step<WaitForCancellationActivity>())
            .Finally(body => body.WaitFor<Approval>("cleanup")
                .Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new TryData());
        var resuming = engine.ResumeAsync(started.InstanceId, "begin", new Approval());
        await services.GetRequiredService<CancellationProbe>().Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });

        var cleaning = await resuming.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowExecutionStatus.Suspended, cleaning.Status);
        var completed = await engine.ResumeAsync(started.InstanceId, "cleanup", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Cancelled, completed.Status);
        Assert.Equal(1, Data(completed).Finally);
    }

    [Fact]
    public async Task BusinessCancellationDoesNotInterruptAnActiveFinallyActivity()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<ManualCleanupProbe>();
        collection.AddTransient<ManualCleanupActivity>();
        collection.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        using var services = collection.BuildServiceProvider();
        var definition = Workflow.Create<TryData>("CallerCancellationDuringFinally")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("begin"))
            .Finally(body => body.Step<ManualCleanupActivity>(setup => setup
                .Output(activity => activity.Completed).To(ctx => ctx.WorkflowData.Finally)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new TryData());
        var resuming = engine.ResumeAsync(started.InstanceId, "begin", new Approval());
        var probe = services.GetRequiredService<ManualCleanupProbe>();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
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
                .Do(branch => branch.Step<ThrowActivity>())
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("approval"))
                    .Finally(body => body.Step<LoggedCountActivity>(setup => setup
                        .Input(activity => activity.Name).From(_ => "Finally")
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal("boom", result.ErrorMessage);
        Assert.Equal(["Finally:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(result).Finally);
    }

    [Fact]
    public async Task OuterParallelFailureLetsNestedFinallyJoinFinish()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<CleanupJoinProbe>();
        services.AddTransient<ThrowAfterCleanupStartsActivity>();
        services.AddTransient<WaitForCleanupReleaseActivity>();
        services.AddSingleton<StepLog>();
        services.AddTransient<LoggedCountActivity>();
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<TryData>("NestedFinallyJoin")
            .Step<StartActivity>()
            .Parallel(outer => outer
                .Do(branch => branch.Step<ThrowAfterCleanupStartsActivity>())
                .Do(branch => branch.Try(body => body.Step<StartActivity>())
                    .Finally(cleanup => cleanup
                        .Parallel(inner => inner
                            .Do(child => child.Step<WaitForCleanupReleaseActivity>())
                            .Do(child => child.Step<StartActivity>()))
                        .Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("outer failure", result.ErrorMessage);
        Assert.Equal(["Finally:1"], provider.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(result).Finally);
    }

    [Fact]
    public async Task FailureInLosingNestedFinallyCancelsOuterSibling()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NestedJoinProbe>();
        services.AddTransient<WaitForNestedCancellationActivity>();
        services.AddTransient<CompleteNestedWinnerActivity>();
        services.AddTransient<WaitForOuterCancellationActivity>();
        services.AddTransient<ThrowLosingCleanupActivity>();
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<TryData>("NestedJoinFailure")
            .Step<StartActivity>()
            .Parallel(outer => outer
                .Do(branch => branch.Parallel(inner => inner.WaitAny()
                    .Do(child => child.Try(body => body.Step<WaitForNestedCancellationActivity>())
                        .Finally(cleanup => cleanup.Step<ThrowLosingCleanupActivity>()))
                    .Do(child => child.Step<CompleteNestedWinnerActivity>())))
                .Do(branch => branch.Step<WaitForOuterCancellationActivity>()))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("losing cleanup failure", result.ErrorMessage);
        Assert.True(provider.GetRequiredService<NestedJoinProbe>().OuterCancelled);
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
                        .Try(inner => inner.Step<ThrowActivity>())
                        .Catch<InvalidOperationException, MessageFault>(inner => inner.Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Catch")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Catch)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch)))
                        .Finally(inner => inner.Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
                .Do(branch => branch.Step<StartActivity>()))
            .Step<LoggedCountActivity>(setup => setup
                .Input(activity => activity.Name).From(_ => "After")
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.After)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.After))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["Catch:1", "Finally:1", "After:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal((0, 0, 1), (Data(result).Catch, Data(result).Finally, Data(result).After));
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
                        .Do(path => path.Step<ThrowActivity>())
                        .Do(path => path.Step<StartActivity>()))))
                .Do(branch => branch.Step<StartActivity>())))
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Catch)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(0, Data(result).Catch);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task FailureInParallelNestedInsideFinallyDoesNotRestartTheJoin()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<FinallyJoinProbe>();
        services.AddTransient<CountFinallyWinnerActivity>();
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        services.AddSingleton<IWorkflowStateRepository>(new BoundedCheckpointStore());
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<TryData>("ParallelInsideFinallyFailure")
            .Step<StartActivity>()
            .Try(body => body.Step<StartActivity>())
            .Finally(body => body.Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch.Step<CountFinallyWinnerActivity>())
                .Do(branch => branch.Try(inner => inner.WaitFor<Approval>("approval"))
                    .Finally(cleanup => cleanup.Step<ThrowActivity>()))))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, provider.GetRequiredService<FinallyJoinProbe>().WinnerRuns);
    }

    [Fact]
    public async Task UncaughtParallelFailureCanWaitForSiblingCleanupBeforeFaulting()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("UncaughtParallelCleanupWait")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<ThrowActivity>())
                .Do(branch => branch
                    .Try(body => body.WaitFor<Approval>("approval"))
                    .Finally(body => body.WaitFor<Approval>("cleanup")
                        .Step<LoggedCountActivity>(setup => setup
                            .Input(activity => activity.Name).From(_ => "Finally")
                            .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))))
            .Build();

        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await engine.ResumeAsync(started.InstanceId, "cleanup", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal("boom", result.ErrorMessage);
        Assert.Equal(["Finally:1"], services.GetRequiredService<StepLog>().Entries);
        Assert.Equal(0, Data(result).Finally);
    }

    [Fact]
    public async Task ParallelActivityInsideCatchReceivesTheCaughtException()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TryData>("ParallelInsideCatch")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowActivity>())
            .Catch<InvalidOperationException, MessageFault>(body => body
                .Step<StartActivity>()
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
                ((FaultContext<TryData, MessageFault, StartActivity>)context).Fault.Message
        });

        var started = await engine.RunAsync(definition, new TryData());
        var completed = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());

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
            var engine = firstProvider.GetRequiredService<IWorkflowClient>();
            var started = await engine.RunAsync(CustomErrorWorkflow(), new TryData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(CustomErrorWorkflow());
        var completed = await secondProvider.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, "review", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("restored: custom failure", Data(completed).Error);
        Assert.Equal(42, Data(completed).After);
    }

    [Fact]
    public void CatchWithoutFaultDoesNotExposeException()
    {
        Assert.Null(typeof(FaultContext<TryData, EmptyFault, StartActivity>)
            .GetProperty("Exception"));
        Assert.Null(typeof(EmptyFault).GetProperty("Message"));
    }

    [Fact]
    public void FaultShapeIsCheckedWhenBuildingWorkflow()
    {
        var missing = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<TryData>("MissingFaultField")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowCustomActivity>())
            .Catch<ApprovalException, MissingPropertyFault>(body => body.Step<StartActivity>())
            .Build());
        Assert.Contains("no matching public property", missing.Message);

        var mismatched = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<TryData>("WrongFaultFieldType")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowCustomActivity>())
            .Catch<ApprovalException, WrongCodeFault>(body => body.Step<StartActivity>())
            .Build());
        Assert.Contains("has type", mismatched.Message);

        var callback = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<TryData>("DelegateFault")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowCallbackActivity>())
            .Catch<CallbackException, DelegateFault>(body => body.Step<StartActivity>())
            .Build());
        Assert.Contains("cannot be checkpointed", callback.Message);
    }

    [Fact]
    public async Task UnselectedDelegateOnExceptionDoesNotEnterCheckpoint()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<TryData>("CallbackExceptionCatch")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowCallbackActivity>())
            .Catch<CallbackException, MessageFault>(body => body
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
                .WaitFor<Approval>("review"))
            .Build();

        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new TryData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal("callback failure", Data(started).Error);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var completed = await secondProvider.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, "review", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("callback failure", Data(completed).Error);
    }

    [Fact]
    public void CatchMappingRejectsUnmarkedObjectPropertyAtBuild()
    {
        var failure = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<TryData>("UnmarkedFaultObject")
            .Step<StartActivity>()
            .Try(body => body.Step<StartActivity>())
            .Catch<UnmarkedDetailsException, ObjectDetailsFault>(body => body.Step<NoopObjectActivity>(setup => setup
                .Input(activity => activity.Details).From(context => context.Fault.Details)))
            .Build());

        Assert.Contains("cannot be checkpointed", failure.Message);
    }

    [Fact]
    public void CatchMappingRejectsUnsupportedAllowlistedTypeAtBuild()
    {
        var failure = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<TryData>("InvalidFaultAllowlist")
            .Step<StartActivity>()
            .Try(body => body.Step<StartActivity>())
            .Catch<InvalidAllowlistException, ObjectDetailsFault>(body => body.Step<NoopObjectActivity>(setup => setup
                .Input(activity => activity.Details).From(context => context.Fault.Details)))
            .Build());

        Assert.Contains("cannot be checkpointed", failure.Message);
    }

    [Fact]
    public void WholeExceptionMappingFailsAtBuildWithoutWait()
    {
        var failure = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<TryData>("WholeFault")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowCustomActivity>())
            .Catch<ApprovalException, Exception>(body => body
                .Step<ReadWholeExceptionActivity>())
            .Build());

        Assert.Contains("cannot be checkpointed", failure.Message);
    }

    [Fact]
    public async Task MarkedObjectPropertySurvivesCatchWaitAndProviderRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowClient>()
                .RunAsync(MarkedObjectWorkflow(), new TryData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal(0, Data(started).After);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(MarkedObjectWorkflow());
        var completed = await secondProvider.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, "review", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(42, Data(completed).After);
    }

    [Fact]
    public void ObjectFaultIsRejectedAtBuildEvenWhenCatchWaits()
    {
        var failure = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<TryData>("ObjectFaultWait")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowUnlistedDetailsActivity>())
            .Catch<UnmarkedDetailsException, ObjectDetailsFault>(body => body
                .Step<NoopObjectActivity>(setup => setup
                    .Input(activity => activity.Details).From(context => context.Fault.Details))
                .WaitFor<Approval>("review"))
            .Build());

        Assert.Contains("cannot be checkpointed", failure.Message);
    }

    [Fact]
    public async Task NullExceptionDetailUsesConditionalFallbackAcrossWait()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<TryData>("NullableFaultDetail")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowNullableDetailsActivity>())
            .Catch<NullableDetailsException, NullableDetailsFault>(body => body
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx =>
                        ctx.Fault.Details == null ? "fallback" : ctx.Fault.Details.Code)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
                .WaitFor<Approval>("review"))
            .Build();

        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new TryData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal("fallback", Data(started).Error);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var completed = await secondProvider.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, "review", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("fallback", Data(completed).Error);
    }

    [Fact]
    public void NullIntermediateFaultDetailSurvivesProjection()
    {
        var fault = (NullableDetailsFault)FaultProjection
            .For(typeof(NullableDetailsException), typeof(NullableDetailsFault))
            .Capture(new NullableDetailsException());

        Assert.Null(fault.Details);
    }

    [Fact]
    public void ExceptionCheckpointVerifiesCustomPropertiesAndInnerExceptions()
    {
        var saved = SerializedException.From(new InvalidOperationException(
            "outer", new ApprovalException("custom failure", 42)));

        Assert.True(saved.IsRestorable, saved.RestorationFailureReason);
        var restored = Assert.IsType<InvalidOperationException>(saved.Restore());
        Assert.Equal("outer", restored.Message);
        Assert.Equal(42, Assert.IsType<ApprovalException>(restored.InnerException).Code);
    }

    [Fact]
    public void ExceptionCheckpointRejectsCustomPropertyOmittedByConstructor()
    {
        var saved = SerializedException.From(new MutableApprovalException("custom failure") { Code = 42 });

        Assert.False(saved.IsRestorable);
        Assert.Contains("Code", saved.RestorationFailureReason);
    }

    [Fact]
    public void ExceptionCheckpointRejectsDataThatCannotBeRestored()
    {
        var error = new InvalidOperationException("business failure");
        error.Data["decision"] = "declined";

        var saved = SerializedException.From(error);

        Assert.False(saved.IsRestorable);
        Assert.Contains("Data", saved.RestorationFailureReason);
    }

    [Fact]
    public async Task UnrestorableCustomPropertyIsStillAvailableToLiveCatch()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("MutableCatchNoWait")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowMutableApprovalActivity>())
            .Catch<MutableApprovalException, ApprovalFault>(body => body.Step<CaptureCodeActivity>(setup => setup
                .Input(activity => activity.Code).From(ctx => ctx.Fault.Code)
                .Output(activity => activity.Code).To(ctx => ctx.WorkflowData.After)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(42, Data(result).After);
    }

    [Fact]
    public async Task UnselectedCustomPropertyDoesNotBlockCatchWait()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("MutableCatchWait")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowMutableApprovalActivity>())
            .Catch<MutableApprovalException, ApprovalFault>(body => body
                .Step<StartActivity>().WaitFor<Approval>("review"))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        var completed = await services.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(result.InstanceId, "review", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, Data(completed).Finally);
    }

    [Fact]
    public async Task FailureInCatchRunsFinallyAndFaults()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("FailureInCatch")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowActivity>())
            .Catch<InvalidOperationException, MessageFault>(body => body
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
                .Step<ThrowActivity>())
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new TryData());
        var result = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, Data(result).Finally);
        Assert.Equal("boom", Data(result).Error);
    }

    [Fact]
    public async Task FailureInCatchDoesNotEnterAnotherCatchOnTheSameTry()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("CatchFailureDoesNotMatchSibling")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowActivity>())
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<ThrowArgumentActivity>())
            .Catch<ArgumentException, MessageFault>(body => body.Step<CountActivity>(setup => setup
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(0, Data(result).Catch);
        Assert.Equal(1, Data(result).Finally);
    }

    [Fact]
    public async Task WaitInsideFinally_CompletesBeforeTheNextStep()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
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

        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(1, Data(started).Body);
        Assert.Equal(0, Data(started).After);

        var completed = await engine.ResumeAsync(started.InstanceId, "audit", new Approval());
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
            .Try(body => body.WaitFor<Approval>("approval").Step<ThrowActivity>())
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Finally)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally)))
            .Build();

        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new TryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var result = await engine.ResumeAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, Data(result).Finally);
        Assert.Contains("boom", result.ErrorMessage);
    }

    private static WorkflowDefinition CatchWaitWorkflow() => Workflow.Create<TryData>("WaitInCatch")
        .Step<StartActivity>()
        .Try(body => body.Step<ThrowActivity>())
        .Catch<InvalidOperationException, MessageFault>(body => body
            .Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
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
        return Workflow.Create<TryData>("CustomCatchRecovery")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowCustomActivity>())
            .Catch<ApprovalException, ApprovalFault>(body => body
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
                .WaitFor<Approval>("review")
                .Step<CaptureCodeActivity>(setup => setup
                    .Input(activity => activity.Code).From(ctx => ctx.Fault.Code)
                    .Output(activity => activity.Code).To(ctx => ctx.WorkflowData.After))
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => "restored: " + ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();
    }

    private static WorkflowDefinition MarkedObjectWorkflow()
    {
        return Workflow.Create<TryData>("MarkedFaultObjectRecovery")
            .Step<StartActivity>()
            .Try(body => body.Step<ThrowMarkedDetailsActivity>())
            .Catch<MarkedDetailsException, MarkedDetailsFault>(body => body
                .Step<NoopObjectActivity>(setup => setup
                    .Input(activity => activity.Details).From(context => context.Fault.Details))
                .WaitFor<Approval>("review")
                .Step<CaptureDetailsActivity>(setup => setup
                    .Input(activity => activity.Details).From(ctx => ctx.Fault.Details)
                    .Output(activity => activity.Code).To(context => context.WorkflowData.After)))
            .Build();
    }

    private static ServiceProvider CreateServices(IWorkflowStateRepository? repository = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<StepLog>();
        services.AddTransient<LoggedCountActivity>();
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
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
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        return services.BuildServiceProvider();
    }

    private static TryData Data(WorkflowExecutionResult result) => Assert.IsType<TryData>(result.WorkflowData);

    [Fact]
    public async Task OutputMappingFailureRunsCatchAndFinally()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<OutputMappingData>("OutputMappingFailure")
            .Step<StartActivity>()
            .Try(body => body.Step<CountActivity>(setup => setup
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.RejectedOutput)))
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.CaughtError)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinallyCount)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new OutputMappingData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<OutputMappingData>(result.WorkflowData);
        Assert.Contains("output mapping failed", data.CaughtError);
        Assert.Equal(1, data.FinallyCount);
    }

    [Fact]
    public async Task FaultProjectionFailureRunsFinallyAndReachesOuterCatch()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<TryData>("FaultProjectionFailure")
            .Step<StartActivity>()
            .Try(outer => outer
                .Try(inner => inner.Step<ThrowFragileActivity>())
                .Catch<FragileException, FragileFault>(handler => handler.Step<CountActivity>(setup => setup
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Catch)))
                .Finally(body => body.Step<CountActivity>(setup => setup
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Finally))))
            .Catch<InvalidOperationException, MessageFault>(handler => handler.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new TryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(0, Data(result).Catch);
        Assert.Equal(1, Data(result).Finally);
        Assert.Contains("Fault projection", Data(result).Error);
    }

    [Fact]
    public async Task WaitOutputMappingFailureRunsCatchAndFinally()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<OutputMappingData>("WaitOutputMappingFailure")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval", null, setup => setup
                .Output(reply => reply.Value).To(ctx => ctx.WorkflowData.RejectedOutput)))
            .Catch<InvalidOperationException, MessageFault>(handler => handler.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.CaughtError)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinallyCount)))
            .Build();

        var started = await engine.RunAsync(definition, new OutputMappingData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await engine.ResumeAsync(started.InstanceId, "approval", new Approval { Value = 1 });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<OutputMappingData>(result.WorkflowData);
        Assert.Contains("output mapping failed", data.CaughtError);
        Assert.Equal(1, data.FinallyCount);
    }

    [Fact]
    public async Task AcceptedWaitOutputFailureRecoversThroughCatchAfterRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<OutputMappingData>("RecoveredWaitOutputFailure")
            .Step<StartActivity>()
            .Try(body => body.WaitFor<Approval>("approval", null, setup => setup
                .Output(reply => reply.Value).To(ctx => ctx.WorkflowData.RejectedOutput)))
            .Catch<InvalidOperationException, MessageFault>(handler => handler.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.CaughtError)))
            .Finally(body => body.Step<CountActivity>(setup => setup
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinallyCount)))
            .Build();

        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new OutputMappingData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        var saved = (await repository.GetWorkflowInstanceAsync(instanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        var wait = Assert.Single(checkpoint.Waits);
        var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
        continuation.AcceptedWait = new AcceptedWaitState
        {
            StepId = wait.StepId,
            Event = SerializedValue.From(new Approval { Value = 1 })!
        };
        checkpoint.Waits.Clear();
        continuation.Status = ContinuationStatus.Active;
        saved.Status = WorkflowStatus.Running;
        saved.ExecutionStateJson = System.Text.Json.JsonSerializer.Serialize(checkpoint);
        await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(repository, saved.InstanceId)).Epoch, saved.Revision, Guid.NewGuid().ToString("N")));
        await StoreTestSupport.ReleaseAsync(repository, saved.InstanceId);

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var result = await secondProvider.GetRequiredService<IWorkflowClient>()
            .RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<OutputMappingData>(result.WorkflowData);
        Assert.Contains("output mapping failed", data.CaughtError);
        Assert.Equal(1, data.FinallyCount);
    }

    public sealed class FragileException : Exception
    {
        public int Code => throw new InvalidOperationException("property getter failed");
    }

    public sealed class FragileFault
    {
        public int Code { get; set; }
    }

    public sealed class ThrowFragileActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new FragileException();
    }

    public sealed class OutputMappingData
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public int RejectedOutput { get => 0; set => throw new InvalidOperationException("output mapping failed"); }
        public int FinallyCount { get; set; }
        public string? CaughtError { get; set; }
    }

    public sealed class TryData
    {
        public int Body { get; set; }
        public int Catch { get; set; }
        public int Finally { get; set; }
        public int After { get; set; }
        public string? Error { get; set; }
    }

    public sealed class Approval
    {
        public int Value { get; set; }
    }

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

    public sealed class FinallyJoinProbe
    {
        public int WinnerRuns;
    }

    public sealed class BoundedCheckpointStore : IWorkflowStateRepository
    {
        public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
            _inner.RegisterMemberAsync(member, cancellationToken);
        public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
            CancellationToken cancellationToken = default) =>
            _inner.HeartbeatAsync(memberId, capabilities, cancellationToken);
        public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
            _inner.UnregisterMemberAsync(memberId, cancellationToken);
        public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
            _inner.GetLiveMembersAsync(cancellationToken);

        public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
            CancellationToken cancellationToken = default) =>
            _inner.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken);
        public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId) =>
            _inner.GetTombstoneAsync(instanceId);
        public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
            _inner.AdmitWorkflowInstanceAsync(commit, admission);
        public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
            _inner.GetWorkflowOwnershipAsync(instanceId);
        public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
            _inner.GetWorkflowAdmissionAsync(sender, trigger, key);

        public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false) =>
            _inner.RegisterPublisherAsync(name, eventTypes, replayRetained);
        public Task UnregisterPublisherAsync(string name, string actor = "host") => _inner.UnregisterPublisherAsync(name, actor);
        public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query) => _inner.ListDeliveriesAsync(query);
        public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor) => _inner.RetryDeliveryAsync(eventId, publisherName, actor);
        public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync() => _inner.ListPublishersAsync();
        public Task<bool> EnablePublisherAsync(string publisherName, string actor) => _inner.EnablePublisherAsync(publisherName, actor);
        public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration, int limit) =>
            _inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);
        public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration) =>
            _inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);
        public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result) =>
            _inner.SettleDeliveryAsync(eventId, publisherName, token, result);
        public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName) =>
            _inner.GetDeliveryAsync(eventId, publisherName);
        public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName) =>
            _inner.GetPublisherAsync(publisherName);

        public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
            _inner.AppendEventAsync(workflowEvent);
        public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
            _inner.GetPendingEventsAsync(instanceId, afterSequence, limit);
        public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
            _inner.GetEventAsync(instanceId, eventId);
        public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
            _inner.GetWaitAsync(instanceId, waitId);
        public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
            IReadOnlyCollection<string> instanceIds) =>
            _inner.GetInboxSequencesAsync(instanceIds);
        public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
            _inner.GetOpenWaitsAsync(query);

        private readonly InMemoryWorkflowStateRepository _inner = new();
        public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason) =>
            _inner.RequestCancellationAsync(instanceId, reason);
        public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
            _inner.GetCancellationRequestAsync(instanceId);
        public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit) =>
            _inner.GetRunnableInstancesAsync(limit);
        public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            _inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration);
        public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            _inner.RenewExecutionLeaseAsync(instanceId, token, duration);
        public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
            _inner.ReleaseExecutionLeaseAsync(instanceId, token);
        private int _commits;

        public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var instance = commit.ReadTrunk();
            if (Interlocked.Increment(ref _commits) > 80)
                throw new InvalidOperationException("Parallel cleanup exceeded the checkpoint budget");
            return _inner.CommitWorkflowInstanceAsync(commit);
        }

        public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) =>
            _inner.GetWorkflowInstanceSummaryAsync(instanceId);
        public Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
            IReadOnlyCollection<string> instanceIds) =>
            StoreTestSupport.ReadCancellationsAsync(instanceIds, GetCancellationRequestAsync);

        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
            _inner.GetWorkflowInstanceAsync(instanceId);
        public Task<InstancePage> QueryInstancesAsync(InstanceQuery query) =>
            _inner.QueryInstancesAsync(query);

        public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason) =>
            _inner.RequestTerminationAsync(instanceId, reason);
        public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
            int limit = 1000) =>
            _inner.GetTraceAsync(instanceId, afterSequence, limit);
        public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null) =>
            _inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention);
    }

    public sealed class CountFinallyWinnerActivity(FinallyJoinProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.WinnerRuns);
            return Task.CompletedTask;
        }
    }

    public sealed class ThrowArgumentActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new ArgumentException("catch failed");
    }

    /// <summary>What the logged steps did, in order: each entry is the property a step wrote and its value.</summary>
    public sealed class StepLog
    {
        private readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public void Add(string entry)
        {
            lock (_entries)
                _entries.Add(entry);
        }
    }

    /// <summary>Counts like <see cref="CountActivity"/> and logs what it wrote, so cleanup proves it ran even when its data is discarded.</summary>
    public sealed class LoggedCountActivity(StepLog log) : IAsyncActivity
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count + 1;
            log.Add($"{Name}:{Result}");
            return Task.CompletedTask;
        }
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

    public sealed class MutableApprovalException(string message) : Exception(message)
    {
        public int Code { get; set; }
    }

    public sealed class ThrowMutableApprovalActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new MutableApprovalException("custom failure") { Code = 42 };
    }

    public sealed class CallbackException() : Exception("callback failure")
    {
        public Action Callback { get; } = () => { };
    }

    public sealed class ThrowCallbackActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new CallbackException();
    }

    public sealed class ApiErrorDetails
    {
        public int Code { get; set; }
    }

    public sealed class OtherErrorDetails
    {
        public int Code { get; set; }
    }

    public sealed class UnmarkedDetailsException(object details) : Exception("unmarked details")
    {
        public object Details { get; } = details;
    }

    public sealed class MarkedDetailsException(ApiErrorDetails details) : Exception("marked details")
    {
        public ApiErrorDetails Details { get; } = details;
    }

    public sealed class InvalidAllowlistException : Exception
    {
        public object Details { get; } = new();
    }

    public sealed class OptionalErrorDetails
    {
        public string Code { get; set; } = "";
    }

    public sealed class NullableDetailsException : Exception
    {
        public OptionalErrorDetails? Details { get; }
    }

    public sealed class ThrowNullableDetailsActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new NullableDetailsException();
    }

    public sealed class ThrowMarkedDetailsActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new MarkedDetailsException(new ApiErrorDetails { Code = 42 });
    }

    public sealed class ThrowUnlistedDetailsActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new UnmarkedDetailsException(new OtherErrorDetails { Code = 17 });
    }

    public sealed class NoopObjectActivity : IAsyncActivity
    {
        public object? Details { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class ReadWholeExceptionActivity : IAsyncActivity
    {
        public Exception? Error { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class CaptureDetailsActivity : IAsyncActivity
    {
        public object? Details { get; set; }
        public int Code { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Code = ((ApiErrorDetails)Details!).Code;
            return Task.CompletedTask;
        }
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

    public sealed class CleanupJoinProbe
    {
        public TaskCompletionSource CleanupEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class NestedJoinProbe
    {
        public TaskCompletionSource InnerStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OuterStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool OuterCancelled { get; set; }
    }

    public sealed class WaitForNestedCancellationActivity(NestedJoinProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.InnerStarted.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public sealed class CompleteNestedWinnerActivity(NestedJoinProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            await Task.WhenAll(probe.InnerStarted.Task, probe.OuterStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public sealed class WaitForOuterCancellationActivity(NestedJoinProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.OuterStarted.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                probe.OuterCancelled = true;
                throw;
            }
        }
    }

    public sealed class ThrowLosingCleanupActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("losing cleanup failure");
    }

    public sealed class ThrowAfterCleanupStartsActivity(CleanupJoinProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            await probe.CleanupEntered.Task.WaitAsync(cancellationToken);
            probe.Release.TrySetResult();
            throw new InvalidOperationException("outer failure");
        }
    }

    public sealed class WaitForCleanupReleaseActivity(CleanupJoinProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.CleanupEntered.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
        }
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
