using System.Diagnostics.Metrics;
using IxIFlow.Builders;
using IxIFlow.Tests.Infrastructure;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "D-41")]
[Trait("Spec", "D-05")]
[Trait("Spec", "H-05")]
public sealed class RetentionHostTests
{
    [Fact]
    public void DefaultsAndOverridesAreRegisteredWithTheHost()
    {
        var defaults = new IxIFlowOptions();
        Assert.Equal(TimeSpan.FromDays(30), defaults.FinishedInstanceRetention);
        Assert.Equal(TimeSpan.FromDays(90), defaults.TombstoneRetention);
        Assert.Equal(TimeSpan.FromHours(1), defaults.RetentionScanInterval);
        Assert.Equal(100, defaults.RetentionBatchSize);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options =>
        {
            options.FinishedInstanceRetention = TimeSpan.FromDays(2);
            options.TombstoneRetention = TimeSpan.FromDays(7);
            options.RetentionScanInterval = TimeSpan.FromMinutes(5);
            options.RetentionBatchSize = 4;
        });
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<RetentionSettings>();
        Assert.Equal(new RetentionSettings(TimeSpan.FromDays(2), TimeSpan.FromDays(7), TimeSpan.FromMinutes(5), 4), settings);
        Assert.Single(provider.GetServices<IHostedService>().OfType<WorkflowRetentionService>());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidRetentionSettingsAreRefused(int invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow(options =>
        {
            if (invalid == 0) options.FinishedInstanceRetention = TimeSpan.FromTicks(-1);
            if (invalid == 1) options.TombstoneRetention = TimeSpan.Zero;
            if (invalid == 2) options.RetentionScanInterval = TimeSpan.Zero;
            if (invalid == 3) options.RetentionBatchSize = 0;
        }));
    }

    [Fact]
    public async Task ScannerUsesConfiguredBatchesAndStopsOnShutdown()
    {
        var time = new FakeTimeProvider();
        var store = new ObservedStore(time);
        foreach (var id in new[] { "first", "second" })
            await store.SaveWorkflowInstanceAsync(new WorkflowInstance { InstanceId = id, WorkflowName = "HostRetention", Status = WorkflowStatus.Completed });
        using var service = new WorkflowRetentionService(store,
            new RetentionSettings(TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromHours(1), 1),
            time, NullLogger<WorkflowRetentionService>.Instance);
        await service.StartAsync(default);
        Assert.Equal(0, (await store.Passes.Reader.ReadAsync()).InstancesRemoved);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, (await store.Passes.Reader.ReadAsync()).InstancesRemoved);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, (await store.Passes.Reader.ReadAsync()).InstancesRemoved);
        await service.StopAsync(default);
        // A large fake-time jump can enqueue additional zero-work ticks before shutdown.
        while (store.Passes.Reader.TryRead(out var earlier)) Assert.Equal(0, earlier.InstancesRemoved);
        time.Advance(TimeSpan.FromHours(2));
        Assert.False(store.Passes.Reader.TryRead(out _));
        Assert.NotNull(await store.GetTombstoneAsync("first"));
        Assert.NotNull(await store.GetTombstoneAsync("second"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(599999999)]
    public void FinishedRetentionBelowOneMinuteIsRefusedAtConfiguration(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow(options =>
            options.FinishedInstanceRetention = TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void OneMinuteFinishedRetentionIsAcceptedAtConfiguration()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow(options => options.FinishedInstanceRetention = TimeSpan.FromMinutes(1));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(1), provider.GetRequiredService<RetentionSettings>().FinishedRetention);
    }

    [Fact]
    public void InvalidScanIntervalNamesTheSetting()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow(options =>
            options.RetentionScanInterval = TimeSpan.Zero));
        Assert.Equal(nameof(IxIFlowOptions.RetentionScanInterval), error.ParamName);
    }

    [Fact]
    public async Task TestDecoratorForwardsRetentionAndTombstones()
    {
        var time = new FakeTimeProvider();
        var store = new FaultInjectingRepository(time);
        await store.SaveWorkflowInstanceAsync(new WorkflowInstance
            { InstanceId = "decorated", WorkflowName = "Retention", Status = WorkflowStatus.Completed });
        time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await store.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(7), 1)).InstancesRemoved);
        var tombstone = (await store.GetTombstoneAsync("decorated"))!;
        Assert.Equal(TimeSpan.FromDays(7), tombstone.ExpiresAtUtc - tombstone.RemovedAtUtc);
    }

    [Fact]
    public async Task OnlyExplicitStartsReadTheTombstone()
    {
        var probe = OutboxServiceTests.Probe.Wrap(new InMemoryWorkflowStateRepository());
        var reads = 0;
        probe.OnCall = (method, _) =>
        {
            if (method.Name == nameof(IWorkflowStateRepository.GetTombstoneAsync)) Interlocked.Increment(ref reads);
            return null;
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(probe.Store);
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<RetentionStoreContract.ResultData>("GeneratedRetentionId")
            .Step<RetentionStoreContract.Noop>().Build();
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RunAsync(definition, new RetentionStoreContract.ResultData())).Status);
        Assert.Equal(0, reads);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RunAsync(definition, new RetentionStoreContract.ResultData(),
            new WorkflowOptions { InstanceId = Guid.NewGuid().ToString("N") })).Status);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task NeverAttemptedDeliveriesProduceAPublisherWarningAndMetric()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var publisher = Guid.NewGuid().ToString("N");
        await store.RegisterPublisherAsync(publisher, [OutboxEvent.WorkflowCompleted]);
        await store.SaveWorkflowInstanceAsync(new WorkflowInstance
            { InstanceId = "blocked", WorkflowName = "Retention", Status = WorkflowStatus.Completed });
        time.Advance(TimeSpan.FromDays(31));
        long measured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Telemetry.Name && instrument.Name == "ixiflow.retention.skipped_unattempted")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == Telemetry.PublisherName && Equals(tag.Value, publisher)) Interlocked.Add(ref measured, value);
        });
        listener.Start();
        var logger = new RetentionLogger();
        using var service = new WorkflowRetentionService(store, RetentionSettings.Default, time, logger);
        Assert.Equal(0, (await service.PurgeOnceAsync(default)).InstancesRemoved);
        Assert.Equal(1, measured);
        Assert.Contains(publisher, Assert.Single(logger.Warnings));
        var delivery = Assert.Single(await store.ClaimDeliveriesAsync(publisher, "owner", TimeSpan.FromMinutes(1), 1));
        Assert.Equal(0, (await service.PurgeOnceAsync(default)).InstancesRemoved);
        Assert.Equal(1, measured);
        Assert.Single(logger.Warnings);
        Assert.True(await store.SettleDeliveryAsync(delivery.Event.Id, publisher, "owner", OutboxPublishResult.Delivered()));
        Assert.Equal(1, (await service.PurgeOnceAsync(default)).InstancesRemoved);
        Assert.Single(logger.Warnings);
    }

    private sealed class RetentionLogger : ILogger<WorkflowRetentionService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class ObservedStore(TimeProvider time) : InMemoryWorkflowStateRepository(time), IWorkflowStateRepository
    {
        public Channel<RetentionResult> Passes { get; } = Channel.CreateUnbounded<RetentionResult>();
        async Task<RetentionResult> IWorkflowStateRepository.PurgeAsync(TimeSpan retention, TimeSpan tombstones, int size,
            CancellationToken cancellationToken)
        {
            var result = await base.PurgeAsync(retention, tombstones, size, cancellationToken);
            Passes.Writer.TryWrite(result);
            return result;
        }
    }
}
