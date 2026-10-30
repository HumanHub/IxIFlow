using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "A-03")]
public sealed class Slice4ProviderCommitTests
{
    [Fact]
    public async Task SqliteReturnsRetryAfterARealWriterLockTimeout()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Slice4Retry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "retry.db");

        try
        {
            await using var store = new SqliteWorkflowStateRepository(connectionString);

            var instance = new WorkflowInstance
            {
                InstanceId = "sqlite-retry", WorkflowName = "SqliteRetry", Status = WorkflowStatus.Running,
                ExecutionStateJson = StoreTestSupport.CheckpointJson
            };

            await store.SeedAsync(instance);
            var owner = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
            var commit = CheckpointCommit.Create(instance, owner.Epoch, instance.Revision, commitId: "locked-writer");

            await using var holder =
                new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

            await holder.OpenAsync();

            await using (var transaction = holder.BeginTransaction(deferred: false))
            {
                var result = await Task.Run(() => store.CommitWorkflowInstanceAsync(commit)).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(WorkflowCommitStatus.Retry, result.Status);
                await transaction.RollbackAsync();
            }

            Assert.Equal(instance.Revision, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        }
        finally
        {
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerReturnsRetryAfterARealLockTimeout() => LockTimeout(true);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlReturnsRetryAfterARealLockTimeout() => LockTimeout(false);

    private static async Task LockTimeout(bool sqlServer)
    {
        var connectionString = sqlServer
            ? SqlTestDatabase<Slice4ProviderCommitTests>.ConnectionString
            : PostgreSqlTestDatabase<Slice4ProviderCommitTests>.ConnectionString;

        IWorkflowStateRepository store = sqlServer
            ? new SqlServerWorkflowStateRepository(connectionString)
            : new PostgreSqlWorkflowStateRepository(connectionString);

        await using DbConnection connection = sqlServer ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "RealCommitRetry", Status = WorkflowStatus.Running,
            ExecutionStateJson = StoreTestSupport.CheckpointJson
        };

        await store.SeedAsync(instance);
        var grant = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        var commit = CheckpointCommit.Create(instance, grant.Epoch, instance.Revision, commitId: "lock-timeout");

        await connection.ExecuteAsync(
            sqlServer
                ? "CREATE TABLE dbo.Slice4CommitLock (Id int NOT NULL PRIMARY KEY, Value int NOT NULL); " +
                "INSERT dbo.Slice4CommitLock VALUES (1,0)"
                : "CREATE TABLE slice4_commit_lock (id integer PRIMARY KEY, value integer NOT NULL); " +
                "INSERT INTO slice4_commit_lock VALUES (1,0)");

        try
        {
            // The trigger sets the actual provider session's timeout before touching the held sentinel row.
            // No exception or commit result is fabricated by a repository decorator.
            await connection.ExecuteAsync(
                sqlServer
                    ? "CREATE TRIGGER dbo.Slice4CommitTimeout ON dbo.IxIFlowWorkflowInstances AFTER UPDATE AS " +
                    "BEGIN SET LOCK_TIMEOUT 100; UPDATE dbo.Slice4CommitLock SET Value=Value+1 WHERE Id=1; END"
                    : "CREATE FUNCTION slice4_commit_timeout() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN " +
                    "PERFORM set_config('lock_timeout','100ms',true); " +
                    "UPDATE slice4_commit_lock SET value=value+1 WHERE id=1; RETURN NEW; END $$; " +
                    "CREATE TRIGGER slice4_commit_timeout AFTER UPDATE ON ixiflow_workflow_instances " +
                    "FOR EACH ROW EXECUTE FUNCTION slice4_commit_timeout()");

            await using (var held = await connection.BeginTransactionAsync())
            {
                await connection.ExecuteAsync(
                    sqlServer
                        ? "UPDATE dbo.Slice4CommitLock SET Value=Value+1 WHERE Id=1"
                        : "UPDATE slice4_commit_lock SET value=value+1 WHERE id=1", transaction: held);

                var reply = await store.CommitWorkflowInstanceAsync(commit).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(WorkflowCommitStatus.Retry, reply.Status);
                Assert.Equal(instance.Revision, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
                await held.RollbackAsync();
            }

            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
            Assert.Equal(instance.Revision + 1, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        }
        finally
        {
            await connection.ExecuteAsync(
                sqlServer
                    ? "DROP TRIGGER IF EXISTS dbo.Slice4CommitTimeout; DROP TABLE dbo.Slice4CommitLock"
                    : "DROP TRIGGER IF EXISTS slice4_commit_timeout ON ixiflow_workflow_instances; " +
                    "DROP FUNCTION IF EXISTS slice4_commit_timeout(); DROP TABLE slice4_commit_lock");

            await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlWaitingClaimAllocatesItsEpochAfterTheHeldLeaseRow()
    {
        var connectionString = PostgreSqlTestDatabase<Slice4ProviderCommitTests>.ConnectionString;
        var store = new PostgreSqlWorkflowStateRepository(connectionString);
        var id = Guid.NewGuid().ToString("N");
        var query = new ClaimQuery(Guid.Empty, new Dictionary<string, string>());
        var initial = (await store.TryAcquireExecutionLeaseAsync(id, "initial", TimeSpan.FromMinutes(1), query))!;
        Assert.True((await store.ReleaseExecutionLeaseAsync(id, "initial")).Released);
        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync();
        var session = await holder.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
        Task<ExecutionLeaseGrant?>? claiming = null;
        long intervening;

        try
        {
            await using (var transaction = await holder.BeginTransactionAsync())
            {
                Assert.Equal(
                    initial.Epoch,
                    await holder.ExecuteScalarAsync<long>(
                        "SELECT epoch FROM ixiflow_workflow_leases WHERE instance_id=@Id FOR UPDATE", new { Id = id }, transaction));

                claiming = store.TryAcquireExecutionLeaseAsync(id, "waiting", TimeSpan.FromMinutes(1), query);
                await using var observer = new NpgsqlConnection(connectionString);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

                while (!await observer.ExecuteScalarAsync<bool>(
                           "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @Session=ANY(pg_blocking_pids(pid)))",
                           new { Session = session }))
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException($"Instance '{id}': expected a waiting claim; last state: no blocked session");

                    await Task.Delay(10);
                }

                Assert.False(claiming.IsCompleted);
                intervening = await holder.ExecuteScalarAsync<long>("SELECT nextval('ixiflow_execution_epoch')", transaction: transaction);
                Assert.True(intervening > initial.Epoch);

                await holder.ExecuteAsync(
                    "UPDATE ixiflow_workflow_leases SET epoch=@Epoch, expires_at_utc='0001-01-01' WHERE instance_id=@Id",
                    new { Epoch = intervening, Id = id }, transaction);

                await transaction.CommitAsync();
            }

            var successor = await claiming.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(successor);
            Assert.True(successor.Epoch > intervening);

            Assert.Equal(
                successor.Epoch,
                await holder.ExecuteScalarAsync<long>("SELECT epoch FROM ixiflow_workflow_leases WHERE instance_id=@Id", new { Id = id }));
        }
        finally
        {
            if (claiming != null)
                await claiming.WaitAsync(TimeSpan.FromSeconds(15));

            await store.DeleteWorkflowInstanceAsync(id);
        }
    }
}