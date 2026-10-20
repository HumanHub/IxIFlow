using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Collection("Operational metrics")]
[Trait("Feature", "recovery")]
public sealed class CoordinatorNoticeLoggingTests
{
    [Fact]
    public async Task DroppedNoticeWarningsAreAggregatedOncePerInterval()
    {
        var time = new FakeTimeProvider();
        long measuredDrops = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == "IxIFlow" && instrument.Name == "ixiflow.runnable.notice.dropped")
                    owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref measuredDrops, value));
        listener.Start();
        using var provider = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        var notifier = new BlockedNotifier();
        var logger = new WarningLogger();
        using var notifications = ActivatorUtilities.CreateInstance<RunnableNotifications>(provider, notifier, logger);
        var notice = new RunnableNotice("first", DemandSet.Create([]).Id, null, null, true, false);
        notifications.Publish(notice);
        await notifier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Parallel.For(0, 3000, _ => notifications.Publish(notice));
        Assert.Single(logger.Warnings);
        time.Advance(TimeSpan.FromSeconds(29));
        notifications.Publish(notice);
        Assert.Single(logger.Warnings);
        time.Advance(TimeSpan.FromSeconds(1));
        notifications.Publish(notice);
        Assert.Equal(2, logger.Warnings.Count);
        Assert.All(logger.Warnings, values => Assert.True(Convert.ToInt64(values["DroppedCount"]) > 0));
        Assert.Equal(3002 - 256, logger.Warnings.Sum(values => Convert.ToInt64(values["DroppedCount"])));
        Assert.Equal(3002 - 256, Volatile.Read(ref measuredDrops));
        notifier.Release.TrySetResult();
    }

    private sealed class BlockedNotifier : IRunnableNotifier
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task NotifyAsync(RunnableNotice notice, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
    private sealed class WarningLogger : ILogger<RunnableNotifications>
    {
        internal readonly ConcurrentQueue<Dictionary<string, object?>> Warnings = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Enqueue(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(item => item.Key, item => item.Value));
        }
    }
}
