using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The host's one cancellation watch (B-10): each lease has its own outage clock, a delivered
/// request ends that lease's watch, a hung read counts as an outage, one failed read does not hide
/// the others, and an applied commit reports the instance's pending request.
/// </summary>
[Trait("Feature", "host")]
public sealed class CancellationWatchTests : IClassFixture<SqliteTestDatabase<CancellationWatchTests>>
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task ALeaseWatchedDuringAnOutageGetsItsOwnTolerance()
    {
        var time = new FakeTimeProvider();
        var store = new ScriptedStore { Fail = true };
        var watch = new CancellationWatch(store, time);
        var first = new Subscription();
        using var firstWatch = first.Watch(watch, "first");
        await store.WaitForReadsAsync(1);

        await TickAsync(time, store, TimeSpan.FromSeconds(2));
        var second = new Subscription();
        using var secondWatch = second.Watch(watch, "second");
        await TickAsync(time, store, TimeSpan.FromSeconds(1.5));

        Assert.True(first.IsLost);
        Assert.False(second.IsLost);
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task ADeliveredRequestEndsTheWatchSoALaterOutageIsNotALoss()
    {
        var time = new FakeTimeProvider();
        var store = new ScriptedStore { Requests = { ["cancelled"] = new CancellationReason { ReasonCode = "stop" } } };
        var watch = new CancellationWatch(store, time);
        var cancelled = new Subscription();
        var running = new Subscription();
        using var cancelledWatch = cancelled.Watch(watch, "cancelled");
        using var runningWatch = running.Watch(watch, "running");

        Assert.Equal("stop", (await cancelled.Cancellation.Task.WaitAsync(Patience)).ReasonCode);
        store.Fail = true;
        await TickAsync(time, store, Tolerance + TimeSpan.FromSeconds(1));

        Assert.False(cancelled.IsLost);
        Assert.True(running.IsLost);
        Assert.DoesNotContain("cancelled", store.LastBatch);
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task ALeaseThatAlreadyHasItsCancellationIsNotLostToAnOutage()
    {
        var time = new FakeTimeProvider();
        var store = new ScriptedStore { Fail = true };
        var watch = new CancellationWatch(store, time);
        var cancelled = new Subscription();
        var running = new Subscription();
        using var cancelledWatch = cancelled.Watch(watch, "cancelled");
        using var runningWatch = running.Watch(watch, "running");
        // The request reached this lease another way, such as a commit reply.
        cancelled.Cancellation.SetResult(new CancellationReason { ReasonCode = "stop" });
        await store.WaitForReadsAsync(1);

        await TickAsync(time, store, Tolerance + TimeSpan.FromSeconds(1));

        Assert.True(running.IsLost);
        Assert.False(cancelled.IsLost);
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task AHungReadCountsAsAnOutage()
    {
        var time = new FakeTimeProvider();
        var store = new ScriptedStore { Hang = true };
        var watch = new CancellationWatch(store, time);
        var subscription = new Subscription();
        using var registration = subscription.Watch(watch, "hung");
        await store.WaitForReadsAsync(1);

        // The read never returns; its deadline passes after the lease's tolerance.
        while (!subscription.IsLost)
        {
            await Task.Delay(20);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(time.GetUtcNow() - time.Start < TimeSpan.FromMinutes(1), "The hung read never ended");
        }
        Assert.IsType<TimeoutException>(subscription.LossError);
        Assert.Equal(1, store.Reads);
        // Out longer than any lease can wait, the stalled read is given up and a fresh one is tried;
        // each is waited for in turn rather than queued behind the other.
        for (var step = 0; step < 15; step++)
        {
            await Task.Delay(20);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.InRange(store.Reads, 2, 3);
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task ALeaseWatchedDuringAHungReadIsJudgedWhenTheReadFails()
    {
        var time = new FakeTimeProvider();
        var store = new ScriptedStore { Hang = true };
        var watch = new CancellationWatch(store, time);
        var first = new Subscription();
        using var firstWatch = first.Watch(watch, "first");
        await store.WaitForReadsAsync(1);
        await Task.Delay(20);
        time.Advance(TimeSpan.FromSeconds(1));
        var second = new Subscription();
        using var secondWatch = second.Watch(watch, "second");

        while (!first.IsLost)
        {
            await Task.Delay(20);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(time.GetUtcNow() - time.Start < TimeSpan.FromMinutes(1), "The hung read never ended");
        }

        // The second lease was not in the stalled read, but its own tolerance has passed too.
        Assert.True(second.IsLost);
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task TheBatchReadReportsTheInstancesItCouldRead()
    {
        IWorkflowStateRepository store = new SingleReadStore
        {
            Requests = { ["a"] = new CancellationReason { ReasonCode = "a" } },
            Failing = { "broken" }
        };

        var requests = await store.GetCancellationRequestsAsync(["a", "broken", "quiet"]);

        Assert.Equal("a", Assert.Single(requests).Value.ReasonCode);
        await Assert.ThrowsAsync<AggregateException>(() => store.GetCancellationRequestsAsync(["broken"]));
    }

    [Fact]
    [Trait("Spec", "B-10")]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteBatchReadReportsTheInstancesItCouldRead()
    {
        var connectionString = SqliteTestDatabase<CancellationWatchTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(connectionString);
        var readable = Guid.NewGuid().ToString("N");
        var broken = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var id in new[] { readable, broken })
            {
                await store.SeedAsync(new WorkflowInstance
                {
                    InstanceId = id, Status = WorkflowStatus.Running,
                    ExecutionStateJson = StoreTestSupport.CheckpointJson
                });
                Assert.True(await store.RequestCancellationAsync(id, new CancellationReason { ReasonCode = id }));
            }
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE ixiflow_workflow_instances SET cancellation_json = 'invalid' WHERE instance_id = @Id";
                command.Parameters.AddWithValue("@Id", broken);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            var requests = await store.GetCancellationRequestsAsync([readable, broken, "quiet", readable]);
            Assert.Equal(readable, Assert.Single(requests).Value.ReasonCode);
            await Assert.ThrowsAsync<AggregateException>(() => store.GetCancellationRequestsAsync([broken, broken]));
            Assert.Empty(await store.GetCancellationRequestsAsync([]));
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(readable);
            await store.DeleteWorkflowInstanceAsync(broken);
        }
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public Task MemoryStoreReportsAPendingRequestWithACommit() =>
        VerifyCommitReportsPendingCancellation(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "B-10")]
    public Task SqlStoreReportsAPendingRequestWithACommit() =>
        VerifyCommitReportsPendingCancellation(new SqlWorkflowStateRepository(SqlTestDatabase<CancellationWatchTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "B-10")]
    public Task PostgreSqlStoreReportsAPendingRequestWithACommit() =>
        VerifyCommitReportsPendingCancellation(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CancellationWatchTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "B-10")]
    public async Task SqliteStoreReportsAPendingRequestWithACommit()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<CancellationWatchTests>.ConnectionString);
        await VerifyCommitReportsPendingCancellation(repository);
    }

    private static async Task VerifyCommitReportsPendingCancellation(IWorkflowStateRepository repository)
    {
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "CommitCancellation",
            Status = WorkflowStatus.Running, WorkflowDataJson = "{}"
        };
        try
        {
            var first = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0));
            Assert.Equal(WorkflowCommitStatus.Applied, first.Status);
            Assert.Null(first.PendingCancellation);

            Assert.True(await repository.RequestCancellationAsync(instance.InstanceId,
                new CancellationReason { ReasonCode = "pending" }));
            var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, first.Revision);
            var applied = await repository.CommitWorkflowInstanceAsync(commit);
            var repeated = await repository.CommitWorkflowInstanceAsync(commit);

            Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
            Assert.Equal("pending", applied.PendingCancellation?.ReasonCode);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, repeated.Status);
            Assert.Equal("pending", repeated.PendingCancellation?.ReasonCode);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    // Advances fake time one interval at a time, each time until the watch has read again.
    private static async Task TickAsync(FakeTimeProvider time, ScriptedStore store, TimeSpan duration)
    {
        var end = time.GetUtcNow() + duration;
        while (time.GetUtcNow() < end)
        {
            var reads = store.Reads;
            await Task.Delay(20);
            time.Advance(Interval);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (store.Reads == reads && DateTime.UtcNow < deadline)
                await Task.Delay(5);
        }
    }

    private sealed class Subscription
    {
        private Exception? _lossError;

        public TaskCompletionSource<CancellationReason> Cancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsLost => Volatile.Read(ref _lossError) != null;
        public Exception? LossError => Volatile.Read(ref _lossError);

        public IDisposable Watch(CancellationWatch watch, string instanceId) =>
            watch.Watch(instanceId, Cancellation, error => Interlocked.CompareExchange(ref _lossError, error, null),
                Tolerance, Interval);
    }

    /// <summary>Answers batch reads from a script: requests, failures or a read that never returns.</summary>
    private sealed class ScriptedStore : FaultInjectingRepository
    {
        private int _reads;

        public Dictionary<string, CancellationReason> Requests { get; } = new(StringComparer.Ordinal);
        public volatile bool Fail;
        public volatile bool Hang;
        public int Reads => Volatile.Read(ref _reads);
        public IReadOnlyCollection<string> LastBatch { get; private set; } = [];

        public async Task WaitForReadsAsync(int count)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (Reads < count)
            {
                Assert.True(DateTime.UtcNow < deadline, "The watch did not read");
                await Task.Delay(5);
            }
        }

        public override Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
            IReadOnlyCollection<string> instanceIds)
        {
            LastBatch = instanceIds.ToArray();
            Interlocked.Increment(ref _reads);
            if (Hang)
                return new TaskCompletionSource<IReadOnlyDictionary<string, CancellationReason>>().Task;
            if (Fail)
                return Task.FromException<IReadOnlyDictionary<string, CancellationReason>>(
                    new IOException("Cancellation requests are unavailable"));
            return Task.FromResult<IReadOnlyDictionary<string, CancellationReason>>(Requests
                .Where(request => instanceIds.Contains(request.Key))
                .ToDictionary(request => request.Key, request => request.Value));
        }
    }

    /// <summary>Injects failures in individual reads of the explicit memory batch operation.</summary>
    private sealed class SingleReadStore : InMemoryWorkflowStateRepository
    {
        public Dictionary<string, CancellationReason> Requests { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);
        public override Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
            Failing.Contains(instanceId)
                ? Task.FromException<CancellationReason?>(new IOException($"Cannot read {instanceId}"))
                : Task.FromResult(Requests.GetValueOrDefault(instanceId));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "B-10")]
    [Trait("Spec", "C-11")]
    public async Task DisposalWaitsForInFlightStoreReads(bool readArrivals)
    {
        var time = new FakeTimeProvider();
        var store = new PausedWatchStore(readArrivals);
        var watch = new CancellationWatch(store, time);
        using var subscription = watch.Watch("held", new(TaskCreationOptions.RunContinuationsAsynchronously),
            _ => { }, Tolerance, Interval, arrivals: readArrivals ? _ => { } : null);
        await store.Started.Task.WaitAsync(Patience);

        var disposing = watch.DisposeAsync().AsTask();
        try
        {
            await Task.Delay(20);
            Assert.False(disposing.IsCompleted);
        }
        finally
        {
            store.Release();
        }
        await disposing.WaitAsync(Patience);
        Assert.True(disposing.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "B-10")]
    public async Task DisposalAbandonsAReadThatNeverCompletesWithinTheReadTimeout(bool readArrivals)
    {
        var time = new JoinTimeProvider();
        var store = new PausedWatchStore(readArrivals);
        var logger = new WatchLogger();
        var watch = new CancellationWatch(store, time, logger);
        using var subscription = watch.Watch("hung", new(TaskCreationOptions.RunContinuationsAsynchronously),
            _ => { }, Tolerance, Interval, arrivals: readArrivals ? _ => { } : null);
        await store.Started.Task.WaitAsync(Patience);

        await time.ReadStarted.Task.WaitAsync(Patience);
        time.Disposing = true;
        var disposing = watch.DisposeAsync().AsTask();
        await time.JoinStarted.Task.WaitAsync(Patience);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(disposing.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await disposing.WaitAsync(Patience);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Abandoned 1", warning);
        Assert.Contains(readArrivals ? "inbox sequences for hung" : "cancellation requests for hung", warning);
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task WatchAfterDisposalThrows()
    {
        var watch = new CancellationWatch(new ScriptedStore());
        await watch.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => new Subscription().Watch(watch, "disposed"));
    }

    private sealed class JoinTimeProvider : FakeTimeProvider
    {
        public bool Disposing { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource JoinStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(10))
            {
                if (Disposing)
                    JoinStarted.TrySetResult();
                else
                    ReadStarted.TrySetResult();
            }
            return timer;
        }
    }

    private sealed class WatchLogger : ILogger<CancellationWatch>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class PausedWatchStore(bool readArrivals) : FaultInjectingRepository, IWorkflowInbox
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyDictionary<string, CancellationReason>> _cancellations =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyDictionary<string, long>> _arrivals =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
            IReadOnlyCollection<string> instanceIds)
        {
            if (readArrivals)
                return Task.FromResult<IReadOnlyDictionary<string, CancellationReason>>(
                    new Dictionary<string, CancellationReason>());
            Started.TrySetResult();
            return _cancellations.Task;
        }

        Task<IReadOnlyDictionary<string, long>> IWorkflowInbox.GetInboxSequencesAsync(
            IReadOnlyCollection<string> instanceIds)
        {
            Started.TrySetResult();
            return _arrivals.Task;
        }

        public void Release()
        {
            _cancellations.TrySetResult(new Dictionary<string, CancellationReason>());
            _arrivals.TrySetResult(new Dictionary<string, long>());
        }
    }

}
