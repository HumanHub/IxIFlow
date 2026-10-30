using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Category", "SqliteIntegration")]
public sealed class SqliteHostDisposalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncHostDisposalWaitsForSqliteReadsInRunningAndLingeringScopes(bool lingering)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.SqliteDisposal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var time = new PausedReadClock();
        var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "workflows.db"), time);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddSingleton<IWorkflowStateRepository>(store);
        registrations.AddIxIFlow();
        var services = registrations.BuildServiceProvider();
        Task? read = null;
        Task? disposing = null;

        try
        {
            await store.GetWorkflowVersionsAsync();
            var host = services.GetRequiredService<WorkflowHost>();

            var running = host.RunAsync("paused-read", async (_, _, _) =>
            {
                if (lingering)
                {
                    read = Task.Run(async () =>
                    {
                        Assert.True(host.TryRunInline((_, _, _) => ReadAsync(), CancellationToken.None, out var inline));
                        await inline!;
                    });

                    await time.Entered.Task.WaitAsync(Patience);
                }
                else
                    await ReadAsync();

                return true;
            }, CancellationToken.None);

            await time.Entered.Task.WaitAsync(Patience);

            if (lingering)
                await running.WaitAsync(Patience);
            else
                read = running;

            // The clock pauses after the repository has opened its connection and read transaction.
            // Dispose the host before its stores; container disposal does not order them.
            disposing = ((IAsyncDisposable)host).DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            time.Resume.Set();
            await disposing.WaitAsync(Patience);
            await read!.WaitAsync(Patience);
            await services.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(folder, recursive: true);

            async Task<bool> ReadAsync()
            {
                time.PauseNextRead();
                await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>()));

                return true;
            }
        }
        finally
        {
            time.Resume.Set();

            if (read != null)
                await read.WaitAsync(Patience);

            if (disposing != null)
                await disposing.WaitAsync(Patience);

            await services.DisposeAsync();
            await store.DisposeAsync();

            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class PausedReadClock : TimeProvider, IDisposable
    {
        private int _pause;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new();
        public void PauseNextRead() => Interlocked.Exchange(ref _pause, 1);

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _pause, 0) == 1)
            {
                Entered.TrySetResult();

                if (!Resume.Wait(Patience))
                    throw new TimeoutException("The SQLite read was not released");
            }

            return base.GetUtcNow();
        }

        public void Dispose() => Resume.Dispose();
    }
}