using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "waits"), Trait("Spec", "D-02")]
public sealed class LostEventWakeScenarios
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public static IEnumerable<object[]> Deliveries => StoreProfiles.ProfileNames.Where(profile => profile != StoreProfiles.LocalSqlite)
        .SelectMany(profile => new[] { "resume", "reply", "publish" }.Select(delivery => new object[] { profile, delivery }));

    [Theory(Timeout = 60000), MemberData(nameof(Deliveries))]
    public async Task AnArrivalDuringAnAbortedOwnersReleaseIsTakenWithoutAScan(string profile, string delivery)
    {
        var activity = new ActivityGate();
        var hostNumber = 0;

        await using var cluster = await ConformanceCluster.CreateAsync(profile, services =>
        {
            services.AddSingleton(activity);
            services.AddTransient<Hold>();
            services.AddTransient<Count>();

            // Freeze only the publisher's retry timer. Its initial WakeNow must lose to the owner;
            // the release must make progress without a timer retry hiding a missing handoff.
            if (Interlocked.Increment(ref hostNumber) == 1 && delivery == "publish")
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        });

        RepositoryGate writer = null!;
        RepositoryGate owner = null!;
        var first = cluster.AddHost(inner => RepositoryGate.Wrap(inner, out writer));
        var second = cluster.AddHost(inner => RepositoryGate.Wrap(inner, out owner));

        var definition = Workflow.Create<Data>("LostEventWake")
            .Step<Hold>().WaitFor<Signal>("approval").Step<Count>().WaitFor<Signal>("approval").Build();

        await cluster.Registry.RegisterWorkflowAsync(definition);
        const string id = "lost-event-wake";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var starting = first.RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = id }, cancellation.Token);
        Task<WorkflowExecutionResult>? recovering = null;
        Task<WorkflowExecutionResult>? sending = null;
        Task<IReadOnlyList<EventDelivery>>? publishing = null;

        try
        {
            await activity.Entered.Task.WaitAsync(Patience);

            // Read a Running checkpoint, but do not claim until its owner has suspended.
            owner.HoldClaim = true;
            recovering = second.RecoverAsync(id, cancellation.Token);
            await owner.BeforeClaim.Task.WaitAsync(Patience);
            activity.Continue.TrySetResult();
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await starting.WaitAsync(Patience)).Status);

            // A first delivery makes the idle checkpoint due, so the stale recovery can claim it.
            var store = cluster.NewRepository();

            Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(id, "earlier",
                WorkflowTypeIdentity.SavedName(typeof(OtherSignal)), "{}", Key: "approval"))).Status);

            owner.ContinueClaim.TrySetResult();
            await owner.BeforeRelease.Task.WaitAsync(Patience);

            // The revision check aborted this owner without assigning an executor or committing.
            // Its lease is still live. No checkpoint will inspect the arrival below.
            var wait = Assert.Single(await first.GetOpenWaitsAsync(new(InstanceId: id), cancellation.Token));
            writer.ObserveRefusal = true;

            switch (delivery)
            {
                case "resume":
                    sending = first.ResumeAsync(id, "approval", new Signal(), "delivery-1", cancellation.Token);

                    break;
                case "reply":
                    sending = first.ReplyAsync(wait.Token, new Signal(), "delivery-1", cancellation.Token);

                    break;
                default:
                    publishing = first.PublishAsync("approval", new Signal(), "delivery-1", cancellation.Token);

                    break;
            }

            await writer.RefusedClaim.Task.WaitAsync(Patience);
            Assert.Equal(EventOutcome.Pending, (await store.GetEventAsync(id, "delivery-1"))!.Outcome);
            Assert.Equal(WorkflowStatus.Suspended, (await store.GetWorkflowInstanceAsync(id))!.Status);
            Assert.Empty(await store.GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(cluster.Registry)));
            Assert.Equal(0, activity.Executions);

            owner.ContinueRelease.TrySetResult();
            Assert.Contains("changed before recovery", (await recovering.WaitAsync(Patience)).ErrorMessage);

            // No recovery service or manual scan runs in this cluster. A publish has no waiting caller,
            // so only the release's wake can take it; a waiting resume or reply caller also retries its
            // take, so either host may decide it, and the exactly-once checks below hold either way.
            if (publishing != null)
                await owner.Reclaimed.Task.WaitAsync(Patience);

            await activity.Counted.Task.WaitAsync(Patience);

            if (sending != null)
                Assert.NotEqual(EventOutcome.Pending, (await sending.WaitAsync(Patience)).Event!.Outcome);

            if (publishing != null)
                Assert.Equal(EventAppendStatus.Stored, Assert.Single(await publishing.WaitAsync(Patience)).Status);

            await second.AwaitAsync(id, cancellationToken: cancellation.Token).WaitAsync(Patience);
            Assert.Equal(1, activity.Executions);
            Assert.Equal(EventOutcome.Rejected, (await store.GetEventAsync(id, "earlier"))!.Outcome);
            Assert.Equal(EventOutcome.Consumed, (await store.GetEventAsync(id, "delivery-1"))!.Outcome);
            Assert.Equal(1, owner.ClaimableReleases);
        }
        finally
        {
            activity.Continue.TrySetResult();
            owner.ContinueClaim.TrySetResult();
            owner.ContinueRelease.TrySetResult();
            await cancellation.CancelAsync();

            // Observe every caller before disposing the stores, including the expected red failure.
            foreach (var task in new Task?[] { starting, recovering, sending, publishing })
                if (task != null)
                {
                    try
                    {
                        await task.WaitAsync(Patience);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
        }
    }

    [Theory(Timeout = 60000), MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnArrivalAfterTheLastInboxReadSurvivesAnIdleCommitAndRelease(string profile)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile);
        var client = cluster.AddHost();
        var definition = Workflow.Create<Data>("UnreadAtRelease").Step<Noop>().WaitFor<Signal>("approval").Build();
        var idle = await client.RunAsync(definition, new Data());
        var store = cluster.NewRepository();
        var query = await StoreTestSupport.ClaimAsync(cluster.Registry);

        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(idle.InstanceId, "earlier",
            WorkflowTypeIdentity.SavedName(typeof(Signal)), "{}", Key: "approval"))).Status);

        var grant = Assert.IsType<ExecutionLeaseGrant>(await store.TryAcquireExecutionLeaseAsync(idle.InstanceId,
            "owner", TimeSpan.FromMinutes(1), query));

        var snapshot = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
        var page = await store.GetPendingEventsAsync(idle.InstanceId, 0, 100);
        Assert.Single(page.Events);

        // An event arriving after this owner's final inbox read must survive its idle checkpoint.
        var appended = await store.AppendEventAsync(new(idle.InstanceId, "delivery-1",
            WorkflowTypeIdentity.SavedName(typeof(Signal)), "{}", Key: "approval"));

        Assert.True(appended.MadeRunnable);
        Assert.Null(appended.Notice);

        var commit = CheckpointCommit.Create(snapshot, grant.Epoch, snapshot.Revision,
            inboxReadThrough: page.ReadThrough);

        var committed = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, committed.Status);
        Assert.True(committed.UnreadEvents);
        Assert.True(committed.RunnableAtUtc <= committed.StoreTimeUtc);
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(idle.InstanceId, "contender", TimeSpan.FromMinutes(1), query));
        var refused = await store.ReleaseExecutionLeaseAsync(idle.InstanceId, "wrong-token");
        Assert.False(refused.Released);
        Assert.False(refused.Claimable);
        Assert.False(refused.UnreadEvents);
        var released = await store.ReleaseExecutionLeaseAsync(idle.InstanceId, "owner");
        Assert.True(released.Released);
        Assert.True(released.Claimable);
        Assert.True(released.UnreadEvents);
        var repeated = await store.ReleaseExecutionLeaseAsync(idle.InstanceId, "owner");
        Assert.False(repeated.Released);
        Assert.False(repeated.Claimable);
        Assert.False(repeated.UnreadEvents);
        Assert.Contains(await store.GetRunnableInstancesAsync(query), item => item.InstanceId == idle.InstanceId);
        Assert.Equal(EventOutcome.Pending, (await store.GetEventAsync(idle.InstanceId, "delivery-1"))!.Outcome);
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(idle.InstanceId, "contender", TimeSpan.FromMinutes(1), query));
        Assert.True((await store.ReleaseExecutionLeaseAsync(idle.InstanceId, "contender")).Released);
    }

    public class RepositoryGate : DispatchProxy
    {
        private IWorkflowStateRepository _inner = null!;
        public bool HoldClaim;
        public bool ObserveRefusal;
        public int ClaimableReleases;
        public readonly TaskCompletionSource BeforeClaim = NewSignal();
        public readonly TaskCompletionSource ContinueClaim = NewSignal();
        public readonly TaskCompletionSource BeforeRelease = NewSignal();
        public readonly TaskCompletionSource ContinueRelease = NewSignal();
        public readonly TaskCompletionSource RefusedClaim = NewSignal();
        public readonly TaskCompletionSource Reclaimed = NewSignal();

        public static IWorkflowStateRepository Wrap(IWorkflowStateRepository inner, out RepositoryGate gate)
        {
            var proxy = Create<IWorkflowStateRepository, RepositoryGate>();
            gate = (RepositoryGate)proxy;
            gate._inner = inner;

            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync))
                return ClaimAsync(method, args);

            if (method.Name == nameof(IWorkflowStateRepository.ReleaseExecutionLeaseAsync) && HoldClaim)
                return ReleaseAsync(method, args);

            return Forward(method, args);
        }

        private object? Forward(MethodInfo method, object?[]? args)
        {
            try
            {
                return method.Invoke(_inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();

                throw;
            }
        }

        private async Task<ExecutionLeaseGrant?> ClaimAsync(MethodInfo method, object?[]? args)
        {
            if (HoldClaim)
            {
                BeforeClaim.TrySetResult();
                await ContinueClaim.Task.WaitAsync(Patience);
            }

            var grant = await (Task<ExecutionLeaseGrant?>)Forward(method, args)!;

            if (grant == null && ObserveRefusal)
                RefusedClaim.TrySetResult();

            if (grant != null && !HoldClaim && BeforeRelease.Task.IsCompleted)
                Reclaimed.TrySetResult();

            return grant;
        }

        private async Task<ExecutionLeaseReleaseResult> ReleaseAsync(MethodInfo method, object?[]? args)
        {
            BeforeRelease.TrySetResult();
            await ContinueRelease.Task.WaitAsync(Patience);
            HoldClaim = false;
            var released = await (Task<ExecutionLeaseReleaseResult>)Forward(method, args)!;

            if (released.Claimable)
                Interlocked.Increment(ref ClaimableReleases);

            return released;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class ActivityGate
    {
        public readonly TaskCompletionSource Entered = NewSignal();
        public readonly TaskCompletionSource Continue = NewSignal();
        public readonly TaskCompletionSource Counted = NewSignal();
        public int Executions;
    }

    public sealed class Data;

    public sealed class Signal;

    public sealed class OtherSignal;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Hold(ActivityGate gate) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            gate.Entered.TrySetResult();
            await gate.Continue.Task.WaitAsync(cancellationToken);
        }
    }

    public sealed class Count(ActivityGate gate) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref gate.Executions);
            gate.Counted.TrySetResult();

            return Task.CompletedTask;
        }
    }
}