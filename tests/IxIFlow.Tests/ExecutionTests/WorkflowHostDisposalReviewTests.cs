using System.Collections.Concurrent;
using System.Diagnostics;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class WorkflowHostDisposalReviewTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericHostSynchronousDisposalDoesNotNeedTheCallingContext(bool stopFirst)
    {
        var genericHost = new HostBuilder().ConfigureServices(services =>
            services.AddIxIFlow(options => options.DrainTimeout = TimeSpan.Zero)).Build();

        var host = genericHost.Services.GetRequiredService<WorkflowHost>();
        Assert.Contains(host, genericHost.Services.GetServices<IHostedService>());
        var entered = Signal();
        var cancelled = Signal();
        var finishRun = Signal();
        var disposed = Signal();
        var context = new HeldSynchronizationContext();

        var running = host.RunAsync("context-disposal", async (_, _, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await finishRun.Task;

            return true;
        }, CancellationToken.None);

        await entered.Task.WaitAsync(Patience);

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);

            try
            {
                if (stopFirst)
                    host.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

                genericHost.Dispose();
                disposed.TrySetResult();
            }
            catch (Exception error)
            {
                disposed.TrySetException(error);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }) { IsBackground = true };

        thread.Start();

        try
        {
            // The synchronous caller is blocked in shutdown before work finishes on another thread.
            await cancelled.Task.WaitAsync(Patience);
            Assert.False(disposed.Task.IsCompleted);
            finishRun.TrySetResult();
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(context.Posts);
        }
        finally
        {
            finishRun.TrySetResult();

            // Rescue a regressed implementation so a failed test never leaves a blocked host/thread.
            context.AllowDispatch();
            await disposed.Task.WaitAsync(Patience);
            await running.WaitAsync(Patience);
            Assert.True(thread.Join(Patience));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownIsBoundedAndWarnsWithTheHostAndRunsStillReleasing(bool stopAsync)
    {
        var logger = new RecordingLogger();
        var registrations = new ServiceCollection();

        registrations.AddIxIFlow(options =>
        {
            options.HostName = "slow-release-host";
            options.Slots = 2;
            options.DrainTimeout = TimeSpan.Zero;
        });

        registrations.AddSingleton<ILogger<WorkflowHost>>(logger);
        await using var provider = registrations.BuildServiceProvider();
        var host = provider.GetRequiredService<WorkflowHost>();
        var release = Signal();
        var inlineEntered = Signal();
        var runEntered = Signal();
        Task? inline = null;
        using var watch = host.WatchSettled("lingering", out var settled);

        var lingeringRun = host.RunAsync("lingering", async (_, _, _) =>
        {
            Assert.True(host.TryRunInline(async (_, _, _) =>
            {
                inlineEntered.TrySetResult();
                await release.Task; // Deliberately ignores cancellation.

                return true;
            }, CancellationToken.None, out var call));

            inline = call;
            await inlineEntered.Task;

            return true;
        }, CancellationToken.None);

        var admittedRun = host.RunAsync("admitted", async (_, _, _) =>
        {
            runEntered.TrySetResult();
            await release.Task; // Deliberately ignores cancellation.

            return true;
        }, CancellationToken.None);

        try
        {
            await lingeringRun.WaitAsync(Patience);
            await settled.WaitAsync(Patience);
            await runEntered.Task.WaitAsync(Patience);
            var elapsed = Stopwatch.StartNew();
            var shutdown = stopAsync ? host.StopAsync(CancellationToken.None) : ((IAsyncDisposable)host).DisposeAsync().AsTask();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(9));
            Assert.False(admittedRun.IsCompleted);
            Assert.False(inline!.IsCompleted);
            var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Equal("slow-release-host", warning.Properties["HostName"]);
            Assert.Equal(2, warning.Properties["RunCount"]);
            Assert.Equal(TimeSpan.FromSeconds(10), warning.Properties["Timeout"]);
            Assert.Contains("still releasing", warning.Message);
        }
        finally
        {
            release.TrySetResult();
            await admittedRun.WaitAsync(Patience);
            await lingeringRun.WaitAsync(Patience);

            if (inline != null)
                await inline.WaitAsync(Patience);

            await ((IAsyncDisposable)host).DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentAndRepeatedDisposalsShareOneBoundedWaitAndWarning()
    {
        var logger = new RecordingLogger();
        var registrations = new ServiceCollection();
        registrations.AddIxIFlow(options => options.HostName = "double-disposal-host");
        registrations.AddSingleton<ILogger<WorkflowHost>>(logger);
        await using var provider = registrations.BuildServiceProvider();
        var host = provider.GetRequiredService<WorkflowHost>();

        // Resolve the hosted-service alias too, so DI captures the same host twice for disposal.
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<WorkflowHost>());
        Assert.Same(host, hosted);
        var entered = Signal();
        var release = Signal();
        var beginDisposal = Signal();
        using var watch = host.WatchSettled("double-disposal", out var settled);

        var running = host.RunAsync("double-disposal", async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task; // Deliberately ignores cancellation, keeping disposal at its bound.

            return true;
        }, CancellationToken.None);

        try
        {
            await entered.Task.WaitAsync(Patience);
            var first = BeginDisposalAsync(host);
            var second = BeginDisposalAsync(hosted);
            var elapsed = Stopwatch.StartNew();
            beginDisposal.TrySetResult();
            var disposals = await Task.WhenAll(first, second).WaitAsync(Patience);
            Assert.Same(disposals[0], disposals[1]);
            Assert.False(disposals[0].IsCompleted);
            await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(9));
            Assert.False(running.IsCompleted);
            var repeated = ((IAsyncDisposable)host).DisposeAsync().AsTask();
            Assert.Same(disposals[0], repeated);
            Assert.True(repeated.IsCompletedSuccessfully);
            var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Equal("double-disposal-host", warning.Properties["HostName"]);
            Assert.Equal(1, warning.Properties["RunCount"]);
            Assert.Equal(TimeSpan.FromSeconds(10), warning.Properties["Timeout"]);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(Patience);
            await settled.WaitAsync(Patience);
            await provider.DisposeAsync();
        }

        // The provider's two captures also reuse the completed disposal, without another warning.
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);

        Task<Task> BeginDisposalAsync(IAsyncDisposable service) => Task.Run(async () =>
        {
            await beginDisposal.Task;

            return service.DisposeAsync().AsTask();
        });
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class HeldSynchronizationContext : SynchronizationContext
    {
        private readonly object _lock = new();
        private bool _dispatch;
        public ConcurrentQueue<(SendOrPostCallback Callback, object? State)> Posts { get; } = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_lock)
            {
                if (!_dispatch)
                    Posts.Enqueue((callback, state));
                else
                    ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
        }

        public void AllowDispatch()
        {
            lock (_lock)
            {
                _dispatch = true;

                while (Posts.TryDequeue(out var post))
                    ThreadPool.QueueUserWorkItem(static queued => queued.Callback(queued.State), post, preferLocal: false);
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Dictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger<WorkflowHost>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue(new(logLevel,
            formatter(state, exception), ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary()));
    }
}