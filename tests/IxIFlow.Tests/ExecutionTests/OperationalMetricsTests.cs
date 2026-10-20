using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[CollectionDefinition("Operational metrics", DisableParallelization = true)]
public sealed class OperationalMetricsCollection;

[Collection("Operational metrics"), Trait("Feature", "tracing"), Trait("Spec", "G-02")]
public sealed class OperationalMetricsTests
{
    [Fact]
    public async Task BacklogAgeUsesStoreSecondsAndWorkflowTagsOncePerClaim()
    {
        using var capture = new Capture("ixiflow.backlog.age");
        var time = new FakeTimeProvider();
        var store = new FaultInjectingRepository(time);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("Backlog-" + Guid.NewGuid().ToString("N"), 7)
            .Step<Noop>().Delay(TimeSpan.FromSeconds(10)).Step<Noop>().Build();
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.RunAsync(definition, new Data(),
            new WorkflowOptions { InstanceId = id })).Status);
        time.Advance(TimeSpan.FromSeconds(22.5));
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RecoverAsync(id)).Status);
        var measurement = Assert.Single(capture.Values);
        Assert.Equal(12.5, measurement.Value);
        Assert.Equal("s", measurement.Unit);
        Assert.IsType<Histogram<double>>(measurement.Instrument);
        Assert.Equal(2, measurement.Tags.Count);
        Assert.Equal(definition.Name, measurement.Tags["ixiflow.workflow.name"]);
        Assert.Equal(7, measurement.Tags["ixiflow.workflow.version"]);
    }

    [Fact]
    public async Task LeaseLostCountsOneLossWithoutInstanceTags()
    {
        using var capture = new Capture("ixiflow.lease.lost");
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        await using var watch = new CancellationWatch(store, time);
        var loss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = await WorkflowExecutionLease.TryAcquireAsync(store,
            new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "LeaseMetric" },
            new ExecutionLeaseSettings(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)), watch, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()), error => loss.TrySetResult(error), _ => { }, _ => { }, time);
        Assert.NotNull(owner);
        time.Advance(TimeSpan.FromSeconds(31));
        await loss.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.First.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ExecutionOwnershipLostException>(owner.ThrowIfLost);
        Assert.Throws<ExecutionOwnershipLostException>(owner.ThrowIfLost);
        var measurement = Assert.Single(capture.Values);
        Assert.Equal(1, measurement.Value);
        Assert.Equal("{lease}", measurement.Unit);
        Assert.IsType<Counter<long>>(measurement.Instrument);
        Assert.Empty(measurement.Tags);
    }

    [Fact]
    public async Task OutboxAgeUsesStoreSecondsAndPublisherTagsForEveryAttempt()
    {
        using var capture = new Capture("ixiflow.outbox.age");
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var publisher = new Publisher();
        await store.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
        var instance = await OutboxStoreContract.CompleteAsync(store);
        using var service = new WorkflowOutboxService(store, [publisher],
            new HostSettings(4, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), "metric"),
            new OutboxSettings(), time, NullLogger<WorkflowOutboxService>.Instance);
        time.Advance(TimeSpan.FromSeconds(42.25));
        var first = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "first", TimeSpan.FromMinutes(1), 1));
        await service.DeliverAsync(publisher, first, default);
        time.Advance(TimeSpan.FromSeconds(5));
        var second = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "second", TimeSpan.FromMinutes(1), 1));
        await service.DeliverAsync(publisher, second, default);
        Assert.Equal(new[] { 42.25, 47.25 }, capture.Values.Select(v => v.Value));
        Assert.All(capture.Values, value =>
        {
            Assert.Equal("s", value.Unit);
            Assert.IsType<Histogram<double>>(value.Instrument);
            Assert.Equal(3, value.Tags.Count);
            Assert.Equal(instance.WorkflowName, value.Tags["ixiflow.workflow.name"]);
            Assert.Equal(instance.WorkflowVersion, value.Tags["ixiflow.workflow.version"]);
            Assert.Equal(publisher.Name, value.Tags["ixiflow.publisher.name"]);
        });
        Assert.Equal(OutboxDeliveryStatus.Delivered, (await store.GetDeliveryAsync(first.Event.Id, publisher.Name))!.Status);
    }

    private sealed record Measurement(Instrument Instrument, double Value, string? Unit, Dictionary<string, object?> Tags);
    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task First => _first.Task;
        internal readonly ConcurrentQueue<Measurement> Values = new();
        internal Capture(string name)
        {
            _listener = new MeterListener { InstrumentPublished = (instrument, listener) =>
            { if (instrument.Meter.Name == "IxIFlow" && instrument.Name == name) listener.EnableMeasurementEvents(instrument); } };
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
            _listener.Start();
        }
        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Values.Enqueue(new(instrument, value, instrument.Unit, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
            _first.TrySetResult();
        }
        public void Dispose() => _listener.Dispose();
    }
    private sealed class Publisher : IOutboxPublisher
    {
        public string Name => "metric-publisher";
        public IReadOnlyCollection<string> EventTypes => [OutboxEvent.WorkflowCompleted];
        public Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery, CancellationToken cancellationToken) =>
            Task.FromResult(delivery.Attempts == 1 ? OutboxPublishResult.RetryLater("offline") : OutboxPublishResult.Delivered());
    }
    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
