using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

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
        using (var limited = Services(store, options => options.MaxActiveBranches = 3))
        {
            // The root and three branches make four.
            var stopped = await Client(limited).RunAsync(definition, new LimitData());

            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
            Assert.Contains("over the configured limit of 3", stopped.ErrorMessage);
            instanceId = stopped.InstanceId;
            var saved = (await store.GetWorkflowInstanceAsync(instanceId))!;
            var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
            Assert.Equal(OperatorStopKind.ResourceLimit, checkpoint.OperatorStop!.Kind);
            Assert.Single(checkpoint.Continuations);
            Assert.Null(saved.NextDueAtUtc);
            Assert.Equal(1, Probe(limited).Quick);
        }

        using var raised = Services(store, options => options.MaxActiveBranches = 4);
        await raised.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
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
        using (var limited = Services(store, options => options.MaxActiveBranches = 5))
        {
            // The outer fork makes three live; the inner one would make six.
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

        using var raised = Services(store, options => options.MaxActiveBranches = 6);
        await raised.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        Probe(raised).Gate.SetResult();
        var released = await Client(raised).ReleaseAsync(instanceId);

        // The branch that finished before the stop is not run again.
        Assert.Equal(WorkflowExecutionStatus.Success, released.Status);
        Assert.Equal(0, Probe(raised).Gated);
        Assert.Equal(3, Probe(raised).Quick);
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
    public async Task SqlLeasesAndCancellationKeepWorkingWhenTheMainPoolIsExhausted()
    {
        var builder = new SqlConnectionStringBuilder(SqlServerTestServer.ConnectionString)
        {
            ApplicationName = $"pool-test-{Guid.NewGuid():N}",
            MaxPoolSize = 1,
            ConnectTimeout = 2
        };
        var connectionString = builder.ConnectionString;
        var store = new SqlWorkflowStateRepository(connectionString);
        var instanceId = Guid.NewGuid().ToString("N");
        Assert.Null(await store.GetWorkflowInstanceAsync(instanceId));

        await using (var hog = new SqlConnection(connectionString))
        {
            await hog.OpenAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => store.GetWorkflowInstanceAsync(instanceId));

            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instanceId, "owner", Patience));
            Assert.True(await store.RenewExecutionLeaseAsync(instanceId, "owner", Patience));
            Assert.Empty(await store.GetCancellationRequestsAsync([instanceId]));
            Assert.Null(await store.GetCancellationRequestAsync(instanceId));
            Assert.True(await store.ReleaseExecutionLeaseAsync(instanceId, "owner"));
        }
        await store.DeleteWorkflowInstanceAsync(instanceId);
    }

    [Fact]
    public async Task ALeaseWhoseRenewalHangsIsLostWhenItMayHaveExpired()
    {
        var time = new FakeTimeProvider();
        var store = new HangingRenewalStore();
        var settings = new ExecutionLeaseSettings(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Hanging" };
        await using var lease = (await WorkflowExecutionLease.TryAcquireAsync(store, instance, settings,
            new CancellationWatch(store, time), time))!;
        store.Hang = true;

        var started = time.GetUtcNow();
        while (!lease.Loss.IsCompleted)
        {
            await Task.Delay(20);
            time.Advance(TimeSpan.FromMilliseconds(250));
            Assert.True(time.GetUtcNow() - started < TimeSpan.FromSeconds(10), "The lease was never lost");
        }

        // The hanging renewal was sent one second in, while the lease ran until three.
        Assert.True(time.GetUtcNow() - started <= TimeSpan.FromSeconds(3.5));
        Assert.IsType<TimeoutException>(((Task<Exception>)lease.Loss).Result);
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
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Client(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    private static LimitProbe Probe(IServiceProvider services) => services.GetRequiredService<LimitProbe>();

    public sealed class LimitData;

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
    }

    public sealed class QuickActivity(LimitProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.RanQuick();
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
}
