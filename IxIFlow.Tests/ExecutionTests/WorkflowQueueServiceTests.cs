using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class WorkflowQueueServiceTests
{
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
}
