using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-11")]
[Trait("Feature", "durability")]
public sealed class OutboxServiceTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WorkflowOutboxService Service(IWorkflowStateRepository store, FakeTimeProvider time,
        TestLogger log, int concurrency, params IOutboxPublisher[] publishers) => new(store, publishers,
        new HostSettings(4, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), "outbox-service"),
        new OutboxSettings(concurrency), time, log);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulPublishWithRejectedOrFailedSettlementIsLoggedAndCounted(bool throws)
    {
        var time = new FakeTimeProvider();
        var inner = new InMemoryWorkflowStateRepository(time);
        var publisher = new Publisher("settle-" + Guid.NewGuid().ToString("N"), (_, _) => Task.FromResult(OutboxPublishResult.Delivered()));
        await inner.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
        await OutboxStoreContract.CompleteAsync(inner);
        var claim = Assert.Single(await inner.ClaimDeliveriesAsync(publisher.Name, "owner", TimeSpan.FromSeconds(30), 1));
        var probe = Probe.Wrap(inner);
        probe.OnCall = (method, _) => method.Name == nameof(IWorkflowOutbox.SettleDeliveryAsync)
            ? throws ? Task.FromException<bool>(new IOException("lost acknowledgement")) : Task.FromResult(false) : null;
        var count = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "ixiflow.outbox.settle_failure") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == Telemetry.PublisherName && Equals(tag.Value, publisher.Name)) Interlocked.Add(ref count, value);
        });
        listener.Start();
        var log = new TestLogger();
        using var service = Service(probe.Store, time, log, 1, publisher);
        await service.DeliverAsync(publisher, claim, default);
        Assert.Equal(1, count);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("may be delivered again"));
        Assert.Equal(OutboxDeliveryStatus.Pending, (await inner.GetDeliveryAsync(claim.Event.Id, publisher.Name))!.Status);
    }

    [Fact]
    public async Task LostLeaseCancelsThePublisherAndLogsAWarning()
    {
        var time = new FakeTimeProvider();
        var inner = new InMemoryWorkflowStateRepository(time);
        var entered = Signal();
        var publisher = new Publisher("lost", async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return OutboxPublishResult.Delivered();
        });
        await inner.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
        await OutboxStoreContract.CompleteAsync(inner);
        var claim = Assert.Single(await inner.ClaimDeliveriesAsync(publisher.Name, "owner", TimeSpan.FromSeconds(30), 1));
        var probe = Probe.Wrap(inner);
        probe.OnCall = (method, _) => method.Name == nameof(IWorkflowOutbox.RenewDeliveryLeaseAsync) ? Task.FromResult(false) : null;
        var log = new TestLogger();
        using var service = Service(probe.Store, time, log, 1, publisher);
        var delivery = service.DeliverAsync(publisher, claim, default);
        await entered.Task;
        time.Advance(TimeSpan.FromSeconds(10));
        await delivery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("Lost outbox delivery lease"));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task RenewalStopsBeforeSettlement()
    {
        var time = new FakeTimeProvider();
        var inner = new InMemoryWorkflowStateRepository(time);
        var publisher = new Publisher("renew-stop", (_, _) => Task.FromResult(OutboxPublishResult.Delivered()));
        await inner.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
        await OutboxStoreContract.CompleteAsync(inner);
        var claim = Assert.Single(await inner.ClaimDeliveriesAsync(publisher.Name, "owner", TimeSpan.FromSeconds(30), 1));
        var renewals = 0;
        var probe = Probe.Wrap(inner);
        probe.OnCall = (method, _) =>
        {
            if (method.Name == nameof(IWorkflowOutbox.SettleDeliveryAsync)) time.Advance(TimeSpan.FromSeconds(10));
            if (method.Name == nameof(IWorkflowOutbox.RenewDeliveryLeaseAsync)) Interlocked.Increment(ref renewals);
            return null;
        };
        var log = new TestLogger();
        using var service = Service(probe.Store, time, log, 1, publisher);
        await service.DeliverAsync(publisher, claim, default);
        Assert.Equal(0, renewals);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task IdlePollingMakesOneBatchClaimPerPublisherPerInterval()
    {
        var time = new FakeTimeProvider();
        var probe = Probe.Wrap(new InMemoryWorkflowStateRepository(time));
        var claims = new ConcurrentQueue<(string Name, int Limit)>();
        var secondPoll = Signal();
        probe.OnCall = (method, args) =>
        {
            if (method.Name == nameof(IWorkflowOutbox.ClaimDeliveriesAsync))
            {
                claims.Enqueue(((string)args![0]!, (int)args[3]!));
                if (claims.Count == 4) secondPoll.TrySetResult();
            }
            return null;
        };
        var first = new Publisher("first", (_, _) => Task.FromResult(OutboxPublishResult.Delivered()));
        var second = new Publisher("second", (_, _) => Task.FromResult(OutboxPublishResult.Delivered()));
        using var service = Service(probe.Store, time, new TestLogger(), 8, first, second);
        await service.StartAsync(default);
        Assert.Equal(2, claims.Count);
        Assert.All(claims, c => Assert.Equal(4, c.Limit));
        time.Advance(TimeSpan.FromSeconds(1));
        await secondPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(default);
        Assert.Equal(2, claims.Count(c => c.Name == "first"));
        Assert.Equal(2, claims.Count(c => c.Name == "second"));
    }

    [Fact]
    public async Task AClaimedBatchRunsWithinTheFreeShareAndShutdownLeavesTheBacklog()
    {
        var time = new FakeTimeProvider();
        var inner = new InMemoryWorkflowStateRepository(time);
        await inner.RegisterPublisherAsync("batch", [OutboxEvent.WorkflowCompleted]);
        for (var i = 0; i < 6; i++) await OutboxStoreContract.CompleteAsync(inner);
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        var publisher = new Publisher("batch", async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 4) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return OutboxPublishResult.Delivered();
        });
        var probe = Probe.Wrap(inner);
        var limits = new List<int>();
        probe.OnCall = (method, args) =>
        {
            if (method.Name == nameof(IWorkflowOutbox.ClaimDeliveriesAsync)) limits.Add((int)args![3]!);
            return null;
        };
        using var service = Service(probe.Store, time, new TestLogger(), 4, publisher);
        await service.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([4], limits);
        var stop = service.StopAsync(default);
        release.SetResult();
        await stop;
        Assert.Equal(4, calls);
        Assert.Equal(2, (await inner.ClaimDeliveriesAsync("batch", "next", TimeSpan.FromSeconds(30), 100)).Count);
    }

    [Fact]
    public async Task BuilderCanRequestReplayAndRetireAPublisher()
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        var published = Signal();
        var publisher = new Publisher("replay-builder", (_, _) =>
        {
            published.TrySetResult();
            return Task.FromResult(OutboxPublishResult.Delivered());
        });
        services.AddIxIFlow().AddOutboxPublisher(_ => publisher)
            .ReplayRetainedOutboxEvents(publisher.Name).UnregisterOutboxPublisher("retired");
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkflowStateRepository>();
        await store.RegisterPublisherAsync("retired", [OutboxEvent.WorkflowCompleted]);
        await OutboxStoreContract.CompleteAsync(store);
        var pending = Assert.Single(await store.ClaimDeliveriesAsync("retired", "owner", TimeSpan.FromSeconds(30), 1));
        var service = provider.GetServices<IHostedService>().OfType<WorkflowOutboxService>().Single();
        await service.StartAsync(default);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(default);
        Assert.Null(await store.GetPublisherAsync("retired"));
        Assert.Equal(OutboxDeliveryStatus.GivenUp, (await store.GetDeliveryAsync(pending.Event.Id, "retired"))!.Status);
    }

    private sealed class Publisher(string name, Func<OutboxPublishContext, CancellationToken, Task<OutboxPublishResult>> publish) : IOutboxPublisher
    {
        public string Name => name;
        public IReadOnlyCollection<string> EventTypes => [OutboxEvent.WorkflowCompleted];
        public Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery, CancellationToken cancellationToken) => publish(delivery, cancellationToken);
    }

    private sealed class TestLogger : ILogger<WorkflowOutboxService>
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    public class Probe : DispatchProxy
    {
        private IWorkflowStateRepository _inner = null!;
        public IWorkflowStateRepository Store { get; private set; } = null!;
        public Func<MethodInfo, object?[]?, object?>? OnCall { get; set; }
        public static Probe Wrap(IWorkflowStateRepository inner)
        {
            var store = Create<IWorkflowStateRepository, Probe>();
            var probe = (Probe)store;
            probe._inner = inner;
            probe.Store = store;
            return probe;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            OnCall?.Invoke(targetMethod!, args) ?? targetMethod!.Invoke(_inner, args);
    }
}
