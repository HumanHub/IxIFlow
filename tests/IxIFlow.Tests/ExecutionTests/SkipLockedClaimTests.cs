using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "C-02")]
[Trait("Spec", "C-11")]
public sealed class SkipLockedClaimTests
{
    private static readonly TimeSpan ClaimBound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly ClaimQuery Query = new(Guid.NewGuid(), new Dictionary<string, string>(), 100);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerClaimsSkipLockedRows() => ClaimsSkipLockedRows(false);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlClaimsSkipLockedRows() => ClaimsSkipLockedRows(true);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerClaimPassReachesTheFreeInstance() => ClaimPassReachesTheFreeInstance(false);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlClaimPassReachesTheFreeInstance() => ClaimPassReachesTheFreeInstance(true);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerConcurrentClaimsGrantExactlyOnce() => ConcurrentClaimsGrantExactlyOnce(false);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlConcurrentClaimsGrantExactlyOnce() => ConcurrentClaimsGrantExactlyOnce(true);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerClaimsSkipWithReadCommittedSnapshotEnabled()
    {
        var connectionString = await SqlServerTestServer.CreateDatabaseAsync("IxIFlow_SkipLockedSnapshot");
        try
        {
            await using (var connection = new SqlConnection(connectionString))
                await connection.ExecuteAsync($"ALTER DATABASE [{connection.Database}] SET READ_COMMITTED_SNAPSHOT ON");
            await ClaimsSkipLockedRows(false, connectionString);
            await ClaimPassReachesTheFreeInstance(false, connectionString);
        }
        finally { await SqlServerTestServer.DropDatabaseAsync(connectionString); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerClaimSkipsAnAbsentKeyHeldByCreationOrDeletion()
    {
        var (store, connect) = Provider(false);
        var id = Guid.NewGuid().ToString("N");
        await store.GetWorkflowInstanceAsync(id); // Initialize schema before taking the lock.
        Task<ExecutionLeaseGrant?>? attempt = null;
        try
        {
            await using (var hold = await HoldAsync(connect))
            {
                await hold.Connection.ExecuteAsync("""
                    SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                    WHERE InstanceId = @Id
                    """, new { Id = id }, hold.Transaction);
                attempt = ClaimAsync(store, id);
                Assert.Null(await attempt.WaitAsync(ClaimBound));
            }
            Assert.NotNull(await ClaimAsync(store, id));
        }
        finally
        {
            if (attempt != null) await attempt.WaitAsync(DrainBound);
            await store.DeleteWorkflowInstanceAsync(id);
        }
    }

    private static (IWorkflowStateRepository Store, Func<DbConnection> Connect) Provider(bool postgres, string? connectionString = null)
    {
        if (postgres)
        {
            var cs = connectionString ?? PostgreSqlTestDatabase<SkipLockedClaimTests>.ConnectionString;
            return (new PostgreSqlWorkflowStateRepository(cs), () => new NpgsqlConnection(cs));
        }
        var sql = connectionString ?? SqlTestDatabase<SkipLockedClaimTests>.ConnectionString;
        return (new SqlWorkflowStateRepository(sql), () => new SqlConnection(sql));
    }

    private static WorkflowInstance Instance(string? id = null) => new()
    {
        InstanceId = id ?? Guid.NewGuid().ToString("N"), WorkflowName = "SkipLocked",
        Status = WorkflowStatus.Running, ExecutionStateJson = StoreTestSupport.CheckpointJson,
        WorkflowDataJson = "{}"
    };

    private static Task<ExecutionLeaseGrant?> ClaimAsync(IWorkflowStateRepository store, string id,
        ExecutionClaimKind kind = ExecutionClaimKind.Runnable, string token = "claim") =>
        store.TryAcquireExecutionLeaseAsync(id, token, LeaseDuration, Query, kind);

    private static async Task ClaimsSkipLockedRows(bool postgres, string? connectionString = null)
    {
        var (store, connect) = Provider(postgres, connectionString);
        foreach (var kind in Enum.GetValues<ExecutionClaimKind>())
        foreach (var exclusive in new[] { false, true })
        {
            var instance = Instance();
            Task<ExecutionLeaseGrant?>? attempt = null;
            try
            {
                await store.SeedAsync(instance, query: Query);
                if (kind == ExecutionClaimKind.Termination)
                    await store.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "test" });
                await using (var hold = await HoldRowAsync(connect, instance.InstanceId, exclusive))
                {
                    attempt = ClaimAsync(store, instance.InstanceId, kind);
                    // This must finish while the transaction still owns the row, for every claim kind.
                    Assert.Null(await attempt.WaitAsync(ClaimBound));
                }
                var granted = await ClaimAsync(store, instance.InstanceId, kind);
                Assert.NotNull(granted);
                Assert.Equal(0, granted.CrashCount);
            }
            finally
            {
                if (attempt != null) await attempt.WaitAsync(DrainBound);
                await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
            }
        }
    }

    private static async Task ClaimPassReachesTheFreeInstance(bool postgres, string? connectionString = null)
    {
        var (store, connect) = Provider(postgres, connectionString);
        foreach (var exclusive in new[] { false, true })
        {
            var locked = Instance("0-locked-" + Guid.NewGuid().ToString("N"));
            var free = Instance("1-free-" + Guid.NewGuid().ToString("N"));
            Task<List<string>>? pass = null;
            try
            {
                await store.SeedAsync(locked, query: Query);
                await store.SeedAsync(free, query: Query);
                await using (var hold = await HoldRowAsync(connect, locked.InstanceId, exclusive))
                {
                    pass = ClaimPassAsync();
                    Assert.Equal(free.InstanceId, Assert.Single(await pass.WaitAsync(ClaimBound)));
                }
                // No work is consumed by the skipped claim: the next pass still finds and claims it.
                Assert.Contains(await store.GetRunnableInstancesAsync(Query), row => row.InstanceId == locked.InstanceId);
                Assert.NotNull(await ClaimAsync(store, locked.InstanceId));
            }
            finally
            {
                if (pass != null) await pass.WaitAsync(DrainBound);
                await store.DeleteWorkflowInstanceAsync(locked.InstanceId);
                await store.DeleteWorkflowInstanceAsync(free.InstanceId);
            }

            async Task<List<string>> ClaimPassAsync()
            {
                var candidates = await store.GetRunnableInstancesAsync(Query);
                if (!exclusive)
                    Assert.Equal(locked.InstanceId, candidates[0].InstanceId);
                var granted = new List<string>();
                foreach (var candidate in candidates)
                    if (await ClaimAsync(store, candidate.InstanceId) != null) granted.Add(candidate.InstanceId);
                return granted;
            }
        }
    }

    private static async Task ConcurrentClaimsGrantExactlyOnce(bool postgres)
    {
        var (store, _) = Provider(postgres);
        foreach (var existing in new[] { false, true })
        {
            var instance = Instance();
            await store.GetWorkflowInstanceAsync(instance.InstanceId);
            if (existing) await store.SeedAsync(instance, query: Query);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 16).Select(async index =>
            {
                await start.Task;
                return await ClaimAsync(store, instance.InstanceId, token: "owner-" + index);
            }).ToArray();
            var all = Task.WhenAll(attempts);
            start.SetResult();
            try
            {
                var winner = Assert.Single(await all.WaitAsync(DrainBound), grant => grant != null);
                Assert.True(winner!.Epoch > 0);
                Assert.Equal(0, winner.CrashCount);
            }
            finally
            {
                await all.WaitAsync(DrainBound);
                await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
            }
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlClaimSkipsTheIdentityLockDuringCreation()
    {
        var (store, connect) = Provider(true);
        var source = Instance();
        var id = Guid.NewGuid().ToString("N");
        Task<ExecutionLeaseGrant?>? attempt = null;
        try
        {
            await store.SeedAsync(source, query: Query);
            await using (var hold = await HoldAsync(connect))
            {
                await HoldPostgreSqlIdentityAsync(hold, id);
                // Even before the creator inserts its row, the ID lock must refuse the claim.
                attempt = ClaimAsync(store, id);
                Assert.Null(await attempt.WaitAsync(ClaimBound));
                await hold.Connection.ExecuteAsync("""
                    INSERT INTO ixiflow_workflow_instances
                        (instance_id, workflow_name, workflow_version, created_at_utc, status,
                         runnable_at_utc, demand_set_id, state_json, revision)
                    SELECT @Id, workflow_name, workflow_version, created_at_utc, status,
                        runnable_at_utc, demand_set_id,
                        jsonb_set(state_json::jsonb, '{InstanceId}', to_jsonb(CAST(@Id AS text)))::text, revision
                    FROM ixiflow_workflow_instances WHERE instance_id = @Source
                    """, new { Id = id, Source = source.InstanceId }, hold.Transaction);
                attempt = ClaimAsync(store, id);
                Assert.Null(await attempt.WaitAsync(ClaimBound));
                await hold.Transaction.CommitAsync();
            }
            Assert.Contains(await store.GetRunnableInstancesAsync(Query), row => row.InstanceId == id);
            Assert.NotNull(await ClaimAsync(store, id));
        }
        finally
        {
            if (attempt != null) await attempt.WaitAsync(DrainBound);
            await store.DeleteWorkflowInstanceAsync(id);
            await store.DeleteWorkflowInstanceAsync(source.InstanceId);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlClaimSkipsTheIdentityLockDuringDeletion()
    {
        var (store, connect) = Provider(true);
        var instance = Instance();
        Task<ExecutionLeaseGrant?>? attempt = null;
        try
        {
            await store.SeedAsync(instance, query: Query);
            await using (var hold = await HoldAsync(connect))
            {
                await HoldPostgreSqlIdentityAsync(hold, instance.InstanceId);
                await hold.Connection.ExecuteAsync("DELETE FROM ixiflow_workflow_instances WHERE instance_id = @Id",
                    new { Id = instance.InstanceId }, hold.Transaction);
                attempt = ClaimAsync(store, instance.InstanceId);
                Assert.Null(await attempt.WaitAsync(ClaimBound));
                // Disposal rolls deletion back: the same row must still be claimable afterwards.
            }
            Assert.NotNull(await ClaimAsync(store, instance.InstanceId));
        }
        finally
        {
            if (attempt != null) await attempt.WaitAsync(DrainBound);
            await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    private static Task HoldPostgreSqlIdentityAsync(LockHold hold, string id) => hold.Connection.ExecuteAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended(@Id, 0))", new { Id = id }, hold.Transaction);

    private static async Task<LockHold> HoldRowAsync(Func<DbConnection> connect, string id, bool exclusive)
    {
        var hold = await HoldAsync(connect);
        try
        {
            var sql = hold.Connection is SqlConnection
                ? exclusive
                    ? "UPDATE dbo.IxIFlowWorkflowInstances WITH (ROWLOCK) SET StateJson = StateJson WHERE InstanceId = @Id"
                    : "SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK) WHERE InstanceId = @Id"
                : "SELECT instance_id FROM ixiflow_workflow_instances WHERE instance_id = @Id FOR UPDATE";
            await hold.Connection.ExecuteAsync(sql, new { Id = id }, hold.Transaction);
            return hold;
        }
        catch { await hold.DisposeAsync(); throw; }
    }

    private static async Task<LockHold> HoldAsync(Func<DbConnection> connect)
    {
        var connection = connect();
        try
        {
            await connection.OpenAsync();
            return new LockHold(connection, await connection.BeginTransactionAsync());
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private sealed class LockHold(DbConnection connection, DbTransaction transaction) : IAsyncDisposable
    {
        public DbConnection Connection => connection;
        public DbTransaction Transaction => transaction;
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
