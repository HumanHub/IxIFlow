using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
[Trait("Spec", "A-09")]
public sealed class ActivityRecoveryTests
{
    [Fact]
    public async Task InitialCheckpointFailureExposesTheRepositoryError()
    {
        var store = new FaultStore { FailNextCommits = 3 };
        using var services = CreateServices(store, new ChargeLedger(store));

        var error = await EngineAssert.StoreFailureAsync(() =>
            services.GetRequiredService<IWorkflowClient>().RunAsync(
                ChargeWorkflow(), new ChargeData { OrderId = "initial-checkpoint" }));

        Assert.Equal("Checkpoint unavailable", error.InnerException!.Message);
    }

    [Fact]
    public async Task ResumeCheckpointFailureExposesTheRepositoryError()
    {
        var store = new FaultStore();
        using var services = CreateServices(store, new ChargeLedger(store));
        var definition = Workflow.Create<ChargeData>("ResumeCheckpointFailure")
            .Step<NoopActivity>()
            .WaitFor<RecoveryReply>("reply")
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new ChargeData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        store.FailNextCommits = 3;
        var error = await EngineAssert.StoreFailureAsync(() =>
            engine.ResumeAsync(started.InstanceId, "reply", new RecoveryReply()));

        Assert.Equal("Checkpoint unavailable", error.InnerException!.Message);
    }

    [Fact]
    public async Task FailureCheckpointFailureExposesTheRepositoryError()
    {
        var store = new FaultStore { FailFailureCommits = 3 };
        using var services = CreateServices(store, new ChargeLedger(store));
        var definition = Workflow.Create<ChargeData>("FailureCheckpointFailure")
            .Step<NoopActivity>()
            .Step<ThrowActivity>()
            .Build();

        var error = await EngineAssert.StoreFailureAsync(() =>
            services.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new ChargeData()));

        Assert.Equal("Checkpoint unavailable", error.InnerException!.Message);
    }

    [Fact]
    public async Task InvalidActivityStateFaultsInsteadOfLeavingARunningInstance()
    {
        var store = new FaultStore();
        using var services = CreateServices(store, new ChargeLedger(store));
        var definition = Workflow.Create<CheckpointData>("InvalidActivityState")
            .Step<NoopActivity>()
            .Step<InvalidActivityStateActivity>()
            .WaitFor<RecoveryReply>("reply")
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new CheckpointData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("checkpoint", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var saved = await store.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.Equal(WorkflowStatus.Failed, saved?.Status);
    }

    [Fact]
    public async Task InvalidWorkflowDataFaultsAndKeepsTheLastSavedData()
    {
        var store = new FaultStore();
        using var services = CreateServices(store, new ChargeLedger(store));
        var definition = Workflow.Create<CheckpointData>("InvalidWorkflowData")
            .Step<NoopActivity>()
            .Step<InvalidWorkflowDataActivity>()
            .WaitFor<RecoveryReply>("reply")
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new CheckpointData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("checkpoint", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var saved = await store.GetWorkflowInstanceAsync(result.InstanceId);
        Assert.Equal(WorkflowStatus.Failed, saved?.Status);
        Assert.Null(JsonSerializer.Deserialize<CheckpointData>(saved!.WorkflowDataJson)?.Payload);
    }

    [Fact]
    public async Task StartIsCommittedBeforeTheActivityCallsAnExternalSystem()
    {
        var store = new FaultStore();
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var data = new ChargeData { OrderId = "order-1" };

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(ChargeWorkflow(), data);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(ledger.SawCommittedStart);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.NotNull(Assert.IsType<ChargeData>(result.WorkflowData).ChargeId);
    }

    [Fact]
    public async Task CrashAfterEffectBeforeEndUsesRecoveryWithoutAnotherCharge()
    {
        var store = new FaultStore { FailCompletionBeforeApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();

        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            ChargeWorkflow(), new ChargeData { OrderId = "order-2" }));

        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        Assert.Equal(1, ledger.ChargeCount);
        var trace = await TraceAsync(store, saved.InstanceId);
        Assert.Equal(1, trace.Count(entry => entry.EntryType == TraceEntryType.ActivityStarted));
        Assert.DoesNotContain(trace, entry => entry.EntryType == TraceEntryType.ActivityCompleted);

        var recovered = await engine.RecoverAsync(saved.InstanceId);

        Assert.True(recovered.Status == WorkflowExecutionStatus.Success, recovered.ErrorMessage);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.Equal(1, ledger.RecoveryCount);
        Assert.NotNull(((ChargeData)recovered.WorkflowData!).ChargeId);
    }

    [Fact]
    public async Task StartWithoutEndOfOrdinaryActivityNeedsResolution()
    {
        var store = new FaultStore { FailCompletionBeforeApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();

        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            OrdinaryChargeWorkflow(), new ChargeData { OrderId = "order-3" }));
        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await store.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(saved.InstanceId));
        Assert.Equal(nameof(OrdinaryChargeActivity), pending.ActivityName);
        Assert.Contains("ChargeId", pending.RequiredOutputProperties);
        Assert.False(string.IsNullOrWhiteSpace(pending.InvocationId));
    }

    [Fact]
    [Trait("Spec", "D-01")]
    public async Task OperatorCanCompleteUnknownActivityWithObservedOutput()
    {
        var store = new FaultStore { FailCompletionBeforeApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            OrdinaryChargeWorkflow(), new ChargeData { OrderId = "operator-complete" }));
        var running = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        var unresolved = await engine.RecoverAsync(running.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, unresolved.Status);
        var invocationId = (await TraceAsync(store, running.InstanceId)).Last(entry =>
            entry.EntryType == TraceEntryType.ActivityStarted).Metadata["InvocationId"].ToString()!;

        var resolved = await engine.ResolveAsync(running.InstanceId, invocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>
            {
                ["ChargeId"] = "observed-charge"
            }, "Checked the provider receipt", "operator-1"));

        Assert.Equal(WorkflowExecutionStatus.Success, resolved.Status);
        Assert.Equal("observed-charge", ((ChargeData)resolved.WorkflowData!).ChargeId);
        Assert.Equal(1, ledger.ChargeCount);
        var history = await TraceAsync(store, running.InstanceId);
        var decision = history.Last(entry => entry.EntryType == TraceEntryType.ActivityStarted);
        Assert.Equal("OperatorCompleted", decision.Metadata["Kind"].ToString());
        Assert.Equal("operator-1", Assert.IsType<ActivityResolutionTrace>(history.Single(entry =>
            entry.EntryType == TraceEntryType.ActivityResolved).Payload).DecidedBy);
        Assert.Equal("Checked the provider receipt", decision.Metadata["DecisionNote"].ToString());
    }

    [Fact]
    public async Task OperatorCanFailUnknownActivityWithoutRepeatingExternalEffect()
    {
        var store = new FaultStore { FailCompletionBeforeApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            OrdinaryChargeWorkflow(), new ChargeData { OrderId = "operator-fail" }));
        var running = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution,
            (await engine.RecoverAsync(running.InstanceId)).Status);
        var invocationId = (await TraceAsync(store, running.InstanceId)).Last(entry =>
            entry.EntryType == TraceEntryType.ActivityStarted).Metadata["InvocationId"].ToString()!;

        var resolved = await engine.ResolveAsync(running.InstanceId, invocationId,
            ActivityResolution.Failed("Operator confirmed the operation failed"));

        Assert.Equal(WorkflowExecutionStatus.Faulted, resolved.Status);
        Assert.Equal(WorkflowStatus.Failed,
            (await store.GetWorkflowInstanceAsync(running.InstanceId))!.Status);
        Assert.Equal(1, ledger.ChargeCount);
    }

    [Fact]
    public async Task OperatorTypedFailureEntersMatchingCatchWithFaultProperties()
    {
        var store = new FaultStore();
        var ledger = new ChargeLedger(store) { FailCompletionAfterCharge = true };
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ChargeData>("OperatorTypedFailure")
            .Step<NoopActivity>()
            .Try(body => body.Step<OrdinaryChargeActivity>(step => step
                .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(activity => activity.ChargeId).To(ctx => ctx.WorkflowData.ChargeId)))
            .Catch<OperatorFaultException, OperatorFaultData>(handler => handler
                .Step<HandleOperatorFaultActivity>(step => step
                    .Input(activity => activity.Code).From(ctx => ctx.Fault.Code)
                    .Output(activity => activity.Code).To(ctx => ctx.WorkflowData.FaultCode)))
            .Build();
        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            definition, new ChargeData { OrderId = "typed-failure" }));
        var running = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        await engine.RecoverAsync(running.InstanceId);
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(running.InstanceId));
        var savedCheckpoint = ExecutionCheckpoint.Read(
            (await store.GetWorkflowInstanceAsync(running.InstanceId))!.ExecutionStateJson);
        Assert.Contains(savedCheckpoint.Continuations.SelectMany(item => item.Stack),
            frame => frame.TryState != null);

        var resolved = await engine.ResolveAsync(running.InstanceId, pending.InvocationId,
            ActivityResolution.Failed(new OperatorFaultException(41)));

        Assert.True(resolved.Status == WorkflowExecutionStatus.Success, resolved.ErrorMessage);
        Assert.Equal(41, ((ChargeData)resolved.WorkflowData!).FaultCode);
        Assert.Equal(1, ledger.ChargeCount);
    }

    [Fact]
    public async Task InterruptedCatchActivityNeedsResolutionBeforeFinally()
    {
        var store = new FaultStore();
        var ledger = new ChargeLedger(store) { FailCompletionAfterCharge = true };
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ChargeData>("InterruptedCatch")
            .Step<NoopActivity>()
            .Try(body => body.Step<ThrowActivity>())
            .Catch<InvalidOperationException, MessageFault>(body => body
                .Step<OrdinaryChargeActivity>(step => step
                    .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(activity => activity.ChargeId).To(ctx => ctx.WorkflowData.ChargeId)))
            .Finally(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();

        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            definition, new ChargeData { OrderId = "catch-effect" }));
        var running = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        var unresolved = await engine.RecoverAsync(running.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, unresolved.Status);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.False(Assert.IsType<ChargeData>(unresolved.WorkflowData).FinallyRan);
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(running.InstanceId));
        Assert.Equal(nameof(OrdinaryChargeActivity), pending.ActivityName);

        var completed = await engine.ResolveAsync(running.InstanceId,
            pending.InvocationId, ActivityResolution.Completed(
                new Dictionary<string, object?> { ["ChargeId"] = "observed-charge" }));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.True(Assert.IsType<ChargeData>(completed.WorkflowData).FinallyRan);
        Assert.Equal("observed-charge", ((ChargeData)completed.WorkflowData!).ChargeId);
        Assert.Equal(1, ledger.ChargeCount);
    }

    [Fact]
    public async Task InterruptedCompensationNeedsResolutionBeforeCatchAndFinally()
    {
        var store = new FaultStore();
        var ledger = new ChargeLedger(store) { FailCompletionAfterCharge = true };
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ChargeData>("InterruptedCompensation")
            .Step<NoopActivity>()
            .Try(body => body.Saga(saga => saga
                .Step<NoopActivity>(step => step
                    .CompensateWith<OrdinaryChargeActivity>(comp => comp
                        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(activity => activity.ChargeId).To(ctx => ctx.WorkflowData.ChargeId)))
                .Step<ThrowActivity>()))
            .Catch<InvalidOperationException, MessageFault>(body => body
                .Step<MarkCaughtActivity>(step => step
                    .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.Caught)))
            .Finally(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();

        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            definition, new ChargeData { OrderId = "compensation-effect" }));
        var running = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        var unresolved = await engine.RecoverAsync(running.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, unresolved.Status);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.False(Assert.IsType<ChargeData>(unresolved.WorkflowData).Caught);
        Assert.False(((ChargeData)unresolved.WorkflowData!).FinallyRan);
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(running.InstanceId));
        Assert.Equal(nameof(OrdinaryChargeActivity), pending.ActivityName);

        var completed = await engine.ResolveAsync(running.InstanceId,
            pending.InvocationId, ActivityResolution.Completed(
                new Dictionary<string, object?> { ["ChargeId"] = "observed-compensation" }));

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.True(Assert.IsType<ChargeData>(completed.WorkflowData).Caught);
        Assert.True(((ChargeData)completed.WorkflowData!).FinallyRan);
        Assert.Equal("observed-compensation", ((ChargeData)completed.WorkflowData!).ChargeId);
        Assert.Equal(1, ledger.ChargeCount);
    }

    [Fact]
    public async Task OperatorResolutionRequiresMappedOutputBeforeChangingCheckpoint()
    {
        var store = new FaultStore { FailCompletionBeforeApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            OrdinaryChargeWorkflow(), new ChargeData { OrderId = "missing-output" }));
        var running = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        await engine.RecoverAsync(running.InstanceId);
        var invocationId = (await TraceAsync(store, running.InstanceId)).Last(entry =>
            entry.EntryType == TraceEntryType.ActivityStarted).Metadata["InvocationId"].ToString()!;

        await Assert.ThrowsAsync<ArgumentException>(() => engine.ResolveAsync(
            running.InstanceId, invocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>())));

        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await store.GetWorkflowInstanceAsync(running.InstanceId))!.Status);
        Assert.Equal(1, ledger.ChargeCount);
    }

    [Fact]
    public async Task SavedOperatorDecisionSurvivesCompletionCheckpointFailure()
    {
        var store = new FaultStore { FailCompletionBeforeApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            OrdinaryChargeWorkflow(), new ChargeData { OrderId = "resolution-crash" }));
        var running = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        await engine.RecoverAsync(running.InstanceId);
        var invocationId = (await TraceAsync(store, running.InstanceId)).Last(entry =>
            entry.EntryType == TraceEntryType.ActivityStarted).Metadata["InvocationId"].ToString()!;
        store.FailCompletionBeforeApply = 3;

        await EngineAssert.StoreFailureAsync(() => engine.ResolveAsync(
            running.InstanceId, invocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>
            {
                ["ChargeId"] = "observed-after-crash"
            })));
        var afterCrash = (await store.GetWorkflowInstanceAsync(running.InstanceId))!;
        Assert.Equal(WorkflowStatus.Running, afterCrash.Status);
        Assert.Equal(1, ledger.ChargeCount);

        var recovered = await engine.RecoverAsync(running.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal("observed-after-crash", ((ChargeData)recovered.WorkflowData!).ChargeId);
        Assert.Equal(1, ledger.ChargeCount);
    }

    [Fact]
    public async Task OperatorResolutionHonorsCancellationRequestedBeforeHostStopped()
    {
        var store = new FaultStore { FailCompletionBeforeApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            OrdinaryChargeWorkflow(), new ChargeData { OrderId = "cancel-before-resolution" },
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMinutes(1) }));
        var running = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        await engine.RecoverAsync(running.InstanceId);
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(running.InstanceId));
        Assert.True(await store.RequestCancellationAsync(running.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn" }));
        var awaitingResolution = await engine.RecoverAsync(running.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, awaitingResolution.Status);
        Assert.Null((await store.GetWorkflowInstanceAsync(running.InstanceId))!.RunnableAtUtc);

        var resolved = await engine.ResolveAsync(running.InstanceId, pending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>
            {
                ["ChargeId"] = "observed-charge"
            }));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, resolved.Status);
        Assert.Equal(WorkflowStatus.Cancelled,
            (await store.GetWorkflowInstanceAsync(running.InstanceId))!.Status);
        Assert.Equal(1, ledger.ChargeCount);
    }

    [Fact]
    public async Task CommittedStartWithoutDispatchCanExecuteAfterRecovery()
    {
        var store = new FaultStore { FailStartAfterApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();

        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(
            ChargeWorkflow(), new ChargeData { OrderId = "order-4" }));
        Assert.Equal(0, ledger.ChargeCount);
        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverAsync(saved.InstanceId);

        Assert.True(recovered.Status == WorkflowExecutionStatus.Success, recovered.ErrorMessage);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.Equal(1, ledger.RecoveryCount);
    }

    [Fact]
    public async Task LostCompletionAcknowledgmentDoesNotRepeatTheActivity()
    {
        var store = new FaultStore { FailCompletionAfterApply = 1 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(ChargeWorkflow(), new ChargeData { OrderId = "order-5" });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.Equal(0, ledger.RecoveryCount);
    }

    [Fact]
    public async Task ARecoveredParentWaitsForTheChildItStartedAndStartsNoOther()
    {
        // The commit that starts the child is applied, but its acknowledgement is lost every time.
        var store = new FaultStore { FailChildStartAfterApply = 3 };
        using var services = CreateServices(store, new ChargeLedger(store));
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ChargeData>("InterruptedChildInvocation")
            .Step<WorkflowInvocationTests.PrepareDataActivity>(_ => { })
            .Invoke<WorkflowInvocationTests.ChildWorkflow, WorkflowInvocationTests.ChildWorkflowData>(step => step
                .Input(child => child.InputData).From(ctx => ctx.WorkflowData.OrderId)
                .Output(child => child.ProcessedResult).To(ctx => ctx.WorkflowData.ChildResult))
            .Build();

        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(definition,
            new ChargeData { OrderId = "order-6" }));
        var parent = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Suspended));

        // The parent is saved waiting for the child its commit started: there is nothing to recover in it,
        // and asking starts no other child.
        await engine.RecoverAsync(parent.InstanceId);
        Assert.Equal(WorkflowStatus.Suspended, (await store.GetWorkflowInstanceAsync(parent.InstanceId))!.Status);
        var child = Assert.Single(await ChildrenOfAsync(store, parent));
        await engine.RecoverAsync(child.InstanceId);
        var finished = await engine.AwaitAsync(parent.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.NotEmpty(((ChargeData)finished.WorkflowData!).ChildResult);
        Assert.Single(await ChildrenOfAsync(store, parent));
    }

    [Fact]
    public async Task OperatorCanCompleteAChildInvocationRefusedAtTheLimit()
    {
        var store = new FaultStore();
        using var services = CreateServices(store, new ChargeLedger(store), maxCheckpointBytes: 8_000);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ChargeData>("ResolveChildInvocation")
            .Step<WorkflowInvocationTests.PrepareDataActivity>(_ => { })
            .Invoke<WorkflowInvocationTests.ChildWorkflow, WorkflowInvocationTests.ChildWorkflowData>(step => step
                .Input(child => child.InputData).From(_ => new string('x', 20_000))
                .Output(child => child.ProcessedResult).To(ctx => ctx.WorkflowData.ChildResult))
            .Build();
        var stopped = await engine.RunAsync(definition, new ChargeData { OrderId = "child-1" });
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(stopped.InstanceId));
        Assert.Contains("ProcessedResult", pending.RequiredOutputProperties);

        var resolved = await engine.ResolveAsync(stopped.InstanceId, pending.InvocationId,
            ActivityResolution.CompletedInvocation(new WorkflowInvocationTests.ChildWorkflowData
            {
                ProcessedResult = "observed-child-result"
            }));

        Assert.Equal(WorkflowExecutionStatus.Success, resolved.Status);
        Assert.Equal("observed-child-result", ((ChargeData)resolved.WorkflowData!).ChildResult);
        Assert.Empty(await ChildrenOfAsync(store, (await store.GetWorkflowInstanceAsync(stopped.InstanceId))!));
    }

    [Fact]
    public async Task OperatorCanFailAChildInvocationRefusedAtTheLimitIntoCatch()
    {
        var store = new FaultStore();
        using var services = CreateServices(store, new ChargeLedger(store), maxCheckpointBytes: 8_000);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ChargeData>("FailChildInvocation")
            .Step<NoopActivity>()
            .Try(body => body
                .Invoke<WorkflowInvocationTests.ChildWorkflow, WorkflowInvocationTests.ChildWorkflowData>(step => step
                    .Input(child => child.InputData).From(_ => new string('x', 20_000))))
            .Catch<OperatorFaultException, OperatorFaultData>(handler => handler
                .Step<HandleOperatorFaultActivity>(step => step
                    .Input(activity => activity.Code).From(ctx => ctx.Fault.Code)
                    .Output(activity => activity.Code).To(ctx => ctx.WorkflowData.FaultCode)))
            .Build();
        var stopped = await engine.RunAsync(definition, new ChargeData { OrderId = "child-failed" });
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(stopped.InstanceId));

        var resolved = await engine.ResolveAsync(stopped.InstanceId, pending.InvocationId,
            ActivityResolution.Failed(new OperatorFaultException(42)));

        Assert.Equal(WorkflowExecutionStatus.Success, resolved.Status);
        Assert.Equal(42, ((ChargeData)resolved.WorkflowData!).FaultCode);
    }

    private static async Task<WorkflowInstance[]> ChildrenOfAsync(FaultStore store, WorkflowInstance parent) =>
        (await store.GetWorkflowInstancesByCorrelationIdAsync(parent.CorrelationId))
        .Where(instance => instance.ParentInstanceId == parent.InstanceId).ToArray();

    [Fact]
    public async Task WaitAnyCannotDiscardAnUnresolvedLosingBranch()
    {
        var store = new FaultStore();
        var ledger = new ChargeLedger(store) { FailCompletionAfterCharge = true };
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ChargeData>("UnresolvedParallelCharge")
            .Step<WorkflowInvocationTests.PrepareDataActivity>(_ => { })
            .Parallel(join => join.WaitAny()
                .Do(branch => branch.Step<OrdinaryChargeActivity>(step => step
                    .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)))
                .Do(branch => branch.Step<RecoverableChargeActivity>(step => step
                    .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId))))
            .Build();

        await EngineAssert.StoreFailureAsync(() => engine.RunAsync(definition,
            new ChargeData { OrderId = "order-7" }));
        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(2, ledger.ChargeCount);
        var after = (await store.GetWorkflowInstanceAsync(saved.InstanceId))!;
        Assert.Equal(WorkflowStatus.NeedsResolution, after.Status);
    }

    [Theory]
    [InlineData(ActivityRecoveryDisposition.Execute)]
    [InlineData(ActivityRecoveryDisposition.Faulted)]
    public async Task CancelledBranchRecoveryKeepsAnUnconfirmedEffectForResolution(
        ActivityRecoveryDisposition disposition)
    {
        var repository = new InMemoryWorkflowStateRepository();
        var probe = new CancelledRecoveryProbe { Disposition = disposition };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(repository));
        services.AddSingleton(probe);
        services.AddTransient<CancelledRecoveryActivity>();
        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ChargeData>("CancelledRecovery")
            .Step<StepIndexActivity>()
            .Parallel(join => join.WaitAny()
                .Do(branch => branch.WaitFor<RecoveryReply>("a")
                    .Step<CancelledRecoveryActivity>())
                .Do(branch => branch.WaitFor<RecoveryReply>("b")))
            .Build();
        var started = await engine.RunAsync(definition, new ChargeData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var saved = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        var losingWait = checkpoint.Waits.Single(wait => wait.Key == "a");
        var winningWait = checkpoint.Waits.Single(wait => wait.Key == "b");
        var losing = checkpoint.Continuations.Single(item => item.Id == losingWait.ContinuationId);
        var winning = checkpoint.Continuations.Single(item => item.Id == winningWait.ContinuationId);
        var scopes = new WorkflowScopeCatalog(definition);
        losing.Stack[^1].NextStepIndex++;
        losing.Status = ContinuationStatus.Cancelling;
        losing.CancellationUnwind = true;
        losing.PendingActivity = new ActivityInvocationState
        {
            StepId = scopes.Id(definition.Steps[1].ParallelBranches[0][1]),
            RecoveryState = SerializedValue.From("ready"),
            Attempts = [new ActivityAttemptState()]
        };
        winning.Status = ContinuationStatus.Completed;
        checkpoint.Joins.Single().IsCompleting = true;
        checkpoint.Waits.Clear();
        saved.Status = WorkflowStatus.Running;
        saved.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        var committed = await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(saved, (await StoreTestSupport.LeaseAsync(repository, saved.InstanceId)).Epoch, saved.Revision, Guid.NewGuid().ToString("N")));
        Assert.Equal(WorkflowCommitStatus.Applied, committed.Status);
        await StoreTestSupport.ReleaseAsync(repository, saved.InstanceId);

        var recovered = await engine.RecoverAsync(started.InstanceId);

        Assert.Equal(disposition == ActivityRecoveryDisposition.Execute
            ? WorkflowExecutionStatus.NeedsResolution
            : WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal(1, probe.RecoveryCount);
        Assert.Equal(0, probe.ExecuteCount);
    }

    [Fact]
    public async Task ActivityContextAndStartEndTraceUseTheSameStepNumber()
    {
        var store = new FaultStore();
        using var services = CreateServices(store, new ChargeLedger(store));
        var definition = Workflow.Create<ChargeData>("StepPositions")
            .Step<StepIndexActivity>(step => step
                .Output(activity => activity.Index).To(ctx => ctx.WorkflowData.FirstIndex))
            .Step<StepIndexActivity>(step => step
                .Output(activity => activity.Index).To(ctx => ctx.WorkflowData.SecondIndex))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new ChargeData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ChargeData>(result.WorkflowData);
        Assert.Equal(0, data.FirstIndex);
        Assert.Equal(1, data.SecondIndex);
        var trace = await TraceAsync(store, result.InstanceId);
        Assert.Equal([0, 1], trace
            .Where(entry => entry.EntryType == TraceEntryType.ActivityStarted)
            .Select(entry => entry.StepNumber));
        Assert.Equal([0, 1], trace
            .Where(entry => entry.EntryType == TraceEntryType.ActivityCompleted)
            .Select(entry => entry.StepNumber));
    }

    private static WorkflowDefinition ChargeWorkflow() => Workflow
        .Create<ChargeData>("RecoverableCharge")
        .Step<RecoverableChargeActivity>(step => step
            .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
            .Output(activity => activity.ChargeId).To(ctx => ctx.WorkflowData.ChargeId))
        .Build();

    private static WorkflowDefinition OrdinaryChargeWorkflow() => Workflow
        .Create<ChargeData>("OrdinaryCharge")
        .Step<OrdinaryChargeActivity>(step => step
            .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
            .Output(activity => activity.ChargeId).To(ctx => ctx.WorkflowData.ChargeId))
        .Build();

    private static ServiceProvider CreateServices(FaultStore store, ChargeLedger ledger,
        long? maxCheckpointBytes = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options =>
        {
            if (maxCheckpointBytes is { } limit)
                options.MaxCheckpointBytes = limit;
        });
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(store));
        services.AddSingleton(ledger);
        services.RegisterWorkflow<WorkflowInvocationTests.ChildWorkflow>();
        services.AddTransient<RecoverableChargeActivity>();
        services.AddTransient<OrdinaryChargeActivity>();
        services.AddTransient<StepIndexActivity>();
        services.AddTransient<HandleOperatorFaultActivity>();
        return services.BuildServiceProvider();
    }

    public sealed class ChargeData
    {
        public string OrderId { get; set; } = "";
        public string? ChargeId { get; set; }
        public int FirstIndex { get; set; }
        public int SecondIndex { get; set; }
        public int FaultCode { get; set; }
        public string? ChildResult { get; set; }
        public bool FinallyRan { get; set; }
        public bool Caught { get; set; }
    }

    public sealed class OperatorFaultException(int code) : Exception($"operator fault {code}")
    {
        public int Code { get; } = code;
    }

    public sealed class OperatorFaultData
    {
        public int Code { get; set; }
    }

    public sealed class HandleOperatorFaultActivity : IAsyncActivity
    {
        public int Code { get; set; }
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class CheckpointData
    {
        public object? Payload { get; set; }
    }

    public sealed class InvalidActivityStateActivity : IAsyncActivity
    {
        public object? State { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            State = (Action)(() => { });
            return Task.CompletedTask;
        }
    }

    public sealed class InvalidWorkflowDataActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((CheckpointData)context.WorkflowData).Payload = (Action)(() => { });
            return Task.CompletedTask;
        }
    }

    public sealed class StepIndexActivity : IAsyncActivity
    {
        public int Index { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            Index = context.StepNumber;
            return Task.CompletedTask;
        }
    }

    public sealed class MarkFinallyActivity : IAsyncActivity
    {
        public bool Ran { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    public sealed class MarkCaughtActivity : IAsyncActivity
    {
        public bool Ran { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    public sealed class ChargeLedger(FaultStore store)
    {
        private readonly Dictionary<string, string> _charges = [];
        public int ChargeCount { get; private set; }
        public int RecoveryCount { get; private set; }
        public bool SawCommittedStart { get; private set; }
        public bool FailCompletionAfterCharge { get; set; }

        public async Task<string> ChargeAsync(string instanceId, string invocationId)
        {
            SawCommittedStart = (await TraceAsync(store, instanceId)).Any(entry =>
                entry.EntryType == TraceEntryType.ActivityStarted &&
                entry.Metadata.TryGetValue("InvocationId", out var id) &&
                id?.ToString() == invocationId);
            if (!_charges.TryGetValue(invocationId, out var chargeId))
            {
                chargeId = $"charge-{++ChargeCount}";
                _charges.Add(invocationId, chargeId);
            }
            if (FailCompletionAfterCharge)
            {
                store.FailCompletionBeforeApply = 3;
                FailCompletionAfterCharge = false;
            }
            return chargeId;
        }

        public string? Resolve(string invocationId)
        {
            RecoveryCount++;
            return _charges.GetValueOrDefault(invocationId);
        }
    }

    public sealed class RecoverableChargeActivity(ChargeLedger ledger)
        : IRecoverableActivity<string>
    {
        public string OrderId { get; set; } = "";
        public string? ChargeId { get; set; }

        public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;

        public Task<ActivityRecoveryResult<string>> RecoverAsync(
            string state, IActivityContext context, CancellationToken cancellationToken = default)
        {
            var chargeId = ledger.Resolve(state);
            if (chargeId != null)
            {
                ChargeId = chargeId;
                return Task.FromResult<ActivityRecoveryResult<string>>(
                    new ActivityRecoveryResult<string>.Completed());
            }
            return Task.FromResult<ActivityRecoveryResult<string>>(
                new ActivityRecoveryResult<string>.Execute());
        }

        public async Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) =>
            ChargeId = await ledger.ChargeAsync(context.WorkflowInstanceId,
                context.GetRecoveryState<string>());
    }

    public sealed class OrdinaryChargeActivity(ChargeLedger ledger) : IAsyncActivity
    {
        public string OrderId { get; set; } = "";
        public string? ChargeId { get; set; }

        public async Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) =>
            ChargeId = await ledger.ChargeAsync(context.WorkflowInstanceId, context.InvocationId);
    }

    public sealed class FaultStore : IWorkflowStateRepository
    {
        public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) =>
            _inner.GetDefinitionAsync(name, version);
        public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) =>
            _inner.SaveDefinitionAsync(definition, expectedRevision);
        public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) =>
            _inner.RegisterWorkflowVersionAsync(version, artifacts);
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) =>
            _inner.GetWorkflowVersionAsync(name, version);
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) =>
            _inner.GetWorkflowVersionsAsync(name);
        public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null) =>
            _inner.SetWorkflowVersionStateAsync(name, version, state, artifacts);
        public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) =>
            _inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
            _inner.PutAsync(kind, content, cancellationToken);
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            _inner.OpenAsync(digest, cancellationToken);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            _inner.ExistsAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
            _inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(digest, cancellationToken);

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
        public Task UnregisterPublisherAsync(string name) => _inner.UnregisterPublisherAsync(name);
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
        public int FailNextCommits { get; set; }
        public int FailStartAfterApply { get; set; }
        public int FailCompletionBeforeApply { get; set; }
        public int FailCompletionAfterApply { get; set; }
        public int FailFailureCommits { get; set; }
        public int FailChildStartAfterApply { get; set; }

        public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var instance = commit.ReadTrunk();
            if (FailNextCommits-- > 0)
            {
                throw new IOException("Checkpoint unavailable");
            }
            if (instance.Status == WorkflowStatus.Failed && FailFailureCommits-- > 0)
                throw new IOException("Checkpoint unavailable");
            var started = commit.ReadTraceEntries().LastOrDefault()?.EntryType ==
                TraceEntryType.ActivityStarted;
            var completed = commit.ReadTraceEntries().LastOrDefault()?.EntryType ==
                TraceEntryType.ActivityCompleted;
            if (completed && FailCompletionBeforeApply-- > 0)
                throw new IOException("Completion commit unavailable");
            var result = await _inner.CommitWorkflowInstanceAsync(commit);
            if (started && FailStartAfterApply-- > 0)
                throw new IOException("Start acknowledgment lost");
            if (completed && FailCompletionAfterApply-- > 0)
                throw new IOException("Completion acknowledgment lost");
            if (commit.ChildStarts.Count > 0 && FailChildStartAfterApply-- > 0)
                throw new IOException("Child start acknowledgment lost");
            return result;
        }

        public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) =>
            _inner.GetWorkflowInstanceSummaryAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId) =>
            _inner.GetChildInstancesAsync(parentInstanceId);
        public Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
            IReadOnlyCollection<string> instanceIds) =>
            StoreTestSupport.ReadCancellationsAsync(instanceIds, GetCancellationRequestAsync);

        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
            _inner.GetWorkflowInstanceAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
            _inner.GetWorkflowInstancesByNameAsync(workflowName);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
            _inner.GetWorkflowInstancesByStatusAsync(status);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
            _inner.GetWorkflowInstancesByCorrelationIdAsync(correlationId);
        public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
            int limit = 1000) =>
            _inner.GetTraceAsync(instanceId, afterSequence, limit);
        public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null) =>
            _inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention);
    }

    public sealed class RecoveryReply;

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class ThrowActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("business failure");
    }

    public sealed class CancelledRecoveryProbe
    {
        public ActivityRecoveryDisposition Disposition { get; set; }
        public int RecoveryCount { get; set; }
        public int ExecuteCount { get; set; }
    }

    public sealed class CancelledRecoveryActivity(CancelledRecoveryProbe probe) : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) => "ready";

        public Task<ActivityRecoveryResult<string>> RecoverAsync(
            string state, IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.RecoveryCount++;
            ActivityRecoveryResult<string> result = probe.Disposition switch
            {
                ActivityRecoveryDisposition.Execute => new ActivityRecoveryResult<string>.Execute(),
                ActivityRecoveryDisposition.Faulted =>
                    new ActivityRecoveryResult<string>.Faulted(new InvalidOperationException("losing branch failed")),
                _ => throw new InvalidOperationException("Unsupported test disposition")
            };
            return Task.FromResult(result);
        }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.ExecuteCount++;
            return Task.CompletedTask;
        }
    }

    // What the instance's commits stored of its trace (G-01).
    private static async Task<IReadOnlyList<ExecutionTraceEntry>> TraceAsync(IWorkflowStateRepository store,
        string instanceId) =>
        (await store.GetTraceAsync(instanceId)).Select(stored => stored.Entry).ToArray();
}
