using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "A-03")]
public sealed class StoreWideEpochContractTests
{
    private enum Scenario { IncreasingEpochs, ConcurrentClaims, LateFirstCommit, UnleasedIncarnation, LeasedIncarnation }
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

    [Fact]
    public Task MemoryLeasedCommitRequiresCurrentIncarnationAtCurrentEpoch() => Memory(Scenario.LeasedIncarnation);

    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteEpochsIncreaseAcrossInstancesAndRecreation() => Sqlite(Scenario.IncreasingEpochs);
    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteConcurrentClaimsHaveDistinctEpochs() => Sqlite(Scenario.ConcurrentClaims);
    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteLateFirstCommitCannotUseRecreatedLease() => Sqlite(Scenario.LateFirstCommit);
    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteUnleasedCommitRequiresCurrentIncarnation() => Sqlite(Scenario.UnleasedIncarnation);

    [Fact, Trait("Category", "SqliteIntegration")]
    public Task SqliteLeasedCommitRequiresCurrentIncarnationAtCurrentEpoch() => Sqlite(Scenario.LeasedIncarnation);

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteRefusedClaimsDoNotWrite()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.StoreWideEpoch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var connection = "Data Source=" + Path.Combine(folder, "refused.db");
            var time = new FakeTimeProvider();
            await using var store = new SqliteWorkflowStateRepository(connection, time);
            var id = Guid.NewGuid().ToString("N");
            var first = (await store.TryAcquireExecutionLeaseAsync(id, "owner", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.NotNull(first);
            await using var observer = new SqliteConnection(new SqliteConnectionStringBuilder(connection)
                { Pooling = false }.ConnectionString);
            await observer.OpenAsync();
            var dataVersion = await observer.ExecuteScalarAsync<long>("PRAGMA data_version");

            Assert.Null(await store.TryAcquireExecutionLeaseAsync(id, "contender", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
            Assert.Equal(first.Epoch, await observer.ExecuteScalarAsync<long>(
                "SELECT epoch FROM ixiflow_execution_epoch WHERE id = 1"));
            Assert.Equal(dataVersion, await observer.ExecuteScalarAsync<long>("PRAGMA data_version"));
            Assert.True((await store.RenewExecutionLeasesAsync([new(id, "owner", Duration)]))[0].Status == ExecutionLeaseRenewalStatus.Renewed);

            Assert.True((await store.ReleaseExecutionLeaseAsync(id, "owner")).Released);
            var next = (await store.TryAcquireExecutionLeaseAsync(id, "next", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.NotNull(next);
            Assert.Equal(first.Epoch + 1, next.Epoch);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

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
                earlier = (await store.TryAcquireExecutionLeaseAsync(id, "before", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!.Epoch;
                await store.DeleteWorkflowInstanceAsync(id);
            }
            await using var reopened = new SqliteWorkflowStateRepository(connection);
            var current = (await reopened.TryAcquireExecutionLeaseAsync(id, "after", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
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

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerLeasedCommitRequiresCurrentIncarnationAtCurrentEpoch() => SqlServer(Scenario.LeasedIncarnation);

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerWaitingClaimCannotInstallAnOlderEpoch()
    {
        var connection = SqlTestDatabase<StoreWideEpochContractTests>.ConnectionString;
        var id = Guid.NewGuid().ToString("N");
        var application = "epoch-claim-" + id;
        var store = new SqlWorkflowStateRepository(new SqlConnectionStringBuilder(connection)
            { ApplicationName = application }.ConnectionString);
        Task<ExecutionLeaseGrant?>? waiting = null;
        try
        {
            var initial = (await store.TryAcquireExecutionLeaseAsync(id, "initial", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.NotNull(initial);
            Assert.True((await store.ReleaseExecutionLeaseAsync(id, "initial")).Released);
            await using var holder = new SqlConnection(connection);
            await holder.OpenAsync();
            var session = await holder.ExecuteScalarAsync<int>("SELECT @@SPID");
            long interveningEpoch;
            await using (var transaction = await holder.BeginTransactionAsync())
            {
                Assert.Equal(initial.Epoch, await holder.ExecuteScalarAsync<long>("""
                    SELECT Epoch FROM dbo.IxIFlowWorkflowLeases WITH (UPDLOCK, HOLDLOCK)
                    WHERE InstanceId = @Id
                    """, new { Id = id }, transaction));
                waiting = store.TryAcquireExecutionLeaseAsync(id, "waiting", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
                await using var observer = new SqlConnection(connection);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                // Wait for an actual lease-row lock conflict, not a scheduling delay.
                while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT CAST(CASE WHEN EXISTS (
                        SELECT 1 FROM sys.dm_exec_requests AS request
                        JOIN sys.dm_exec_sessions AS session ON session.session_id = request.session_id
                        WHERE request.blocking_session_id = @Session
                            AND request.wait_type LIKE 'LCK_M%'
                            AND session.program_name LIKE @Application + '%')
                    THEN 1 ELSE 0 END AS bit)
                    """, new { Session = session, Application = application }, cancellationToken: timeout.Token)))
                    await Task.Yield();
                Assert.False(waiting.IsCompleted);

                // Complete an intervening claim under the held lock, then make it available.
                // A waiter that allocated before taking this lock would now install an older value.
                interveningEpoch = await holder.ExecuteScalarAsync<long>(
                    "SELECT NEXT VALUE FOR dbo.IxIFlowExecutionEpoch", transaction: transaction);
                Assert.True(interveningEpoch > initial.Epoch);
                await holder.ExecuteAsync("""
                    UPDATE dbo.IxIFlowWorkflowLeases
                    SET Epoch = @Epoch, Token = 'intervening', ExpiresAtUtc = CAST('0001-01-01' AS DATETIME2)
                    WHERE InstanceId = @Id
                    """, new { Id = id, Epoch = interveningEpoch }, transaction);
                await transaction.CommitAsync();
            }

            var grant = await waiting.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(grant);
            Assert.True(grant.Epoch > interveningEpoch);
            var installed = await holder.QuerySingleAsync<(long Epoch, string Token)>(
                "SELECT Epoch, Token FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId = @Id", new { Id = id });
            Assert.Equal(grant.Epoch, installed.Epoch);
            Assert.Equal("waiting", installed.Token);
            Assert.True((await store.RenewExecutionLeasesAsync([new(id, "waiting", Duration)]))[0].Status == ExecutionLeaseRenewalStatus.Renewed);
        }
        finally
        {
            try
            {
                if (waiting != null) await waiting.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { await store.DeleteWorkflowInstanceAsync(id); }
        }
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlEpochsIncreaseAcrossInstancesAndRecreation() => PostgreSql(Scenario.IncreasingEpochs);
    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlConcurrentClaimsHaveDistinctEpochs() => PostgreSql(Scenario.ConcurrentClaims);
    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlLateFirstCommitCannotUseRecreatedLease() => PostgreSql(Scenario.LateFirstCommit);
    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlUnleasedCommitRequiresCurrentIncarnation() => PostgreSql(Scenario.UnleasedIncarnation);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlLeasedCommitRequiresCurrentIncarnationAtCurrentEpoch() => PostgreSql(Scenario.LeasedIncarnation);

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
            Scenario.LeasedIncarnation => LeasedIncarnation(store, expire),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

    private static async Task IncreasingEpochs(IWorkflowStateRepository store, Func<string, Task> expire)
    {
        var id = Guid.NewGuid().ToString("N");
        var otherId = Guid.NewGuid().ToString("N");
        try
        {
            var first = (await store.TryAcquireExecutionLeaseAsync(id, "first", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.NotNull(first);
            Assert.True(first.Epoch > 0);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(Instance(id), first.Epoch, 0))).Status);
            var other = (await store.TryAcquireExecutionLeaseAsync(otherId, "other", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.NotNull(other);
            Assert.True(other.Epoch > first.Epoch);
            Assert.True((await store.ReleaseExecutionLeaseAsync(otherId, "other")).Released);
            var next = (await store.TryAcquireExecutionLeaseAsync(otherId, "next", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.NotNull(next);
            Assert.True(next.Epoch > other.Epoch);

            await store.DeleteWorkflowInstanceAsync(id, Tombstones);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(id, "reserved", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
            await expire(id);
            var recreated = (await store.TryAcquireExecutionLeaseAsync(id, "recreated", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
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
                return await store.TryAcquireExecutionLeaseAsync(id, "owner", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
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
            var old = (await store.TryAcquireExecutionLeaseAsync(id, "host-a", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            var late = CheckpointCommit.Create(Instance(id, "old"), old.Epoch, 0);
            Assert.Null(await store.GetWorkflowInstanceAsync(id));
            await store.DeleteWorkflowInstanceAsync(id);
            Assert.Null(await store.GetTombstoneAsync(id));
            var current = (await store.TryAcquireExecutionLeaseAsync(id, "host-b", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.True(current.Epoch > old.Epoch);
            var first = CheckpointCommit.Create(Instance(id, "replacement"), current.Epoch, 0);

            CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, 0),
                await store.CommitWorkflowInstanceAsync(late));
            Assert.Null(await store.GetWorkflowInstanceAsync(id));
            Assert.True((await store.RenewExecutionLeasesAsync([new(id, "host-b", Duration)]))[0].Status == ExecutionLeaseRenewalStatus.Renewed);
            CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 1),
                await store.CommitWorkflowInstanceAsync(first));
            CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, 1),
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
                CheckpointCommit.Create(original, (await StoreTestSupport.LeaseAsync(store, original.InstanceId)).Epoch, 0, wakeAfter: TimeSpan.Zero))).Status);
            original = (await store.GetWorkflowInstanceAsync(id))!.CopyForResume();
            original.Status = WorkflowStatus.Completed;
            var late = CheckpointCommit.Create(original, (await StoreTestSupport.LeaseAsync(store, original.InstanceId)).Epoch, original.Revision);
            await store.DeleteWorkflowInstanceAsync(id, Tombstones);
            await expire(id);
            var replacement = Instance(id, "replacement");
            replacement.Status = WorkflowStatus.Suspended;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(replacement, (await StoreTestSupport.LeaseAsync(store, replacement.InstanceId)).Epoch, 0, wakeAfter: TimeSpan.Zero))).Status);
            replacement = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(late.ExpectedRevision, replacement.Revision);
            Assert.NotEqual(late.Incarnation, replacement.Incarnation);

            CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, replacement.Revision),
                await store.CommitWorkflowInstanceAsync(late));
            var unchanged = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(replacement.Revision, unchanged.Revision);
            Assert.Equal(replacement.Incarnation, unchanged.Incarnation);
            Assert.Equal(WorkflowStatus.Suspended, unchanged.Status);
            Assert.Equal(replacement.WorkflowDataJson, unchanged.WorkflowDataJson);
            var applied = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(unchanged,
                (await StoreTestSupport.LeaseAsync(store, unchanged.InstanceId)).Epoch, unchanged.Revision));
            CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 2),
                applied with { Notice = null, MadeRunnable = [] });
            Assert.Empty(applied.MadeRunnable);
            Assert.NotNull(applied.Notice);
            Assert.Equal(id, applied.Notice.InstanceId);
            Assert.True(applied.Notice.Idle);
            Assert.False(applied.Notice.Claimable);
        }
        finally { await store.DeleteWorkflowInstanceAsync(id); }
    }

    private static async Task LeasedIncarnation(IWorkflowStateRepository store, Func<string, Task> expire)
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var old = (await store.TryAcquireExecutionLeaseAsync(id, "old-owner", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.NotNull(old);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(Instance(id, "old"), old.Epoch, 0))).Status);
            var original = (await store.GetWorkflowInstanceAsync(id))!.CopyForResume();
            await store.DeleteWorkflowInstanceAsync(id, Tombstones);
            await expire(id);
            var current = (await store.TryAcquireExecutionLeaseAsync(id, "current-owner", Duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.NotNull(current);
            Assert.True(current.Epoch > old.Epoch);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(Instance(id, "replacement"), current.Epoch, 0))).Status);
            var replacement = (await store.GetWorkflowInstanceAsync(id))!.CopyForResume();
            Assert.Equal(original.Revision, replacement.Revision);
            Assert.True(replacement.Revision > 0);
            Assert.NotEqual(original.Incarnation, replacement.Incarnation);
            var before = JsonSerializer.Serialize(replacement);

            // The epoch and revision are current: only the incarnation can fence this payload.
            var stale = CheckpointCommit.Create(original.CopyForResume(), current.Epoch, replacement.Revision);
            CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, replacement.Revision),
                await store.CommitWorkflowInstanceAsync(stale));
            var unchanged = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(replacement.Revision, unchanged.Revision);
            Assert.Equal(replacement.Incarnation, unchanged.Incarnation);
            Assert.Equal(before, JsonSerializer.Serialize(unchanged));
            Assert.True((await store.RenewExecutionLeasesAsync([new(id, "current-owner", Duration)]))[0].Status == ExecutionLeaseRenewalStatus.Renewed);
            CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, replacement.Revision + 1),
                await store.CommitWorkflowInstanceAsync(
                    CheckpointCommit.Create(unchanged.CopyForResume(), current.Epoch, unchanged.Revision)));
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(replacement.Revision + 1, saved.Revision);
            Assert.Equal(replacement.Incarnation, saved.Incarnation);
            Assert.Equal(replacement.WorkflowDataJson, saved.WorkflowDataJson);
        }
        finally { await store.DeleteWorkflowInstanceAsync(id); }
    }

    private static WorkflowInstance Instance(string id, string owner = "first") => new()
    { ExecutionStateJson = StoreTestSupport.CheckpointJson,
        InstanceId = id, WorkflowName = "StoreWideEpoch", Status = WorkflowStatus.Running,
        WorkflowDataJson = "{\"owner\":\"" + owner + "\"}"
    };
}
