using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Generators;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Tests.ExecutionTests;

public class EngineContractRegressionTests
{
    [Trait("Feature", "registration")]
    [Fact]
    public void WorkflowWithoutANameIsRefusedWhenBuilt()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<RegressionData>("")
            .Step<MarkChildActivity>().Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("name is required", error.Message);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public void StructureHashIncludesOutcomeLabelsAndErrorPolicies()
    {
        WorkflowDefinition Outcome(string label) => new()
        {
            Name = "OutcomeVersion",
            WorkflowDataType = typeof(RegressionData),
            Steps =
            [
                new WorkflowStep
                {
                    Name = "Outcome",
                    StepType = WorkflowStepType.Conditional,
                    WorkflowDataType = typeof(RegressionData),
                    OutcomeSelector = _ => "fixed",
                    OutcomeBranches = [new WorkflowOutcomeBranch { Value = label }]
                }
            ]
        };

        WorkflowDefinition Policy(StepErrorAction action) => new()
        {
            Name = "SagaPolicyVersion",
            WorkflowDataType = typeof(RegressionData),
            Steps =
            [
                new WorkflowStep
                {
                    Name = "Saga step",
                    StepType = WorkflowStepType.Activity,
                    ActivityType = typeof(MarkChildActivity),
                    WorkflowDataType = typeof(RegressionData),
                    StepMetadata = new Dictionary<string, object>
                    {
                        ["StepErrorHandlers"] = new List<StepErrorHandlerInfo>
                        {
                            new() { ExceptionType = typeof(Exception), HandlerAction = action }
                        }
                    }
                }
            ]
        };

        WorkflowDefinition SagaPolicy(SagaContinuationAction action) => new()
        {
            Name = "SagaContinuationVersion",
            WorkflowDataType = typeof(RegressionData),
            Steps =
            [
                new WorkflowStep
                {
                    Name = "Saga",
                    StepType = WorkflowStepType.Saga,
                    WorkflowDataType = typeof(RegressionData),
                    CatchBlocks =
                    [
                        new WorkflowStep
                        {
                            Name = "Handler",
                            StepType = WorkflowStepType.CatchBlock,
                            WorkflowDataType = typeof(RegressionData),
                            ExceptionType = typeof(Exception),
                            FaultType = typeof(EmptyFault),
                            StepMetadata = new Dictionary<string, object>
                            {
                                ["SagaErrorConfig"] = new SagaErrorConfiguration
                                {
                                    ContinuationAction = action
                                }
                            }
                        }
                    ]
                }
            ]
        };

        Assert.NotEqual(new WorkflowScopeCatalog(Outcome("approve")).StructureHash,
            new WorkflowScopeCatalog(Outcome("reject")).StructureHash);
        Assert.NotEqual(new WorkflowScopeCatalog(Policy(StepErrorAction.Retry)).StructureHash,
            new WorkflowScopeCatalog(Policy(StepErrorAction.Terminate)).StructureHash);
        Assert.NotEqual(new WorkflowScopeCatalog(SagaPolicy(SagaContinuationAction.Retry)).StructureHash,
            new WorkflowScopeCatalog(SagaPolicy(SagaContinuationAction.Terminate)).StructureHash);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public void StructureHashChangesWhenOnlyConditionChanges()
    {
        var always = Workflow.Create<RegressionData>("ChangedCondition")
            .Step<MarkChildActivity>()
            .If(ctx => true, then => then.Step<MarkChildActivity>())
            .Build();
        var never = Workflow.Create<RegressionData>("ChangedCondition")
            .Step<MarkChildActivity>()
            .If(ctx => false, then => then.Step<MarkChildActivity>())
            .Build();

        Assert.NotEqual(new WorkflowScopeCatalog(always).StructureHash,
            new WorkflowScopeCatalog(never).StructureHash);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public void StructureHashChangesWhenInputMappingChanges()
    {
        var fromData = Workflow.Create<RegressionData>("ChangedMapping")
            .Step<MarkChildActivity>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value))
            .Build();
        var fromLiteral = Workflow.Create<RegressionData>("ChangedMapping")
            .Step<MarkChildActivity>(step => step
                .Input(activity => activity.Value).From(ctx => "fixed"))
            .Build();

        Assert.NotEqual(new WorkflowScopeCatalog(fromData).StructureHash,
            new WorkflowScopeCatalog(fromLiteral).StructureHash);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public void StructureHashIsUnchangedWhenCapturedConditionValueChanges()
    {
        WorkflowDefinition Build(bool allow) => Workflow.Create<RegressionData>("CapturedCondition")
            .Step<MarkChildActivity>()
            .If(ctx => allow, then => then.Step<MarkChildActivity>())
            .Build();

        Assert.Equal(new WorkflowScopeCatalog(Build(true)).StructureHash,
            new WorkflowScopeCatalog(Build(false)).StructureHash);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public void StructureHashIsUnchangedWhenCapturedMappingValueChanges()
    {
        WorkflowDefinition Build(string value) => Workflow.Create<RegressionData>("CapturedMapping")
            .Step<MarkChildActivity>(step => step
                .Input(activity => activity.Value).From(ctx => value))
            .Build();

        Assert.Equal(new WorkflowScopeCatalog(Build("one")).StructureHash,
            new WorkflowScopeCatalog(Build("two")).StructureHash);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public void StructureHashIsUnchangedWhenCapturedObjectStateChanges()
    {
        WorkflowDefinition Build(bool enabled)
        {
            var rule = new DecisionRule { Enabled = enabled };
            return Workflow.Create<RegressionData>("CapturedObject")
                .Step<MarkChildActivity>()
                .If(ctx => rule.Enabled,
                    then => then.Step<MarkChildActivity>())
                .Build();
        }

        Assert.Equal(new WorkflowScopeCatalog(Build(true)).StructureHash,
            new WorkflowScopeCatalog(Build(false)).StructureHash);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public void DurableConditionAcceptsAnObjectCapture()
    {
        using var source = new CancellationTokenSource();
        WorkflowDefinition Define() => Workflow.Create<RegressionData>("Captured cancellation")
            .Step<MarkChildActivity>()
            .If(ctx => source.IsCancellationRequested, then => then.Step<MarkChildActivity>()).Build();
        var before = new WorkflowScopeCatalog(Define()).StructureHash;
        source.Cancel();
        Assert.Equal(before, new WorkflowScopeCatalog(Define()).StructureHash);
    }

    private sealed class DecisionRule
    {
        public bool Enabled { get; set; }
    }

    [Trait("Feature", "registration")]
    [Fact]
    public async Task EquivalentRebuiltWorkflowCanRunTwiceInOneHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<MarkChildActivity>();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowClient>();
        WorkflowDefinition Build() => Workflow.Create<RegressionData>("RepeatedDefinition")
            .Step<MarkChildActivity>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value))
            .Build();

        var first = await engine.RunAsync(Build(), new RegressionData { Value = "one" });
        var second = await engine.RunAsync(Build(), new RegressionData { Value = "two" });

        Assert.Equal(WorkflowExecutionStatus.Success, first.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
    }

    [Trait("Feature", "tracing")]
    [Fact]
    public async Task ExecutionResultReturnsRecordedTraceEntries()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<MarkChildActivity>();
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>()
            .RunAsync(Workflow.Create<RegressionData>("TraceResult")
                .Step<MarkChildActivity>().Build(), new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Contains(result.TraceEntries, entry => entry.EntryType == TraceEntryType.ActivityStarted);
        Assert.Contains(result.TraceEntries, entry => entry.EntryType == TraceEntryType.ActivityCompleted);
    }

    [Trait("Feature", "tracing")]
    [Fact]
    public async Task DisablingTracingKeepsHistoryEmptyAcrossWaitAndResume()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<MarkChildActivity>();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowClient>();
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var definition = Workflow.Create<RegressionData>("TraceOption")
            .Step<MarkChildActivity>()
            .WaitFor<RegressionApprovalEvent>("approval")
            .Step<MarkChildActivity>()
            .Build();

        var started = await engine.RunAsync(definition, new RegressionData(),
            new WorkflowOptions { EnableTracing = false });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Empty(await repository.GetTraceAsync(started.InstanceId));

        var completed = await engine.ResumeAsync(started.InstanceId, "approval",
            new RegressionApprovalEvent());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Empty(await repository.GetTraceAsync(started.InstanceId));
    }

    [Trait("Feature", "durability")]
    [Fact]
    public async Task TerminatedInstanceReturnsTerminalStatusOnCancellation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<RegressionData>("TerminatedBeforeCancel")
            .Step<MarkChildActivity>()
            .Suspend<RegressionApprovalEvent>("termination")
            .Build();
        var instance = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new RegressionData());
        var store = provider.GetRequiredService<IWorkflowStateRepository>();
        var checkpoint = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        checkpoint.Status = WorkflowStatus.Terminated;
        var grant = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(checkpoint, grant.Epoch, checkpoint.Revision))).Status);
        Assert.Equal(WorkflowStatus.Terminated,
            (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.Status);

        var result = await provider.GetRequiredService<IWorkflowClient>()
            .CancelAsync(instance.InstanceId,
                new CancellationReason { ReasonCode = "operator" });

        Assert.Equal(WorkflowExecutionStatus.Terminated, result.Status);
    }

    // A cancellation that arrives after the instance completed changes nothing and does not report it cancelled.
    [Trait("Feature", "durability")]
    [Fact]
    public async Task CompletedInstanceStaysCompletedOnCancellation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowClient>();
        var completed = await engine.RunAsync(Workflow.Create<RegressionData>("CompletedBeforeCancel")
            .Step<MarkChildActivity>().Build(), new RegressionData());

        var result = await engine.CancelAsync(completed.InstanceId,
            new CancellationReason { ReasonCode = "too-late" });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var saved = (await provider.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(completed.InstanceId))!;
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        Assert.Null(saved.CancellationReason);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public async Task FluentBuilder_ExecutesInProcessWithoutHostRegistration()
    {
        var builder = Workflow.Create<RegressionData>("Local");
        builder.Step<MarkChildActivity>();

        var result = await builder.ExecuteAsync(new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
    }

    [Trait("Feature", "durability")]
    [Fact]
    public async Task DefaultStateRepository_PreservesAnInstanceAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();

        WorkflowExecutionResult instance;
        using (var firstScope = provider.CreateScope())
        {
            instance = await firstScope.ServiceProvider.GetRequiredService<IWorkflowClient>()
                .RunAsync(Workflow.Create<RegressionData>("AcrossScopes").Step<MarkChildActivity>().Build(),
                    new RegressionData());
        }

        using var secondScope = provider.CreateScope();
        var restored = await secondScope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(instance.InstanceId);
        Assert.NotNull(restored);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public void ConfiguredStateRepository_ReplacesTheMemoryDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options => options.UseCustomStateRepository = typeof(ConfiguredStateRepository));

        var registrations = services.Where(service => service.ServiceType == typeof(IWorkflowStateRepository)).ToList();
        Assert.Single(registrations);
        Assert.Equal(typeof(ConfiguredStateRepository), registrations[0].ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, registrations[0].Lifetime);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.NotNull(provider.GetRequiredService<IHostedService>());
    }

    [Trait("Feature", "durability")]
    [Fact]
    public async Task InProcessWorkflow_ResumesFromAnotherScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();

        var definition = Workflow.Create<RegressionData>("LocalResume")
            .Step<MarkChildActivity>()
            .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
            .Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.ChildExecuted))
            .Build();

        string instanceId;
        using (var firstScope = provider.CreateScope())
        {
            var engine = firstScope.ServiceProvider.GetRequiredService<IWorkflowClient>();
            var suspended = await engine.RunAsync(definition, new RegressionData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
            instanceId = suspended.InstanceId;
        }

        using var secondScope = provider.CreateScope();
        var resumed = await secondScope.ServiceProvider.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.True(Assert.IsType<RegressionData>(resumed.WorkflowData).ChildExecuted);
    }

    [Trait("Feature", "durability")]
    [Fact]
    public async Task ConcurrentResume_WaitsForTheFirstAndDoesNotRepeatTheContinuation()
    {
        var gate = new ResumeActivityGate();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(gate);
        services.AddTransient<BlockingResumeActivity>();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("ConcurrentResume")
            .Step<MarkChildActivity>()
            .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
            .Step<BlockingResumeActivity>()
            .Build();
        var firstEngine = firstScope.ServiceProvider.GetRequiredService<IWorkflowClient>();
        var suspended = await firstEngine.RunAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);

        var firstResume = firstEngine.ResumeAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The first event answered the wait, so the second finds no open wait when it arrives
            // and is refused at once, while the first run is still going (D-33).
            var secondResume = await secondScope.ServiceProvider.GetRequiredService<IWorkflowClient>()
                .ResumeAsync(suspended.InstanceId, new RegressionApprovalEvent { Approved = true });
            EventAssert.Rejected(secondResume, EventRejection.NoOpenWait);
            gate.Release.TrySetResult();
            Assert.Equal(1, gate.ExecutionCount);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        Assert.Equal(WorkflowExecutionStatus.Success, (await firstResume).Status);
        Assert.Equal(1, gate.ExecutionCount);
    }

    [Trait("Feature", "durability")]
    [Fact]
    public Task MemoryStore_AppliesOneDeliveryOnceAcrossHosts()
    {
        var repository = new InMemoryWorkflowStateRepository();
        return ResumeOnceScenario.RunAsync(repository, repository, "resume-once-memory");
    }

    [Trait("Feature", "try")]
    [Fact]
    public async Task ExceptionFromPreviousStep_CannotBeCaughtByLaterTryBlock()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("ExceptionScope")
            .Step<ThrowRegressionActivity>()
            .Try(tryBlock => tryBlock.Step<MarkChildActivity>())
            .Catch<Exception>(catchBlock => catchBlock.Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.ChildExecuted)))
            .Build();

        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.False(Assert.IsType<RegressionData>(result.WorkflowData).ChildExecuted);
    }

    [Trait("Feature", "try")]
    [Fact]
    public async Task LoopBodyFailure_ReachesOuterCatchWithoutRepeatingTheBody()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("LoopBodyFailure")
            .Step<MarkChildActivity>()
            .Try(tryBlock => tryBlock.WhileDo(_ => true,
                body => body.Step<ThrowRegressionActivity>()))
            .Catch<ApplicationException>(catchBlock => catchBlock
                .Step<MarkChildActivity>(setup => setup
                    .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.CatchExecuted)))
            .Build();

        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<RegressionData>(result.WorkflowData).CatchExecuted);
    }

    [Trait("Feature", "control-flow")]
    [Fact]
    public async Task NestedConditional_PreservesPreviousResultInsideSequence()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("SequencePreviousResult")
            .Step<MarkChildActivity>(setup => setup
                .Input(activity => activity.Value).From(_ => "outer"))
            .Sequence(sequence => sequence
                .If(_ => true, then => then.Step<MarkChildActivity>(setup => setup
                    .Input(activity => activity.Value).From(_ => "inner")))
                .Step<MarkChildActivity>(setup => setup
                    .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Value)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Result)))
            .Build();

        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("outer", Assert.IsType<RegressionData>(result.WorkflowData).Result);
    }

    [Trait("Feature", "control-flow")]
    [Fact]
    public async Task NestedConditional_PreservesPreviousResultInsideParallelBranch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("ParallelPreviousResult")
            .Step<MarkChildActivity>(setup => setup
                .Input(activity => activity.Value).From(_ => "outer"))
            .Parallel(parallel => parallel.Do(branch => branch
                .If(_ => true, then => then.Step<MarkChildActivity>(setup => setup
                    .Input(activity => activity.Value).From(_ => "inner")))
                .Step<MarkChildActivity>(setup => setup
                    .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Value)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Result))))
            .Build();

        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("outer", Assert.IsType<RegressionData>(result.WorkflowData).Result);
    }

    [Trait("Feature", "waits")]
    [Fact]
    public async Task ResumeInsideLoop_ReevaluatesLoopAndSuspendsOnNextIteration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<LoopResumeRegressionData>("LoopResume")
            .Step<MarkChildActivity>()
            .WhileDo(ctx => ctx.WorkflowData.Count < 2, body => body
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                .Step<IncrementRegressionActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count)))
            .Build();

        var started = await engine.RunAsync(definition, new LoopResumeRegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var resumed = await engine.ResumeAsync(started.InstanceId,
            new RegressionApprovalEvent { Approved = true });

        Assert.True(resumed.Status == WorkflowExecutionStatus.Suspended, $"Expected another suspension, got {resumed.Status}: {resumed.ErrorMessage}");
        Assert.Equal(1, Assert.IsType<LoopResumeRegressionData>(resumed.WorkflowData).Count);

        var completed = await engine.ResumeAsync(started.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(2, Assert.IsType<LoopResumeRegressionData>(completed.WorkflowData).Count);
    }

    [Trait("Feature", "try")]
    [Fact]
    public async Task ResumeInsideTry_HandlesLaterFailureAndRunsFinallyOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<RegressionData>("TryResumeFailure")
            .Step<MarkChildActivity>()
            .Try(tryBlock => tryBlock
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                .Step<ThrowRegressionActivity>())
            .Catch<ApplicationException>(catchBlock => catchBlock
                .Step<MarkChildActivity>(setup => setup
                    .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.CatchExecuted)))
            .Finally(finallyBlock => finallyBlock
                .Step<MarkChildActivity>(setup => setup
                    .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.AfterTryExecuted))
            .Build();

        var suspended = await engine.RunAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var resumed = await engine.ResumeAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        var data = Assert.IsType<RegressionData>(resumed.WorkflowData);
        Assert.True(data.CatchExecuted);
        Assert.True(data.FinallyExecuted);
        Assert.True(data.AfterTryExecuted);
    }

    [Trait("Feature", "waits")]
    [Fact]
    public async Task ResumeInsideConditional_RestoresValueBeforeTheBranch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<RegressionData>("ConditionalEntryResume")
            .Step<MarkChildActivity>(setup => setup
                .Input(activity => activity.Value).From(_ => "entry"))
            .If(ctx => true, then => then
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved))
            .Step<MarkChildActivity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Value)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Result))
            .Build();

        var suspended = await engine.RunAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var resumed = await engine.ResumeAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal("entry", Assert.IsType<RegressionData>(resumed.WorkflowData).Result);
    }

    [Trait("Feature", "try")]
    [Fact]
    public async Task ResumeInsideCatch_DoesNotReplayHandledStepsAndRunsFinally()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<RegressionData>("CatchWait")
            .Step<MarkChildActivity>()
            .Try(tryBlock => tryBlock.Step<ThrowRegressionActivity>())
            .Catch<ApplicationException>(catchBlock => catchBlock
                .Step<IncrementRegressionActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.CatchBeforeWaitCount)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.CatchBeforeWaitCount))
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                .Step<MarkChildActivity>(setup => setup
                    .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.CatchExecuted)))
            .Finally(finallyBlock => finallyBlock.Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Build();

        var suspended = await engine.RunAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var suspendedData = Assert.IsType<RegressionData>(suspended.WorkflowData);
        Assert.Equal(1, suspendedData.CatchBeforeWaitCount);
        Assert.False(suspendedData.FinallyExecuted);

        var resumed = await engine.ResumeAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.True(resumed.Status == WorkflowExecutionStatus.Success,
            $"Expected catch continuation to succeed, got {resumed.Status}: {resumed.ErrorMessage}");
        var data = Assert.IsType<RegressionData>(resumed.WorkflowData);
        Assert.Equal(1, data.CatchBeforeWaitCount);
        Assert.True(data.CatchExecuted);
        Assert.True(data.FinallyExecuted);
    }

    [Trait("Feature", "try")]
    [Fact]
    public async Task ResumeInsideFinally_DoesNotReplayCleanup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<RegressionData>("FinallyWait")
            .Step<MarkChildActivity>()
            .Try(tryBlock => tryBlock.Step<MarkChildActivity>())
            .Finally(finallyBlock => finallyBlock
                .Step<IncrementRegressionActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.FinallyBeforeWaitCount)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.FinallyBeforeWaitCount))
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                .Step<MarkChildActivity>(setup => setup
                    .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.AfterTryExecuted))
            .Build();

        var suspended = await engine.RunAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var suspendedData = Assert.IsType<RegressionData>(suspended.WorkflowData);
        Assert.Equal(1, suspendedData.FinallyBeforeWaitCount);
        Assert.False(suspendedData.AfterTryExecuted);

        var resumed = await engine.ResumeAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.True(resumed.Status == WorkflowExecutionStatus.Success,
            $"Expected finally continuation to succeed, got {resumed.Status}: {resumed.ErrorMessage}");
        var data = Assert.IsType<RegressionData>(resumed.WorkflowData);
        Assert.Equal(1, data.FinallyBeforeWaitCount);
        Assert.True(data.FinallyExecuted);
        Assert.True(data.AfterTryExecuted);
    }

    [Trait("Feature", "waits")]
    [Fact]
    public async Task ResumeInsideParallel_DoesNotReplayCompletedBranch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<RegressionData>("ParallelWait")
            .Step<MarkChildActivity>()
            .Parallel(parallel =>
            {
                parallel.Do(branch => branch.Step<IncrementRegressionActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.ParallelCompletedBranchCount)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.ParallelCompletedBranchCount)));
                parallel.Do(branch => branch
                    .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                    .Step<MarkChildActivity>(setup => setup
                        .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.ParallelWaitBranchCompleted))
                    .Suspend<RegressionApprovalEvent>("second approval", (evt, _) => evt.Approved));
            })
            .Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.AfterTryExecuted))
            .Build();

        var suspended = await engine.RunAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        // While the parallel step is open, the branches' work is read from their saved changes (D-14).
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();
        var suspendedData = await BranchProgress.ReadAsync<RegressionData>(repository, suspended.InstanceId, 0);
        Assert.Equal(1, suspendedData.ParallelCompletedBranchCount);
        Assert.False(suspendedData.AfterTryExecuted);

        var resumed = await engine.ResumeAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.True(resumed.Status == WorkflowExecutionStatus.Suspended,
            $"Expected a second parallel wait, got {resumed.Status}: {resumed.ErrorMessage}");
        var data = await BranchProgress.ReadAsync<RegressionData>(repository, resumed.InstanceId, 0);
        Assert.Equal(1, data.ParallelCompletedBranchCount);
        Assert.True((await BranchProgress.ReadAsync<RegressionData>(repository, resumed.InstanceId, 1))
            .ParallelWaitBranchCompleted);
        Assert.False(data.AfterTryExecuted);

        var completed = await engine.ResumeAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.True(completed.Status == WorkflowExecutionStatus.Success,
            $"Expected parallel continuation to succeed, got {completed.Status}: {completed.ErrorMessage}");
        data = Assert.IsType<RegressionData>(completed.WorkflowData);
        Assert.Equal(1, data.ParallelCompletedBranchCount);
        Assert.True(data.AfterTryExecuted);
    }

    [Trait("Feature", "waits")]
    [Fact]
    public async Task ParallelWithTwoWaitingBranches_ResumesEachBranch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("ParallelTwoWaits")
            .Step<MarkChildActivity>()
            .Parallel(parallel =>
            {
                parallel.Do(branch => branch.Suspend<RegressionApprovalEvent>("first", (evt, _) => evt.Approved));
                parallel.Do(branch => branch.Suspend<RegressionApprovalEvent>("second", (evt, _) => evt.Approved));
            })
            .Build();

        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.RunAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.NotNull(await repository.GetWorkflowInstanceAsync(started.InstanceId));
        Assert.Equal(2, (await engine.GetSnapshotAsync(started.InstanceId))!.Pointers
            .Count(pointer => pointer.Status == "Waiting"));

        var first = await engine.ResumeAsync(started.InstanceId, "first",
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);

        var second = await engine.ResumeAsync(started.InstanceId, "second",
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
    }

    [Trait("Feature", "registration")]
    [Fact]
    public async Task PublishedWorkflowVersion_CannotBeSilentlyReplaced()
    {
        var registry = new WorkflowVersionRegistry();
        var original = new WorkflowDefinition { Name = "Order", Version = 1, Description = "published" };
        await registry.RegisterWorkflowAsync(original);

        var reworded = new WorkflowDefinition { Name = "Order", Version = 1, Description = "reworded" };
        await registry.RegisterWorkflowAsync(reworded);
        Assert.Same(original, await registry.GetWorkflowDefinitionAsync("Order", 1));
        Assert.Equal("published", original.Description);
        var replacement = Workflow.Create<RegressionData>("Order", 1).Step<MarkChildActivity>().Build();
        await EngineAssert.FailsAsync(WorkflowErrorKind.InvalidRequest,
            () => registry.RegisterWorkflowAsync(replacement));

        Assert.Same(original, await registry.GetWorkflowDefinitionAsync("Order", 1));
    }

    [Trait("Feature", "documents")]
    [Fact]
    public async Task GeneratedYaml_PreservesConditionalSteps()
    {
        var document = Document(new ConditionalStepDocument
        {
            Id = "decision",
            Condition = new ConstantExpressionDocument { Value = true },
            Then = [new ActivityStepDocument { Id = "yes", Activity = "Mark" }]
        });

        var definition = await YamlTestCompilation.BuildAsync(document, ResolvedContracts());
        Assert.Single(definition.Steps);
        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[0].StepType);
        Assert.Single(definition.Steps[0].ThenSteps);
    }

    [Trait("Feature", "documents")]
    [Fact]
    public async Task GeneratedYaml_RejectsExternalStepReferences()
    {
        var document = Document(new StepReferenceStepDocument { Id = "external", Ref = "SharedSequence" });
        await Assert.ThrowsAsync<IxIFlow.Generators.WorkflowSourceException>(() => YamlTestCompilation.BuildAsync(document, ResolvedContracts()));
    }

    [Trait("Feature", "documents")]
    [Fact]
    public async Task GeneratedYaml_BindsWorkflowInputAndOutputPaths()
    {
        var document = Document(new ActivityStepDocument
        {
            Id = "mapped",
            Activity = "Mark",
            Input = [new InputMappingDocument
            {
                Target = "Value",
                From = new PathExpressionDocument { Path = "workflow.Value" }
            }],
            Output = [new OutputMappingDocument
            {
                Source = "Result",
                To = new PathExpressionDocument { Path = "workflow.Result" }
            }]
        });

        var step = Assert.Single((await YamlTestCompilation.BuildAsync(document, ResolvedContracts(typeof(YamlMappingActivity)))).Steps);
        var data = new RegressionData { Value = "input" };
        var context = new WorkflowContext<RegressionData> { WorkflowData = data };

        Assert.Equal("input", Assert.Single(step.InputMappings).SourceFunction(context));
        Assert.Single(step.OutputMappings).TargetAssignmentFunction!(context, "output");
        Assert.Equal("output", data.Result);
    }

    [Trait("Feature", "documents")]
    [Fact]
    public async Task GeneratedYaml_EvaluatesBinaryInputExpressions()
    {
        var document = Document(new ActivityStepDocument
        {
            Id = "expression",
            Activity = "Mark",
            Input = [new InputMappingDocument
            {
                Target = "Flag",
                From = new BinaryExpressionDocument
                {
                    Operator = "eq",
                    Left = new ConstantExpressionDocument { Value = 2 },
                    Right = new ConstantExpressionDocument { Value = 2 }
                }
            }]
        });

        var step = Assert.Single((await YamlTestCompilation.BuildAsync(document, ResolvedContracts(typeof(YamlMappingActivity)))).Steps);
        Assert.Equal(true, Assert.Single(step.InputMappings).SourceFunction(new WorkflowContext<RegressionData>
        {
            WorkflowData = new RegressionData()
        }));
    }

    [Trait("Feature", "documents")]
    [Fact]
    public async Task DocumentValidator_RejectsUnknownFunctionsInsteadOfCompilingNull()
    {
        var document = Document(new ActivityStepDocument
        {
            Id = "function",
            Activity = "Mark",
            Input = [new InputMappingDocument
            {
                Target = "Value",
                From = new FunctionExpressionDocument { Function = "unregisteredFunction" }
            }]
        });

        var result = await YamlTestContracts.ValidateAsync(document, ResolvedContracts());
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, item => item.Code == "EXPR003" && item.Path == "definitions[0].input[0].from.function");
    }

    [Trait("Feature", "documents")]
    [Fact]
    public async Task GeneratedYaml_EnforcesSuspendResumeCondition()
    {
        var document = Document(new SuspendStepDocument
        {
            Id = "approval",
            Event = "Approval",
            ResumeCondition = new PathExpressionDocument { Path = "event.Approved" }
        });

        var step = Assert.Single((await YamlTestCompilation.BuildAsync(document, ResolvedContracts())).Steps);
        Assert.NotNull(step.CompiledCondition);
        Assert.False(step.CompiledCondition(new ResumeEventContext<RegressionData, RegressionApprovalEvent>
        {
            WorkflowData = new RegressionData(), ResumeEvent = new RegressionApprovalEvent { Approved = false }
        }));
        Assert.True(step.CompiledCondition(new ResumeEventContext<RegressionData, RegressionApprovalEvent>
        {
            WorkflowData = new RegressionData(), ResumeEvent = new RegressionApprovalEvent { Approved = true }
        }));
    }

    [Trait("Feature", "documents")]
    [Fact]
    public async Task GeneratedYaml_KeepsChildDataSeparateFromWorkflowType()
    {
        var document = Document(new InvokeWorkflowStepDocument
        {
            Id = "child",
            Workflow = new WorkflowReferenceDocument { Name = "Child", Version = 1 }
        });

        var step = Assert.Single((await YamlTestCompilation.BuildAsync(document, ResolvedContracts())).Steps);
        Assert.Null(step.WorkflowType);
        Assert.Equal("Child", step.WorkflowName);
    }

    [Trait("Feature", "children")]
    [Fact]
    public async Task NamedWorkflowInvocation_ExecutesTheRegisteredChildGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IWorkflowVersionRegistry>();
        var child = Workflow.Create<ChildData>("Child")
            .Step<MarkChildActivity>(setup => setup
                .Input(a => a.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(a => a.Executed).To(ctx => ctx.WorkflowData.Executed))
            .Build();
        await registry.RegisterWorkflowAsync(child);

        var parent = Workflow.Create<RegressionData>("Parent")
            .Step<MarkChildActivity>(setup => setup.Input(a => a.Value).From(ctx => ctx.WorkflowData.Value))
            .Invoke<ChildData>("Child", 1, setup => setup
                .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(data => data.Executed).To(ctx => ctx.WorkflowData.ChildExecuted))
            .Build();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowClient>()
            .RunToEndAsync(parent, new RegressionData { Value = "input" });

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.True(((RegressionData)result.WorkflowData!).ChildExecuted);
    }

    [Trait("Feature", "children")]
    [Fact]
    public async Task NamedWorkflowInvocation_WaitsForAnUnknownVersion()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();

        var parent = Workflow.Create<RegressionData>("MissingChildParent")
            .Step<MarkChildActivity>()
            .Invoke<ChildData>("MissingChild", 1, setup => setup
                .Output(data => data.Executed).To(ctx => ctx.WorkflowData.ChildExecuted))
            .Build();
        var error = await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable,
            () => engine.RunAsync(parent, new RegressionData()));
        Assert.Contains("Workflow definition not found: MissingChild v1", error.Message);
        var saved = Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "MissingChildParent", Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
        Assert.NotEqual(WorkflowStatus.Failed, saved.Status);

        await scope.ServiceProvider.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("MissingChild")
                .Step<MarkChildActivity>(setup => setup
                    .Output(a => a.Executed).To(ctx => ctx.WorkflowData.Executed))
                .Build());
        var result = await engine.FinishedAsync(await engine.RecoverAsync(saved.InstanceId));

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.True(((RegressionData)result.WorkflowData!).ChildExecuted);
    }

    private static WorkflowDocument Document(WorkflowStepDocument step) => new()
    {
        Workflow = new WorkflowDefinitionDocument { Name = "Regression", Version = 1, DataType = "RegressionData" },
        Definitions = [step]
    };

    private static WorkflowSourceOptions ResolvedContracts(Type? activityType = null)
    {
        var options = YamlTestContracts.Create(
            ("RegressionData", typeof(RegressionData)),
            ("Mark", activityType ?? typeof(MarkChildActivity)),
            ("Approval", typeof(RegressionApprovalEvent)));
        options.Workflows["Child@1"] = new(YamlTestContracts.Name(typeof(ChildData)), 1);
        return options;
    }
}

public sealed class ConfiguredStateRepository : InMemoryWorkflowStateRepository
{
}

public sealed class RegressionData
{
    public string Value { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public bool ChildExecuted { get; set; }
    public bool CatchExecuted { get; set; }
    public bool FinallyExecuted { get; set; }
    public bool AfterTryExecuted { get; set; }
    public int CatchBeforeWaitCount { get; set; }
    public int FinallyBeforeWaitCount { get; set; }
    public int ParallelCompletedBranchCount { get; set; }
    public bool ParallelWaitBranchCompleted { get; set; }
}

public sealed class ChildData
{
    public string Value { get; set; } = string.Empty;
    public bool Executed { get; set; }
}

public sealed class RegressionApprovalEvent
{
    public bool Approved { get; set; }
}

public sealed class MarkChildActivity : IAsyncActivity
{
    public string Value { get; set; } = string.Empty;
    public bool Executed { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Executed = true;
        return Task.CompletedTask;
    }
}

public sealed class ThrowRegressionActivity : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new ApplicationException("failure before try");
    }
}

public sealed class LoopResumeRegressionData
{
    public int Count { get; set; }
}

public sealed class IncrementRegressionActivity : IAsyncActivity
{
    public int Count { get; set; }
    public int Result { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Result = Count + 1;
        return Task.CompletedTask;
    }
}

public sealed class ResumeActivityGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ExecutionCount;
}

public sealed class BlockingResumeActivity(ResumeActivityGate gate) : IAsyncActivity
{
    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref gate.ExecutionCount);
        gate.Entered.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
    }
}
