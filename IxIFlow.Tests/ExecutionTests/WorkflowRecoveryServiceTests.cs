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

    private sealed class RecordingEngine : IWorkflowEngine
    {
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
}
