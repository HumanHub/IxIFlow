using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Tests.ExecutionTests;

public class EngineContractRegressionTests
{
    [Fact]
    public async Task UnnamedDefinitionsHaveDistinctStableRecoveryNames()
    {
        var repository = new InMemoryWorkflowStateRepository();
        WorkflowDefinition Immediate() => Workflow.Create<RegressionData>()
            .Step<MarkChildActivity>(_ => { }).Build();
        WorkflowDefinition Waiting() => Workflow.Create<RegressionData>()
            .Step<MarkChildActivity>()
            .WaitFor<RegressionApprovalEvent>("approval").Build();

        string waitingId;
        string waitingName;
        using (var firstProvider = new ServiceCollection().AddLogging().AddIxIFlow()
                   .AddSingleton<IWorkflowStateRepository>(repository).BuildServiceProvider())
        {
            var engine = firstProvider.GetRequiredService<IWorkflowEngine>();
            var first = await engine.ExecuteWorkflowAsync(Immediate(), new RegressionData());
            var waiting = await engine.ExecuteWorkflowAsync(Waiting(), new RegressionData());
            Assert.Equal(WorkflowExecutionStatus.Success, first.Status);
            Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
            var savedFirst = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;
            var savedWaiting = (await repository.GetWorkflowInstanceAsync(waiting.InstanceId))!;
            Assert.False(string.IsNullOrWhiteSpace(savedFirst.WorkflowName));
            Assert.NotEqual(savedFirst.WorkflowName, savedWaiting.WorkflowName);
            waitingId = waiting.InstanceId;
            waitingName = savedWaiting.WorkflowName;
        }

        using var secondProvider = new ServiceCollection().AddLogging().AddIxIFlow()
            .AddSingleton<IWorkflowStateRepository>(repository).BuildServiceProvider();
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(Waiting());
        var recovered = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(waitingId, "approval", new RegressionApprovalEvent());
        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal(waitingName, (await repository.GetWorkflowInstanceAsync(waitingId))!.WorkflowName);
    }

    [Fact]
    public void DefinitionFingerprintIncludesOutcomeLabelsAndErrorPolicies()
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

        Assert.NotEqual(new WorkflowScopeCatalog(Outcome("approve")).Fingerprint,
            new WorkflowScopeCatalog(Outcome("reject")).Fingerprint);
        Assert.NotEqual(new WorkflowScopeCatalog(Policy(StepErrorAction.Retry)).Fingerprint,
            new WorkflowScopeCatalog(Policy(StepErrorAction.Terminate)).Fingerprint);
        Assert.NotEqual(new WorkflowScopeCatalog(SagaPolicy(SagaContinuationAction.Retry)).Fingerprint,
            new WorkflowScopeCatalog(SagaPolicy(SagaContinuationAction.Terminate)).Fingerprint);
    }

    [Fact]
    public async Task UnsupportedExecutionOptionsFailBeforeAnInstanceStarts()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddIxIFlow()
            .BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<RegressionData>("OptionsMustBeHonest")
            .Step<MarkChildActivity>(_ => { }).Build();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.ExecuteWorkflowAsync(definition, new RegressionData(),
                new WorkflowOptions { EnableDebugging = true }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.ExecuteWorkflowAsync(definition, new RegressionData(),
                new WorkflowOptions { WorkflowRetryPolicy = new RetryPolicy(2) }));
        Assert.Empty(await provider.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstancesByNameAsync(definition.Name));
    }

    [Fact]
    public void DefinitionFingerprintChangesWhenOnlyConditionChanges()
    {
        var always = Workflow.Create<RegressionData>("ChangedCondition")
            .Step<MarkChildActivity>()
            .If(ctx => true, then => then.Step<MarkChildActivity>(_ => { }))
            .Build();
        var never = Workflow.Create<RegressionData>("ChangedCondition")
            .Step<MarkChildActivity>()
            .If(ctx => false, then => then.Step<MarkChildActivity>(_ => { }))
            .Build();

        Assert.NotEqual(new WorkflowScopeCatalog(always).Fingerprint,
            new WorkflowScopeCatalog(never).Fingerprint);
    }

    [Fact]
    public void DefinitionFingerprintChangesWhenInputMappingChanges()
    {
        var fromData = Workflow.Create<RegressionData>("ChangedMapping")
            .Step<MarkChildActivity>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value))
            .Build();
        var fromLiteral = Workflow.Create<RegressionData>("ChangedMapping")
            .Step<MarkChildActivity>(step => step
                .Input(activity => activity.Value).From(ctx => "fixed"))
            .Build();

        Assert.NotEqual(new WorkflowScopeCatalog(fromData).Fingerprint,
            new WorkflowScopeCatalog(fromLiteral).Fingerprint);
    }

    [Fact]
    public void DefinitionFingerprintChangesWhenCapturedConditionValueChanges()
    {
        WorkflowDefinition Build(bool allow) => Workflow.Create<RegressionData>("CapturedCondition")
            .Step<MarkChildActivity>()
            .If(ctx => allow, then => then.Step<MarkChildActivity>(_ => { }))
            .Build();

        Assert.NotEqual(new WorkflowScopeCatalog(Build(true)).Fingerprint,
            new WorkflowScopeCatalog(Build(false)).Fingerprint);
    }

    [Fact]
    public void DefinitionFingerprintChangesWhenCapturedMappingValueChanges()
    {
        WorkflowDefinition Build(string value) => Workflow.Create<RegressionData>("CapturedMapping")
            .Step<MarkChildActivity>(step => step
                .Input(activity => activity.Value).From(ctx => value))
            .Build();

        Assert.NotEqual(new WorkflowScopeCatalog(Build("one")).Fingerprint,
            new WorkflowScopeCatalog(Build("two")).Fingerprint);
    }

    [Fact]
    public void DefinitionFingerprintChangesWhenCapturedObjectStateChanges()
    {
        WorkflowDefinition Build(bool enabled)
        {
            var rule = new DecisionRule { Enabled = enabled };
            return Workflow.Create<RegressionData>("CapturedObject")
                .Step<MarkChildActivity>()
                .If(ctx => rule.Enabled,
                    then => then.Step<MarkChildActivity>(_ => { }))
                .Build();
        }

        Assert.NotEqual(new WorkflowScopeCatalog(Build(true)).Fingerprint,
            new WorkflowScopeCatalog(Build(false)).Fingerprint);
    }

    [Fact]
    public void DurableConditionRejectsAnUnfingerprintableCapture()
    {
        using var source = new CancellationTokenSource();

        var error = Assert.Throws<NotSupportedException>(() =>
            Workflow.Create<RegressionData>("UnsafeCapture")
                .Step<MarkChildActivity>()
                .If(ctx => source.IsCancellationRequested,
                    then => then.Step<MarkChildActivity>(_ => { })));

        Assert.Contains("cannot be fingerprinted", error.Message);
    }

    private sealed class DecisionRule
    {
        public bool Enabled { get; set; }
    }

    [Fact]
    public async Task EquivalentRebuiltWorkflowCanRunTwiceInOneHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<MarkChildActivity>();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        WorkflowDefinition Build() => Workflow.Create<RegressionData>("RepeatedDefinition")
            .Step<MarkChildActivity>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value))
            .Build();

        var first = await engine.ExecuteWorkflowAsync(Build(), new RegressionData { Value = "one" });
        var second = await engine.ExecuteWorkflowAsync(Build(), new RegressionData { Value = "two" });

        Assert.Equal(WorkflowExecutionStatus.Success, first.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
    }

    [Fact]
    public async Task ExecutionResultReturnsRecordedTraceEntries()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<MarkChildActivity>();
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(Workflow.Create<RegressionData>("TraceResult")
                .Step<MarkChildActivity>(_ => { }).Build(), new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Contains(result.TraceEntries, entry => entry.EntryType == TraceEntryType.ActivityStarted);
        Assert.Contains(result.TraceEntries, entry => entry.EntryType == TraceEntryType.ActivityCompleted);
    }

    [Fact]
    public async Task DisablingTracingKeepsHistoryEmptyAcrossWaitAndResume()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<MarkChildActivity>();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var definition = Workflow.Create<RegressionData>("TraceOption")
            .Step<MarkChildActivity>(_ => { })
            .WaitFor<RegressionApprovalEvent>("approval")
            .Step<MarkChildActivity>(_ => { })
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new RegressionData(),
            new WorkflowOptions { EnableTracing = false });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Empty((await repository.GetWorkflowInstanceAsync(started.InstanceId))!.ExecutionHistory);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval",
            new RegressionApprovalEvent());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Empty((await repository.GetWorkflowInstanceAsync(started.InstanceId))!.ExecutionHistory);
    }

    [Fact]
    public async Task TerminatedInstanceReturnsTerminalStatusOnCancellation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            Status = WorkflowStatus.Terminated
        };
        await provider.GetRequiredService<IWorkflowStateRepository>()
            .SaveWorkflowInstanceAsync(instance);

        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .CancelWorkflowAsync(instance.InstanceId,
                new CancellationReason { ReasonCode = "operator" });

        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
    }

    [Fact]
    public async Task FluentBuilder_ExecutesInProcessWithoutHostRegistration()
    {
        var builder = Workflow.Create<RegressionData>("Local");
        builder.Step<MarkChildActivity>(_ => { });

        var result = await builder.ExecuteAsync(new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
    }

    [Fact]
    public async Task DefaultStateRepository_PreservesAnInstanceAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();

        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString() };
        using (var firstScope = provider.CreateScope())
        {
            await firstScope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>()
                .SaveWorkflowInstanceAsync(instance);
        }

        using var secondScope = provider.CreateScope();
        var restored = await secondScope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(instance.InstanceId);
        Assert.NotNull(restored);
    }

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

    [Fact]
    public async Task DefaultEventStore_PreservesEventsAcrossScopes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();

        using (var firstScope = provider.CreateScope())
        {
            await firstScope.ServiceProvider.GetRequiredService<IEventStore>()
                .AppendEventAsync("instance-1", new WorkflowEvent { EventType = "Started" });
        }

        using var secondScope = provider.CreateScope();
        var events = await secondScope.ServiceProvider.GetRequiredService<IEventStore>()
            .GetEventsAsync("instance-1");
        Assert.Single(events);
    }

    [Fact]
    public async Task InProcessWorkflow_ResumesFromAnotherScope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();

        var definition = Workflow.Create<RegressionData>("LocalResume")
            .Step<MarkChildActivity>(_ => { })
            .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
            .Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.ChildExecuted))
            .Build();

        string instanceId;
        using (var firstScope = provider.CreateScope())
        {
            var engine = firstScope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            var suspended = await engine.ExecuteWorkflowAsync(definition, new RegressionData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
            instanceId = suspended.InstanceId;
        }

        using var secondScope = provider.CreateScope();
        var resumed = await secondScope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.True(Assert.IsType<RegressionData>(resumed.WorkflowData).ChildExecuted);
    }

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
            .Step<MarkChildActivity>(_ => { })
            .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
            .Step<BlockingResumeActivity>(_ => { })
            .Build();
        var firstEngine = firstScope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        var suspended = await firstEngine.ExecuteWorkflowAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);

        var firstResume = firstEngine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondResumeTask = secondScope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
                .ResumeWorkflowAsync(suspended.InstanceId, new RegressionApprovalEvent { Approved = true });
            Assert.False(secondResumeTask.IsCompleted);
            gate.Release.TrySetResult();
            var secondResume = await secondResumeTask;
            Assert.Equal(WorkflowExecutionStatus.Faulted, secondResume.Status);
            Assert.Contains("not waiting", secondResume.ErrorMessage);
            Assert.Equal(1, gate.ExecutionCount);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        Assert.Equal(WorkflowExecutionStatus.Success, (await firstResume).Status);
        Assert.Equal(1, gate.ExecutionCount);
    }

    [Fact]
    public async Task MemoryStateRepository_ClaimsEachSuspensionOnlyOnce()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var firstWait = new SuspensionInfo { SuspendReason = "approval" };
        await repository.SaveWorkflowInstanceAsync(new WorkflowInstance
        {
            InstanceId = "instance-1",
            Status = WorkflowStatus.Suspended,
            SuspensionInfo = firstWait
        });

        WorkflowInstance Claim(SuspensionInfo wait) => new()
        {
            InstanceId = "instance-1",
            Status = WorkflowStatus.Running,
            SuspensionInfo = wait
        };

        var claims = await Task.WhenAll(
            repository.TryClaimSuspendedWorkflowAsync(Claim(firstWait)),
            repository.TryClaimSuspendedWorkflowAsync(Claim(firstWait)));
        Assert.Single(claims, claimed => claimed);

        var secondWait = new SuspensionInfo { SuspendReason = "approval" };
        await repository.SaveWorkflowInstanceAsync(new WorkflowInstance
        {
            InstanceId = "instance-1",
            Status = WorkflowStatus.Suspended,
            SuspensionInfo = secondWait
        });
        Assert.False(await repository.TryClaimSuspendedWorkflowAsync(Claim(firstWait)));
        Assert.True(await repository.TryClaimSuspendedWorkflowAsync(Claim(secondWait)));
    }

    [Fact]
    public async Task ExceptionFromPreviousStep_CannotBeCaughtByLaterTryBlock()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("ExceptionScope")
            .Step<ThrowRegressionActivity>(_ => { })
            .Try(tryBlock => tryBlock.Step<MarkChildActivity>(_ => { }))
            .Catch<Exception>(catchBlock => catchBlock.Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.ChildExecuted)))
            .Build();

        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.False(Assert.IsType<RegressionData>(result.WorkflowData).ChildExecuted);
    }

    [Fact]
    public async Task LoopBodyFailure_ReachesOuterCatchWithoutRepeatingTheBody()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("LoopBodyFailure")
            .Step<MarkChildActivity>(_ => { })
            .Try(tryBlock => tryBlock.WhileDo(_ => true,
                body => body.Step<ThrowRegressionActivity>(_ => { })))
            .Catch<ApplicationException>(catchBlock => catchBlock
                .Step<MarkChildActivity>(setup => setup
                    .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.CatchExecuted)))
            .Build();

        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<RegressionData>(result.WorkflowData).CatchExecuted);
    }

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

        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("outer", Assert.IsType<RegressionData>(result.WorkflowData).Result);
    }

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

        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("outer", Assert.IsType<RegressionData>(result.WorkflowData).Result);
    }

    [Fact]
    public async Task ResumeInsideLoop_ReevaluatesLoopAndSuspendsOnNextIteration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        var definition = Workflow.Create<LoopResumeRegressionData>("LoopResume")
            .Step<MarkChildActivity>(_ => { })
            .WhileDo(ctx => ctx.WorkflowData.Count < 2, body => body
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                .Step<IncrementRegressionActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count)))
            .Build();

        var started = await engine.ExecuteWorkflowAsync(definition, new LoopResumeRegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var resumed = await engine.ResumeWorkflowAsync(started.InstanceId,
            new RegressionApprovalEvent { Approved = true });

        Assert.True(resumed.Status == WorkflowExecutionStatus.Suspended, $"Expected another suspension, got {resumed.Status}: {resumed.ErrorMessage}");
        Assert.Equal(1, Assert.IsType<LoopResumeRegressionData>(resumed.WorkflowData).Count);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(2, Assert.IsType<LoopResumeRegressionData>(completed.WorkflowData).Count);
    }

    [Fact]
    public async Task ResumeInsideTry_HandlesLaterFailureAndRunsFinallyOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        var definition = Workflow.Create<RegressionData>("TryResumeFailure")
            .Step<MarkChildActivity>(_ => { })
            .Try(tryBlock => tryBlock
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved)
                .Step<ThrowRegressionActivity>(_ => { }))
            .Catch<ApplicationException>(catchBlock => catchBlock
                .Step<MarkChildActivity>(setup => setup
                    .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.CatchExecuted)))
            .Finally(finallyBlock => finallyBlock
                .Step<MarkChildActivity>(setup => setup
                    .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Step<MarkChildActivity>(setup => setup
                .Output(activity => activity.Executed).To(ctx => ctx.WorkflowData.AfterTryExecuted))
            .Build();

        var suspended = await engine.ExecuteWorkflowAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        var data = Assert.IsType<RegressionData>(resumed.WorkflowData);
        Assert.True(data.CatchExecuted);
        Assert.True(data.FinallyExecuted);
        Assert.True(data.AfterTryExecuted);
    }

    [Fact]
    public async Task ResumeInsideConditional_RestoresValueBeforeTheBranch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        var definition = Workflow.Create<RegressionData>("ConditionalEntryResume")
            .Step<MarkChildActivity>(setup => setup
                .Input(activity => activity.Value).From(_ => "entry"))
            .If(ctx => true, then => then
                .Suspend<RegressionApprovalEvent>("approval", (evt, _) => evt.Approved))
            .Step<MarkChildActivity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Value)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Result))
            .Build();

        var suspended = await engine.ExecuteWorkflowAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal("entry", Assert.IsType<RegressionData>(resumed.WorkflowData).Result);
    }

    [Fact]
    public async Task ResumeInsideCatch_DoesNotReplayHandledStepsAndRunsFinally()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        var definition = Workflow.Create<RegressionData>("CatchWait")
            .Step<MarkChildActivity>(_ => { })
            .Try(tryBlock => tryBlock.Step<ThrowRegressionActivity>(_ => { }))
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

        var suspended = await engine.ExecuteWorkflowAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var suspendedData = Assert.IsType<RegressionData>(suspended.WorkflowData);
        Assert.Equal(1, suspendedData.CatchBeforeWaitCount);
        Assert.False(suspendedData.FinallyExecuted);

        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.True(resumed.Status == WorkflowExecutionStatus.Success,
            $"Expected catch continuation to succeed, got {resumed.Status}: {resumed.ErrorMessage}");
        var data = Assert.IsType<RegressionData>(resumed.WorkflowData);
        Assert.Equal(1, data.CatchBeforeWaitCount);
        Assert.True(data.CatchExecuted);
        Assert.True(data.FinallyExecuted);
    }

    [Fact]
    public async Task ResumeInsideFinally_DoesNotReplayCleanup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        var definition = Workflow.Create<RegressionData>("FinallyWait")
            .Step<MarkChildActivity>(_ => { })
            .Try(tryBlock => tryBlock.Step<MarkChildActivity>(_ => { }))
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

        var suspended = await engine.ExecuteWorkflowAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var suspendedData = Assert.IsType<RegressionData>(suspended.WorkflowData);
        Assert.Equal(1, suspendedData.FinallyBeforeWaitCount);
        Assert.False(suspendedData.AfterTryExecuted);

        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.True(resumed.Status == WorkflowExecutionStatus.Success,
            $"Expected finally continuation to succeed, got {resumed.Status}: {resumed.ErrorMessage}");
        var data = Assert.IsType<RegressionData>(resumed.WorkflowData);
        Assert.Equal(1, data.FinallyBeforeWaitCount);
        Assert.True(data.FinallyExecuted);
        Assert.True(data.AfterTryExecuted);
    }

    [Fact]
    public async Task ResumeInsideParallel_DoesNotReplayCompletedBranch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        var definition = Workflow.Create<RegressionData>("ParallelWait")
            .Step<MarkChildActivity>(_ => { })
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

        var suspended = await engine.ExecuteWorkflowAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var suspendedData = Assert.IsType<RegressionData>(suspended.WorkflowData);
        Assert.Equal(1, suspendedData.ParallelCompletedBranchCount);
        Assert.False(suspendedData.AfterTryExecuted);

        var resumed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.True(resumed.Status == WorkflowExecutionStatus.Suspended,
            $"Expected a second parallel wait, got {resumed.Status}: {resumed.ErrorMessage}");
        var data = Assert.IsType<RegressionData>(resumed.WorkflowData);
        Assert.Equal(1, data.ParallelCompletedBranchCount);
        Assert.True(data.ParallelWaitBranchCompleted);
        Assert.False(data.AfterTryExecuted);

        var completed = await engine.ResumeWorkflowAsync(suspended.InstanceId,
            new RegressionApprovalEvent { Approved = true });
        Assert.True(completed.Status == WorkflowExecutionStatus.Success,
            $"Expected parallel continuation to succeed, got {completed.Status}: {completed.ErrorMessage}");
        data = Assert.IsType<RegressionData>(completed.WorkflowData);
        Assert.Equal(1, data.ParallelCompletedBranchCount);
        Assert.True(data.AfterTryExecuted);
    }

    [Fact]
    public async Task ParallelWithTwoWaitingBranches_ResumesEachBranch()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var definition = Workflow.Create<RegressionData>("ParallelTwoWaits")
            .Step<MarkChildActivity>(_ => { })
            .Parallel(parallel =>
            {
                parallel.Do(branch => branch.Suspend<RegressionApprovalEvent>("first", (evt, _) => evt.Approved));
                parallel.Do(branch => branch.Suspend<RegressionApprovalEvent>("second", (evt, _) => evt.Approved));
            })
            .Build();

        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.ExecuteWorkflowAsync(definition, new RegressionData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(2, saved!.ExecutionSnapshot!.Pointers.Count(pointer => pointer.Status == "Waiting"));

        var first = await engine.ResumeWorkflowAsync(started.InstanceId, "first",
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);

        var second = await engine.ResumeWorkflowAsync(started.InstanceId, "second",
            new RegressionApprovalEvent { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
    }

    [Fact]
    public void SqlHostRegistration_UsesSharedWorkflowState()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlowHost(_ => { }, "Server=unused;Database=unused;");

        var registration = services.Last(x => x.ServiceType == typeof(IWorkflowStateRepository));
        Assert.NotEqual(typeof(InMemoryWorkflowStateRepository), registration.ImplementationType);
    }

    [Fact]
    public void DistributedHostRegistration_ResolvesWithScopeValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlowHost(_ => { });
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IHostRegistry, InMemoryHostRegistry>();
        services.AddSingleton<IMessageBus, InMemoryMessageBus>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.NotNull(provider.GetRequiredService<IWorkflowHost>());
        Assert.Contains(provider.GetServices<IHostedService>(), service =>
            service is WorkflowRecoveryService);
    }

    [Fact]
    public void DistributedExecutionCommand_HasSerializableWorkflowReference()
    {
        var command = new ExecuteWorkflowCommand
        {
            InstanceId = "instance-1",
            WorkflowName = "Distributed",
            WorkflowVersion = 1,
            WorkflowDataJson = "{}",
            WorkflowDataType = typeof(RegressionData).AssemblyQualifiedName!
        };

        var json = JsonSerializer.Serialize(command);
        var restored = JsonSerializer.Deserialize<ExecuteWorkflowCommand>(json);
        Assert.NotNull(restored);
        Assert.Equal("Distributed", restored.WorkflowName);
        Assert.Equal(1, restored.WorkflowVersion);
        Assert.DoesNotContain("Definition", json);
    }

    [Fact]
    public async Task PublishedWorkflowVersion_CannotBeSilentlyReplaced()
    {
        var registry = new WorkflowVersionRegistry();
        var original = new WorkflowDefinition { Name = "Order", Version = 1, Description = "published" };
        await registry.RegisterWorkflowAsync(original);

        var replacement = new WorkflowDefinition { Name = "Order", Version = 1, Description = "different graph" };
        try
        {
            await registry.RegisterWorkflowAsync(replacement);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.Same(original, await registry.GetWorkflowDefinitionAsync("Order", 1));
    }

    [Fact]
    public async Task DocumentCompiler_PreservesConditionalSteps()
    {
        var document = Document(new ConditionalStepDocument
        {
            Id = "decision",
            Condition = new ConstantExpressionDocument { Value = JsonSerializer.SerializeToElement(true) },
            Then = [new ActivityStepDocument { Id = "yes", Activity = "Mark" }]
        });

        var definition = await Compiler().CompileAsync(document, Registry());
        Assert.Single(definition.Steps);
        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[0].StepType);
        Assert.Single(definition.Steps[0].ThenSteps);
    }

    [Fact]
    public async Task DocumentCompiler_NeverSilentlyDropsExternalStepReferences()
    {
        var document = Document(new StepReferenceStepDocument { Id = "external", Ref = "SharedSequence" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Compiler().CompileAsync(document, Registry()));
    }

    [Fact]
    public async Task DocumentCompiler_BindsWorkflowInputAndOutputPaths()
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

        var step = Assert.Single((await Compiler().CompileAsync(document, Registry())).Steps);
        var data = new RegressionData { Value = "input" };
        var context = new WorkflowContext<RegressionData> { WorkflowData = data };

        Assert.Equal("input", Assert.Single(step.InputMappings).SourceFunction(context));
        Assert.Single(step.OutputMappings).TargetAssignmentFunction!(context, "output");
        Assert.Equal("output", data.Result);
    }

    [Fact]
    public async Task DocumentCompiler_EvaluatesBinaryInputExpressions()
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
                    Left = new ConstantExpressionDocument { Value = JsonSerializer.SerializeToElement(2) },
                    Right = new ConstantExpressionDocument { Value = JsonSerializer.SerializeToElement(2) }
                }
            }]
        });

        var step = Assert.Single((await Compiler().CompileAsync(document, Registry())).Steps);
        Assert.Equal(true, Assert.Single(step.InputMappings).SourceFunction(new WorkflowContext<RegressionData>
        {
            WorkflowData = new RegressionData()
        }));
    }

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

        var result = await new WorkflowDocumentValidator().ValidateAsync(document, Registry());
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task DocumentCompiler_EnforcesSuspendResumeCondition()
    {
        var document = Document(new SuspendStepDocument
        {
            Id = "approval",
            Event = "Approval",
            ResumeCondition = new PathExpressionDocument { Path = "event.Approved" }
        });

        var step = Assert.Single((await Compiler().CompileAsync(document, Registry())).Steps);
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

    [Fact]
    public async Task DocumentCompiler_DoesNotTreatChildDataTypeAsWorkflowClass()
    {
        var document = Document(new InvokeWorkflowStepDocument
        {
            Id = "child",
            Workflow = new WorkflowReferenceDocument { Name = "Child", Version = 1 }
        });

        var step = Assert.Single((await Compiler().CompileAsync(document, Registry())).Steps);
        Assert.Null(step.WorkflowType);
        Assert.Equal("Child", step.WorkflowName);
    }

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
        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(parent, new RegressionData { Value = "input" });

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(((RegressionData)result.WorkflowData!).ChildExecuted);
    }

    [Fact]
    public async Task NamedWorkflowInvocation_RejectsAnUnknownVersion()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var parent = Workflow.Create<RegressionData>("MissingChildParent")
            .Step<MarkChildActivity>(_ => { })
            .Invoke<ChildData>("MissingChild", 1, _ => { })
            .Build();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(parent, new RegressionData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("Workflow definition not found: MissingChild v1", result.ErrorMessage);
    }

    private static WorkflowDocument Document(WorkflowStepDocument step) => new()
    {
        Workflow = new WorkflowDefinitionDocument { Name = "Regression", Version = 1, DataType = "RegressionData" },
        Definitions = [step]
    };

    private static WorkflowDocumentCompiler Compiler() => new(new WorkflowDocumentValidator());

    private static WorkflowValidationContext Registry()
    {
        var registry = new RegressionRegistry();
        return new WorkflowValidationContext(registry, registry, registry, registry, registry);
    }

    private sealed class RegressionRegistry : IActivityRegistry, IWorkflowCatalog, IEventRegistry,
        IDataTypeRegistry, IStepTemplateRegistry
    {
        ValueTask<ActivityDescriptor?> IActivityRegistry.FindAsync(string activityKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ActivityDescriptor?>(activityKey == "Mark"
                ? new ActivityDescriptor { Key = activityKey, ActivityType = typeof(MarkChildActivity) }
                : null);

        ValueTask<WorkflowDescriptor?> IWorkflowCatalog.FindAsync(string workflowName, int? version, CancellationToken cancellationToken) =>
            ValueTask.FromResult<WorkflowDescriptor?>(workflowName == "Child"
                ? new WorkflowDescriptor
                {
                    Name = "Child", Version = 1,
                    RuntimeDefinition = new WorkflowDefinition { Name = "Child", Version = 1, WorkflowDataType = typeof(ChildData) }
                }
                : null);

        ValueTask<EventDescriptor?> IEventRegistry.FindAsync(string eventKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult<EventDescriptor?>(eventKey == "Approval"
                ? new EventDescriptor { Key = eventKey, EventType = typeof(RegressionApprovalEvent) }
                : null);

        ValueTask<DataTypeDescriptor?> IDataTypeRegistry.FindAsync(string typeKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DataTypeDescriptor?>(typeKey == "RegressionData"
                ? new DataTypeDescriptor { Key = typeKey, ClrType = typeof(RegressionData) }
                : null);

        ValueTask<StepTemplateDescriptor?> IStepTemplateRegistry.FindAsync(string templateKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult<StepTemplateDescriptor?>(templateKey == "SharedSequence"
                ? new StepTemplateDescriptor { Key = templateKey }
                : null);
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
