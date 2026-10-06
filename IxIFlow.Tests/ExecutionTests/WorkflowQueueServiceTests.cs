using IxIFlow.Core;
using IxIFlow.Builders;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class WorkflowQueueServiceTests
{
    [Fact]
    public async Task ExecuteCommandDoesNotPublishCompletionWhenWorkflowWaits()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("QueuedWait")
            .Step<NoopActivity>()
            .WaitFor<QueueEvent>("approval")
            .Build();
        await provider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        var instanceId = Guid.NewGuid().ToString("N");

        await service.ProcessExecuteCommandAsync(new ExecuteWorkflowCommand
        {
            InstanceId = instanceId,
            TargetHostId = "test-host",
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = typeof(QueueData).AssemblyQualifiedName!,
            WorkflowDataJson = System.Text.Json.JsonSerializer.Serialize(new QueueData())
        });

        Assert.Equal(WorkflowStatus.Suspended,
            (await provider.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(instanceId))!.Status);
        Assert.Empty(bus.Completions);
    }

    [Fact]
    public async Task ExecuteCommandPublishesCompletionAfterSuccess()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("QueuedSuccess")
            .Step<NoopActivity>()
            .Build();
        await provider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(definition);
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        var instanceId = Guid.NewGuid().ToString("N");

        await service.ProcessExecuteCommandAsync(new ExecuteWorkflowCommand
        {
            InstanceId = instanceId,
            TargetHostId = "test-host",
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = typeof(QueueData).AssemblyQualifiedName!,
            WorkflowDataJson = System.Text.Json.JsonSerializer.Serialize(new QueueData())
        });

        Assert.Equal(WorkflowExecutionStatus.Success, Assert.Single(bus.Completions).Status);
    }

    [Fact]
    public async Task CancelCommandChangesSavedWaitBeforePublishingCancelled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("QueuedCancel")
            .Step<NoopActivity>()
            .WaitFor<QueueEvent>("approval")
            .Build();
        var started = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new QueueData());
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await service.ProcessCancelCommandAsync(new CancelWorkflowCommand
        {
            InstanceId = started.InstanceId,
            TargetHostId = "test-host",
            Reason = new CancellationReason { ReasonCode = "withdrawn" }
        });

        Assert.Equal(WorkflowStatus.Cancelled,
            (await provider.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        Assert.Equal(WorkflowExecutionStatus.Cancelled,
            Assert.Single(bus.Completions).Status);
    }

    [Fact]
    public async Task CancelCommandDoesNotClaimCompletedWorkflowWasCancelled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("AlreadyCompleted")
            .Step<NoopActivity>()
            .Build();
        var completed = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new QueueData());
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await service.ProcessCancelCommandAsync(new CancelWorkflowCommand
        {
            InstanceId = completed.InstanceId,
            TargetHostId = "test-host",
            Reason = new CancellationReason { ReasonCode = "too-late" }
        });

        Assert.Equal(WorkflowStatus.Completed,
            (await provider.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(completed.InstanceId))!.Status);
        Assert.DoesNotContain(bus.Completions,
            result => result.Status == WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task CancelCommandDoesNotPublishCompletionWhileFinallyWaits()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<QueueData>("QueuedCancelCleanupWait")
            .Step<NoopActivity>()
            .Try(body => body.WaitFor<QueueEvent>("approval"))
            .Finally(body => body.WaitFor<QueueEvent>("cleanup"))
            .Build();
        var started = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new QueueData());
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await service.ProcessCancelCommandAsync(new CancelWorkflowCommand
        {
            InstanceId = started.InstanceId,
            TargetHostId = "test-host",
            Reason = new CancellationReason { ReasonCode = "withdrawn" }
        });

        Assert.Equal(WorkflowStatus.Suspended,
            (await provider.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        Assert.Empty(bus.Completions);
    }

    [Fact]
    public async Task CancelCommandForUnknownInstanceStaysUnacknowledged()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var bus = new RecordingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ProcessCancelCommandAsync(new CancelWorkflowCommand
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                TargetHostId = "test-host",
                Reason = new CancellationReason { ReasonCode = "withdrawn" }
            }));
        Assert.Empty(bus.Completions);
    }
    [Fact]
    public async Task ConsumerFailureStopsTheQueueServiceWithTheOriginalError()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var bus = new FailingBus();
        var coordinator = new WorkflowCoordinator(new MockWorkflowHostClient(),
            new InMemoryHostRegistry());
        using var service = new TestQueueService(provider, bus, coordinator);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                service.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("Execute consumer failed", error.Message);
        }
        finally
        {
            await bus.StopAsync();
        }
    }

    private sealed class TestQueueService(IServiceProvider services, IMessageBus bus,
        IWorkflowCoordinator coordinator) : WorkflowQueueService(
            services, NullLogger<WorkflowQueueService>.Instance, bus, coordinator,
            new WorkflowHostOptions { HostId = "test-host" })
    {
        public Task RunAsync(CancellationToken token) => ExecuteAsync(token);
    }

    private sealed class FailingBus : IMessageBus
    {
        private readonly CancellationTokenSource _stop = new();

        public Task PublishAsync<T>(T message) where T : class => Task.CompletedTask;

        public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
        {
            if (typeof(T) == typeof(ExecuteWorkflowCommand))
                throw new IOException("Execute consumer failed");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
            yield break;
        }

        public Task StopAsync()
        {
            _stop.Cancel();
            return Task.CompletedTask;
        }
    }

    public sealed class QueueData;
    public sealed class QueueEvent;

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingBus : IMessageBus
    {
        public List<WorkflowExecutionCompletedEvent> Completions { get; } = [];
        public Task PublishAsync<T>(T message) where T : class
        {
            if (message is WorkflowExecutionCompletedEvent completion)
                Completions.Add(completion);
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task StopAsync() => Task.CompletedTask;
    }
}
