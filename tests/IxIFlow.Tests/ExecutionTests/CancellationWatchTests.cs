using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The host's one cancellation watch (B-10): each lease has its own outage clock, a delivered
/// request ends that lease's watch, a hung read counts as an outage, one failed read does not hide
/// the others, and an applied commit reports the instance's pending request.
/// </summary>
[Trait("Feature", "host")]
public sealed class CancellationWatchTests
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
        // Later passes wait for the same read instead of queuing more behind it.
        for (var step = 0; step < 15; step++)
        {
            await Task.Delay(20);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task TheDefaultBatchReadReportsTheInstancesItCouldRead()
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
    public Task MemoryStoreReportsAPendingRequestWithACommit() =>
        VerifyCommitReportsPendingCancellation(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "B-10")]
    public Task SqlStoreReportsAPendingRequestWithACommit() =>
        VerifyCommitReportsPendingCancellation(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyCommitReportsPendingCancellation(IWorkflowStateRepository repository)
    {
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "CommitCancellation",
            Status = WorkflowStatus.Running, WorkflowDataJson = "{}"
        };
        try
        {
            var first = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0));
            Assert.Equal(WorkflowCommitStatus.Applied, first.Status);
            Assert.Null(first.PendingCancellation);

            Assert.True(await repository.RequestCancellationAsync(instance.InstanceId,
                new CancellationReason { ReasonCode = "pending" }));
            var commit = CheckpointCommit.Create(instance, 0, first.Revision);
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

    /// <summary>A store that leaves the batch read to the interface's fallback.</summary>
    private sealed class SingleReadStore : IWorkflowStateRepository
    {
        public Dictionary<string, CancellationReason> Requests { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

        public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
            Failing.Contains(instanceId)
                ? Task.FromException<CancellationReason?>(new IOException($"Cannot read {instanceId}"))
                : Task.FromResult(Requests.GetValueOrDefault(instanceId));

        public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason) =>
            throw new NotSupportedException();
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowsRequiringRecoveryAsync() =>
            throw new NotSupportedException();
        public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit) =>
            throw new NotSupportedException();
        public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            throw new NotSupportedException();
        public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            throw new NotSupportedException();
        public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
            throw new NotSupportedException();
        public Task SaveWorkflowInstanceAsync(WorkflowInstance instance) => throw new NotSupportedException();
        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
            throw new NotSupportedException();
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
            throw new NotSupportedException();
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
            throw new NotSupportedException();
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
            throw new NotSupportedException();
        public Task DeleteWorkflowInstanceAsync(string instanceId) => throw new NotSupportedException();
    }
}
