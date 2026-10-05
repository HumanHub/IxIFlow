using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class ActivityRecoveryTests
{
    [Fact]
    public async Task StartIsCommittedBeforeTheActivityCallsAnExternalSystem()
    {
        var store = new FaultStore();
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var data = new ChargeData { OrderId = "order-1" };

        var result = await services.GetRequiredService<WorkflowEngine>()
            .ExecuteWorkflowAsync(ChargeWorkflow(), data);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(ledger.SawCommittedStart);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.NotNull(data.ChargeId);
    }

    [Fact]
    public async Task CrashAfterEffectBeforeEndUsesRecoveryWithoutAnotherCharge()
    {
        var store = new FaultStore { FailCompletionBeforeApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<WorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(
            ChargeWorkflow(), new ChargeData { OrderId = "order-2" }));

        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        Assert.Equal(1, ledger.ChargeCount);
        Assert.Equal(1, saved.ExecutionHistory.Count(entry =>
            entry.EntryType == TraceEntryType.ActivityStarted));
        Assert.DoesNotContain(saved.ExecutionHistory, entry =>
            entry.EntryType == TraceEntryType.ActivityCompleted);

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

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
        var engine = services.GetRequiredService<WorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(
            OrdinaryChargeWorkflow(), new ChargeData { OrderId = "order-3" }));
        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await store.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
    }

    [Fact]
    public async Task CommittedStartWithoutDispatchCanExecuteAfterRecovery()
    {
        var store = new FaultStore { FailStartAfterApply = 3 };
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<WorkflowEngine>();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(
            ChargeWorkflow(), new ChargeData { OrderId = "order-4" }));
        Assert.Equal(0, ledger.ChargeCount);
        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

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

        var result = await services.GetRequiredService<WorkflowEngine>()
            .ExecuteWorkflowAsync(ChargeWorkflow(), new ChargeData { OrderId = "order-5" });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, ledger.ChargeCount);
        Assert.Equal(0, ledger.RecoveryCount);
    }

    [Fact]
    public async Task InterruptedChildWorkflowInvocationIsNotRepeated()
    {
        var store = new FaultStore();
        var ledger = new ChargeLedger(store);
        using var services = CreateServices(store, ledger, new InvocationProbe(store));
        var engine = services.GetRequiredService<WorkflowEngine>();

        var definition = Workflow.Create<ChargeData>("InterruptedChildInvocation")
            .Step<WorkflowInvocationTests.PrepareDataActivity>(_ => { })
            .Invoke<WorkflowInvocationTests.ChildWorkflow, WorkflowInvocationTests.ChildWorkflowData>(step => step
                .Input(child => child.InputData).From(ctx => ctx.WorkflowData.OrderId))
            .Build();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition,
            new ChargeData { OrderId = "order-6" }));
        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(1, services.GetRequiredService<InvocationProbe>().Calls);
    }

    [Fact]
    public async Task WaitAnyCannotDiscardAnUnresolvedLosingBranch()
    {
        var store = new FaultStore();
        var ledger = new ChargeLedger(store) { FailCompletionAfterCharge = true };
        using var services = CreateServices(store, ledger);
        var engine = services.GetRequiredService<WorkflowEngine>();
        var definition = Workflow.Create<ChargeData>("UnresolvedParallelCharge")
            .Step<WorkflowInvocationTests.PrepareDataActivity>(_ => { })
            .Parallel(join => join.WaitAny()
                .Do(branch => branch.Step<OrdinaryChargeActivity>(step => step
                    .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)))
                .Do(branch => branch.Step<RecoverableChargeActivity>(step => step
                    .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId))))
            .Build();

        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition,
            new ChargeData { OrderId = "order-7" }));
        var saved = Assert.Single(await store.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));

        var recovered = await engine.RecoverWorkflowAsync(saved.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(2, ledger.ChargeCount);
        var after = (await store.GetWorkflowInstanceAsync(saved.InstanceId))!;
        Assert.Equal(WorkflowStatus.NeedsResolution, after.Status);
    }

    [Theory]
    [InlineData(ActivityRecoveryDisposition.Execute)]
    [InlineData(ActivityRecoveryDisposition.Faulted)]
    public async Task CancelledBranchRecoveryDoesNotExecuteAnUnstartedEffect(
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
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var definition = Workflow.Create<ChargeData>("CancelledRecovery")
            .Step<StepIndexActivity>(_ => { })
            .Parallel(join => join.WaitAny()
                .Do(branch => branch.WaitFor<RecoveryReply>("a")
                    .Step<CancelledRecoveryActivity>(_ => { }))
                .Do(branch => branch.WaitFor<RecoveryReply>("b")))
            .Build();
        var started = await engine.ExecuteWorkflowAsync(definition, new ChargeData());
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
            saved, saved.Revision, Guid.NewGuid().ToString("N"));
        Assert.Equal(WorkflowCommitStatus.Applied, committed.Status);

        var recovered = await engine.RecoverWorkflowAsync(started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
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

        var result = await services.GetRequiredService<WorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new ChargeData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ChargeData>(result.WorkflowData);
        Assert.Equal(0, data.FirstIndex);
        Assert.Equal(1, data.SecondIndex);
        var saved = (await store.GetWorkflowInstanceAsync(result.InstanceId))!;
        Assert.Equal([0, 1], saved.ExecutionHistory
            .Where(entry => entry.EntryType == TraceEntryType.ActivityStarted)
            .Select(entry => entry.StepNumber));
        Assert.Equal([0, 1], saved.ExecutionHistory
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
        InvocationProbe? invocation = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(store));
        services.AddSingleton(ledger);
        if (invocation != null)
        {
            services.AddSingleton(invocation);
            services.Replace(ServiceDescriptor.Singleton<IWorkflowInvoker>(invocation));
        }
        services.AddTransient<RecoverableChargeActivity>();
        services.AddTransient<OrdinaryChargeActivity>();
        services.AddTransient<StepIndexActivity>();
        services.AddSingleton<WorkflowEngine>();
        return services.BuildServiceProvider();
    }

    public sealed class ChargeData
    {
        public string OrderId { get; set; } = "";
        public string? ChargeId { get; set; }
        public int FirstIndex { get; set; }
        public int SecondIndex { get; set; }
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

    public sealed class ChargeLedger(FaultStore store)
    {
        private readonly Dictionary<string, string> _charges = [];
        public int ChargeCount { get; private set; }
        public int RecoveryCount { get; private set; }
        public bool SawCommittedStart { get; private set; }
        public bool FailCompletionAfterCharge { get; set; }

        public async Task<string> ChargeAsync(string instanceId, string invocationId)
        {
            var saved = await store.GetWorkflowInstanceAsync(instanceId);
            SawCommittedStart = saved?.ExecutionHistory.Any(entry =>
                entry.EntryType == TraceEntryType.ActivityStarted &&
                entry.Metadata.TryGetValue("InvocationId", out var id) &&
                id?.ToString() == invocationId) == true;
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
        private readonly InMemoryWorkflowStateRepository _inner = new();
        public int FailStartAfterApply { get; set; }
        public int FailCompletionBeforeApply { get; set; }
        public int FailCompletionAfterApply { get; set; }

        public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(
            WorkflowInstance instance, long expectedRevision, string commitId)
        {
            var started = instance.ExecutionHistory.LastOrDefault()?.EntryType ==
                TraceEntryType.ActivityStarted;
            var completed = instance.ExecutionHistory.LastOrDefault()?.EntryType ==
                TraceEntryType.ActivityCompleted;
            if (completed && FailCompletionBeforeApply-- > 0)
                throw new IOException("Completion commit unavailable");
            var result = await _inner.CommitWorkflowInstanceAsync(instance, expectedRevision, commitId);
            if (started && FailStartAfterApply-- > 0)
                throw new IOException("Start acknowledgment lost");
            if (completed && FailCompletionAfterApply-- > 0)
                throw new IOException("Completion acknowledgment lost");
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

    public sealed class RecoveryReply;

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

    public sealed class InvocationProbe(FaultStore store) : IWorkflowInvoker
    {
        public int Calls { get; private set; }

        public Task<StepExecutionResult> ExecuteWorkflowInvocationAsync<TWorkflowData>(
            WorkflowStep step, StepExecutionContext<TWorkflowData> context,
            ExecutionState executionState, CancellationToken cancellationToken)
            where TWorkflowData : class
        {
            Calls++;
            store.FailCompletionBeforeApply = 3;
            return Task.FromResult(new StepExecutionResult
            {
                IsSuccess = true,
                OutputData = new WorkflowInvocationTests.ChildWorkflowData()
            });
        }
    }
}
