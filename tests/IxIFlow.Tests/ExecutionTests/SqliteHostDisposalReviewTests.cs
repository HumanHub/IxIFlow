using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Category", "SqliteIntegration")]
public sealed class SqliteHostDisposalReviewTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWaitsForAnAdmittedRunThatBecomesLingering(bool stopAsync)
    {
        var folder = NewFolder();
        using var clock = new PausedReadClock();
        await using var store = new SqliteWorkflowStateRepository(ConnectionString(folder), clock);
        var registrations = new ServiceCollection();
        registrations.AddSingleton<IWorkflowStateRepository>(store);
        registrations.AddIxIFlow();
        var provider = registrations.BuildServiceProvider();
        var finishRun = Signal();
        Task? running = null, read = null, shutdown = null;
        try
        {
            await store.GetWorkflowVersionsAsync();
            var host = provider.GetRequiredService<WorkflowHost>();
            using var watch = host.WatchSettled("becomes-lingering", out var settled);
            running = host.RunAsync("becomes-lingering", async (_, _, _) =>
            {
                read = Task.Run(async () =>
                {
                    Assert.True(host.TryRunInline(async (_, _, _) =>
                    {
                        clock.PauseNextRead();
                        await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>()));
                        return true;
                    }, CancellationToken.None, out var inline));
                    await inline!;
                });
                await clock.Entered.Task;
                await finishRun.Task;
                return true;
            }, CancellationToken.None);
            await clock.Entered.Task.WaitAsync(Patience);

            // Shutdown snapshots the admitted run before it hands its scope to the inline read.
            shutdown = stopAsync ? host.StopAsync(CancellationToken.None) : ((IAsyncDisposable)host).DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            finishRun.TrySetResult();
            await settled.WaitAsync(Patience);
            await running.WaitAsync(Patience);
            Assert.False(read!.IsCompleted);
            Assert.NotSame(shutdown, await Task.WhenAny(shutdown, Task.Delay(500)));

            clock.Resume.Set();
            await shutdown.WaitAsync(Patience);
            await provider.DisposeAsync();
            await store.DisposeAsync();
            // No independent read wait before verifying that shutdown released the connection.
            using (File.Open(Path.Combine(folder, "workflows.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Directory.Delete(folder, recursive: true);
        }
        finally
        {
            finishRun.TrySetResult();
            clock.Resume.Set();
            if (running != null) await running.WaitAsync(Patience);
            if (read != null) await read.WaitAsync(Patience);
            if (shutdown != null) await shutdown.WaitAsync(Patience);
            await provider.DisposeAsync();
            await store.DisposeAsync();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string ConnectionString(string folder) => "Data Source=" + Path.Combine(folder, "workflows.db");
    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.SqliteDisposalReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class PausedReadClock : TimeProvider, IDisposable
    {
        private int _pause;
        public TaskCompletionSource Entered { get; } = Signal();
        public ManualResetEventSlim Resume { get; } = new();
        public void PauseNextRead() => Interlocked.Exchange(ref _pause, 1);
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _pause, 0) == 1)
            {
                Entered.TrySetResult();
                if (!Resume.Wait(Patience)) throw new TimeoutException("The SQLite read was not released");
            }
            return base.GetUtcNow();
        }
        public void Dispose() => Resume.Dispose();
    }
}
