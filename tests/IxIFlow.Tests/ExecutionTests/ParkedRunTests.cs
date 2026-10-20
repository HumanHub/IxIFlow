using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The instance row is the backlog: one runnable time, set in the same step as the save, decides
/// when any host may claim an instance (C-01, C-05). A run waiting a long retry gives its slot and
/// lease back in its commit and the host resumes it on time (C-03, C-12).
/// </summary>
[Trait("Feature", "timers")]
public sealed class ParkedRunTests : IClassFixture<SqliteTestDatabase<ParkedRunTests>>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait("Spec", "C-03")]
    [Trait("Spec", "C-12")]
    public async Task ALongRetryGivesItsSlotToOtherWork()
    {
        var time = new FakeTimeProvider();
        using var services = Services(new InMemoryWorkflowStateRepository(time), slots: 1, time);
        var probe = services.GetRequiredService<ParkProbe>();
        var retrying = Client(services).RunAsync(RetryingWorkflow("ParksItsRetry", 60_000), new ParkData());
        await probe.FailedOnce.Task.WaitAsync(Patience);

        // The only slot is free while the retry waits, so other work runs in between.
        var other = await Client(services).RunAsync(Workflow.Create<ParkData>("RunsMeanwhile")
            .Step<MarkActivity>().Build(), new ParkData()).WaitAsync(Patience);
        // Nothing runs now; the host resumes the parked retry once its minute has passed.
        time.Advance(TimeSpan.FromSeconds(61));
        var retried = await retrying.WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Success, other.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, retried.Status);
        Assert.Equal(["failed", "marked", "succeeded"], probe.Events);
    }

    [Fact]
    [Trait("Spec", "C-03")]
    public async Task AParkedRunReleasesItsLeaseInItsCommit()
    {
        var time = new FakeTimeProvider();
        var store = new LeaseRecordingStore(time);
        using var services = Services(store, slots: 4, time);
        var probe = services.GetRequiredService<ParkProbe>();
        var instanceId = Guid.NewGuid().ToString("N");
        var retrying = Client(services).RunAsync(RetryingWorkflow("ReleasesItsLease", 1500), new ParkData(),
            new WorkflowOptions { InstanceId = instanceId });
        await probe.FailedOnce.Task.WaitAsync(Patience);
        var parked = await WaitForAsync(async () =>
            await store.GetWorkflowInstanceAsync(instanceId) is { RunnableAtUtc: { } due } saved &&
            due > time.GetUtcNow().UtcDateTime ? saved : null);

        // The parking commit releases ownership, but a matching host still cannot claim before due.
        Assert.Equal(WorkflowStatus.Running, parked.Status);
        var formerToken = store.OwnerToken!;
        var query = await StoreTestSupport.ClaimAsync(services);
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(instanceId, "probe", TimeSpan.FromSeconds(5), query));
        Assert.False((await store.RenewExecutionLeasesAsync([new(instanceId, formerToken, TimeSpan.FromSeconds(5))]))[0].Status == ExecutionLeaseRenewalStatus.Renewed);
        Assert.DoesNotContain(await store.GetRunnableInstancesAsync(query), item => item.InstanceId == instanceId);

        // Past the backoff, the host resumes the retry.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(WorkflowExecutionStatus.Success, (await retrying.WaitAsync(Patience)).Status);
        Assert.Equal(0, store.LastGrant!.CrashCount);
    }

    [Fact]
    [Trait("Spec", "C-12")]
    public async Task ACancellationFromInsideARunWakesTheParkedInstanceInItsOwnSlot()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var services = Services(store, slots: 1);
        var probe = services.GetRequiredService<ParkProbe>();
        probe.Target = Guid.NewGuid().ToString("N");
        var parked = Client(services).RunAsync(RetryingWorkflow("CancelledWhileParked", 5000), new ParkData(),
            new WorkflowOptions { InstanceId = probe.Target });
        await probe.FailedOnce.Task.WaitAsync(Patience);
        await WaitForAsync(async () =>
            await store.GetWorkflowInstanceAsync(probe.Target) is { RunnableAtUtc: { } due } saved &&
            due > DateTime.UtcNow ? saved : null);

        var canceller = await Client(services).RunAsync(Workflow.Create<ParkData>("CancelsAnother")
            .Step<CancellingActivity>().Build(), new ParkData()).WaitAsync(Patience);

        // The woken instance waited for the slot the canceller held, rather than unwinding inside it.
        Assert.Equal(WorkflowExecutionStatus.Success, canceller.Status);
        Assert.Equal(WorkflowStatus.Running, probe.TargetStatusAfterCancel);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await parked.WaitAsync(Patience)).Status);
    }

    [Fact]
    [Trait("Spec", "C-03")]
    public Task MemoryStoreParksAtomically()
    {
        var time = new FakeTimeProvider();
        return VerifyParking(new InMemoryWorkflowStateRepository(time), time);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "C-03")]
    public Task SqlStoreParksAtomically() =>
        VerifyParking(new SqlWorkflowStateRepository(SqlTestDatabase<ParkedRunTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "C-03")]
    public Task PostgreSqlStoreParksAtomically() =>
        VerifyParking(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<ParkedRunTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "C-03")]
    public async Task SqliteStoreParksAtomically()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<ParkedRunTests>.ConnectionString, time);
        await VerifyParking(repository, time);
    }

    private static async Task VerifyParking(IWorkflowStateRepository store, FakeTimeProvider? time = null)
    {
        var parkedId = Guid.NewGuid().ToString("N");
        var cancelledId = Guid.NewGuid().ToString("N");
        try
        {
            // A park releases the lease in its commit, and a repeated park is acknowledged.
            var parked = await RunningInstanceAsync(store, parkedId);
            var delay = time != null ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(3);
            var park = Park(parked.Instance, parked.Grant, delay);
            var applied = await store.CommitWorkflowInstanceAsync(park);
            Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
            Assert.True(applied.Parked);
            var repeated = await store.CommitWorkflowInstanceAsync(park);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, repeated.Status);
            Assert.True(repeated.Parked);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == parkedId);
            var claim = await store.TryAcquireExecutionLeaseAsync(parkedId, "next", TimeSpan.FromSeconds(5), new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
            Assert.Null(claim);
            Assert.False((await store.RenewExecutionLeasesAsync([new(parkedId, "owner", TimeSpan.FromSeconds(5))]))[0].Status == ExecutionLeaseRenewalStatus.Renewed);
            if (time != null) time.Advance(delay);
            else await WaitForAsync(async () =>
                (await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)))
                .Any(item => item.InstanceId == parkedId) ? parked.Instance : null);
            var dueClaim = await store.TryAcquireExecutionLeaseAsync(parkedId, "next", TimeSpan.FromSeconds(5),
                new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
            Assert.NotNull(dueClaim);
            Assert.Equal(0, dueClaim.CrashCount);
            Assert.True((await store.ReleaseExecutionLeaseAsync(parkedId, "next")).Released);

            // A cancellation waiting to be taken up keeps the owner and makes the instance due now.
            var cancelled = await RunningInstanceAsync(store, cancelledId);
            Assert.True((await store.RequestCancellationAsync(cancelledId,
                new CancellationReason { ReasonCode = "operator" })).Accepted);
            var refused = await store.CommitWorkflowInstanceAsync(Park(cancelled.Instance, cancelled.Grant));
            Assert.Equal(WorkflowCommitStatus.Applied, refused.Status);
            Assert.False(refused.Parked);
            Assert.Equal("operator", refused.PendingCancellation?.ReasonCode);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(cancelledId, "next", TimeSpan.FromSeconds(5), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
            Assert.True((await store.ReleaseExecutionLeaseAsync(cancelledId, "owner")).Released);
            Assert.Contains(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == cancelledId);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(parkedId);
            await store.DeleteWorkflowInstanceAsync(cancelledId);
        }
    }

    [Fact]
    [Trait("Spec", "C-01")]
    [Trait("Spec", "C-05")]
    public Task MemoryStoreKeepsOneRunnableTime()
    {
        var time = new FakeTimeProvider();
        return VerifyRunnableTimes(new InMemoryWorkflowStateRepository(time), time);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "C-01")]
    [Trait("Spec", "C-05")]
    public Task SqlStoreKeepsOneRunnableTime() =>
        VerifyRunnableTimes(new SqlWorkflowStateRepository(SqlTestDatabase<ParkedRunTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "C-01")]
    [Trait("Spec", "C-05")]
    public Task PostgreSqlStoreKeepsOneRunnableTime() =>
        VerifyRunnableTimes(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<ParkedRunTests>.ConnectionString));

    // On a fake clock the delays end by advancing it; SQL Server's clock is polled until they have ended.

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "C-01")]
    [Trait("Spec", "C-05")]
    public async Task SqliteStoreKeepsOneRunnableTime()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(
            SqliteTestDatabase<ParkedRunTests>.ConnectionString, time);
        await VerifyRunnableTimes(repository, time);
    }
    private static async Task VerifyRunnableTimes(IWorkflowStateRepository store, FakeTimeProvider? time = null)
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        try
        {
            // Three idle instances whose delays end in the reverse order of their commits: the store
            // stamps each deadline from its own clock, so the last one committed is due first. One more
            // waits only for an event. The delays are three seconds apart, so a slow store has seconds to make the
            // commits without changing their order, and all three must be due within the longest and three more.
            var committed = DateTime.UtcNow;
            for (var index = 0; index < 3; index++)
                await Commit(store, new WorkflowInstance
                { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                    InstanceId = ids[index], WorkflowName = "RunnableTimes", Status = WorkflowStatus.Suspended,
                    WorkflowDataJson = StoredJson.Inline("{}")
                }, new OpenedWait($"{ids[index]}-delay", WaitKind.Delay, "delay", "",
                    TimeSpan.FromSeconds(7 - 3 * index)));
            await Commit(store, new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = ids[3], WorkflowName = "RunnableTimes", Status = WorkflowStatus.Suspended,
                WorkflowDataJson = StoredJson.Inline("{}")
            });
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == ids[0]);
            if (time != null)
            {
                time.Advance(TimeSpan.FromSeconds(8));
                Assert.Equal(3, (await Ours(store, ids)).Length);
            }
            else
            {
                await WaitForAsync(async () => (await Ours(store, ids)).Length == 3 ? ids : null);
                Assert.True(DateTime.UtcNow - committed < TimeSpan.FromSeconds(10), "The delays were not due on time");
            }

            // The poll takes the earliest first, no more than it asks for. Other rows of a shared store
            // may be due earlier, so only these instances' order is checked.
            Assert.Single(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1)));
            Assert.Equal([ids[2], ids[1], ids[0]], await Ours(store, ids));

            // A cancellation request makes an idle instance runnable in the same step.
            Assert.True((await store.RequestCancellationAsync(ids[3], new CancellationReason { ReasonCode = "stop" })).Accepted);
            Assert.Contains(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000)), item => item.InstanceId == ids[3]);

            // An owned instance is not offered, however due.
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(ids[2], "owner", TimeSpan.FromSeconds(30), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
            Assert.Equal([ids[1], ids[0], ids[3]], await Ours(store, ids));
        }
        finally
        {
            foreach (var id in ids)
                await store.DeleteWorkflowInstanceAsync(id);
        }
    }

    private static async Task<(WorkflowInstance Instance, ExecutionLeaseGrant Grant)> RunningInstanceAsync(
        IWorkflowStateRepository store, string instanceId)
    {
        var grant = (await store.TryAcquireExecutionLeaseAsync(instanceId, "owner", TimeSpan.FromSeconds(30), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = instanceId, WorkflowName = "Parking", Status = WorkflowStatus.Running, WorkflowDataJson = StoredJson.Inline("{}")
        };
        var result = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, 0));
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        instance.Revision = result.Revision;
        return (instance, grant);
    }

    private static CheckpointCommit Park(WorkflowInstance instance, ExecutionLeaseGrant grant, TimeSpan? delay = null) =>
        CheckpointCommit.Create(instance, grant.Epoch, instance.Revision, parks: true,
            wakeAfter: delay ?? TimeSpan.FromHours(1));

    private sealed class LeaseRecordingStore(TimeProvider time) : InMemoryWorkflowStateRepository(time), IWorkflowStateRepository
    {
        public string? OwnerToken { get; private set; }
        public ExecutionLeaseGrant? LastGrant { get; private set; }

        public new async Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
            TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable)
        {
            var grant = await base.TryAcquireExecutionLeaseAsync(instanceId, token, duration, query, kind);
            if (grant != null)
            {
                OwnerToken = token;
                LastGrant = grant;
            }
            return grant;
        }
    }

    private static async Task<string[]> Ours(IWorkflowStateRepository store, string[] ids) =>
        (await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 1000))).Select(item => item.InstanceId).Where(ids.Contains).ToArray();

    private static async Task Commit(IWorkflowStateRepository store, WorkflowInstance instance,
        params OpenedWait[] waits) =>
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0,
                openedWaits: waits))).Status);

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            if (await probe() is { } found)
                return found;
            Assert.True(DateTime.UtcNow < deadline, "The condition was not reached");
            await Task.Delay(10);
        }
    }

    private static WorkflowDefinition RetryingWorkflow(string name, int backoffMilliseconds) =>
        Workflow.Create<ParkData>(name)
            .Step<MarkActivity>()
            .Saga(saga => saga.Step<FailOnceActivity>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(new RetryPolicy(
                    TimeSpan.FromMilliseconds(backoffMilliseconds), TimeSpan.FromMilliseconds(backoffMilliseconds),
                    1, 1)))))
            .Build();

    private static ServiceProvider Services(IWorkflowStateRepository store, int slots, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        if (time != null)
            services.AddSingleton(time);
        services.AddIxIFlow(options =>
        {
            options.Slots = slots;
            options.ParkRetriesLongerThan = TimeSpan.FromMilliseconds(100);
        });
        services.AddSingleton<ParkProbe>();
        services.AddTransient<MarkActivity>();
        services.AddTransient<FailOnceActivity>();
        services.AddTransient<CancellingActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Client(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    public sealed class ParkData;

    public sealed class ParkProbe
    {
        private readonly List<string> _events = [];
        private int _marks;

        public TaskCompletionSource FailedOnce { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Target { get; set; } = "";
        public WorkflowStatus? TargetStatusAfterCancel { get; set; }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                    return _events.ToArray();
            }
        }

        public void Record(string name)
        {
            lock (_events)
                _events.Add(name);
        }

        // The retrying workflow marks once before its saga; only later marks are recorded.
        public bool CountMark() => Interlocked.Increment(ref _marks) > 1;
    }

    public sealed class MarkActivity(ParkProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (probe.CountMark())
                probe.Record("marked");
            return Task.CompletedTask;
        }
    }

    /// <summary>Cancels the probe's target from inside its own run, then holds the slot a moment.</summary>
    public sealed class CancellingActivity(ParkProbe probe, IWorkflowClient client, IWorkflowStateRepository store)
        : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            await client.CancelAsync(probe.Target, new CancellationReason { ReasonCode = "operator" },
                cancellationToken);
            probe.TargetStatusAfterCancel = (await store.GetWorkflowInstanceAsync(probe.Target))?.Status;
            await Task.Delay(200, cancellationToken);
        }
    }

    public sealed class FailOnceActivity(ParkProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (!probe.FailedOnce.Task.IsCompleted)
            {
                probe.Record("failed");
                probe.FailedOnce.TrySetResult();
                throw new InvalidOperationException("fail once");
            }
            probe.Record("succeeded");
            return Task.CompletedTask;
        }
    }
}
