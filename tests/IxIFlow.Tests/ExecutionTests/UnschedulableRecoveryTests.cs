using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "C-10")]
public sealed class UnschedulableRecoveryTests
{
    [Fact]
    public async Task CheckReportsGaugesWithOnlyNameAndVersionAndRateLimitsEachInstance()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var settings = HostSettings.Default;
        await store.RegisterMemberAsync(UnschedulableTests.Host(settings.Id, new Dictionary<string, string>(), TimeSpan.FromMinutes(20)));
        var first = await UnschedulableTests.Seed(store, [new("gpu", "cuda")]);
        var second = await UnschedulableTests.Seed(store, [new("workflow:changed@1", "old-hash")]);
        var measurements = new ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == "IxIFlow" && instrument.Name.StartsWith("ixiflow.backlog.unschedulable"))
                    meter.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();
        var log = new Warnings();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scan = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(), store, log, time, settings);
        time.Advance(TimeSpan.FromSeconds(40));
        await scan.ScanUnschedulableOnceAsync(default);
        await scan.ScanUnschedulableOnceAsync(default);
        Assert.Equal(2, log.Messages.Count);
        Assert.Contains(log.Messages, text => text.Contains(first.InstanceId) && text.Contains("gpu=cuda"));
        Assert.Contains(log.Messages, text => text.Contains(second.InstanceId) && text.Contains("workflow:changed@1=old-hash"));
        Assert.Contains(measurements, m => m.Name == "ixiflow.backlog.unschedulable" && m.Value == 2);
        Assert.Contains(measurements, m => m.Name == "ixiflow.backlog.unschedulable.age" && m.Value == 40);
        Assert.All(measurements, m =>
        {
            Assert.Equal(new[] { "ixiflow.workflow.name", "ixiflow.workflow.version" }, m.Tags.Select(t => t.Key));
            Assert.Equal("UnschedulableContract", m.Tags[0].Value);
            Assert.Equal(7, m.Tags[1].Value);
        });
        time.Advance(TimeSpan.FromMinutes(5));
        await scan.ScanUnschedulableOnceAsync(default);
        Assert.Equal(4, log.Messages.Count);
        await store.DeleteWorkflowInstanceAsync(first.InstanceId);
        await store.DeleteWorkflowInstanceAsync(second.InstanceId);
        await scan.ScanUnschedulableOnceAsync(default);
        Assert.Contains(measurements, m => m.Name == "ixiflow.backlog.unschedulable" && m.Value == 0);
        Assert.Contains(measurements, m => m.Name == "ixiflow.backlog.unschedulable.age" && m.Value == 0);
    }

    [Fact]
    public async Task OnlyTheLowestLiveHostReportsTheSharedBacklog()
    {
        var store = new InMemoryWorkflowStateRepository();
        var first = HostSettings.Default;
        var second = HostSettings.Default;
        foreach (var setting in new[] { first, second })
            await store.RegisterMemberAsync(UnschedulableTests.Host(setting.Id, new Dictionary<string, string>()));
        await UnschedulableTests.Seed(store, [new("missing")]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var log = new Warnings();
        using var a = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(), store, log, settings: first);
        using var b = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(), store, log, settings: second);
        await a.ScanUnschedulableOnceAsync(default);
        await b.ScanUnschedulableOnceAsync(default);
        Assert.Single(log.Messages);
        await store.UnregisterMemberAsync(new[] { first.Id, second.Id }.Min());
        await a.ScanUnschedulableOnceAsync(default);
        await b.ScanUnschedulableOnceAsync(default);
        Assert.Equal(2, log.Messages.Count);
    }

    [Fact]
    public async Task RecoveryRunsTheCheckOnItsOwnInterval()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var settings = HostSettings.Default with { UnschedulableScanInterval = TimeSpan.FromSeconds(30) };
        await store.RegisterMemberAsync(UnschedulableTests.Host(settings.Id, new Dictionary<string, string>(), TimeSpan.FromMinutes(20)));
        await UnschedulableTests.Seed(store, [new("missing")]);
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var log = new Warnings();
        using var scan = new WorkflowRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(), store, log, time, settings);
        await scan.StartAsync(default);
        // BackgroundService starts ExecuteAsync on the pool; advance after it has had a chance to install its timer.
        for (var i = 0; i < 10 && log.Messages.IsEmpty; i++)
        {
            await Task.Delay(10);
            time.Advance(TimeSpan.FromSeconds(30));
        }
        await log.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 100 && log.Messages.Count < 2; i++) await Task.Delay(10);
        Assert.Equal(2, log.Messages.Count);
        await scan.StopAsync(default);
    }

    [Fact]
    public void ScanIntervalMustBePositive()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new IxIFlowOptions().UnschedulableScanInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow(o => o.UnschedulableScanInterval = TimeSpan.Zero));
    }

    private sealed class Warnings : ILogger<WorkflowRecoveryService>
    {
        internal ConcurrentBag<string> Messages { get; } = [];
        internal TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            Messages.Add(formatter(state, exception));
            First.TrySetResult();
        }
    }
}
