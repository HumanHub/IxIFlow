using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Serialization;

namespace IxIFlow.Tests.ExecutionTests;

public class StructuredSagaWaitTests
{
    [Fact]
    public async Task SagaWait_ResumesAtTheNextStepAfterProviderRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(ForwardWorkflow(), new SagaData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal(1, Data(started).Reserved);
            Assert.Equal(0, Data(started).Completed);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(ForwardWorkflow());
        var result = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Reserved);
        Assert.Equal(1, Data(result).Completed);
    }

    [Fact]
    public async Task SagaWait_RestoresConcreteWorkflowDataWhenStartedAsObject()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            object data = new SagaData();
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(ForwardWorkflow(), data);
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(ForwardWorkflow());
        var result = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Reserved);
        Assert.Equal(1, Data(result).Completed);
    }

    [Fact]
    public async Task SagaFailureAfterWait_CompensatesCompletedStepAfterProviderRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(FailingWorkflow(), new SagaData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(FailingWorkflow());
        var result = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("payment failed", result.ErrorMessage);
        Assert.Equal(1, Data(result).Reserved);
        Assert.Equal(1, Data(result).Compensated);
    }

    [Fact]
    public async Task SagaFailureCompensatesInReverseOrderBeforeOuterCatch()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("SagaReverseCompensation")
            .Step<BeginActivity>()
            .Try(body => body.Saga(saga => saga
                .Step<TraceActivity>(setup => setup
                    .Input(activity => activity.Key).From(ctx => "A")
                    .Input(activity => activity.Trace).From(ctx => ctx.WorkflowData.Trace)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Trace)
                    .CompensateWith<UndoTraceActivity>(comp => comp
                        .Input(activity => activity.Key).From(ctx => "a")
                        .Input(activity => activity.Trace).From(ctx => ctx.WorkflowData.Trace)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Trace)))
                .Step<TraceActivity>(setup => setup
                    .Input(activity => activity.Key).From(ctx => "B")
                    .Input(activity => activity.Trace).From(ctx => ctx.WorkflowData.Trace)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Trace)
                    .CompensateWith<UndoTraceActivity>(comp => comp
                        .Input(activity => activity.Key).From(ctx => "b")
                        .Input(activity => activity.Trace).From(ctx => ctx.WorkflowData.Trace)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Trace)))
                .WaitFor<Approval>("approval")
                .Step<FailActivity>(_ => { })))
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new SagaData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("ABba", Data(result).Trace);
        Assert.Equal("payment failed", Data(result).Error);
    }

    [Fact]
    public async Task WaitAnyCompensatesTheLosingSagaBeforeContinuing()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("LosingSaga")
            .Step<BeginActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch.Saga(saga => saga
                    .Step<ReserveActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                        .CompensateWith<ReleaseActivity>(comp => comp
                            .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))
                    .WaitFor<Approval>("loser")))
                .Do(branch => branch.WaitFor<Approval>("winner")))
            .Step<CompleteActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Compensated)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new SagaData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(1, Data(started).Reserved);

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "winner", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Compensated);
        Assert.Equal(2, Data(result).Completed);
    }

    [Fact]
    public async Task FailedLosingSagaCompensationDoesNotRunAnEnclosingCatch()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("LosingSagaCleanupFailure")
            .Step<BeginActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch
                    .Try(body => body.Saga(saga => saga
                        .Step<ReserveActivity>(setup => setup
                            .CompensateWith<FailCompensationActivity>())
                        .WaitFor<Approval>("loser")))
                    .Catch<InvalidOperationException, MessageFault>(catchBody => catchBody
                        .Step<CompleteActivity>(setup => setup
                            .Input(activity => activity.Count).From(ctx => 10)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed))))
                .Do(branch => branch.WaitFor<Approval>("winner")))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new SagaData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "winner", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(0, Data(result).Completed);
        Assert.Contains("compensation failed", result.ErrorMessage);
    }

    [Fact]
    public async Task EachCompensationReceivesTheOriginalForwardActivityResult()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("MultipleCompensations")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                    .CompensateWith<ReleaseActivity>(comp => comp
                        .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated))
                    .CompensateWith<CompleteActivity>(comp => comp
                        .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed)))
                .WaitFor<Approval>("approval")
                .Step<FailActivity>(_ => { }))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new SagaData());

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal("payment failed", result.ErrorMessage);
        Assert.Equal(1, Data(result).Compensated);
        Assert.Equal(2, Data(result).Completed);
    }

    [Fact]
    public async Task CompensationChainReadsPreviousStepAndPreviousCompensationAfterRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<SagaData>("CompensationChain")
            .Step<SeedActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                    .CompensateWith<CompleteActivity>(comp => comp
                        .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed))
                    .CompensateWith<ChainUndoActivity, CompleteActivity>(comp => comp
                        .Input(activity => activity.Seed).From((SeedActivity seed) => seed.Result)
                        .Input(activity => activity.PreviousCompensation).From((CompleteActivity previous) => previous.Result)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))
                .WaitFor<Approval>("approval")
                .Step<FailActivity>(_ => { }))
            .Build();

        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new SagaData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var result = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("payment failed", result.ErrorMessage);
        Assert.Equal(2, Data(result).Completed);
        Assert.Equal(9, Data(result).Compensated);
    }

    [Fact]
    public async Task CompensationChainUsesTheDeclaredSourceWhenTypesOverlap()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("CompensationAssignableSources")
            .Step<DerivedValueActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                    .CompensateWith<BaseValueActivity>(comp => comp
                        .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result))
                    .CompensateWith<ChainUndoActivity, BaseValueActivity>(comp => comp
                        .Input(activity => activity.Seed).From((DerivedValueActivity seed) => seed.Result)
                        .Input(activity => activity.PreviousCompensation).From((BaseValueActivity previous) => previous.Result)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))
                .WaitFor<Approval>("approval")
                .Step<FailActivity>(_ => { }))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new SagaData());

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(8, Data(result).Compensated);
    }

    [Fact]
    public async Task CompensationChainRejectsAMissingPredecessorBeforeExecution()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("InvalidCompensationChain")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .CompensateWith<ChainUndoActivity, BaseValueActivity>(_ => { }))
                .WaitFor<Approval>("approval"))
            .Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new SagaData()));

        Assert.Contains("requires a preceding", error.Message);
    }

    [Fact]
    public async Task FailedCompensationDoesNotPreventTheRemainingCompensations()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("FailingCompensation")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                    .CompensateWith<FailCompensationActivity>()
                    .CompensateWith<ReleaseActivity>(comp => comp
                        .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))
                .WaitFor<Approval>("approval")
                .Step<FailActivity>(_ => { }))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new SagaData());

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("compensation failed", result.ErrorMessage);
        Assert.Equal(1, Data(result).Compensated);
    }

    [Fact]
    public async Task CancelledSagaCompensatesAnInflightActivityThatSucceeded()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<SlowReserveProbe>();
        collection.AddTransient<SlowReserveActivity>();
        collection.AddIxIFlow();
        using var services = collection.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("CancelledInflightSaga")
            .Step<BeginActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch =>
                {
                    branch.Saga(saga => saga.Step<SlowReserveActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                        .CompensateWith<ReleaseActivity>(comp => comp
                            .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated))));
                })
                .Do(branch => branch.Step<BeginActivity>(_ => { })))
            .Build();
        var executing = services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());
        var probe = services.GetRequiredService<SlowReserveProbe>();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        probe.Release.SetResult();

        var result = await executing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Reserved);
        Assert.Equal(1, Data(result).Compensated);
    }

    [Fact]
    public async Task SagaCompensationInsideCatchUsesItsCurrentStep()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("SagaInsideCatch")
            .Step<BeginActivity>()
            .Try(body => body.WaitFor<Approval>("start").Step<FailActivity>(_ => { }))
            .Catch<InvalidOperationException, MessageFault>(body => body.Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                    .CompensateWith<ReleaseActivity>(comp => comp
                        .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))
                .WaitFor<Approval>("review")
                .Step<FailActivity>(_ => { })))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new SagaData());
        var reviewing = await engine.ResumeWorkflowAsync(started.InstanceId, "start", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Suspended, reviewing.Status);

        var result = await engine.ResumeWorkflowAsync(started.InstanceId, "review", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, Data(result).Compensated);
    }

    [Fact]
    public async Task SagaWaitPredicateReadsPreviousActivityAfterProviderRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(PredicateWorkflow(), new SagaData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(PredicateWorkflow());
        var engine = secondProvider.GetRequiredService<IWorkflowEngine>();
        var rejected = await engine.ResumeWorkflowAsync(instanceId, "approval", new Approval { Expected = 0 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, rejected.Status);

        var result = await engine.ResumeWorkflowAsync(instanceId, "approval", new Approval { Expected = 1 });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Data(result).Completed);
    }

    [Fact]
    public async Task StructuredSagaIgnoresConfiguredStepFailureAndReachesWait()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("SagaStepPolicy")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .Step<FailActivity>(setup => setup
                    .OnError<InvalidOperationException>(handler => handler.ThenIgnore()))
                .WaitFor<Approval>("approval"))
            .Build();

        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new SagaData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var resumed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
    }

    [Fact]
    public async Task SagaContinueRestoresThePreviousStepFromBeforeTheSaga()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("ContinuePrevious")
            .Step<SeedActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved))
                .Step<FailActivity>(_ => { }))
            .OnError<InvalidOperationException>(error => error.Compensate().ThenContinue())
            .Step<CompleteActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.PreviousStep.Result)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(8, Data(result).Completed);
    }

    [Fact]
    public async Task SagaRetryRestoresThePreviousStepFromBeforeTheSaga()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SagaRetryProbe>();
        services.AddTransient<FailOnceSagaActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("RetryPrevious")
            .Step<SeedActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.PreviousStep.Result)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved))
                .Step<FailOnceSagaActivity>(_ => { }))
            .OnError<InvalidOperationException>(error => error.Compensate().ThenRetry(1))
            .Step<CompleteActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.PreviousStep.Result)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(2, provider.GetRequiredService<SagaRetryProbe>().Attempts);
        Assert.Equal(8, Data(result).Reserved);
        Assert.Equal(8, Data(result).Completed);
    }

    [Fact]
    public async Task StructuredSagaRunsOutcomeBranchBeforeWait()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("SagaOutcomeWait")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved))
                .OutcomeOn(ctx => ctx.WorkflowData.Reserved, outcomes => outcomes
                    .Outcome(1, branch => branch.Step<CompleteActivity>(setup => setup
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed))))
                .WaitFor<Approval>("approval"))
            .Build();
        var data = new SagaData();

        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, data);
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(1, data.Reserved);
        Assert.Equal(1, data.Completed);
        var resumed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
    }

    [Fact]
    public async Task OutcomeBranchActivityCompensatesWhenLaterSagaStepFails()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("OutcomeCompensation")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .OutcomeOn(ctx => ctx.WorkflowData.Method, outcomes => outcomes
                    .Outcome("card", branch => branch.Step<ReserveActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                        .CompensateWith<ReleaseActivity>(compensation => compensation
                            .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))))
                .Step<FailActivity>(_ => { }))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData { Method = "card" });

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, Data(result).Reserved);
        Assert.Equal(1, Data(result).Compensated);
    }

    [Fact]
    public async Task OutputMappingFailureCompensatesTheCompletedSagaActivity()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("OutputMappingCompensation")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<ReserveActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.RejectedOutput)
                .CompensateWith<ReleaseActivity>(compensation => compensation
                    .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("output mapping failed", result.ErrorMessage);
        Assert.Equal(1, Data(result).Compensated);
    }

    [Fact]
    public async Task ExhaustedSagaRetryInParallelReachesParentCatch()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("ParallelSagaRetry")
            .Step<BeginActivity>()
            .Try(body => body.Parallel(parallel => parallel
                .Do(branch => branch.Saga(saga => saga.Step<FailActivity>(_ => { }))
                    .OnError<InvalidOperationException>(error => error.Compensate().ThenRetry(1)))
                .Do(branch => branch.Step<BeginActivity>(_ => { }))))
            .Catch<InvalidOperationException, MessageFault>(handler => handler.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Contains("Saga retry exhausted", Data(result).Error);
    }

    [Fact]
    public async Task StepRetryKeepsItsInvocationId()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RetryIdProbe>();
        services.AddTransient<RetryIdActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("StableStepRetryId")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<RetryIdActivity>(setup => setup
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(1))))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var ids = provider.GetRequiredService<RetryIdProbe>().InvocationIds;
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], ids[1]);
    }

    [Fact]
    public async Task FullSagaRetryStartsANewInvocation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RetryIdProbe>();
        services.AddSingleton<SagaRetryProbe>();
        services.AddTransient<SagaAttemptIdActivity>();
        services.AddTransient<FailOnceSagaActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("NewSagaRetryId")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .Step<SagaAttemptIdActivity>(_ => { })
                .Step<FailOnceSagaActivity>(_ => { }))
            .OnError<InvalidOperationException>(error => error.Compensate().ThenRetry(1))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var ids = provider.GetRequiredService<RetryIdProbe>().InvocationIds;
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
    }

    private static WorkflowDefinition ForwardWorkflow() => Workflow.Create<SagaData>("SagaForwardWait")
        .Step<BeginActivity>()
        .Saga(saga => saga
            .Step<ReserveActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved))
            .WaitFor<Approval>("approval")
            .Step<CompleteActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Completed)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed)))
        .Build();

    private static WorkflowDefinition FailingWorkflow() => Workflow.Create<SagaData>("SagaCompensateWait")
        .Step<BeginActivity>()
        .Saga(saga => saga
            .Step<ReserveActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                .CompensateWith<ReleaseActivity>(comp => comp
                    .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))
            .WaitFor<Approval>("approval")
            .Step<FailActivity>(_ => { }))
        .Build();

    private static WorkflowDefinition PredicateWorkflow() => Workflow.Create<SagaData>("SagaPredicateWait")
        .Step<BeginActivity>()
        .Saga(saga => saga
            .Step<ReserveActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved))
            .WaitFor<Approval>("approval", (approval, context) =>
                approval.Expected == context.PreviousStep.Result)
            .Step<CompleteActivity>(setup => setup
                .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Completed)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Completed)))
        .Build();

    private static ServiceProvider CreateServices(IWorkflowStateRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddSingleton(repository);
        return services.BuildServiceProvider();
    }

    private static SagaData Data(WorkflowExecutionResult result) => Assert.IsType<SagaData>(result.WorkflowData);

    public sealed class SagaData
    {
        public string Method { get; set; } = "";
        public int Reserved { get; set; }
        public int Completed { get; set; }
        public int Compensated { get; set; }
        public string Trace { get; set; } = "";
        public string? Error { get; set; }
        [JsonIgnore]
        public int RejectedOutput { get => 0; set => throw new InvalidOperationException("output mapping failed"); }
    }

    public sealed class Approval
    {
        public int Expected { get; set; }
    }

    public sealed class BeginActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class SeedActivity : IAsyncActivity
    {
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = 7;
            return Task.CompletedTask;
        }
    }

    public sealed class ChainUndoActivity : IAsyncActivity
    {
        public int Seed { get; set; }
        public int PreviousCompensation { get; set; }
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Seed + PreviousCompensation;
            return Task.CompletedTask;
        }
    }

    public class BaseValueActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; protected set; }

        public virtual Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count;
            return Task.CompletedTask;
        }
    }

    public sealed class DerivedValueActivity : BaseValueActivity
    {
        public override Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = 7;
            return Task.CompletedTask;
        }
    }

    public sealed class ReserveActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count + 1;
            return Task.CompletedTask;
        }
    }

    public sealed class CompleteActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count + 1;
            return Task.CompletedTask;
        }
    }

    public sealed class ReleaseActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count;
            return Task.CompletedTask;
        }
    }

    public sealed class FailActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("payment failed");
    }

    public sealed class SagaRetryProbe
    {
        public int Attempts { get; set; }
    }

    public sealed class RetryIdProbe
    {
        public List<string> InvocationIds { get; } = [];
    }

    public sealed class RetryIdActivity(RetryIdProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.InvocationIds.Add(context.InvocationId);
            if (probe.InvocationIds.Count == 1)
                throw new InvalidOperationException("retry once");
            return Task.CompletedTask;
        }
    }

    public sealed class SagaAttemptIdActivity(RetryIdProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.InvocationIds.Add(context.InvocationId);
            return Task.CompletedTask;
        }
    }

    public sealed class FailOnceSagaActivity(SagaRetryProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (++probe.Attempts == 1)
                throw new InvalidOperationException("retry once");
            return Task.CompletedTask;
        }
    }

    public sealed class TraceActivity : IAsyncActivity
    {
        public string Key { get; set; } = "";
        public string Trace { get; set; } = "";
        public string Result { get; private set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Trace + Key;
            return Task.CompletedTask;
        }
    }

    public sealed class UndoTraceActivity : IAsyncActivity
    {
        public string Key { get; set; } = "";
        public string Trace { get; set; } = "";
        public string Result { get; private set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Trace + Key;
            return Task.CompletedTask;
        }
    }

    public sealed class ReadErrorActivity : IAsyncActivity
    {
        public string Message { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class FailCompensationActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("compensation failed");
    }

    public sealed class SlowReserveProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class SlowReserveActivity(SlowReserveProbe probe) : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; private set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => probe.Cancelled.TrySetResult());
            probe.Started.SetResult();
            await probe.Release.Task;
            Result = Count + 1;
        }
    }
}
