using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Each owner that loses a running instance, by crashing or by losing its lease, counts one crash
/// on the instance's lease record; progress clears the count. After the configured number of
/// crashes in a row the next claim quarantines the instance at NeedsResolution without running it,
/// and only an operator release runs it again (spec B-08).
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "B-08")]
public sealed class CrashAccountingTests : IClassFixture<SqliteTestDatabase<CrashAccountingTests>>
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(500);

    // Long enough for a commit on a cold SQL Server connection under load; reclaims poll for the expiry.
    private static readonly TimeSpan SqlLease = TimeSpan.FromSeconds(2);

    [Fact]
    public Task MemoryStoreCountsLeasesLostWhileRunning()
    {
        var time = new FakeTimeProvider();
        return VerifyCrashCounting(new InMemoryWorkflowStateRepository(time), Short, time);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlStoreCountsLeasesLostWhileRunning() =>
        VerifyCrashCounting(new SqlWorkflowStateRepository(SqlTestDatabase<CrashAccountingTests>.ConnectionString), SqlLease);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlStoreCountsLeasesLostWhileRunning() =>
        VerifyCrashCounting(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CrashAccountingTests>.ConnectionString), SqlLease);

    // On a fake clock a lease expires by advancing it; on SQL Server's clock the claims poll for the expiry.

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteStoreCountsLeasesLostWhileRunning()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(
            SqliteTestDatabase<CrashAccountingTests>.ConnectionString, time);
        await VerifyCrashCounting(repository, Short, time);
    }
    private static async Task VerifyCrashCounting(IWorkflowStateRepository repository, TimeSpan lease,
        FakeTimeProvider? time = null)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = instanceId, WorkflowName = "CrashCounting", Status = WorkflowStatus.Running,
            WorkflowDataJson = "{}"
        };
        try
        {
            var first = (await repository.TryAcquireExecutionLeaseAsync(instanceId, "first", lease, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.Equal(0, first.CrashCount);
            await CommitAsync(repository, instance, first, clearsCrashCount: true);

            // The owner dies with the instance running: the next claim counts it.
            var second = await ReclaimAfterExpiryAsync(repository, instanceId, "second", lease, time);
            Assert.Equal(1, second.CrashCount);
            // Claiming an activity is not progress; the next loss counts on top.
            await CommitAsync(repository, instance, second, clearsCrashCount: false);
            var third = await ReclaimAfterExpiryAsync(repository, instanceId, "third", lease, time);
            Assert.Equal(2, third.CrashCount);

            // A released lease is not a crash.
            Assert.True((await repository.ReleaseExecutionLeaseAsync(instanceId, "third")).Released);
            var fourth = (await repository.TryAcquireExecutionLeaseAsync(instanceId, "fourth", lease, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.Equal(2, fourth.CrashCount);

            // Progress clears the count.
            await CommitAsync(repository, instance, fourth, clearsCrashCount: true);
            Assert.True((await repository.ReleaseExecutionLeaseAsync(instanceId, "fourth")).Released);
            var fifth = (await repository.TryAcquireExecutionLeaseAsync(instanceId, "fifth", lease, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.Equal(0, fifth.CrashCount);

            // An idle instance whose lease expires lost nothing in flight.
            instance.Status = WorkflowStatus.Suspended;
            await CommitAsync(repository, instance, fifth, clearsCrashCount: false);
            var sixth = (await repository.TryAcquireExecutionLeaseAsync(instanceId, "sixth", lease, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            var seventh = await ReclaimAfterExpiryAsync(repository, instanceId, "seventh", lease, time);
            Assert.Equal(0, sixth.CrashCount);
            Assert.Equal(0, seventh.CrashCount);

            // A recreated instance does not inherit crashes.
            instance.Status = WorkflowStatus.Running;
            await CommitAsync(repository, instance, seventh, clearsCrashCount: false);
            var lost = await ReclaimAfterExpiryAsync(repository, instanceId, "lost", lease, time);
            Assert.Equal(1, lost.CrashCount);
            var tombstoneRetention = TimeSpan.FromSeconds(1);
            await repository.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention: tombstoneRetention);
            Assert.Null(await repository.TryAcquireExecutionLeaseAsync(instanceId, "recreated", lease, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
            time?.Advance(tombstoneRetention);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            ExecutionLeaseGrant? recreated = null;
            while (recreated == null && elapsed.Elapsed < TimeSpan.FromSeconds(3))
            {
                recreated = await repository.TryAcquireExecutionLeaseAsync(instanceId, "recreated", lease, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
                if (recreated == null)
                    await Task.Delay(50);
            }
            Assert.NotNull(recreated);
            Assert.True(recreated.Epoch > lost.Epoch);
            Assert.Equal(0, recreated.CrashCount);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    public async Task ACommitIdCannotBeReusedWithADifferentCrashCountEffect()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "CrashCountReplay",
            Status = WorkflowStatus.Running, WorkflowDataJson = "{}"
        };
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0, "same", clearsCrashCount: true))).Status);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0, "same", clearsCrashCount: false)));
    }

    [Fact]
    public async Task OnlyActivityStartsLeaveTheCountAlone()
    {
        var store = new RecordingStore();
        using var services = Services(store);
        var definition = Workflow.Create<CrashData>("CountedStarts")
            .Step<QuickActivity>()
            .Step<QuickActivity>()
            .Build();

        var result = await Client(services).RunAsync(definition, new CrashData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(2, store.Commits.Count(commit => !commit.ClearsCrashCount));
        Assert.True(store.Commits[^1].ClearsCrashCount);
    }

    [Fact]
    public async Task AnInstanceLostByEveryOwnerIsQuarantinedUntilReleased()
    {
        var time = new FakeTimeProvider();
        var store = new FaultInjectingRepository(time);
        using var services = Services(store, time);
        var instanceId = await InterruptedRunAsync(services, store);
        await CrashOwnersAsync(store, time, instanceId, 3, await StoreTestSupport.ClaimAsync(services));

        var quarantined = await Client(services).RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, quarantined.Status);
        Assert.Contains("quarantined", quarantined.ErrorMessage);
        var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
        Assert.Equal(WorkflowStatus.NeedsResolution, saved.Status);
        Assert.Null(saved.RunnableAtUtc);
        var stop = ExecutionCheckpoint.Read(saved.ExecutionStateJson).OperatorStop!;
        Assert.Equal(OperatorStopKind.Quarantine, stop.Kind);
        Assert.Equal(3, stop.CrashCount);
        Assert.DoesNotContain(await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 100)),
            instance => instance.InstanceId == instanceId);
        Assert.Equal(0, Probe(services).Runs);

        // Automatic recovery, such as a parent checking its child, never releases it.
        var recovered = await Client(services).RecoverAsync(instanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(0, Probe(services).Runs);

        var released = await Client(services).ReleaseAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, released.Status);
        Assert.Equal(1, Probe(services).Runs);
    }

    [Fact]
    public async Task AReleasedInstanceGetsTheFullNumberOfAttemptsAgain()
    {
        var time = new FakeTimeProvider();
        var store = new FaultInjectingRepository(time);
        using var services = Services(store, time);
        var instanceId = await InterruptedRunAsync(services, store);
        await CrashOwnersAsync(store, time, instanceId, 3, await StoreTestSupport.ClaimAsync(services));
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution,
            (await Client(services).RecoverAsync(instanceId)).Status);

        // The release is saved before anything runs, so it is not lost if the next owner dies too.
        store.FailCommit = _ => Probe(services).Runs > 0;
        await Assert.ThrowsAnyAsync<Exception>(() => Client(services).ReleaseAsync(instanceId));
        store.FailCommit = _ => false;
        // Two more lost owners stay under the limit only if the release started the count again.
        await CrashOwnersAsync(store, time, instanceId, 2, await StoreTestSupport.ClaimAsync(services));

        var recovered = await Client(services).RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
    }

    [Fact]
    public async Task ACancelledInstanceStoppedAgainWaitsForARelease()
    {
        var time = new FakeTimeProvider();
        var store = new FaultInjectingRepository(time);
        using var services = Services(store, time);
        // The run dies between two activities, so nothing is in flight when it is cancelled.
        var instanceId = Guid.NewGuid().ToString("N");
        var allowed = 1;
        store.FailCommit = _ => Probe(services).Runs > 0 && Interlocked.Decrement(ref allowed) < 0;
        await Assert.ThrowsAnyAsync<Exception>(() => Client(services).RunAsync(GuardedWorkflow(),
            new CrashData(), new WorkflowOptions { InstanceId = instanceId }));
        store.FailCommit = _ => false;
        await CrashOwnersAsync(store, time, instanceId, 3, await StoreTestSupport.ClaimAsync(services));
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution,
            (await Client(services).RecoverAsync(instanceId)).Status);

        // The cancellation leaves the stop durably, then its cleanup dies with its owner.
        store.FailCommit = _ => Probe(services).Cleanups > 0;
        await Assert.ThrowsAnyAsync<Exception>(() => Client(services).CancelAsync(instanceId,
            new CancellationReason { ReasonCode = "operator" }));
        store.FailCommit = _ => false;
        var unwinding = (await store.GetWorkflowInstanceAsync(instanceId))!;
        Assert.Equal(WorkflowStatus.Running, unwinding.Status);
        Assert.Null(ExecutionCheckpoint.StopOf(unwinding));
        await CrashOwnersAsync(store, time, instanceId, 3, await StoreTestSupport.ClaimAsync(services));
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution,
            (await Client(services).RecoverAsync(instanceId)).Status);

        // The request on file was already applied, so recovery does not reopen the stop.
        var recovered = await Client(services).RecoverAsync(instanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(1, Probe(services).Cleanups);

        var released = await Client(services).ReleaseAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, released.Status);
        Assert.Equal(2, Probe(services).Cleanups);
    }

    [Fact]
    public async Task AnInstanceWhoseDataCannotBeReadIsQuarantinedWithoutReadingIt()
    {
        var time = new FakeTimeProvider();
        var store = new FaultInjectingRepository(time);
        using var services = Services(store, time);
        var instanceId = await InterruptedRunAsync(services, store);
        var grant = (await store.TryAcquireExecutionLeaseAsync(instanceId, "corrupting", Short, await StoreTestSupport.ClaimAsync(services)))!;
        var corrupted = (await store.GetWorkflowInstanceAsync(instanceId))!;
        corrupted.WorkflowDataJson = "{ not json";
        await CommitAsync(store, corrupted, grant, clearsCrashCount: false);
        await CrashOwnersAsync(store, time, instanceId, 3, await StoreTestSupport.ClaimAsync(services), alreadyHeld: true);

        var quarantined = await Client(services).RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, quarantined.Status);
        Assert.Contains("quarantined", quarantined.ErrorMessage);
        Assert.Equal("{ not json", (await store.GetWorkflowInstanceAsync(instanceId))!.WorkflowDataJson);
        // A repeated start is answered without reading the data either.
        var repeated = await Client(services).RunAsync(InterruptedWorkflow(), new CrashData(),
            new WorkflowOptions { InstanceId = instanceId });
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, repeated.Status);
    }

    [Fact]
    public async Task AwaitingAQuarantinedInstanceDoesNotReadItsData()
    {
        var time = new FakeTimeProvider();
        var store = new FaultInjectingRepository(time);
        using var services = Services(store, time);
        var instanceId = await InterruptedRunAsync(services, store);
        var grant = (await store.TryAcquireExecutionLeaseAsync(instanceId, "corrupting", Short, await StoreTestSupport.ClaimAsync(services)))!;
        var corrupted = (await store.GetWorkflowInstanceAsync(instanceId))!;
        corrupted.WorkflowDataJson = "{ not json";
        await CommitAsync(store, corrupted, grant, clearsCrashCount: false);
        await CrashOwnersAsync(store, time, instanceId, 3, await StoreTestSupport.ClaimAsync(services), alreadyHeld: true);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution,
            (await Client(services).RecoverAsync(instanceId)).Status);

        var awaited = await Client(services).AwaitAsync(instanceId).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, awaited.Status);
        Assert.Contains("quarantined", awaited.ErrorMessage);
        Assert.Null(awaited.WorkflowData);
    }

    // Runs a workflow whose commits fail once its activity has run, so it is left running with the
    // activity claimed and its lease released.
    private static async Task<string> InterruptedRunAsync(IServiceProvider services,
        FaultInjectingRepository store)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        store.FailCommit = _ => Probe(services).Runs > 0;
        await Assert.ThrowsAnyAsync<Exception>(() => Client(services).RunAsync(
            InterruptedWorkflow(), new CrashData(),
            new WorkflowOptions { InstanceId = instanceId }));
        store.FailCommit = _ => false;
        Assert.Equal(WorkflowStatus.Running, (await store.GetWorkflowInstanceAsync(instanceId))!.Status);
        Probe(services).Runs = 0;
        return instanceId;
    }

    private static WorkflowDefinition InterruptedWorkflow() =>
        Workflow.Create<CrashData>("Interrupted").Step<CountingActivity>().Build();

    private static WorkflowDefinition GuardedWorkflow() =>
        Workflow.Create<CrashData>("InterruptedWithCleanup")
            .Step<QuickActivity>()
            .Try(body => body.Step<CountingActivity>().Step<QuickActivity>())
            .Finally(cleanup => cleanup.Step<CleanupActivity>())
            .Build();

    // Owners that claim the running instance and die without releasing it; the clock moves past each lease.
    private static async Task CrashOwnersAsync(IWorkflowStateRepository store, FakeTimeProvider time,
        string instanceId, int count, ClaimQuery claim, bool alreadyHeld = false)
    {
        for (var index = 0; index < count; index++)
        {
            if (alreadyHeld || index > 0)
                time.Advance(Short * 2);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instanceId, $"dead-{index}", Short, claim));
        }
        time.Advance(Short * 2);
    }

    // Claims the instance as soon as the lease it had has expired, which is within one lease length of the call,
    // since the lease was taken before it; a slow store is allowed three seconds more. On a fake clock the lease
    // has expired once the clock has moved one lease length.
    private static async Task<ExecutionLeaseGrant> ReclaimAfterExpiryAsync(IWorkflowStateRepository repository,
        string instanceId, string token, TimeSpan lease, FakeTimeProvider? time)
    {
        if (time != null)
        {
            time.Advance(lease);
            var reclaimed = await repository.TryAcquireExecutionLeaseAsync(instanceId, token, lease, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
            Assert.NotNull(reclaimed);
            return reclaimed;
        }
        var deadline = DateTime.UtcNow + lease + TimeSpan.FromSeconds(3);
        while (true)
        {
            if (await repository.TryAcquireExecutionLeaseAsync(instanceId, token, lease, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())) is { } grant)
                return grant;
            Assert.True(DateTime.UtcNow < deadline, "The lease did not expire on time");
            await Task.Delay(50);
        }
    }

    private static async Task CommitAsync(IWorkflowStateRepository repository, WorkflowInstance instance,
        ExecutionLeaseGrant grant, bool clearsCrashCount)
    {
        var commit = CheckpointCommit.Create(instance, grant.Epoch, instance.Revision,
            clearsCrashCount: clearsCrashCount, wakeAfter: instance.Status == WorkflowStatus.Suspended ? TimeSpan.Zero : null);
        var result = await repository.CommitWorkflowInstanceAsync(commit);
        // A store that wrote nothing, for example as a deadlock victim, asks for the same commit again.
        for (var attempt = 0; result.Status == WorkflowCommitStatus.Retry && attempt < 5; attempt++)
            result = await repository.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        instance.Revision = result.Revision;
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repository);
        if (time != null)
            services.AddSingleton(time);
        services.AddIxIFlow(options => options.MaxCrashes = 3);
        services.AddSingleton<CrashProbe>();
        services.AddTransient<QuickActivity>();
        services.AddTransient<CountingActivity>();
        services.AddTransient<CleanupActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Client(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    private static CrashProbe Probe(IServiceProvider services) => services.GetRequiredService<CrashProbe>();

    public sealed class CrashData;

    public sealed class CrashProbe
    {
        private int _runs;
        private int _cleanups;

        public int Cleanups => Volatile.Read(ref _cleanups);

        public void CleanedUp() => Interlocked.Increment(ref _cleanups);

        public int Runs
        {
            get => Volatile.Read(ref _runs);
            set => Volatile.Write(ref _runs, value);
        }

        public void Ran() => Interlocked.Increment(ref _runs);
    }

    public sealed class QuickActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Counts its runs; running it again after a crash is safe.</summary>
    public sealed class CountingActivity(CrashProbe probe) : IRecoverableActivity<bool>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Ran();
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }

    /// <summary>Counts cleanup runs; running it again after a crash is safe.</summary>
    public sealed class CleanupActivity(CrashProbe probe) : IRecoverableActivity<bool>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.CleanedUp();
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }

    /// <summary>Records every commit the engine sends.</summary>
    private sealed class RecordingStore : FaultInjectingRepository
    {
        private readonly List<CheckpointCommit> _commits = [];

        public IReadOnlyList<CheckpointCommit> Commits
        {
            get
            {
                lock (_commits)
                    return _commits.ToArray();
            }
        }

        public override Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            lock (_commits)
                _commits.Add(commit);
            return base.CommitWorkflowInstanceAsync(commit);
        }
    }
}
