using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class DemandSetTests
{
    [Fact]
    public Task MemoryInterningAndCollection() => Contract(new InMemoryWorkflowStateRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteInterningAndCollection()
    {
        var cs = SqliteTestDatabase<DemandSetTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(cs);
        await using var connection = new SqliteConnection(cs);
        await Contract(store, connection);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerInterningAndCollection()
    {
        var cs = SqlTestDatabase<DemandSetTests>.ConnectionString;
        await using var connection = new SqlConnection(cs);
        await Contract(new SqlWorkflowStateRepository(cs), connection);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlInterningAndCollection()
    {
        var cs = PostgreSqlTestDatabase<DemandSetTests>.ConnectionString;
        await using var connection = new NpgsqlConnection(cs);
        await Contract(new PostgreSqlWorkflowStateRepository(cs), connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryDoesNotMutateTheSetAndAPinNeverOverridesIt(bool sqlite)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var sqliteStore = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<DemandSetTests>.ConnectionString, time) : null;
        IWorkflowStateRepository store = sqliteStore ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        var host = Guid.NewGuid();
        ClaimQuery Host(Guid id) => new(id, new Dictionary<string, string> { ["host"] = id.ToString("D") });
        var first = Host(host);
        var other = Host(Guid.NewGuid());
        WorkflowDemand[] demands = [new("temporary", "required", time.GetUtcNow().UtcDateTime.AddMinutes(1)),
            new("host", host.ToString("D"), time.GetUtcNow().UtcDateTime.AddMinutes(2), true)];
        var instance = Instance(demands);
        var owner = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "seed", TimeSpan.FromMinutes(1), first))!;
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, owner.Epoch, 0, parks: true, wakeAfter: TimeSpan.Zero))).Status);
        try
        {
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(first), row => row.InstanceId == instance.InstanceId);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "missing", TimeSpan.FromMinutes(1), first));
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.Contains(await store.GetRunnableInstancesAsync(first), row => row.InstanceId == instance.InstanceId);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "expired-demand", TimeSpan.FromMinutes(1), first));
            await store.ReleaseExecutionLeaseAsync(instance.InstanceId, "expired-demand");
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "live-pin", TimeSpan.FromMinutes(1), other));
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.Contains(await store.GetRunnableInstancesAsync(other), row => row.InstanceId == instance.InstanceId);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "expired-pin", TimeSpan.FromMinutes(1), other));
            Assert.Equal(demands.OrderBy(d => d.Key, StringComparer.Ordinal), await store.GetWorkflowDemandsAsync(instance.InstanceId));
        }
        finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
    }

    private static WorkflowInstance Instance(params WorkflowDemand[] demands) => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Interned", Status = WorkflowStatus.Running,
        ExecutionStateJson = JsonSerializer.Serialize(new ExecutionCheckpoint { StructureHash = "interned", Demands = demands.ToList() })
    };

    private static async Task Contract(IWorkflowStateRepository store, DbConnection? connection = null)
    {
        await store.GetWorkflowDemandsAsync("schema");
        if (connection != null) await connection.OpenAsync();
        var server = connection is SqlConnection;
        var instances = server ? "dbo.IxIFlowWorkflowInstances" : "ixiflow_workflow_instances";
        var sets = server ? "dbo.IxIFlowDemandSets" : "ixiflow_demand_sets";
        var rows = server ? "dbo.IxIFlowDemandSetRows" : "ixiflow_demand_set_rows";
        var setColumn = server ? "DemandSetId" : "demand_set_id";
        var instanceColumn = server ? "InstanceId" : "instance_id";
        var memory = store as InMemoryWorkflowStateRepository;
        object Field(string name) => typeof(InMemoryWorkflowStateRepository).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
        var ids = new List<string>();
        var host = Guid.NewGuid();
        var query = new ClaimQuery(host, new Dictionary<string, string> { ["feature"] = "", ["zone"] = "eu", ["host"] = host.ToString("D") });
        async Task<string> SetId(string id) => connection != null
            ? await connection.QuerySingleAsync<string>($"SELECT {setColumn} FROM {instances} WHERE {instanceColumn} = @Id", new { Id = id })
            : ((Dictionary<string, (string DemandSetId, WorkflowDemand? Pin)>)Field("_placement"))[id].DemandSetId;
        async Task<bool> Exists(string id) => connection != null
            ? await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {sets} WHERE {setColumn} = @Id", new { Id = id }) == 1
            : ((Dictionary<string, WorkflowDemand[]>)Field("_demandSets")).ContainsKey(id);
        async Task<int> RowCount(string id) => connection != null
            ? await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {rows} WHERE {setColumn} = @Id", new { Id = id })
            : ((Dictionary<string, WorkflowDemand[]>)Field("_demandSets"))[id].Length;
        async Task<WorkflowInstance> Seed(params WorkflowDemand[] demands)
        {
            var instance = Instance(demands);
            ids.Add(instance.InstanceId);
            var owner = Assert.IsType<ExecutionLeaseGrant>(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId,
                "seed", TimeSpan.FromMinutes(1), query));
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(instance, owner.Epoch, 0, parks: true, wakeAfter: TimeSpan.Zero))).Status);
            return instance;
        }
        Task Purge() => store.PurgeAsync(TimeSpan.FromDays(1), TimeSpan.FromDays(1), 1000);

        try
        {
            WorkflowDemand[] demands = [new("zone", "eu", Inherit: true), new("feature")];
            var first = await Seed(demands);
            var second = await Seed(demands.Reverse().Append(new WorkflowDemand("host", host.ToString("D"),
                DateTime.UtcNow.AddHours(1), Inherit: true)).ToArray());
            var id = await SetId(first.InstanceId);
            Assert.Equal(id, await SetId(second.InstanceId));
            Assert.Equal(2, await RowCount(id));
            Assert.Equal(demands.OrderBy(d => d.Key, StringComparer.Ordinal), await store.GetWorkflowDemandsAsync(first.InstanceId));
            Assert.Contains(await store.GetWorkflowDemandsAsync(second.InstanceId), d => d.Key == "host" && d.Inherit);
            var raw = connection != null
                ? await connection.QuerySingleAsync<string>($"SELECT {(server ? "StateJson" : "state_json")} FROM {instances} WHERE {instanceColumn} = @Id", new { Id = second.InstanceId })
                : JsonSerializer.Serialize(((System.Collections.Concurrent.ConcurrentDictionary<string, WorkflowInstance>)Field("_instances"))[second.InstanceId]);
            Assert.Null(JsonNode.Parse(raw)!["ExecutionStateJson"]!["Demands"]);
            if (connection != null)
            {
                var pin = await connection.QuerySingleAsync<string>($"SELECT {(server ? "PinnedHost" : "pinned_host")} FROM {instances} WHERE {instanceColumn} = @Id",
                    new { Id = second.InstanceId });
                Assert.Equal(host.ToString("D"), pin);
            }

            // Case, trailing spaces, null/empty values, inheritance and absolute expiry are identity.
            var variants = new WorkflowDemand[][]
            {
                [new("zone", "EU", Inherit: true), new("feature")],
                [new("Zone", "eu", Inherit: true), new("feature")],
                [new("zone", "eu ", Inherit: true), new("feature")],
                [new("zone ", "eu", Inherit: true), new("feature")],
                [new("zone", "eu", Inherit: true), new("feature", "")],
                [new("zone", "eu"), new("feature")],
                [new("zone", "eu", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), true), new("feature")],
                // Two distinct trailing-space keys must coexist, including on SQL Server.
                [new("zone", "eu"), new("zone ", "eu")]
            };
            var variantIds = new List<string> { id };
            foreach (var variant in variants) variantIds.Add(await SetId((await Seed(variant)).InstanceId));
            Assert.Equal(variantIds.Count, variantIds.Distinct().Count());

            // Existing commits cannot replace their immutable placement by changing checkpoint JSON.
            var saved = (await store.GetWorkflowInstanceAsync(first.InstanceId))!;
            var grant = (await store.TryAcquireExecutionLeaseAsync(first.InstanceId, "update", TimeSpan.FromMinutes(1), query))!;
            saved.ExecutionStateJson = Instance(new WorkflowDemand("substituted")).ExecutionStateJson;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(saved, grant.Epoch, saved.Revision, parks: true, wakeAfter: TimeSpan.Zero))).Status);
            Assert.Equal(id, await SetId(first.InstanceId));
            Assert.Equal(demands.OrderBy(d => d.Key, StringComparer.Ordinal), await store.GetWorkflowDemandsAsync(first.InstanceId));

            // Child and ingress creation use the same intern table as ordinary roots.
            var child = Instance(demands);
            child.ParentInstanceId = first.InstanceId;
            child.ParentWaitId = "child";
            ids.Add(child.InstanceId);
            saved = (await store.GetWorkflowInstanceAsync(first.InstanceId))!;
            grant = (await store.TryAcquireExecutionLeaseAsync(first.InstanceId, "child", TimeSpan.FromMinutes(1), query))!;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved,
                grant.Epoch, saved.Revision, childStarts: [new(child.InstanceId, JsonSerializer.Serialize(child), "child")],
                openedWaits: [new("child", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)], parks: true, wakeAfter: TimeSpan.Zero))).Status);
            Assert.Equal(id, await SetId(child.InstanceId));
            var ingress = Instance(demands);
            ids.Add(ingress.InstanceId);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(ingress),
                new("sender", "trigger", ingress.InstanceId, new string('A', 64), "owner"))).Status);
            Assert.Equal(id, await SetId(ingress.InstanceId));

            await store.DeleteWorkflowInstanceAsync(first.InstanceId);
            await Purge();
            Assert.True(await Exists(id));
            Assert.Equal(2, await RowCount(id));
            Assert.Equal(demands.OrderBy(d => d.Key, StringComparer.Ordinal), await store.GetWorkflowDemandsAsync(child.InstanceId));

            // Race reuse of an orphan against collection, repeatedly in both scheduling orders.
            for (var iteration = 0; iteration < 8; iteration++)
            {
                WorkflowDemand[] racingDemands = [new("race-" + Guid.NewGuid().ToString("N"), "value")];
                var orphan = await Seed(racingDemands);
                var racingId = await SetId(orphan.InstanceId);
                await store.DeleteWorkflowInstanceAsync(orphan.InstanceId);
                // On the distributed stores, hold the actual lifecycle lock so both
                // operations must wait at the creation/collection boundary.
                await using var held = iteration == 0 && (connection is SqlConnection or NpgsqlConnection)
                    ? await connection.BeginTransactionAsync() : null;
                if (held != null)
                {
                    var lockSql = (string)store.GetType().GetProperty("DemandSetsLockSql",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
                    await connection!.ExecuteAsync(lockSql, transaction: held);
                }
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var creating = Task.Run(async () => { await start.Task; return await Seed(racingDemands); });
                var collecting = Task.Run(async () => { await start.Task; await Purge(); });
                start.SetResult();
                if (held != null)
                {
                    await Task.Delay(100);
                    Assert.False(creating.IsCompleted);
                    Assert.False(collecting.IsCompleted);
                    await held.RollbackAsync();
                }
                var created = await creating;
                await collecting;
                Assert.Equal(racingId, await SetId(created.InstanceId));
                Assert.True(await Exists(racingId));
                Assert.Equal(racingDemands, await store.GetWorkflowDemandsAsync(created.InstanceId));
                await store.DeleteWorkflowInstanceAsync(created.InstanceId);
                await Purge();
                Assert.False(await Exists(racingId));
                if (connection != null) Assert.Equal(0, await RowCount(racingId));
            }
        }
        finally
        {
            foreach (var id in ids.Distinct()) await store.DeleteWorkflowInstanceAsync(id);
            await Purge();
        }
    }
}
