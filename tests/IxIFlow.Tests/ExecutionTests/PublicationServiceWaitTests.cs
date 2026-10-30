using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class PublicationServiceWaitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestBudgetExpiresWhileTheCatalogClockIsStationary(bool joined)
    {
        var clock = new TimerRecordingClock();
        var store = new InMemoryWorkflowStateRepository(clock);
        var draft = await store.SaveDefinitionAsync(new("WaitBudget", 1, "workflow", "invalid: ["), null);
        var catalog = CatalogProbe.Wrap(store, out var probe);
        var entered = Signal();
        var release = Signal();

        probe.Intercept = (method, args) => method.Name == nameof(IWorkflowCatalogStore.CompletePublicationAsync)
            ? CompleteAsync(args!)
            : null;

        using var owner = new PublicationService(catalog, new WorkflowPublisher(store, store), timeProvider: clock);
        using var other = new PublicationService(store, new WorkflowPublisher(store, store), timeProvider: clock);
        var running = await owner.PublishAsync(draft.Id, TimeSpan.Zero);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var service = joined ? other : owner;
            var result = await service.PublishAsync(draft.Id, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(running.Id, result.Id);
            Assert.Equal(WorkflowPublicationState.Running, result.State);

            using var request = new CancellationTokenSource();
            var waiting = service.PublishAsync(draft.Id, TimeSpan.FromMinutes(1), cancellationToken: request.Token);
            request.Cancel();
            await Assert.ThrowsAsync<TaskCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();

            await TestEventually.UntilAsync(running.Id.ToString(), "publication completes after the canceled request",
                () => store.GetPublicationAsync(running.Id, CatalogScope.Unrestricted),
                publication => publication!.State == WorkflowPublicationState.Failed);

            await owner.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await other.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(WorkflowPublicationState.Failed,
            (await store.GetPublicationAsync(running.Id, CatalogScope.Unrestricted))!.State);

        async Task<WorkflowPublication> CompleteAsync(object?[] args)
        {
            entered.TrySetResult();
            await release.Task;

            return await store.CompletePublicationAsync((Guid)args[0]!, (Guid)args[1]!, (WorkflowPublicationOutcome)args[2]!);
        }
    }

    [Fact]
    public async Task JoinedRequestBudgetIncludesAHeldCatalogRead()
    {
        var clock = new TimerRecordingClock();
        var store = new InMemoryWorkflowStateRepository(clock);
        var draft = await store.SaveDefinitionAsync(new("HeldRead", 1, "workflow", "invalid: ["), null);
        var running = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, TimeSpan.FromMinutes(5));
        var catalog = CatalogProbe.Wrap(store, out var probe);
        var read = new TaskCompletionSource<WorkflowPublication?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken readToken = default;

        probe.Intercept = (method, args) =>
        {
            if (method.Name != nameof(IWorkflowCatalogStore.GetPublicationAsync))
                return null;

            readToken = (CancellationToken)args![2]!;

            return read.Task;
        };

        using var service = new PublicationService(catalog, new WorkflowPublisher(store, store), timeProvider: clock);

        try
        {
            var result = await service.PublishAsync(draft.Id, TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(running.Id, result.Id);
            Assert.Equal(WorkflowPublicationState.Running, result.State);
            Assert.True(readToken.IsCancellationRequested);
        }
        finally
        {
            read.TrySetResult(running);
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(nameof(IWorkflowCatalogStore.BeginPublicationAsync))]
    [InlineData(nameof(IWorkflowCatalogStore.GetPublicationAsync))]
    public async Task CatalogOperationsHaveAFiniteBudgetEvenWhenTheStoreIgnoresCancellation(string operation)
    {
        var clock = new TimerRecordingClock();
        var store = new InMemoryWorkflowStateRepository(clock);
        var draft = await store.SaveDefinitionAsync(new("CatalogBudget", 1, "workflow", "invalid: ["), null);
        var catalog = CatalogProbe.Wrap(store, out var probe);
        var held = new TaskCompletionSource<WorkflowPublication?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken operationToken = default;

        probe.Intercept = (method, args) =>
        {
            if (method.Name != operation)
                return null;

            operationToken = (CancellationToken)args![^1]!;

            return held.Task;
        };

        var options = new WorkflowPublisherOptions
        {
            PublicationHeartbeatInterval = TimeSpan.FromMilliseconds(10), PublicationStaleAfter = TimeSpan.FromMilliseconds(100)
        };

        using var service = new PublicationService(catalog, new WorkflowPublisher(store, store), options, clock);

        try
        {
            Task waiting;

            if (operation == nameof(IWorkflowCatalogStore.BeginPublicationAsync))
                waiting = service.PublishAsync(draft.Id, TimeSpan.Zero);
            else
                waiting = service.GetPublicationAsync(Guid.NewGuid(), CatalogScope.Unrestricted);

            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.True(operationToken.IsCancellationRequested);
        }
        finally
        {
            held.TrySetResult(null);
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CompletionAndShutdownCancelAHeldHeartbeatEvenWhenTheStoreIgnoresCancellation()
    {
        var clock = new TimerRecordingClock();
        var store = new InMemoryWorkflowStateRepository(clock);
        var draft = await store.SaveDefinitionAsync(new("HeldHeartbeat", 1, "workflow", "invalid: ["), null);
        var catalog = CatalogProbe.Wrap(store, out var probe);
        var completing = Signal();
        var release = Signal();
        var heartbeating = Signal();
        var heartbeat = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken heartbeatToken = default;

        probe.Intercept = (method, args) =>
        {
            if (method.Name == nameof(IWorkflowCatalogStore.CompletePublicationAsync))
                return CompleteAsync(args!);

            if (method.Name != nameof(IWorkflowCatalogStore.HeartbeatPublicationAsync))
                return null;

            heartbeatToken = (CancellationToken)args![2]!;
            heartbeating.TrySetResult();

            return heartbeat.Task;
        };

        var options = new WorkflowPublisherOptions
        {
            PublicationHeartbeatInterval = TimeSpan.FromSeconds(1), PublicationStaleAfter = TimeSpan.FromSeconds(10)
        };

        using var service = new PublicationService(catalog, new WorkflowPublisher(store, store), options, clock);
        var waiting = service.PublishAsync(draft.Id, TimeSpan.FromMinutes(1));

        try
        {
            await completing.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await clock.WaitForTimerAsync(timer => timer.Pending &&
                timer.DueAt == clock.GetUtcNow() + options.PublicationHeartbeatInterval);

            clock.Advance(options.PublicationHeartbeatInterval);
            await heartbeating.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();

            Assert.Equal(WorkflowPublicationState.Failed, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).State);
            Assert.True(heartbeatToken.IsCancellationRequested);
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            heartbeat.TrySetResult(false);
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task<WorkflowPublication> CompleteAsync(object?[] args)
        {
            completing.TrySetResult();
            await release.Task;

            return await store.CompletePublicationAsync((Guid)args[0]!, (Guid)args[1]!, (WorkflowPublicationOutcome)args[2]!);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public class CatalogProbe : DispatchProxy
    {
        private IWorkflowCatalogStore _target = null!;
        public Func<MethodInfo, object?[]?, object?>? Intercept;

        public static IWorkflowCatalogStore Wrap(IWorkflowCatalogStore target, out CatalogProbe probe)
        {
            var catalog = Create<IWorkflowCatalogStore, CatalogProbe>();
            probe = (CatalogProbe)catalog;
            probe._target = target;

            return catalog;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            var intercepted = Intercept?.Invoke(method, args);

            if (intercepted != null)
                return intercepted;

            try
            {
                return method.Invoke(_target, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException!).Throw();

                throw;
            }
        }
    }
}