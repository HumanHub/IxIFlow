using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// Faults the engine must survive the same way in every profile (spec J-03): a commit whose
/// acknowledgement is lost, an owner that commits after another took over, cancellation that
/// reaches a child, and a workflow that keeps losing its owners until it is quarantined.
/// </summary>
[Trait("Spec", "J-03")]
public sealed class FaultScenarios
{
    private static readonly TimeSpan ShortLease = TimeSpan.FromMilliseconds(500);

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ALostCommitAcknowledgementIsSentAgainAndAppliedOnce(string profile)
    {
        var counter = new CoreScenarios.ExecutionCounter();
        await using var cluster = await CreateClusterAsync(profile, counter);
        // The commit after the activity is saved, but its acknowledgement never arrives.
        var engine = cluster.AddHost(store => new FaultRepository(store)
        {
            LoseAcknowledgement = _ => counter.Count == 1
        });
        var definition = Workflow.Create<CoreScenarios.CounterData>("LostAcknowledgement")
            .Step<CoreScenarios.CountExecutionActivity>()
            .Step<CoreScenarios.NoopActivity>()
            .Build();

        var result = await engine.RunAsync(definition, new CoreScenarios.CounterData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, counter.Count);
        Assert.Equal(WorkflowStatus.Completed, (await cluster.GetInstanceAsync(result.InstanceId))!.Status);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnOwnerCannotCommitAfterAnotherTookOver(string profile)
    {
        await using var cluster = await CreateClusterAsync(profile);
        var store = cluster.NewRepository();
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Takeover", Status = WorkflowStatus.Running,
            WorkflowDataJson = "{}"
        };
        var former = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "former", ShortLease))!;
        var started = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, former.Epoch, 0));
        instance.Revision = started.Revision;
        await Task.Delay(ShortLease * 3);

        // Another owner takes over the expired lease and commits; the former owner's late commit is fenced.
        var current = (await cluster.NewRepository()
            .TryAcquireExecutionLeaseAsync(instance.InstanceId, "current", TimeSpan.FromSeconds(30)))!;
        Assert.True(current.Epoch > former.Epoch);
        var late = await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, former.Epoch, instance.Revision));

        Assert.Equal(WorkflowCommitStatus.StaleEpoch, late.Status);
        Assert.Equal(WorkflowCommitStatus.Applied, (await cluster.NewRepository().CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, current.Epoch, instance.Revision))).Status);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CancellingAParentFromAnotherHostCancelsItsWaitingChild(string profile)
    {
        await using var cluster = await CreateClusterAsync(profile);
        var engine = cluster.AddHost();
        var operatorHost = cluster.AddHost();
        var child = Workflow.Create<CoreScenarios.CounterData>("FaultChild")
            .Step<CoreScenarios.NoopActivity>()
            .WaitFor<CoreScenarios.Approval>("approval")
            .Build();
        var parent = Workflow.Create<CoreScenarios.CounterData>("FaultParent")
            .Step<CoreScenarios.NoopActivity>()
            .Invoke<CoreScenarios.CounterData>("FaultChild", 1)
            .Build();
        await cluster.Registry.RegisterWorkflowAsync(child);
        await cluster.Registry.RegisterWorkflowAsync(parent);
        var started = await engine.RunAsync(parent, new CoreScenarios.CounterData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var childId = ExecutionCheckpoint.Read((await cluster.GetInstanceAsync(started.InstanceId))!.ExecutionStateJson)
            .Continuations.Single(item => item.PendingActivity?.ChildWorkflowId != null)
            .PendingActivity!.ChildWorkflowId!;

        await cluster.UntilAsync(childId, WorkflowStatus.Suspended);

        // The parent asks its child to cancel and waits for it to end before it ends itself (D-09).
        var waiting = await operatorHost.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        var cancelled = await operatorHost.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal(WorkflowStatus.Cancelled, (await cluster.GetInstanceAsync(childId))!.Status);
    }

    [Theory]
    [Trait("Spec", "B-08")]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AWorkflowThatKeepsLosingItsOwnersIsQuarantinedUntilReleased(string profile)
    {
        var counter = new CoreScenarios.ExecutionCounter();
        await using var cluster = await CreateClusterAsync(profile, counter);
        // The first host dies after the activity ran, before its result is saved.
        var failing = new FaultRepository(cluster.NewRepository()) { FailCommit = _ => counter.Count > 0 };
        var first = cluster.AddHost(_ => failing);
        var definition = Workflow.Create<CoreScenarios.CounterData>("KeepsCrashing")
            .Step<RecoverableCountActivity>()
            .Build();
        await cluster.Registry.RegisterWorkflowAsync(definition);
        var instanceId = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAnyAsync<Exception>(() => first.RunAsync(definition, new CoreScenarios.CounterData(),
            new WorkflowOptions { InstanceId = instanceId }));
        failing.FailCommit = _ => false;

        // Three more owners claim it and vanish without releasing their lease.
        var store = cluster.NewRepository();
        for (var owner = 0; owner < 3; owner++)
        {
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instanceId, $"lost-{owner}", ShortLease));
            await Task.Delay(ShortLease * 3);
        }
        var second = cluster.AddHost();

        var quarantined = await second.RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, quarantined.Status);
        Assert.Contains("quarantined", quarantined.ErrorMessage);
        Assert.Equal(1, counter.Count);
        var released = await second.ReleaseAsync(instanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, released.Status);
        Assert.Equal(2, counter.Count);
    }

    private static Task<ConformanceCluster> CreateClusterAsync(string profile,
        CoreScenarios.ExecutionCounter? counter = null) =>
        ConformanceCluster.CreateAsync(profile, services =>
        {
            services.AddTransient<CoreScenarios.NoopActivity>();
            services.AddTransient<CoreScenarios.CountExecutionActivity>();
            services.AddTransient<RecoverableCountActivity>();
            services.AddSingleton(counter ?? new CoreScenarios.ExecutionCounter());
        });

    /// <summary>Counts its runs; running it again after a crash is safe.</summary>
    public sealed class RecoverableCountActivity(CoreScenarios.ExecutionCounter counter) : IRecoverableActivity<bool>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }
}
