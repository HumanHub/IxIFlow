using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The instance row is the backlog: one runnable time, set in the same step as the save, decides
/// when any host may claim an instance (C-01, C-05). A run waiting a long retry gives its slot and
/// lease back in its commit and the host resumes it on time (C-03, C-12).
/// </summary>
[Trait("Feature", "timers")]
public sealed class ParkedRunTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait("Spec", "C-03")]
    [Trait("Spec", "C-12")]
    public async Task ALongRetryGivesItsSlotToOtherWork()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 1);
        var probe = services.GetRequiredService<ParkProbe>();
        var retrying = Client(services).RunAsync(RetryingWorkflow("ParksItsRetry", 600), new ParkData());
        await probe.FailedOnce.Task.WaitAsync(Patience);

        // The only slot is free while the retry waits, so other work runs in between.
        var other = await Client(services).RunAsync(Workflow.Create<ParkData>("RunsMeanwhile")
            .Step<MarkActivity>().Build(), new ParkData()).WaitAsync(Patience);
        var retried = await retrying.WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Success, other.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, retried.Status);
        Assert.Equal(["failed", "marked", "succeeded"], probe.Events);
    }

    [Fact]
    [Trait("Spec", "C-03")]
    public async Task AParkedRunReleasesItsLeaseInItsCommit()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var services = Services(store, slots: 4);
        var probe = services.GetRequiredService<ParkProbe>();
        var instanceId = Guid.NewGuid().ToString("N");
        var retrying = Client(services).RunAsync(RetryingWorkflow("ReleasesItsLease", 1500), new ParkData(),
            new WorkflowOptions { InstanceId = instanceId });
        await probe.FailedOnce.Task.WaitAsync(Patience);
        var parked = await WaitForAsync(async () =>
            await store.GetWorkflowInstanceAsync(instanceId) is { RunnableAtUtc: { } due } saved &&
            due > DateTime.UtcNow ? saved : null);

        // Running, due at the retry, and unowned: a claim now finds no crash to count.
        Assert.Equal(WorkflowStatus.Running, parked.Status);
        var grant = await store.TryAcquireExecutionLeaseAsync(instanceId, "probe", TimeSpan.FromSeconds(5));
        Assert.NotNull(grant);
        Assert.Equal(0, grant.CrashCount);
        Assert.True(await store.ReleaseExecutionLeaseAsync(instanceId, "probe"));
        Assert.DoesNotContain(await store.GetRunnableInstancesAsync(100), item => item.InstanceId == instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, (await retrying.WaitAsync(Patience)).Status);
    }

    [Fact]
    [Trait("Spec", "C-03")]
    public Task MemoryStoreParksAtomically() => VerifyParking(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "C-03")]
    public Task SqlStoreParksAtomically() =>
        VerifyParking(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyParking(IWorkflowStateRepository store)
    {
        var parkedId = Guid.NewGuid().ToString("N");
        var cancelledId = Guid.NewGuid().ToString("N");
        try
        {
            // A park releases the lease in its commit, and a repeated park is acknowledged.
            var parked = await RunningInstanceAsync(store, parkedId);
            var park = Park(parked.Instance, parked.Grant);
            var applied = await store.CommitWorkflowInstanceAsync(park);
            Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
            Assert.True(applied.Parked);
            var repeated = await store.CommitWorkflowInstanceAsync(park);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, repeated.Status);
            Assert.True(repeated.Parked);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == parkedId);
            var claim = await store.TryAcquireExecutionLeaseAsync(parkedId, "next", TimeSpan.FromSeconds(5));
            Assert.Equal(0, claim?.CrashCount);

            // A cancellation waiting to be taken up keeps the owner and makes the instance due now.
            var cancelled = await RunningInstanceAsync(store, cancelledId);
            Assert.True(await store.RequestCancellationAsync(cancelledId,
                new CancellationReason { ReasonCode = "operator" }));
            var refused = await store.CommitWorkflowInstanceAsync(Park(cancelled.Instance, cancelled.Grant));
            Assert.Equal(WorkflowCommitStatus.Applied, refused.Status);
            Assert.False(refused.Parked);
            Assert.Equal("operator", refused.PendingCancellation?.ReasonCode);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(cancelledId, "next", TimeSpan.FromSeconds(5)));
            Assert.True(await store.ReleaseExecutionLeaseAsync(cancelledId, "owner"));
            Assert.Contains(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == cancelledId);
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
    public Task MemoryStoreKeepsOneRunnableTime() => VerifyRunnableTimes(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "C-01")]
    [Trait("Spec", "C-05")]
    public Task SqlStoreKeepsOneRunnableTime() =>
        VerifyRunnableTimes(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    private static async Task VerifyRunnableTimes(IWorkflowStateRepository store)
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        try
        {
            // Three idle instances due long ago, earliest last; one waits only for an event.
            for (var index = 0; index < 3; index++)
                await Commit(store, new WorkflowInstance
                {
                    InstanceId = ids[index], WorkflowName = "RunnableTimes", Status = WorkflowStatus.Suspended,
                    WorkflowDataJson = "{}", RunnableAtUtc = new DateTime(2000, 1, 3 - index, 0, 0, 0, DateTimeKind.Utc)
                });
            await Commit(store, new WorkflowInstance
            {
                InstanceId = ids[3], WorkflowName = "RunnableTimes", Status = WorkflowStatus.Suspended,
                WorkflowDataJson = "{}"
            });

            // The poll takes the earliest first, no more than it asks for.
            var earliest = await store.GetRunnableInstancesAsync(2);
            Assert.Equal([ids[2], ids[1]], earliest.Select(item => item.InstanceId));
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == ids[3]);

            // A cancellation request makes an idle instance runnable in the same step.
            Assert.True(await store.RequestCancellationAsync(ids[3], new CancellationReason { ReasonCode = "stop" }));
            Assert.Contains(await store.GetRunnableInstancesAsync(1000), item => item.InstanceId == ids[3]);

            // An owned instance is not offered, however due.
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(ids[2], "owner", TimeSpan.FromSeconds(30)));
            Assert.Equal([ids[1], ids[0]], (await store.GetRunnableInstancesAsync(2)).Select(item => item.InstanceId));
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
        var grant = (await store.TryAcquireExecutionLeaseAsync(instanceId, "owner", TimeSpan.FromSeconds(30)))!;
        var instance = new WorkflowInstance
        {
            InstanceId = instanceId, WorkflowName = "Parking", Status = WorkflowStatus.Running, WorkflowDataJson = "{}"
        };
        var result = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, 0));
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        instance.Revision = result.Revision;
        return (instance, grant);
    }

    private static CheckpointCommit Park(WorkflowInstance instance, ExecutionLeaseGrant grant)
    {
        instance.RunnableAtUtc = DateTime.UtcNow.AddHours(1);
        return CheckpointCommit.Create(instance, grant.Epoch, instance.Revision, parks: true);
    }

    private static async Task Commit(IWorkflowStateRepository store, WorkflowInstance instance) =>
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0))).Status);

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

    private static ServiceProvider Services(IWorkflowStateRepository store, int slots)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow(options =>
        {
            options.Slots = slots;
            options.ParkRetriesLongerThan = TimeSpan.FromMilliseconds(100);
        });
        services.AddSingleton<ParkProbe>();
        services.AddTransient<MarkActivity>();
        services.AddTransient<FailOnceActivity>();
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
