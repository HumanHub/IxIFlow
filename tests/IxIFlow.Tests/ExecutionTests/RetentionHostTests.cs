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
