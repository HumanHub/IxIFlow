using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "A-03")]
public sealed class StoreWideEpochContractTests
{
    private enum Scenario { IncreasingEpochs, ConcurrentClaims, LateFirstCommit, UnleasedIncarnation }
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Tombstones = TimeSpan.FromDays(1);

    [Fact]
    public Task MemoryEpochsIncreaseAcrossInstancesAndRecreation() => Memory(Scenario.IncreasingEpochs);
    [Fact]
    public Task MemoryConcurrentClaimsHaveDistinctEpochs() => Memory(Scenario.ConcurrentClaims);
    [Fact]
    public Task MemoryLateFirstCommitCannotUseRecreatedLease() => Memory(Scenario.LateFirstCommit);
    [Fact]
    public Task MemoryUnleasedCommitRequiresCurrentIncarnation() => Memory(Scenario.UnleasedIncarnation);

    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteEpochsIncreaseAcrossInstancesAndRecreation() => Sqlite(Scenario.IncreasingEpochs);
    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteConcurrentClaimsHaveDistinctEpochs() => Sqlite(Scenario.ConcurrentClaims);
    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteLateFirstCommitCannotUseRecreatedLease() => Sqlite(Scenario.LateFirstCommit);
    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteUnleasedCommitRequiresCurrentIncarnation() => Sqlite(Scenario.UnleasedIncarnation);

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteEpochsSurviveRepositoryRecreation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.StoreWideEpoch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var connection = "Data Source=" + Path.Combine(folder, "reopen.db");
            var id = Guid.NewGuid().ToString("N");
            long earlier;
            await using (var store = new SqliteWorkflowStateRepository(connection))
            {
                earlier = (await store.TryAcquireExecutionLeaseAsync(id, "before", Duration))!.Epoch;
                await store.DeleteWorkflowInstanceAsync(id);
            }
            await using var reopened = new SqliteWorkflowStateRepository(connection);
            var current = (await reopened.TryAcquireExecutionLeaseAsync(id, "after", Duration))!;
            Assert.True(current.Epoch > earlier);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerEpochsIncreaseAcrossInstancesAndRecreation() => SqlServer(Scenario.IncreasingEpochs);
    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerConcurrentClaimsHaveDistinctEpochs() => SqlServer(Scenario.ConcurrentClaims);
    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerLateFirstCommitCannotUseRecreatedLease() => SqlServer(Scenario.LateFirstCommit);
    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerUnleasedCommitRequiresCurrentIncarnation() => SqlServer(Scenario.UnleasedIncarnation);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlEpochsIncreaseAcrossInstancesAndRecreation() => PostgreSql(Scenario.IncreasingEpochs);
    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlConcurrentClaimsHaveDistinctEpochs() => PostgreSql(Scenario.ConcurrentClaims);
    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlLateFirstCommitCannotUseRecreatedLease() => PostgreSql(Scenario.LateFirstCommit);
    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlUnleasedCommitRequiresCurrentIncarnation() => PostgreSql(Scenario.UnleasedIncarnation);

    private static Task Memory(Scenario scenario)
    {
        var time = new FakeTimeProvider();
        return Verify(new InMemoryWorkflowStateRepository(time), scenario,
            _ => { time.Advance(Tombstones + TimeSpan.FromDays(1)); return Task.CompletedTask; });
    }

    private static async Task Sqlite(Scenario scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.StoreWideEpoch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var time = new FakeTimeProvider();
            await using var store = new SqliteWorkflowStateRepository(
                "Data Source=" + Path.Combine(folder, "epochs.db"), time);
            await Verify(store, scenario,
                _ => { time.Advance(Tombstones + TimeSpan.FromDays(1)); return Task.CompletedTask; });
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    private static Task SqlServer(Scenario scenario)
    {
        var connection = SqlTestDatabase<StoreWideEpochContractTests>.ConnectionString;
        return Verify(new SqlWorkflowStateRepository(connection), scenario,
            id => ExpireTombstone(new SqlConnection(connection), id));
    }

    private static Task PostgreSql(Scenario scenario)
    {
        var connection = PostgreSqlTestDatabase<StoreWideEpochContractTests>.ConnectionString;
        return Verify(new PostgreSqlWorkflowStateRepository(connection), scenario,
            id => ExpireTombstone(new NpgsqlConnection(connection), id));
    }

    private static async Task ExpireTombstone(DbConnection connection, string id)
    {
        await using (connection)
            await connection.ExecuteAsync(connection is SqlConnection
                ? "UPDATE dbo.IxIFlowWorkflowTombstones SET ExpiresAtUtc=DATEADD(DAY, -2, SYSUTCDATETIME()) WHERE InstanceId=@Id"
                : "UPDATE ixiflow_workflow_tombstones SET expires_at_utc=clock_timestamp() - INTERVAL '2 days' WHERE instance_id=@Id",
                new { Id = id });
    }

    private static Task Verify(IWorkflowStateRepository store, Scenario scenario, Func<string, Task> expire) =>
        scenario switch
        {
            Scenario.IncreasingEpochs => IncreasingEpochs(store, expire),
            Scenario.ConcurrentClaims => ConcurrentClaims(store),
            Scenario.LateFirstCommit => LateFirstCommit(store),
            Scenario.UnleasedIncarnation => UnleasedIncarnation(store, expire),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

    private static async Task IncreasingEpochs(IWorkflowStateRepository store, Func<string, Task> expire)
    {
        var id = Guid.NewGuid().ToString("N");
        var otherId = Guid.NewGuid().ToString("N");
        try
        {
            var first = (await store.TryAcquireExecutionLeaseAsync(id, "first", Duration))!;
            Assert.NotNull(first);
            Assert.True(first.Epoch > 0);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(Instance(id), first.Epoch, 0))).Status);
            var other = (await store.TryAcquireExecutionLeaseAsync(otherId, "other", Duration))!;
            Assert.NotNull(other);
            Assert.True(other.Epoch > first.Epoch);
            Assert.True(await store.ReleaseExecutionLeaseAsync(otherId, "other"));
            var next = (await store.TryAcquireExecutionLeaseAsync(otherId, "next", Duration))!;
            Assert.NotNull(next);
            Assert.True(next.Epoch > other.Epoch);

            await store.DeleteWorkflowInstanceAsync(id, Tombstones);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(id, "reserved", Duration));
            await expire(id);
            var recreated = (await store.TryAcquireExecutionLeaseAsync(id, "recreated", Duration))!;
            Assert.NotNull(recreated);
            Assert.All(new[] { first, other, next }, earlier => Assert.True(recreated.Epoch > earlier.Epoch));
            Assert.Equal(0, recreated.CrashCount);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(Instance(id), recreated.Epoch, 0))).Status);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(id);
            await store.DeleteWorkflowInstanceAsync(otherId);
        }
    }

    private static async Task ConcurrentClaims(IWorkflowStateRepository store)
    {
        var ids = Enumerable.Range(0, 24).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claims = ids.Select(id => Task.Run(async () =>
            {
                await start.Task;
                return await store.TryAcquireExecutionLeaseAsync(id, "owner", Duration);
            })).ToArray();
            start.SetResult();
            var grants = await Task.WhenAll(claims);
            Assert.All(grants, grant => { Assert.NotNull(grant); Assert.True(grant.Epoch > 0); });
            Assert.Equal(ids.Length, grants.Select(grant => grant!.Epoch).Distinct().Count());
        }
        finally
        {
            foreach (var id in ids)
                await store.DeleteWorkflowInstanceAsync(id);
        }
    }

    private static async Task LateFirstCommit(IWorkflowStateRepository store)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var old = (await store.TryAcquireExecutionLeaseAsync(id, "host-a", Duration))!;
            var late = CheckpointCommit.Create(Instance(id, "old"), old.Epoch, 0);
            Assert.Null(await store.GetWorkflowInstanceAsync(id));
            await store.DeleteWorkflowInstanceAsync(id);
            Assert.Null(await store.GetTombstoneAsync(id));
            var current = (await store.TryAcquireExecutionLeaseAsync(id, "host-b", Duration))!;
            Assert.True(current.Epoch > old.Epoch);
            var first = CheckpointCommit.Create(Instance(id, "replacement"), current.Epoch, 0);

            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, 0),
                await store.CommitWorkflowInstanceAsync(late));
            Assert.Null(await store.GetWorkflowInstanceAsync(id));
            Assert.True(await store.RenewExecutionLeaseAsync(id, "host-b", Duration));
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 1),
                await store.CommitWorkflowInstanceAsync(first));
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, 1),
                await store.CommitWorkflowInstanceAsync(late));
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(first.Incarnation, saved.Incarnation);
            Assert.Equal("{\"owner\":\"replacement\"}", saved.WorkflowDataJson);
            Assert.Equal(1, saved.Revision);
        }
        finally { await store.DeleteWorkflowInstanceAsync(id); }
    }

    private static async Task UnleasedIncarnation(IWorkflowStateRepository store, Func<string, Task> expire)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var original = Instance(id, "old");
            original.Status = WorkflowStatus.Suspended;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(original, 0, 0))).Status);
            original = (await store.GetWorkflowInstanceAsync(id))!.CopyForResume();
            original.Status = WorkflowStatus.Completed;
            var late = CheckpointCommit.Create(original, 0, original.Revision);
            await store.DeleteWorkflowInstanceAsync(id, Tombstones);
            await expire(id);
            var replacement = Instance(id, "replacement");
            replacement.Status = WorkflowStatus.Suspended;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(replacement, 0, 0))).Status);
            replacement = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(late.ExpectedRevision, replacement.Revision);
            Assert.NotEqual(late.Incarnation, replacement.Incarnation);

            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, replacement.Revision),
                await store.CommitWorkflowInstanceAsync(late));
            var unchanged = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(replacement.Revision, unchanged.Revision);
            Assert.Equal(replacement.Incarnation, unchanged.Incarnation);
            Assert.Equal(WorkflowStatus.Suspended, unchanged.Status);
            Assert.Equal(replacement.WorkflowDataJson, unchanged.WorkflowDataJson);
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 2),
                await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(unchanged, 0, unchanged.Revision)));
        }
        finally { await store.DeleteWorkflowInstanceAsync(id); }
    }

    private static WorkflowInstance Instance(string id, string owner = "first") => new()
    {
        InstanceId = id, WorkflowName = "StoreWideEpoch", Status = WorkflowStatus.Running,
        WorkflowDataJson = "{\"owner\":\"" + owner + "\"}"
    };
}
