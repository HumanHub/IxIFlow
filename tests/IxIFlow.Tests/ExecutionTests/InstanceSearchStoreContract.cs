using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Tests.ExecutionTests;

internal static class InstanceSearchStoreContract
{
    internal static async Task VerifyAsync(IWorkflowStateRepository store)
    {
        var name = "Search" + Guid.NewGuid().ToString("N");
        var created = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234560);
        var rows = new List<WorkflowInstance>();
        async Task Add(string suffix, int version = 2, WorkflowStatus status = WorkflowStatus.Suspended,
            OperatorStopKind? stop = null)
        {
            var checkpoint = ExecutionCheckpoint.ReadTrunk(StoreTestSupport.CheckpointJson);
            if (stop is { } kind) checkpoint.OperatorStop = new OperatorStopState { Kind = kind, CrashCount = 7 };
            var instance = new WorkflowInstance
            {
                InstanceId = name + suffix, WorkflowName = name, WorkflowVersion = version, Status = status,
                CreatedAt = created, CorrelationId = name, ParentInstanceId = "parent-" + name,
                WorkflowDataJson = "{\"private\":42}", ExecutionStateJson = JsonSerializer.Serialize(checkpoint)
            };
            var lease = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
            var commit = CheckpointCommit.Create(instance, lease.Epoch, 0, stopped: stop != null,
                openedWaits: [new OpenedWait(instance.InstanceId, WaitKind.Event, "approve", "approval", null)]);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
            rows.Add(instance);
        }
        try
        {
            await Add("a"); await Add("b"); await Add("c"); await Add("d", version: 3);
            await Add("e", status: WorkflowStatus.Completed);
            await Add("f", status: WorkflowStatus.NeedsResolution, stop: OperatorStopKind.Quarantine);
            await Add("g", status: WorkflowStatus.NeedsResolution);
            var query = new InstanceQuery(Statuses: [WorkflowStatus.Suspended, WorkflowStatus.Completed], WorkflowName: name,
                WorkflowVersion: 2, CorrelationId: name, ParentInstanceId: "parent-" + name,
                CreatedFrom: created, CreatedTo: created.AddSeconds(1), Limit: 2);
            var first = await store.QueryInstancesAsync(query);
            Assert.Equal(new[] { name + "a", name + "b" }, first.Items.Select(row => row.InstanceId));
            Assert.NotNull(first.NextCursor);
            Assert.All(first.Items, row => Assert.NotEqual(Guid.Empty, row.Incarnation));
            // A newer insertion cannot shift a keyset page; changing the page size keeps the cursor valid.
            created = created.AddMilliseconds(500); await Add("new");
            var second = await store.QueryInstancesAsync(query with { After = first.NextCursor, Limit = 1 });
            Assert.Equal(name + "c", Assert.Single(second.Items).InstanceId);
            var last = await store.QueryInstancesAsync(query with { After = second.NextCursor });
            Assert.Equal(name + "e", Assert.Single(last.Items).InstanceId);
            Assert.Null(last.NextCursor);
            var repeated = await store.QueryInstancesAsync(query with { After = first.NextCursor, Limit = 1 });
            Assert.Equal(second.Items, repeated.Items);
            Assert.Equal(second.NextCursor, repeated.NextCursor);
            await Assert.ThrowsAsync<ArgumentException>(() => store.QueryInstancesAsync(query with { After = "invalid" }));
            await Assert.ThrowsAsync<ArgumentException>(() => store.QueryInstancesAsync(query with { After = first.NextCursor, WorkflowVersion = 3 }));
            var held = Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(WorkflowName: name, StopKind: WorkflowStopKind.Quarantine))).Items);
            Assert.Equal(7, held.CrashCount);
            var waits = await store.GetOpenWaitsAsync(new WaitQuery(InstanceIds: [name + "a", name + "g"], Limit: int.MaxValue));
            Assert.Equal(new[] { name + "a", name + "g" }, waits.Select(wait => wait.InstanceId).Order());
            Assert.Empty(await store.GetOpenWaitsAsync(new WaitQuery(InstanceIds: [])));
            Assert.Single(await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: name + "a", InstanceIds: [name + "a", name + "g"])));
            var released = (await store.GetWorkflowInstanceAsync(held.InstanceId))!;
            var cleared = ExecutionCheckpoint.Read(released.ExecutionStateJson);
            cleared.OperatorStop = null;
            released.ExecutionStateJson = JsonSerializer.Serialize(cleared);
            released.Stored = null;
            released.Status = WorkflowStatus.Suspended;
            released.WorkflowVersion = 4;
            var update = CheckpointCommit.Create(released, (await StoreTestSupport.LeaseAsync(store, released.InstanceId)).Epoch, released.Revision);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(update)).Status);
            var projected = Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(InstanceId: held.InstanceId))).Items);
            Assert.Equal(4, projected.WorkflowVersion);
            Assert.Null(projected.StopKind); Assert.Null(projected.CrashCount);
            Assert.Equal(name + "g", Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(WorkflowName: name, StopKind: WorkflowStopKind.ActivityResolution))).Items).InstanceId);
            foreach (var stop in new[] { OperatorStopKind.ResourceLimit, OperatorStopKind.Verification })
            {
                await Add(stop.ToString(), status: WorkflowStatus.NeedsResolution, stop: stop);
                Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(WorkflowName: name, StopKind: Enum.Parse<WorkflowStopKind>(stop.ToString())))).Items);
            }
            var ended = Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(WorkflowName: name, EndedFrom: DateTime.UnixEpoch, EndedTo: DateTime.UtcNow.AddDays(1)))).Items);
            Assert.Equal(WorkflowStatus.Completed, ended.Status);
            Assert.Empty((await store.QueryInstancesAsync(new InstanceQuery(WorkflowName: name, Statuses: []))).Items);
        }
        finally { foreach (var instance in rows) await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
    }
}

[Trait("Feature", "durability")]
public sealed class InstanceSearchStoreTests : IClassFixture<SqliteTestDatabase<InstanceSearchStoreTests>>
{
    [Fact]
    public Task Memory() => InstanceSearchStoreContract.VerifyAsync(new InMemoryWorkflowStateRepository());

    [Fact]
    public async Task Sqlite()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<InstanceSearchStoreTests>.ConnectionString);
        await InstanceSearchStoreContract.VerifyAsync(store);
    }

    [SqlServerFact]
    public Task SqlServer() => InstanceSearchStoreContract.VerifyAsync(new SqlWorkflowStateRepository(SqlTestDatabase<InstanceSearchStoreTests>.ConnectionString));

    [PostgreSqlFact]
    public Task PostgreSql() => InstanceSearchStoreContract.VerifyAsync(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InstanceSearchStoreTests>.ConnectionString));

    [Fact]
    public async Task SearchReadsNeitherCheckpointNorParts()
    {
        var cs = SqliteTestDatabase<InstanceSearchStoreTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(cs);
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "RowOnly", WorkflowVersion = 4,
            CreatedAt = DateTime.UtcNow, Status = WorkflowStatus.Suspended, WorkflowDataJson = "{}", ExecutionStateJson = StoreTestSupport.CheckpointJson };
        await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0));
        try
        {
            await using var connection = new SqliteConnection(cs);
            await connection.ExecuteAsync("UPDATE ixiflow_workflow_instances SET state_json='not JSON' WHERE instance_id=@InstanceId; DELETE FROM ixiflow_workflow_parts WHERE instance_id=@InstanceId", new { instance.InstanceId });
            Assert.Equal(4, Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(InstanceId: instance.InstanceId))).Items).WorkflowVersion);
        }
        finally
        {
            await using var connection = new SqliteConnection(cs);
            await connection.ExecuteAsync("UPDATE ixiflow_workflow_instances SET state_json=@Json WHERE instance_id=@InstanceId", new { instance.InstanceId, Json = JsonSerializer.Serialize(instance) });
            await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }
}
