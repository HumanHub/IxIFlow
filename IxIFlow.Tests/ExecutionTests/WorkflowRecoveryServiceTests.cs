using IxIFlow.Core;
using IxIFlow.Builders;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class WorkflowRecoveryServiceTests
{
    [Fact]
    public async Task ScannerMovesAnInterruptedOrdinaryActivityToNeedsResolution()
    {
        var repository = new ActivityRecoveryTests.FaultStore
        {
            FailCompletionBeforeApply = 3
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(repository));
        services.AddTransient<ActivityRecoveryTests.NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<RecoveryData>("ScanInterrupted")
            .Step<ActivityRecoveryTests.NoopActivity>()
            .Build();
        await Assert.ThrowsAsync<IOException>(() => provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new RecoveryData()));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        using var scanner = new WorkflowRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);

        await scanner.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
    }

    [Fact]
    public async Task HostShutdownDuringRecoveryDoesNotCancelTheWorkflow()
    {
        var repository = new ActivityRecoveryTests.FaultStore
        {
            FailCompletionBeforeApply = 3
        };
        var probe = new RecoveryStopProbe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(repository));
        services.AddSingleton(probe);
        services.AddTransient<RecoveryStopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<RecoveryData>("HostStopDuringRecovery")
            .Step<RecoveryStopActivity>()
            .Build();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition, new RecoveryData()));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        using var scanner = new WorkflowRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);
        using var stopping = new CancellationTokenSource();
        var scan = scanner.RecoverOnceAsync(stopping.Token);
        await probe.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);

        Assert.Equal(WorkflowStatus.Running,
            (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
    }

    [Fact]
    public async Task ScannerRecoversRunningInstancesOnly()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var runningId = Guid.NewGuid().ToString("N");
        var waitingId = Guid.NewGuid().ToString("N");
        await repository.SaveWorkflowInstanceAsync(new WorkflowInstance
        {
            InstanceId = runningId,
            WorkflowName = "Running",
            Status = WorkflowStatus.Running
        });
        await repository.SaveWorkflowInstanceAsync(new WorkflowInstance
        {
            InstanceId = waitingId,
            WorkflowName = "Waiting",
            Status = WorkflowStatus.Suspended
        });
        var engine = new RecordingEngine();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowEngine>(engine);
        using var provider = services.BuildServiceProvider();
        using var scanner = new WorkflowRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);

        await scanner.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal([runningId], engine.Recovered);
    }

    [Fact]
    public async Task ScannerFinishesCancellationRequestedBeforeSuspendedHostStopped()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(repository));
        services.AddTransient<ActivityRecoveryTests.NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<RecoveryData>("CancelAfterHostStopped")
            .Step<ActivityRecoveryTests.NoopActivity>()
            .WaitFor<RecoveryEvent>("approval")
            .Build();
        var started = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new RecoveryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.True(await repository.RequestCancellationAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn" }));
        using var scanner = new WorkflowRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);

        await scanner.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(WorkflowStatus.Cancelled,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
    }

    [Fact]
    public async Task ScannerDoesNotRepeatCancellationWhileFinallyWaits()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(repository));
        services.AddTransient<ActivityRecoveryTests.NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<RecoveryData>("CancelFinallyWait")
            .Step<ActivityRecoveryTests.NoopActivity>()
            .Try(body => body.WaitFor<RecoveryEvent>("approval"))
            .Finally(body => body.WaitFor<RecoveryEvent>("cleanup"))
            .Build();
        var started = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new RecoveryData());
        Assert.True(await repository.RequestCancellationAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn" }));
        using var scanner = new WorkflowRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);
        var waiting = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Suspended, waiting.Status);
        Assert.Empty(await repository.GetWorkflowsRequiringRecoveryAsync());

        await scanner.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(waiting.Revision,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Revision);
    }

    [Fact]
    public async Task UnresolvedActivityRetainsCancellationUntilOperatorDecision()
    {
        var repository = new ActivityRecoveryTests.FaultStore
        {
            FailCompletionBeforeApply = 3
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(repository));
        services.AddTransient<ActivityRecoveryTests.NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<RecoveryData>("CancelUnresolved")
            .Step<ActivityRecoveryTests.NoopActivity>()
            .Build();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        await Assert.ThrowsAsync<IOException>(() => engine.ExecuteWorkflowAsync(definition, new RecoveryData()));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running));
        await engine.RecoverWorkflowAsync(saved.InstanceId);
        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
        Assert.True(await repository.RequestCancellationAsync(saved.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn" }));
        using var scanner = new WorkflowRecoveryService(
            provider.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);

        await scanner.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(saved.InstanceId));
        var result = await engine.ResolveActivityAsync(saved.InstanceId, pending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>()));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(WorkflowStatus.Cancelled,
            (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
    }

    private sealed class RecordingEngine : IWorkflowEngine
    {
        public Task<WorkflowExecutionResult> ResumeWorkflowDeliveryAsync<TEventData>(
            string instanceId, string? key, TEventData @event, string deliveryId,
            CancellationToken cancellationToken = default) where TEventData : class =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId) =>
            throw new NotSupportedException();
        public Task<WorkflowExecutionResult> ResolveActivityAsync(string instanceId,
            string invocationId, ActivityResolution resolution,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkflowExecutionResult> CancelWorkflowAsync(string instanceId,
            CancellationReason reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public List<string> Recovered { get; } = [];

        public Task<WorkflowExecutionResult> RecoverWorkflowAsync(string instanceId,
            CancellationToken cancellationToken = default)
        {
            Recovered.Add(instanceId);
            return Task.FromResult(new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = WorkflowExecutionStatus.NeedsResolution
            });
        }

        public Task<WorkflowExecutionResult> ExecuteWorkflowAsync<TWorkflowData>(
            WorkflowDefinition definition, TWorkflowData workflowData,
            WorkflowOptions? options = null, CancellationToken cancellationToken = default)
            where TWorkflowData : class => throw new NotSupportedException();
        public Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(
            string instanceId, TEventData @event, CancellationToken cancellationToken = default)
            where TEventData : class => throw new NotSupportedException();
        public Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEventData>(
            string instanceId, string key, TEventData @event,
            CancellationToken cancellationToken = default)
            where TEventData : class => throw new NotSupportedException();
    }

    public sealed class RecoveryData;
    public sealed class RecoveryEvent;

    public sealed class RecoveryStopProbe
    {
        public TaskCompletionSource RecoveryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class RecoveryStopActivity(RecoveryStopProbe probe) : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) => "ready";

        public async Task<ActivityRecoveryResult<string>> RecoverAsync(
            string state, IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.RecoveryStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ActivityRecoveryResult<string>.Completed();
        }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
