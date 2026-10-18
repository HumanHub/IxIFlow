using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-11")]
[Trait("Spec", "D-12")]
[Trait("Feature", "durability")]
public sealed class OutboxHostTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WorkflowOutboxService Service(InMemoryWorkflowStateRepository store, TimeProvider time,
        int concurrency, params IOutboxPublisher[] publishers) => new(store, publishers,
        new HostSettings(4, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), "outbox-tests"),
        new OutboxSettings(concurrency), time, NullLogger<WorkflowOutboxService>.Instance);

    [Fact]
    public async Task CommittingHostCompletesItsOwnAwaitWithoutAnOutboxDelivery()
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IOutboxPublisher>());
        var client = provider.GetRequiredService<IWorkflowClient>();
        var store = provider.GetRequiredService<IWorkflowStateRepository>();
        var definition = Workflow.Create<Data>("LocalAwait").Step<Noop>().WaitFor<Approval>("approval").Build();
        var started = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var awaiting = client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished);
        Assert.False(awaiting.IsCompleted);
        await client.ResumeAsync(started.InstanceId, "approval", new Approval());
        // The fake clock never advances, so store polling cannot complete the await; only the host can.
        Assert.Equal(WorkflowExecutionStatus.Success, (await awaiting.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Null(await store.GetPublisherAsync("ixiflow.in-process"));
    }

    [Fact]
    public async Task ShutdownFinishesInFlightAndLeavesQueuedDeliveriesUnclaimed()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        await store.RegisterPublisherAsync("drain", [OutboxEvent.WorkflowCompleted]);
        await OutboxStoreContract.CompleteAsync(store);
        await OutboxStoreContract.CompleteAsync(store);
        var entered = Signal();
        var release = Signal();
        var attempts = 0;
        var publisher = new Publisher("drain", async (_, token) =>
        {
            Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return OutboxPublishResult.Delivered();
        });
        using var service = Service(store, time, 1, publisher);
        await service.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = service.StopAsync(default);
        Assert.False(stopping.IsCompleted);
        release.SetResult();
        await stopping;
        Assert.Equal(1, attempts);
        Assert.Single(await store.ClaimDeliveriesAsync("drain", "again", TimeSpan.FromMinutes(1), 100));
    }

    [Fact]
    public async Task DrainTimeoutCancelsTheAttemptAndLeavesItRecoverable()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        await store.RegisterPublisherAsync("timeout", [OutboxEvent.WorkflowCompleted]);
        await OutboxStoreContract.CompleteAsync(store);
        var entered = new TaskCompletionSource<OutboxPublishContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = Signal();
        var publisher = new Publisher("timeout", async (delivery, token) =>
        {
            entered.SetResult(delivery);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
            return OutboxPublishResult.Delivered();
        });
        using var service = Service(store, time, 1, publisher);
        await service.StartAsync(default);
        var delivery = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = service.StopAsync(default);
        time.Advance(TimeSpan.FromSeconds(20));
        await stopping;
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OutboxDeliveryStatus.Pending, (await store.GetDeliveryAsync(delivery.Event.Id, publisher.Name))!.Status);
        time.Advance(TimeSpan.FromMinutes(1));
        var recovered = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "next-host", TimeSpan.FromSeconds(30), 1));
        Assert.Equal(delivery.Event.Id, recovered.Event.Id);
    }

    [Fact]
    public async Task ASlowPublisherCannotOccupyEveryWorker()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        await store.RegisterPublisherAsync("slow", [OutboxEvent.WorkflowCompleted]);
        await store.RegisterPublisherAsync("fast", [OutboxEvent.WorkflowCompleted]);
        for (var i = 0; i < 3; i++) await OutboxStoreContract.CompleteAsync(store);
        var slowEntered = Signal();
        var release = Signal();
        var fastDone = Signal();
        var fastCount = 0;
        var slow = new Publisher("slow", async (_, token) =>
        {
            slowEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return OutboxPublishResult.GiveUp("unavailable");
        });
        var fast = new Publisher("fast", (_, _) =>
        {
            if (Interlocked.Increment(ref fastCount) == 3) fastDone.TrySetResult();
            return Task.FromResult(OutboxPublishResult.Delivered());
        });
        using var service = Service(store, time, 2, slow, fast);
        await service.StartAsync(default);
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, fastCount);
        release.SetResult();
        await service.StopAsync(default);
    }

    [Fact]
    public async Task LeaseIsRenewedWhileDeliveryIsInFlight()
    {
        var time = new ObservedStoreClock();
        var store = new InMemoryWorkflowStateRepository(time);
        await store.RegisterPublisherAsync("renew", [OutboxEvent.WorkflowCompleted]);
        await OutboxStoreContract.CompleteAsync(store);
        var entered = new TaskCompletionSource<OutboxPublishContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Signal();
        var publisher = new Publisher("renew", async (delivery, token) =>
        {
            entered.TrySetResult(delivery);
            await release.Task.WaitAsync(token);
            return OutboxPublishResult.Delivered();
        });
        using var service = Service(store, time, 1, publisher);
        await service.StartAsync(default);
        var original = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var heartbeat = time.ObserveNextStoreRead();
        time.Advance(TimeSpan.FromSeconds(10));
        await heartbeat.WaitAsync(TimeSpan.FromSeconds(5));
        var renewed = (await store.GetDeliveryAsync(original.Event.Id, publisher.Name))!;
        Assert.True(renewed.LeaseUntilUtc > original.StoreNowUtc.AddSeconds(30));
        heartbeat = time.ObserveNextStoreRead();
        time.Advance(TimeSpan.FromSeconds(21));
        await heartbeat;
        Assert.Empty(await store.ClaimDeliveriesAsync(publisher.Name, "intruder", TimeSpan.FromSeconds(30), 1));
        release.SetResult();
        await service.StopAsync(default);
    }

    [Fact]
    public async Task BuilderRegistersACustomSingletonPublisher()
    {
        var publisher = new Publisher("custom", (_, _) => Task.FromResult(OutboxPublishResult.Delivered()));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow().ConfigureOutbox(2).AddOutboxPublisher(_ => publisher);
        await using var provider = services.BuildServiceProvider();
        Assert.Contains(publisher, provider.GetServices<IOutboxPublisher>());
        Assert.Equal(2, provider.GetRequiredService<OutboxSettings>().Concurrency);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31 * 24 * 3600)]
    public async Task InvalidRetryDelayIsRecordedAsThePublishersAttemptFailure(int seconds)
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var publisher = new Publisher("invalid", (_, _) =>
            Task.FromResult(OutboxPublishResult.RetryLater(delay: TimeSpan.FromSeconds(seconds))));
        await store.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
        await OutboxStoreContract.CompleteAsync(store);
        var delivery = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "owner", TimeSpan.FromSeconds(30), 1));
        using var service = Service(store, time, 1, publisher);
        await service.DeliverAsync(publisher, delivery, default);
        var saved = (await store.GetDeliveryAsync(delivery.Event.Id, publisher.Name))!;
        Assert.Equal(1, saved.Attempts);
        Assert.Equal(OutboxDeliveryStatus.Pending, saved.Status);
        Assert.Contains("Retry delay", saved.LastError);
        Assert.Null(saved.LeaseToken);
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddSeconds(5), saved.NextAttemptAtUtc);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(31, 30)]
    public void HostClampsRawRetryResultsBeforeSettlement(int requestedDays, int expectedDays)
    {
        var raw = new OutboxPublishResult(OutboxPublishOutcome.RetryLater, TimeSpan.FromDays(requestedDays));
        Assert.Equal(TimeSpan.FromDays(expectedDays), WorkflowOutboxService.ClampRetry(raw, 1, TimeSpan.FromDays(40)).RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(2), WorkflowOutboxService.ClampRetry(
            OutboxPublishResult.RetryLater(), 1, TimeSpan.FromSeconds(2)).RetryAfter);
    }

    [Fact]
    public async Task RetryHorizonStartsAtFirstAttemptAndSurvivesAHostRestart()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var calls = 0;
        var publisher = new Publisher("bounded", (_, _) =>
        {
            calls++;
            return Task.FromResult(OutboxPublishResult.RetryLater("offline"));
        });
        await store.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
        await OutboxStoreContract.CompleteAsync(store);
        time.Advance(TimeSpan.FromDays(60)); // Retention age does not consume the retry horizon.
        for (var i = 0; i < 3; i++)
        {
            var delivery = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "owner", TimeSpan.FromSeconds(30), 1));
            using var service = new WorkflowOutboxService(store, [publisher],
                new HostSettings(4, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), "bounded"),
                new OutboxSettings(retryHorizon: TimeSpan.FromSeconds(10)), time, NullLogger<WorkflowOutboxService>.Instance);
            await service.DeliverAsync(publisher, delivery, default);
            time.Advance(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, calls);
        Assert.Empty(await store.ClaimDeliveriesAsync(publisher.Name, "owner", TimeSpan.FromSeconds(30), 1));
        Assert.Equal(TimeSpan.FromDays(30), new OutboxSettings().RetryHorizon);
    }

    private sealed class Publisher(string name,
        Func<OutboxPublishContext, CancellationToken, Task<OutboxPublishResult>> publish) : IOutboxPublisher
    {
        public string Name => name;
        public IReadOnlyCollection<string> EventTypes => [OutboxEvent.WorkflowCompleted];
        public Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery, CancellationToken cancellationToken) =>
            publish(delivery, cancellationToken);
    }

    // Signals the store's clock read inside a heartbeat, independently of timer scheduling.
    private sealed class ObservedStoreClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        private TaskCompletionSource? _read;
        public Task ObserveNextStoreRead() => (_read = Signal()).Task;
        public void Advance(TimeSpan duration) => _clock.Advance(duration);
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref _read, null)?.TrySetResult();
            return _clock.GetUtcNow();
        }
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _clock.CreateTimer(callback, state, dueTime, period);
    }

    public sealed class Data { }

    public sealed class Approval { }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
