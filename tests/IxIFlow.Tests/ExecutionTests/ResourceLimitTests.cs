using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A parallel step that would make too many branches live stops the instance for an operator before
/// forking, after its running activities are saved; a run keeps a bounded number of activities in
/// flight; and lease and cancellation traffic keeps working when the store is saturated (spec B-09).
/// </summary>
[Trait("Feature", "host")]
[Trait("Spec", "B-09")]
public sealed class ResourceLimitTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AParallelStepOverTheBranchLimitStopsBeforeForking()
    {
        var store = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<LimitData>("TooManyBranches")
            .Step<QuickActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<QuickActivity>())
                .Do(branch => branch.Step<QuickActivity>())
                .Do(branch => branch.Step<QuickActivity>()))
            .Build();
        string instanceId;
        using (var limited = Services(store, options => options.MaxActiveBranches = 2))
        {
            // Three branches are one over the limit; the root does not count.
            var stopped = await Client(limited).RunAsync(definition, new LimitData());

            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
            Assert.Contains("over the configured limit of 2", stopped.ErrorMessage);
            instanceId = stopped.InstanceId;
            var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
            var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
            Assert.Equal(OperatorStopKind.ResourceLimit, checkpoint.OperatorStop!.Kind);
            Assert.Single(checkpoint.Continuations);
            Assert.Null(saved.RunnableAtUtc);
            Assert.Equal(1, Probe(limited).Quick);
        }

        using var raised = Services(store, options => options.MaxActiveBranches = 3);
        await raised.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var released = await Client(raised).ReleaseAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, released.Status);
        Assert.Equal(3, Probe(raised).Quick);
    }

    [Fact]
    public async Task RunningBranchesFinishAndAreSavedBeforeTheStop()
    {
        var store = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<LimitData>("NestedTooWide")
            .Step<QuickActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.Step<QuickActivity>().Parallel(inner => inner
                    .Do(nested => nested.Step<QuickActivity>())
                    .Do(nested => nested.Step<QuickActivity>())
                    .Do(nested => nested.Step<QuickActivity>()))))
            .Build();
        string instanceId;
        using (var limited = Services(store, options => options.MaxActiveBranches = 3))
        {
            // The outer fork makes two branches live. The inner one would make four: the gated branch and
            // three nested ones; the branch that forks them waits on its join and does not count.
            var run = Client(limited).RunAsync(definition, new LimitData());
            await Probe(limited).Started.Task.WaitAsync(Patience);
            await Task.Delay(200);
            Assert.False(run.IsCompleted);

            Probe(limited).Gate.SetResult();
            var stopped = await run.WaitAsync(Patience);

            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
            instanceId = stopped.InstanceId;
            Assert.Equal(1, Probe(limited).Gated);
        }

        using var raised = Services(store, options => options.MaxActiveBranches = 4);
        await raised.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        Probe(raised).Gate.SetResult();
        var released = await Client(raised).ReleaseAsync(instanceId);

        // The branch that finished before the stop is not run again.
        Assert.Equal(WorkflowExecutionStatus.Success, released.Status);
        Assert.Equal(0, Probe(raised).Gated);
        Assert.Equal(3, Probe(raised).Quick);
    }

    [Fact]
    public async Task ACancellationWhileStoppingAtTheLimitCancelsTheInstance()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var services = Services(store, options => options.MaxActiveBranches = 3);
        var definition = Workflow.Create<LimitData>("CancelledAtTheLimit")
            .Step<QuickActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.Step<QuickActivity>().Parallel(inner => inner
                    .Do(nested => nested.Step<QuickActivity>())
                    .Do(nested => nested.Step<QuickActivity>())
                    .Do(nested => nested.Step<QuickActivity>()))))
            .Build();
        var instanceId = Guid.NewGuid().ToString("N");
        var run = Client(services).RunAsync(definition, new LimitData(), new WorkflowOptions { InstanceId = instanceId });
        await Probe(services).Started.Task.WaitAsync(Patience);
        // The other branch has run its activity and goes on to the fork that hits the limit.
        await Probe(services).WaitForQuickAsync(2);

        await Client(services).CancelAsync(instanceId, new CancellationReason { ReasonCode = "operator" });
        Probe(services).Gate.SetResult();
        var result = await run.WaitAsync(Patience);

        // The fork that hit the limit is never run, so the stop no longer applies: the gated branch and
        // three nested ones are one over the limit, and no nested branch ran.
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(2, Probe(services).Quick);
        var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
        Assert.Equal(WorkflowStatus.Cancelled, saved.Status);
        Assert.Null(ExecutionCheckpoint.StopOf(saved));
    }

    [Fact]
    public async Task ABranchThatForksDoesNotCountAgainstItsOwnFork()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), options => options.MaxActiveBranches = 1);
        var definition = Workflow.Create<LimitData>("OneLeafAtATime")
            .Step<QuickActivity>()
            .Parallel(outer => outer.Do(branch => branch
                .Parallel(inner => inner.Do(nested => nested.Step<QuickActivity>()))))
            .Build();

        // The outer branch waits on its own join once it forks, so one leaf branch is live at a time.
        var result = await Client(services).RunAsync(definition, new LimitData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(2, Probe(services).Quick);
    }

    [Fact]
    [Trait("Spec", "E-12")]
    public async Task AnItemThatCannotAdmitTheNextUnderALowerLimitCompletesAfterARelease()
    {
        var store = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<LimitData>("CappedBesideAWait")
            .Step<QuickActivity>()
            .Parallel(outer => outer
                .Do(sibling => sibling.WaitFor<Signal>("sibling"))
                .Do(list => list.ForEach(_ => new[] { 1, 2 },
                    (body, _) => body.WaitFor<Signal>("item").Step<QuickActivity>(),
                    setup => setup.Parallel(maxConcurrency: 1))))
            .Build();
        string instanceId;
        using (var roomy = Services(store, options => options.MaxActiveBranches = 10))
            instanceId = (await Client(roomy).RunAsync(definition, new LimitData())).InstanceId;

        // An operator lowers the limit: the first item cannot complete, as completing it starts the second
        // beside the waiting sibling.
        using (var lowered = Services(store, options => options.MaxActiveBranches = 1))
        {
            await lowered.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            var stopped = await Client(lowered).ResumeAsync(instanceId, "item", new Signal());
            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        }

        using var raised = Services(store, options => options.MaxActiveBranches = 10);
        await raised.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await Client(raised).ReleaseAsync(instanceId)).Status);
        Assert.Equal(WorkflowExecutionStatus.Suspended,
            (await Client(raised).ResumeAsync(instanceId, "item", new Signal())).Status);
        var finished = await Client(raised).ResumeAsync(instanceId, "sibling", new Signal());

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        // The first item's activity ran before the stop and is not run again.
        Assert.Equal(1, Probe(raised).Quick);
    }

    [Fact]
    [Trait("Spec", "E-12")]
    public async Task AnItemThatDecidesItsJoinCompletesUnderALowerLimit()
    {
        var store = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<LimitData>("FirstOfTwoBesideAWait")
            .Step<QuickActivity>()
            .Parallel(outer => outer
                .Do(sibling => sibling.WaitFor<Signal>("sibling"))
                .Do(list => list.ForEach(_ => new[] { 1, 2 },
                    (body, _) => body.WaitFor<Signal>("item").Step<QuickActivity>(),
                    setup => setup.Parallel(maxConcurrency: 1).WaitAny())))
            .Build();
        string instanceId;
        using (var roomy = Services(store, options => options.MaxActiveBranches = 10))
            instanceId = (await Client(roomy).RunAsync(definition, new LimitData())).InstanceId;

        // The first item's completion decides the join, so it starts no other item and needs no room.
        using var lowered = Services(store, options => options.MaxActiveBranches = 1);
        await lowered.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        Assert.Equal(WorkflowExecutionStatus.Suspended,
            (await Client(lowered).ResumeAsync(instanceId, "item", new Signal())).Status);
        var finished = await Client(lowered).ResumeAsync(instanceId, "sibling", new Signal());

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(1, Probe(lowered).Quick);
    }

    [Fact]
    public async Task AFreedActivityPermitGoesToTheBranchThatWaitedLongest()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(),
            options => options.MaxActivitiesPerRun = 1);
        var definition = Workflow.Create<LimitData>("FairActivities")
            .Step<QuickActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<FirstLaneActivity>().Step<FirstLaneActivity>()
                    .Step<FirstLaneActivity>().Step<FirstLaneActivity>())
                .Do(branch => branch.Step<SecondLaneActivity>()))
            .Build();

        var result = await Client(services).RunAsync(definition, new LimitData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["first", "second", "first", "first", "first"], Probe(services).Lanes);
    }

    [Fact]
    public async Task ARunKeepsAtMostTheConfiguredActivitiesInFlight()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(),
            options => options.MaxActivitiesPerRun = 2);
        var definition = Workflow.Create<LimitData>("ThrottledActivities")
            .Step<QuickActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.Step<GatedActivity>()))
            .Build();

        var run = Client(services).RunAsync(definition, new LimitData());
        var probe = Probe(services);
        await probe.WaitForGatedAsync(2);
        await Task.Delay(200);
        Assert.Equal(2, probe.Gated);

        probe.Gate.SetResult();
        var result = await run.WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(4, probe.Gated);
        Assert.Equal(2, probe.MostInFlight);
    }

    [Theory]
    [InlineData("branches")]
    [InlineData("activities")]
    public void LimitsMustBePositive(string limit)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddIxIFlow(options =>
        {
            if (limit == "branches")
                options.MaxActiveBranches = 0;
            else
                options.MaxActivitiesPerRun = 0;
        }));
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlLeasesAndCancellationKeepWorkingWhenTheMainPoolIsExhausted() => VerifyLeasesAndCancellationWithExhaustedMainPool(new SqlConnectionStringBuilder(SqlTestDatabase<ResourceLimitTests>.ConnectionString) { ApplicationName = $"pool-test-{Guid.NewGuid():N}", MaxPoolSize = 1, ConnectTimeout = 2 }.ConnectionString, connectionString => new SqlWorkflowStateRepository(connectionString), connectionString => new SqlConnection(connectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlLeasesAndCancellationKeepWorkingWhenTheMainPoolIsExhausted() => VerifyLeasesAndCancellationWithExhaustedMainPool(new NpgsqlConnectionStringBuilder(PostgreSqlTestDatabase<ResourceLimitTests>.ConnectionString) { ApplicationName = $"pool-test-{Guid.NewGuid():N}", MaxPoolSize = 1, Timeout = 2 }.ConnectionString, connectionString => new PostgreSqlWorkflowStateRepository(connectionString), connectionString => new NpgsqlConnection(connectionString));

    [Fact]
    public async Task ALeaseWhoseRenewalHangsIsLostWhenItMayHaveExpired()
    {
        var time = new FakeTimeProvider();
        var store = new HangingRenewalStore();
        var settings = new ExecutionLeaseSettings(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Hanging" };
        var loss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watch = new CancellationWatch(store, time);
        await using var lease = (await WorkflowExecutionLease.TryAcquireAsync(store, instance, settings,
            watch, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()), error => loss.TrySetResult(error), _ => { }, _ => { }, time))!;
        store.Hang = true;

        var started = time.GetUtcNow();
        while (!loss.Task.IsCompleted)
        {
            await Task.Delay(20);
            time.Advance(TimeSpan.FromMilliseconds(250));
            Assert.True(time.GetUtcNow() - started < TimeSpan.FromSeconds(10), "The lease was never lost");
        }

        // The hanging renewal was sent one second in, while the lease ran until three.
        Assert.True(time.GetUtcNow() - started <= TimeSpan.FromSeconds(3.5));
        Assert.IsType<TimeoutException>(await loss.Task);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, Action<IxIFlowOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow(configure);
        services.AddSingleton<LimitProbe>();
        services.AddTransient<QuickActivity>();
        services.AddTransient<GatedActivity>();
        services.AddTransient<FirstLaneActivity>();
        services.AddTransient<SecondLaneActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Client(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    private static LimitProbe Probe(IServiceProvider services) => services.GetRequiredService<LimitProbe>();

    public sealed class LimitData;

    public sealed class Signal;

    public sealed class LimitProbe
    {
        private int _quick;
        private int _gated;
        private int _inFlight;
        private int _mostInFlight;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Quick => Volatile.Read(ref _quick);
        public int Gated => Volatile.Read(ref _gated);
        public int MostInFlight => Volatile.Read(ref _mostInFlight);

        public void RanQuick() => Interlocked.Increment(ref _quick);

        private readonly List<string> _lanes = [];

        public IReadOnlyList<string> Lanes
        {
            get
            {
                lock (_lanes)
                    return _lanes.ToArray();
            }
        }

        public void Ran(string lane)
        {
            lock (_lanes)
                _lanes.Add(lane);
        }

        public async Task RunGatedAsync()
        {
            Interlocked.Increment(ref _gated);
            var now = Interlocked.Increment(ref _inFlight);
            int most;
            while (now > (most = Volatile.Read(ref _mostInFlight)) &&
                   Interlocked.CompareExchange(ref _mostInFlight, now, most) != most)
            {
            }
            Started.TrySetResult();
            try
            {
                await Gate.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public async Task WaitForGatedAsync(int count)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (Gated < count)
            {
                Assert.True(DateTime.UtcNow < deadline, "The activities did not start");
                await Task.Delay(10);
            }
        }

        public async Task WaitForQuickAsync(int count)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (Quick < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Only {Quick} of {count} quick activities ran");
                await Task.Delay(10);
            }
        }
    }

    public sealed class QuickActivity(LimitProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.RanQuick();
            return Task.CompletedTask;
        }
    }

    public sealed class FirstLaneActivity(LimitProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Ran("first");
            return Task.CompletedTask;
        }
    }

    public sealed class SecondLaneActivity(LimitProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Ran("second");
            return Task.CompletedTask;
        }
    }

    public sealed class GatedActivity(LimitProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            probe.RunGatedAsync();
    }

    /// <summary>Renewals never return while <see cref="Hang"/> is set.</summary>
    private sealed class HangingRenewalStore : FaultInjectingRepository
    {
        public volatile bool Hang;

        public override Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            Hang ? new TaskCompletionSource<bool>().Task : base.RenewExecutionLeaseAsync(instanceId, token, duration);
    }
    private static async Task VerifyLeasesAndCancellationWithExhaustedMainPool(string connectionString, Func<string, IWorkflowStateRepository> newRepository, Func<string, System.Data.Common.DbConnection> newConnection)
    {
        var store = newRepository(connectionString);
        var instanceId = Guid.NewGuid().ToString("N");
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(
            new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId, WorkflowName = "PoolReservation", Status = WorkflowStatus.Running,
                WorkflowDataJson = StoredJson.Inline("{}")
            }, (await StoreTestSupport.LeaseAsync(store, instanceId)).Epoch, 0))).Status);
        Assert.True((await StoreTestSupport.ReleaseAsync(store, instanceId)).Released);

        await using (var hog = newConnection(connectionString))
        {
            await hog.OpenAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => store.GetWorkflowInstanceAsync(instanceId));

            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instanceId, "owner", Patience, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
            Assert.True(await store.RenewExecutionLeaseAsync(instanceId, "owner", Patience));
            Assert.Empty((await store.GetControlRequestsAsync([instanceId])).Cancellations);
            Assert.True((await store.RequestCancellationAsync(instanceId,
                new CancellationReason { ReasonCode = "operator" })).Accepted);
            Assert.Equal("operator", (await store.GetCancellationRequestAsync(instanceId))?.ReasonCode);
            Assert.Single((await store.GetControlRequestsAsync([instanceId])).Cancellations);
            Assert.True((await store.ReleaseExecutionLeaseAsync(instanceId, "owner")).Released);
        }
        await store.DeleteWorkflowInstanceAsync(instanceId);
    }


}
