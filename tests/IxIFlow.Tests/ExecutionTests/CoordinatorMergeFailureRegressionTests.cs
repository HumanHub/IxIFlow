using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
public sealed class CoordinatorMergeFailureRegressionTests : IClassFixture<SqliteTestDatabase<CoordinatorMergeFailureRegressionTests>>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixtureClaimsMeetSavedDemandsButNeverImpersonateAPinnedHost(bool sqlite)
    {
        var time = new FakeTimeProvider();
        var store = Store(sqlite, time);
        try
        {
            var host = Guid.NewGuid();
            var demands = new WorkflowDemand[]
            {
                new("workflow:fixture:1"), new("region", "eu"), new("host", host.ToString("D"), time.GetUtcNow().UtcDateTime.AddHours(1))
            };
            var instance = Instance("fixture", demands);
            await store.SeedAsync(instance);
            var refused = new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => StoreTestSupport.LeaseAsync(store, instance.InstanceId, refused));
            await Assert.ThrowsAsync<InvalidOperationException>(() => StoreTestSupport.LeaseAsync(store, instance.InstanceId));
            var pinned = new ClaimQuery(host, new Dictionary<string, string> { ["workflow:fixture:1"] = "", ["region"] = "eu", ["host"] = host.ToString("D") });
            var saved = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance,
                (await StoreTestSupport.LeaseAsync(store, instance.InstanceId, pinned)).Epoch, instance.Revision));
            Assert.Equal(WorkflowCommitStatus.Applied, saved.Status);
            Assert.Equal(host, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.LastHostId);
            Assert.Equal(demands.OrderBy(d => d.Key), (await store.GetWorkflowDemandsAsync(instance.InstanceId)).OrderBy(d => d.Key));
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletingAChildReportsItsOwnRunnableTimeAndAFreshReplayClock(bool sqlite)
    {
        var time = new FakeTimeProvider();
        var store = Store(sqlite, time);
        try
        {
            var parent = Instance("parent");
            var child = Instance("child");
            child.ParentInstanceId = parent.InstanceId;
            child.ParentWaitId = "child-wait";
            parent.Status = WorkflowStatus.Suspended;
            var lease = await StoreTestSupport.LeaseAsync(store, parent.InstanceId);
            var started = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, lease.Epoch, 0,
                openedWaits: [new("child-wait", WaitKind.Child, "", ChildCompleted.EventType, null)],
                childStarts: [new(child.InstanceId, JsonSerializer.Serialize(child), "child-wait")]));
            Assert.Equal(child.InstanceId, Assert.Single(started.MadeRunnable).InstanceId);
            child = (await store.GetWorkflowInstanceAsync(child.InstanceId))!;
            lease = await StoreTestSupport.LeaseAsync(store, child.InstanceId);
            child.Status = WorkflowStatus.Completed;
            var commit = CheckpointCommit.Create(child, lease.Epoch, child.Revision,
                parentNotice: new(parent.InstanceId, "child-wait", "completed", ChildCompleted.EventType, "{}"));
            var result = await store.CommitWorkflowInstanceAsync(commit);
            Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
            Assert.Null(result.RunnableAtUtc);
            Assert.Equal((await store.GetWorkflowInstanceAsync(child.InstanceId))!.RunnableAtUtc, result.RunnableAtUtc);
            Assert.Equal(parent.InstanceId, Assert.Single(result.MadeRunnable).InstanceId);
            Assert.True(result.MadeRunnable[0].Claimable);
            time.Advance(TimeSpan.FromMinutes(1));
            var replay = await store.CommitWorkflowInstanceAsync(commit);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, replay.Status);
            Assert.True(replay.StoreTimeUtc > result.StoreTimeUtc);
            Assert.Equal(result.RunnableAtUtc, replay.RunnableAtUtc);
            Assert.Equal(result.Notice, replay.Notice);
            Assert.Equal(result.MadeRunnable, replay.MadeRunnable);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Fact]
    public async Task SqliteKeepsGlobalCreationOrderForSearchAndUsesDemandSetsForClaims()
    {
        var cs = SqliteTestDatabase<CoordinatorMergeFailureRegressionTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(cs);
        await store.QueryInstancesAsync(new InstanceQuery());
        await using var connection = new SqliteConnection(cs + ";Pooling=False");
        var creation = (await connection.QueryAsync<IndexColumn>("PRAGMA index_xinfo('ix_ixiflow_instances_creation')"))
            .Where(column => column.Key == 1).ToArray();
        Assert.Equal(new[] { "created_at_utc", "instance_id" }, creation.Select(column => column.Name));
        Assert.Equal(new[] { 1, 0 }, creation.Select(column => column.Desc));
        foreach (var name in new[] { "ix_ixiflow_instances_set_runnable", "ix_ixiflow_instances_set_affinity" })
            Assert.Equal("demand_set_id", (await connection.QueryAsync<IndexColumn>($"PRAGMA index_xinfo('{name}')")).First().Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakingAChildForCancellationKeepsTheParentsFuturePark(bool sqlite)
    {
        var time = new FakeTimeProvider();
        var store = Store(sqlite, time);
        try
        {
            var parent = Instance("parked-parent");
            var child = Instance("cancelled-child");
            child.ParentInstanceId = parent.InstanceId;
            child.ParentWaitId = "child-wait";
            child.Status = WorkflowStatus.Suspended;
            await store.SeedAsync(child);
            var lease = await StoreTestSupport.LeaseAsync(store, parent.InstanceId);
            var result = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, lease.Epoch, 0,
                parks: true, wakeAfter: TimeSpan.FromHours(1),
                openedWaits: [new("child-wait", WaitKind.Child, "", ChildCompleted.EventType, null)],
                childCancellations: [new(child.InstanceId, new CancellationReason { ReasonCode = "cancel-child" }, "child-wait")]));
            Assert.True(result.Parked);
            Assert.Equal(time.GetUtcNow().UtcDateTime.AddHours(1), result.RunnableAtUtc);
            Assert.Equal((await store.GetWorkflowInstanceAsync(parent.InstanceId))!.RunnableAtUtc, result.RunnableAtUtc);
            Assert.Equal(child.InstanceId, Assert.Single(result.MadeRunnable).InstanceId);
            var due = await store.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
            Assert.Contains(due, row => row.InstanceId == child.InstanceId);
            Assert.DoesNotContain(due, row => row.InstanceId == parent.InstanceId);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    private static IWorkflowStateRepository Store(bool sqlite, TimeProvider time) => sqlite
        ? new SqliteWorkflowStateRepository(SqliteTestDatabase<CoordinatorMergeFailureRegressionTests>.ConnectionString, time)
        : new InMemoryWorkflowStateRepository(time);

    private static WorkflowInstance Instance(string name, WorkflowDemand[]? demands = null) => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = name, Status = WorkflowStatus.Running,
        ExecutionStateJson = JsonSerializer.Serialize(new ExecutionCheckpoint { StructureHash = "fixture", Demands = demands?.ToList() ?? [] })
    };

    private sealed class IndexColumn
    {
        public string Name { get; set; } = "";
        public int Desc { get; set; }
        public int Key { get; set; }
    }
}
