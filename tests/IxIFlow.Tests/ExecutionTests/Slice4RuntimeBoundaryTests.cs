using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class Slice4RuntimeBoundaryTests
{
    [Fact]
    [Trait("Spec", "B-12")]
    public async Task DrainTimeoutCancelsABlockedActivityReleasesItsLeaseAndAllowsRecovery()
    {
        var store = new InMemoryWorkflowStateRepository();
        var probe = new Probe();
        var catalog = new WorkflowCatalog();
        await using var first = Services(store, probe, catalog);
        var definition = Workflow.Create<Data>("BlockedDrain").Step<Block>().Build();
        var client = first.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(definition, new Data());
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stop = first.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, probe.Starts);
        Assert.False(probe.AllowCompletion);

        var claim = await store.TryAcquireExecutionLeaseAsync(
            id, "after-drain", TimeSpan.FromMinutes(1),
            await StoreTestSupport.ClaimAsync(catalog));

        Assert.NotNull(claim);
        Assert.Equal(0, claim.CrashCount);
        Assert.True((await store.ReleaseExecutionLeaseAsync(id, "after-drain")).Released);
        probe.AllowCompletion = true;
        await using var successor = Services(store, probe, catalog);
        var result = await successor.GetRequiredService<IWorkflowClient>().RecoverAsync(id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(2, probe.Starts);
    }

    [Fact]
    [Trait("Spec", "E-07")]
    public async Task ACrashAtTheCombiningJoinCommitDoesNotApplyTheSumTwice()
    {
        var store = new MergeCrashStore();
        var catalog = new WorkflowCatalog();
        var probe = new Probe();
        await using var first = Services(store, probe, catalog);

        var definition = new WorkflowBuilder<Data, object>([], "JoinCrash")
            .Parallel(parallel => parallel
                .Do(left => left.Assign(c => c.WorkflowData.Total, _ => 2))
                .Do(right => right.Assign(c => c.WorkflowData.Total, _ => 3)))
            .WaitFor<string>("finish").Build();

        var client = first.GetRequiredService<IWorkflowClient>();
        const string id = "combining-join-crash";

        await EngineAssert.StoreFailureAsync(() => client.RunAsync(definition, new Data(), new() { InstanceId = id }));
        Assert.Single(store.FailedJoinIds);
        Assert.Equal(0, JsonSerializer.Deserialize<Data>((await store.GetWorkflowInstanceAsync(id))!.WorkflowDataJson.InlineText)!.Total);
        store.FailJoin = false;
        await using var second = Services(store, probe, catalog);
        var successor = second.GetRequiredService<IWorkflowClient>();
        var resumed = await successor.RecoverAsync(id);
        Assert.Equal(WorkflowExecutionStatus.Suspended, resumed.Status);
        Assert.Equal(5, Assert.IsType<Data>(resumed.WorkflowData).Total);
        var ended = await successor.ResumeAsync(id, "finish", "done");
        Assert.Equal(WorkflowExecutionStatus.Success, ended.Status);
        Assert.Equal(5, Assert.IsType<Data>(ended.WorkflowData).Total);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, Probe probe, IWorkflowCatalog catalog)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => options.DrainTimeout = TimeSpan.FromMilliseconds(40));
        services.AddSingleton(store);
        services.AddSingleton(probe);
        services.AddSingleton(catalog);

        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        [Combine<Sum<int>>] public int Total { get; set; }
    }

    public sealed class Probe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AllowCompletion;
        public int Starts;
    }

    public sealed class Block(Probe probe) : IRecoverableActivity<bool>
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Starts);
            probe.Entered.TrySetResult();

            if (probe.AllowCompletion)
                return;

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                probe.Cancelled.TrySetResult();
            }
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(
            bool state,
            IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }

    private sealed class MergeCrashStore : TestWorkflowStateRepository
    {
        public bool FailJoin = true;
        public HashSet<string> FailedJoinIds { get; } = [];

        public override Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(
            CheckpointCommit commit,
            CancellationToken cancellationToken = default)
        {
            var parts = commit.Parts.ToDictionary(part => part.Key, part => part.Json);

            if (FailJoin && parts.TryGetValue(CheckpointParts.DataKey, out var data) &&
                JsonSerializer.Deserialize<Data>(data.InlineText)!.Total == 5)
            {
                var checkpoint = ExecutionCheckpoint.ReadTrunk(commit.ReadTrunk().ExecutionStateJson);
                Assert.Empty(checkpoint.Joins);
                FailedJoinIds.Add(commit.CommitId);

                throw new IOException("Crash at the commit carrying the combined root data");
            }

            return base.CommitWorkflowInstanceAsync(commit, cancellationToken);
        }
    }
}