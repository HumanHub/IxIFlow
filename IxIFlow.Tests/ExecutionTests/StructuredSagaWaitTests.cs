using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Tests.ExecutionTests;

public class StructuredSagaWaitTests
{
    [Fact]
    public void SagaStepErrorPolicyMatchesSavedOriginalExceptionType()
    {
        var definition = Workflow.Create<SagaData>("SavedSagaStepError")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<ThrowNonRestorableSagaActivity>(step => step
                .OnError<NonRestorableSagaException>(handler => handler.ThenIgnore())))
            .Build();
        var scopes = new WorkflowScopeCatalog(definition);
        var saga = definition.Steps[1];
        var continuation = new ContinuationState
        {
            Stack =
            [
                new ScopePosition { ScopeId = "root", NextStepIndex = 1,
                    SagaState = new SagaScopeState() },
                new ScopePosition { ScopeId = scopes.SagaScope(saga) }
            ]
        };
        var saved = SerializedException.From(
            new NonRestorableSagaException("private constructor value"));
        Assert.False(saved.IsRestorable);

        Assert.True(SagaScopeTransitions.HandleStepFailure(scopes, continuation,
            saved.ForPropagation()));
        Assert.Equal(1, continuation.Stack[^1].NextStepIndex);
    }

    [Fact]
    public async Task SagaErrorHandlerWithoutContinuationRethrowsAfterHandler()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("ImplicitSagaRethrow")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<FailActivity>(_ => { }))
            .OnError<InvalidOperationException>(handler => handler.Step<HandlerMarkerActivity>())
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("payment failed", result.ErrorMessage);
        Assert.Equal("H", Data(result).Trace);
    }

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
    public async Task CancellationDuringSagaCompensationRemainsCancelled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<PausedCompensationProbe>();
        services.AddTransient<PausedReleaseActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("CancelDuringCompensation")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .Step<ReserveActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                    .CompensateWith<PausedReleaseActivity>(compensation => compensation
                        .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))
                .Step<FailActivity>(_ => { }))
            .OnError<InvalidOperationException>(error => error.Compensate().ThenRetry(1))
            .Build();

        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var instanceId = Guid.NewGuid().ToString("N");
        var executing = engine.ExecuteWorkflowAsync(definition, new SagaData(),
            new WorkflowOptions { InstanceId = instanceId });
        var probe = provider.GetRequiredService<PausedCompensationProbe>();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.CancelWorkflowAsync(instanceId,
            new CancellationReason { ReasonCode = "operator" });
        probe.Release.SetResult();

        var result = await executing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Status == WorkflowExecutionStatus.Cancelled,
            $"Expected cancellation, got {result.Status}: {result.ErrorMessage}");
        Assert.Equal(1, Data(result).Compensated);
    }

    [Fact]
    public async Task UnserializableSagaOutputStopsForResolutionBeforeCompensation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<UnsafeOutputProbe>();
        services.AddTransient<UnsafeOutputActivity>();
        services.AddTransient<UnsafeOutputCompensation>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("UnsafeSagaOutput")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<UnsafeOutputActivity>(step => step
                .CompensateWith<UnsafeOutputCompensation>()))
            .Build();

        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var result = await engine.ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        Assert.Contains("saga output could not be saved",
            Assert.Single(await engine.GetPendingActivitiesAsync(result.InstanceId))
                .Reason ?? string.Empty);
        Assert.Equal(1, provider.GetRequiredService<UnsafeOutputProbe>().Effects);
        Assert.Equal(0, provider.GetRequiredService<UnsafeOutputProbe>().Compensations);
    }

    public sealed class UnsafeOutputProbe
    {
        public int Effects;
        public int Compensations;
    }

    public sealed class UnsafeOutputActivity(UnsafeOutputProbe probe) : IAsyncActivity
    {
        public object? Result { get; set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Effects);
            Result = (Action)(() => { });
            return Task.CompletedTask;
        }
    }

    public sealed class UnsafeOutputCompensation(UnsafeOutputProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Compensations);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RecoveredSagaHandlerPreservesTheOriginalErrorForOuterCatch()
    {
        var repository = new HandlerStartCrashStore();
        using var services = CreateServices(repository);
        var definition = Workflow.Create<SagaData>("RecoveredSagaHandlerError")
            .Step<BeginActivity>()
            .Try(body => body
                .Saga(saga => saga.Step<FailActivity>(_ => { }))
                .OnError<InvalidOperationException, MessageFault>(error => error
                    .Step<RecoverableHandlerActivity>()))
            .Catch<InvalidOperationException, MessageFault>(handler => handler.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition, new SagaData()));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal("payment failed", Data(recovered).Error);
    }

    [Fact]
    public async Task RecoveredNonRestorableSagaErrorMatchesOuterCatch()
    {
        var repository = new HandlerStartCrashStore();
        using var services = CreateServices(repository);
        var definition = Workflow.Create<SagaData>("RecoveredNonRestorableSagaError")
            .Step<BeginActivity>()
            .Try(body => body
                .Saga(saga => saga.Step<ThrowNonRestorableSagaActivity>(_ => { }))
                .OnError<NonRestorableSagaException, MessageFault>(error => error
                    .Step<RecoverableHandlerActivity>()))
            .Catch<NonRestorableSagaException, MessageFault>(handler => handler.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition, new SagaData()));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal("payment failed", Data(recovered).Error);
    }

    [Fact]
    public async Task RecoveredSagaErrorProjectsASecondOuterFaultShape()
    {
        var repository = new HandlerStartCrashStore();
        using var services = CreateServices(repository);
        var definition = Workflow.Create<SagaData>("RecoveredSagaWithDifferentOuterFault")
            .Step<BeginActivity>()
            .Try(body => body
                .Saga(saga => saga.Step<ThrowNonRestorableSagaActivity>(_ => { }))
                .OnError<NonRestorableSagaException, MessageFault>(error => error
                    .Step<RecoverableHandlerActivity>()))
            .Catch<NonRestorableSagaException, MessageCodeFault>(handler => handler
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.PublicCode)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Trace))
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition, new SagaData()));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal("private constructor value", Data(recovered).Trace);
        Assert.Equal("payment failed", Data(recovered).Error);
    }

    [Fact]
    public async Task UnusedOuterFaultProjectionDoesNotInterruptHandledSagaError()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("UnusedOuterFaultProjection")
            .Step<BeginActivity>()
            .Try(body => body
                .Saga(saga => saga.Step<ThrowFragileSagaActivity>(_ => { }))
                .OnError<FragileSagaException, MessageFault>(error => error
                    .CompensateNone().ThenContinue()))
            .Catch<FragileSagaException, FragileSagaFault>(handler => handler
                .Step<HandlerMarkerActivity>(_ => { }))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("", Data(result).Trace);
    }

    [Fact]
    public async Task RecoveredUnhandledSagaErrorReportsOriginalExceptionType()
    {
        var repository = new HandlerStartCrashStore();
        using var services = CreateServices(repository);
        var definition = Workflow.Create<SagaData>("RecoveredUnhandledSagaError")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<ThrowNonRestorableSagaActivity>(_ => { }))
            .OnError<NonRestorableSagaException, MessageFault>(handler => handler
                .Step<RecoverableHandlerActivity>())
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition, new SagaData()));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Faulted, recovered.Status);
        Assert.Equal(typeof(NonRestorableSagaException).FullName, recovered.ErrorType);
    }

    [Fact]
    public async Task RecoveredOuterFaultReadsBasePropertyHiddenByInnerException()
    {
        var repository = new HandlerStartCrashStore();
        using var services = CreateServices(repository);
        var definition = Workflow.Create<SagaData>("RecoveredHiddenFaultProperty")
            .Step<BeginActivity>()
            .Try(body => body
                .Saga(saga => saga.Step<ThrowHiddenPropertySagaActivity>(_ => { }))
                .OnError<HiddenPropertySagaException, MessageCodeFault>(handler => handler
                    .Step<RecoverableHandlerActivity>()))
            .Catch<BaseCodeSagaException, MessageCodeFault>(handler => handler
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.PublicCode)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Trace)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition, new SagaData()));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal("base value", Data(recovered).Trace);
    }

    [Fact]
    public async Task SagaTerminationSurvivesAnEnclosingFinallyWait()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = CreateServices(repository);
        var definition = Workflow.Create<SagaData>("SagaTerminationFinallyWait")
            .Step<BeginActivity>()
            .Try(outer => outer
                .Try(inner => inner
                    .Saga(saga => saga.Step<ThrowNonRestorableSagaActivity>(_ => { }))
                    .OnError<NonRestorableSagaException, MessageFault>(handler => handler
                        .CompensateNone().ThenTerminate()))
                .Finally(cleanup => cleanup.WaitFor<Approval>("cleanup")))
            .Catch<SagaTerminatedException, MessageFault>(handler => handler
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();

        var suspended = await engine.ExecuteWorkflowAsync(definition, new SagaData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);

        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId, "cleanup", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Contains("Saga terminated", Data(resumed).Error);
    }

    [Fact]
    public void SagaTerminationExceptionCanBeRestoredFromCheckpoint()
    {
        var original = new SagaTerminatedException("policy", new InvalidOperationException("payment failed"));

        var saved = IxIFlow.Core.Runtime.SerializedException.From(original);

        Assert.True(saved.IsRestorable, saved.RestorationFailureReason);
        var restored = Assert.IsType<SagaTerminatedException>(saved.Restore());
        Assert.Equal(original.Message, restored.Message);
        Assert.Equal("payment failed", restored.OriginalException.Message);
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
    public async Task FaultProjectionFailureStillCompensatesAndReachesOuterCatch()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("SagaFaultProjectionFailure")
            .Step<BeginActivity>()
            .Try(body => body
                .Saga(saga => saga
                    .Step<ReserveActivity>(setup => setup
                        .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Reserved)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Reserved)
                        .CompensateWith<ReleaseActivity>(compensation => compensation
                            .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated)))
                    .Step<ThrowFragileSagaActivity>(_ => { }))
                .OnError<FragileSagaException, FragileSagaFault>(error => error.Compensate().ThenTerminate()))
            .Catch<InvalidOperationException, MessageFault>(handler => handler.Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(1, Data(result).Compensated);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Contains("Fault projection", Data(result).Error);
    }

    [Fact]
    public async Task RetryingFailedOutputMappingDoesNotRepeatTheCompletedActivity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<CompletedStepProbe>();
        services.AddTransient<CountedReserveActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("SagaOutputRetry")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<CountedReserveActivity>(setup => setup
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.RejectedOutput)
                .CompensateWith<ReleaseActivity>(compensation => compensation
                    .Input(activity => activity.Count).From(ctx => ctx.CurrentStep.Result)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Compensated))
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(1))))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(1, Data(result).Compensated);
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, provider.GetRequiredService<CompletedStepProbe>().Executions);
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
    public async Task StepRetryHonorsItsConfiguredDelay()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<RetryIdProbe>();
        services.AddTransient<RetryIdActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("DelayedStepRetry")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<RetryIdActivity>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(
                    new RetryPolicy(TimeSpan.FromMilliseconds(180),
                        TimeSpan.FromMilliseconds(180), 1, 1)))))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var starts = provider.GetRequiredService<RetryIdProbe>().StartedTicks;
        Assert.Equal(2, starts.Count);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(starts[0], starts[1]) >=
            TimeSpan.FromMilliseconds(130));
    }

    [Fact]
    public async Task SagaRetryHonorsItsConfiguredDelay()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SagaRetryProbe>();
        services.AddTransient<FailOnceSagaActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<SagaData>("DelayedSagaRetry")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<FailOnceSagaActivity>(_ => { }))
            .OnError<InvalidOperationException>(handler => handler.CompensateNone().ThenRetry(
                new RetryPolicy(TimeSpan.FromMilliseconds(180),
                    TimeSpan.FromMilliseconds(180), 1, 1)))
            .Build();

        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var starts = provider.GetRequiredService<SagaRetryProbe>().StartedTicks;
        Assert.Equal(2, starts.Count);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(starts[0], starts[1]) >=
            TimeSpan.FromMilliseconds(130));
    }

    [Fact]
    public async Task InterruptedRetryKeepsItsDeadlineAcrossRecovery()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowStateRepository>(repository);
        services.AddSingleton<RetryIdProbe>();
        services.AddTransient<RetryIdActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<SagaData>("InterruptedStepRetry")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<RetryIdActivity>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(
                    new RetryPolicy(TimeSpan.FromMilliseconds(700),
                        TimeSpan.FromMilliseconds(700), 1, 1)))))
            .Build();
        using var cancellation = new CancellationTokenSource();
        var running = engine.ExecuteWorkflowAsync(definition, new SagaData(),
            new WorkflowOptions { InstanceId = instanceId }, cancellation.Token);

        await WaitForRetryCheckpointAsync(repository, instanceId);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var recovered = await engine.RecoverWorkflowAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        var starts = provider.GetRequiredService<RetryIdProbe>().StartedTicks;
        Assert.Equal(2, starts.Count);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(starts[0], starts[1]) >=
            TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task CancellationInterruptsPendingRetry()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowStateRepository>(repository);
        services.AddSingleton<RetryIdProbe>();
        services.AddTransient<RetryIdActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var instanceId = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<SagaData>("CancelPendingRetry")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<RetryIdActivity>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(
                    new RetryPolicy(TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(10), 1, 1)))))
            .Build();
        var running = engine.ExecuteWorkflowAsync(definition, new SagaData(),
            new WorkflowOptions { InstanceId = instanceId });

        await WaitForRetryCheckpointAsync(repository, instanceId);
        await engine.CancelWorkflowAsync(instanceId,
            new CancellationReason { ReasonCode = "operator" });
        var stopped = await running.WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, stopped.Status);
        Assert.Single(provider.GetRequiredService<RetryIdProbe>().StartedTicks);
    }

    private static async Task WaitForRetryCheckpointAsync(IWorkflowStateRepository repository,
        string instanceId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var saved = await repository.GetWorkflowInstanceAsync(instanceId);
            if (saved != null)
            {
                using var checkpoint = JsonDocument.Parse(saved.ExecutionStateJson);
                if (checkpoint.RootElement.GetProperty("Continuations")[0]
                        .GetProperty("RetryAfterUtc").ValueKind == JsonValueKind.String)
                    return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The retry deadline was not saved");
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

    [Fact]
    public async Task SagaErrorHandler_RunsAfterCompensationAndBeforeContinuation()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("SagaErrorHandlerOrder")
            .Step<BeginActivity>()
            .Saga(saga => saga
                .Step<TraceActivity>(setup => setup
                    .Input(activity => activity.Key).From(_ => "A")
                    .Input(activity => activity.Trace).From(ctx => ctx.WorkflowData.Trace)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Trace)
                    .CompensateWith<UndoTraceActivity>(comp => comp
                        .Input(activity => activity.Key).From(_ => "B")
                        .Input(activity => activity.Trace).From(ctx => ctx.WorkflowData.Trace)
                        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Trace)))
                .Step<FailActivity>(_ => { }))
            .OnError<InvalidOperationException, MessageFault>(error => error
                .Step<HandlerMarkerActivity>()
                .Step<ReadErrorActivity>(setup => setup
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
                .Compensate().ThenContinue())
            .Step<TraceActivity>(setup => setup
                .Input(activity => activity.Key).From(_ => "D")
                .Input(activity => activity.Trace).From(ctx => ctx.WorkflowData.Trace)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Trace))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("ABHD", Data(result).Trace);
        Assert.Equal("payment failed", Data(result).Error);
    }

    [Fact]
    public async Task SagaErrorHandler_RestoresFaultAndPositionAfterWait()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = SagaErrorWaitWorkflow();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new SagaData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal("payment failed", Data(started).Error);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(SagaErrorWaitWorkflow());
        var resumed = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "review", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal("payment failed", Data(resumed).Error);
        Assert.Equal("payment failedH", Data(resumed).Trace);
    }

    [Fact]
    public async Task SagaErrorHandlerFailure_FaultsTheWorkflow()
    {
        using var services = CreateServices(new InMemoryWorkflowStateRepository());
        var definition = Workflow.Create<SagaData>("SagaHandlerFailure")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<FailActivity>(_ => { }))
            .OnError<InvalidOperationException>(error => error
                .Step<FailHandlerActivity>()
                .CompensateNone().ThenContinue())
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new SagaData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("handler failed", result.ErrorMessage);
    }

    [Fact]
    public void SagaErrorHandler_RejectsFaultPropertiesThatCannotBeCheckpointed()
    {
        Assert.Throws<NotSupportedException>(() => Workflow.Create<SagaData>("InvalidSagaFault")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<FailActivity>(_ => { }))
            .OnError<InvalidOperationException, InvalidFault>(error => error
                .CompensateNone().ThenContinue())
            .Build());
    }

    [Fact]
    public void SagaErrorHandler_RequiresAnOutcomeWhenItWaits()
    {
        Assert.Throws<InvalidOperationException>(() => Workflow.Create<SagaData>("MissingSagaOutcome")
            .Step<BeginActivity>()
            .Saga(saga => saga.Step<FailActivity>(_ => { }))
            .OnError<InvalidOperationException, MessageFault>(error => error
                .WaitFor<Approval>("review"))
            .Build());
    }

    [Fact]
    public async Task SagaErrorHandler_CanRetryAfterWaitAndRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = SagaRetryAfterWaitWorkflow();
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
            .RegisterWorkflowAsync(SagaRetryAfterWaitWorkflow());
        var resumed = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "retry", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal(2, Data(resumed).Completed);
    }

    [Fact]
    public async Task SagaErrorHandler_CanTerminateAfterWaitAndRestart()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var definition = SagaTerminateAfterWaitWorkflow();
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
            .RegisterWorkflowAsync(SagaTerminateAfterWaitWorkflow());
        var resumed = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "terminate", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, resumed.Status);
        Assert.Contains("Error policy requested termination", resumed.ErrorMessage);
    }

    private static WorkflowDefinition SagaRetryAfterWaitWorkflow() => Workflow.Create<SagaData>("SagaRetryAfterWait")
        .Step<BeginActivity>()
        .Saga(saga => saga.Step<FailOnceInDataActivity>(_ => { }))
        .OnError<InvalidOperationException, MessageFault>(error => error
            .WaitFor<Approval>("retry")
            .CompensateNone().ThenRetry(1))
        .Build();

    private static WorkflowDefinition SagaTerminateAfterWaitWorkflow() => Workflow.Create<SagaData>("SagaTerminateAfterWait")
        .Step<BeginActivity>()
        .Saga(saga => saga.Step<FailActivity>(_ => { }))
        .OnError<InvalidOperationException, MessageFault>(error => error
            .WaitFor<Approval>("terminate")
            .CompensateNone().ThenTerminate())
        .Build();

    private static WorkflowDefinition SagaErrorWaitWorkflow() => Workflow.Create<SagaData>("SagaErrorWait")
        .Step<BeginActivity>()
        .Saga(saga => saga.Step<FailActivity>(_ => { }))
        .OnError<InvalidOperationException, MessageFault>(error => error
            .Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Error))
            .WaitFor<Approval>("review")
            .Step<ReadErrorActivity>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.Trace))
            .Step<HandlerMarkerActivity>()
            .CompensateNone().ThenContinue())
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

    public sealed class FragileSagaException : Exception
    {
        public int Code => throw new InvalidOperationException("property getter failed");
    }

    public sealed class FragileSagaFault
    {
        public int Code { get; set; }
    }

    public sealed class ThrowFragileSagaActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new FragileSagaException();
    }

    public sealed class NonRestorableSagaException(string secret) : Exception("payment failed")
    {
        public string PublicCode => secret;
    }

    public sealed class MessageCodeFault
    {
        public string Message { get; set; } = "";
        public string PublicCode { get; set; } = "";
    }

    public sealed class ThrowNonRestorableSagaActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new NonRestorableSagaException("private constructor value");
    }

    public class BaseCodeSagaException(string baseCode) : Exception("hidden property failed")
    {
        public string PublicCode => baseCode;
    }

    public sealed class HiddenPropertySagaException(string baseCode, string derivedCode)
        : BaseCodeSagaException(baseCode)
    {
        public new string PublicCode => derivedCode;
    }

    public sealed class ThrowHiddenPropertySagaActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new HiddenPropertySagaException("base value", "derived value");
    }

    public sealed class CompletedStepProbe
    {
        public int Executions;
    }

    public sealed class CountedReserveActivity(CompletedStepProbe probe) : IAsyncActivity
    {
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Executions);
            Result = 1;
            return Task.CompletedTask;
        }
    }

    public sealed class PausedCompensationProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class PausedReleaseActivity(PausedCompensationProbe probe) : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; private set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Started.SetResult();
            await probe.Release.Task;
            Result = Count;
        }
    }

    public sealed class RecoverableHandlerActivity : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) => "handler";

        public Task<ActivityRecoveryResult<string>> RecoverAsync(
            string state, IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<string>>(new ActivityRecoveryResult<string>.Execute());

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class HandlerStartCrashStore : IWorkflowStateRepository
    {
        private readonly InMemoryWorkflowStateRepository _inner = new();
        public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason) =>
            _inner.RequestCancellationAsync(instanceId, reason);
        public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
            _inner.GetCancellationRequestAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowsRequiringRecoveryAsync() =>
            _inner.GetWorkflowsRequiringRecoveryAsync();
        public Task<bool> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            _inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration);
        public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            _inner.RenewExecutionLeaseAsync(instanceId, token, duration);
        public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
            _inner.ReleaseExecutionLeaseAsync(instanceId, token);
        private int _lostAcknowledgments = 3;

        public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(
            WorkflowInstance instance, long expectedRevision, string commitId)
        {
            var handlerStarted = instance.ExecutionHistory.LastOrDefault() is
            { EntryType: TraceEntryType.ActivityStarted, ActivityName: nameof(RecoverableHandlerActivity) };
            var result = await _inner.CommitWorkflowInstanceAsync(instance, expectedRevision, commitId);
            if (handlerStarted && _lostAcknowledgments-- > 0)
                throw new IOException("Handler start acknowledgment lost");
            return result;
        }

        public Task SaveWorkflowInstanceAsync(WorkflowInstance instance) =>
            _inner.SaveWorkflowInstanceAsync(instance);
        public Task<bool> TryClaimSuspendedWorkflowAsync(WorkflowInstance instance) =>
            _inner.TryClaimSuspendedWorkflowAsync(instance);
        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
            _inner.GetWorkflowInstanceAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
            _inner.GetWorkflowInstancesByNameAsync(workflowName);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
            _inner.GetWorkflowInstancesByStatusAsync(status);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
            _inner.GetWorkflowInstancesByCorrelationIdAsync(correlationId);
        public Task DeleteWorkflowInstanceAsync(string instanceId) =>
            _inner.DeleteWorkflowInstanceAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsReadyForResumptionAsync() =>
            _inner.GetSuspendedWorkflowsReadyForResumptionAsync();
    }

    public sealed class SagaRetryProbe
    {
        public int Attempts { get; set; }
        public List<long> StartedTicks { get; } = [];
    }

    public sealed class RetryIdProbe
    {
        public List<string> InvocationIds { get; } = [];
        public List<long> StartedTicks { get; } = [];
    }

    public sealed class RetryIdActivity(RetryIdProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.InvocationIds.Add(context.InvocationId);
            probe.StartedTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp());
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
            probe.StartedTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp());
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

    public sealed class HandlerMarkerActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((SagaData)context.WorkflowData).Trace += "H";
            return Task.CompletedTask;
        }
    }

    public sealed class FailHandlerActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("handler failed");
    }

    public sealed class InvalidFault
    {
        public Exception Error { get; set; } = new Exception();
    }

    public sealed class FailOnceInDataActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (++((SagaData)context.WorkflowData).Completed == 1)
                throw new InvalidOperationException("retry after approval");
            return Task.CompletedTask;
        }
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
