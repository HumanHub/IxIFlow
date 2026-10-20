using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "C-02")]
[Trait("Spec", "C-11")]
public sealed class SkipLockedClaimTests
{
    private static readonly TimeSpan ClaimBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly ClaimQuery Query = new(Guid.NewGuid(), new Dictionary<string, string>(), 100);

    [Fact]
    public Task MemoryRunnableScanContinuesInOrder() => RunnableScanContinuesInOrder(new InMemoryWorkflowStateRepository());

    [Fact]
    public async Task SqliteRunnableScanContinuesInOrder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "skip-locked-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "state.db"));
            await RunnableScanContinuesInOrder(store);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerRunnableScanContinuesInOrder() => RunnableScanContinuesInOrder(Provider(false).Store);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRunnableScanContinuesInOrder() => RunnableScanContinuesInOrder(Provider(true).Store);

    private static async Task RunnableScanContinuesInOrder(IWorkflowStateRepository store)
    {
        var instances = Enumerable.Range(0, 12).Select(_ => Instance()).ToArray();
        try
        {
            foreach (var instance in instances) await store.SeedAsync(instance, query: Query);
            var all = await store.GetRunnableInstancesAsync(Query);
            Assert.Equal(instances.Length, all.Count);
            for (var offset = 0; offset <= all.Count; offset += 3)
                Assert.Equal(all.Skip(offset).Take(3), await store.GetRunnableInstancesAsync(Query.Continue(offset, 3)));
        }
        finally
        {
            foreach (var instance in instances) await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    [Fact]
    public async Task AWakeImmediatelyRetriesARefusedInstance()
    {
        var time = new FakeTimeProvider();
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var client = new Mock<IWorkflowClient>();
        client.Setup(c => c.RecoverAsync("wake", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var attempt = Interlocked.Increment(ref calls);
                if (attempt > 1) retried.TrySetResult();
                return Task.FromResult(new WorkflowExecutionResult { InstanceId = "wake",
                    Status = attempt == 1 ? WorkflowExecutionStatus.Running : WorkflowExecutionStatus.Success });
            });
        var store = new Mock<IWorkflowStateRepository>();
        store.Setup(r => r.GetRunnableInstancesAsync(It.IsAny<ClaimQuery>())).Returns<ClaimQuery>(query =>
        {
            if (Volatile.Read(ref calls) == 1) refused.TrySetResult();
            return Task.FromResult<IReadOnlyList<RunnableInstance>>(query.ScanOffset == 0
                ? [new("wake", time.GetUtcNow().UtcDateTime)] : []);
        });
        var wake = new TestWakeSource();
        using var provider = RecoveryProvider(client.Object);
        using var scanner = new WorkflowRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(), store.Object,
            NullLogger<WorkflowRecoveryService>.Instance, timeProvider: time,
            settings: HostSettings.Default with { Slots = 1, PollInterval = TimeSpan.FromHours(1) }, wakeSource: wake);
        await scanner.StartAsync(CancellationToken.None);
        try
        {
            await refused.Task.WaitAsync(ClaimBound);
            await wake.Listening.Task.WaitAsync(ClaimBound);
            wake.Wake!();
            await retried.Task.WaitAsync(ClaimBound);
            Assert.Equal(2, Volatile.Read(ref calls)); // The fake poll clock has never advanced.
        }
        finally { await scanner.StopAsync(CancellationToken.None).WaitAsync(DrainBound); }
    }

    private sealed class TestWakeSource : IRunnableWakeSource
    {
        internal readonly TaskCompletionSource Listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? Wake;
        public async Task ListenAsync(Action wake, CancellationToken cancellationToken)
        {
            Wake = wake;
            Listening.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerHostStampRefusesSharedLocksWithEitherSnapshotSetting()
    {
        foreach (var snapshot in new[] { "OFF", "ON" })
        {
            var cs = await SqlServerTestServer.CreateDatabaseAsync("IxIFlow_SharedClaim");
            try
            {
                await using (var connection = new SqlConnection(cs))
                {
                    await connection.OpenAsync();
                    await connection.ExecuteAsync($"ALTER DATABASE [{connection.Database}] SET READ_COMMITTED_SNAPSHOT {snapshot}");
                }
                var (store, connect) = Provider(false, cs);
                foreach (var kind in Enum.GetValues<ExecutionClaimKind>())
                foreach (var existingLease in new[] { false, true })
                {
                    var instance = Instance();
                    Task<ExecutionLeaseGrant?>? attempt = null;
                    try
                    {
                        await store.SeedAsync(instance, query: Query);
                        if (kind == ExecutionClaimKind.Termination)
                            await store.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "test" });
                        if (!existingLease)
                        {
                            await using var connection = connect();
                            await connection.OpenAsync();
                            await connection.ExecuteAsync("DELETE FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId=@Id",
                                new { Id = instance.InstanceId });
                        }
                        await using (var hold = await HoldAsync(connect))
                        {
                            await hold.Connection.QueryAsync<string>("""
                                SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (HOLDLOCK, ROWLOCK)
                                WHERE InstanceId=@Id
                                """, new { Id = instance.InstanceId }, hold.Transaction);
                            attempt = ClaimAsync(store, instance.InstanceId, kind);
                            Assert.Null(await attempt.WaitAsync(ClaimBound));
                            // The lease insert/update and host assignment must have rolled back.
                            var lease = await hold.Connection.QuerySingleOrDefaultAsync<(string Token, Guid? Host)>("""
                                SELECT Token, HostId FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId=@Id
                                """, new { Id = instance.InstanceId }, hold.Transaction);
                            Assert.NotEqual("claim", lease.Token);
                            Assert.Equal(existingLease ? 1 : 0, await hold.Connection.ExecuteScalarAsync<int>(
                                "SELECT COUNT(*) FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId=@Id",
                                new { Id = instance.InstanceId }, hold.Transaction));
                        }
                        Assert.NotNull(await ClaimAsync(store, instance.InstanceId, kind));
                    }
                    finally
                    {
                        try { if (attempt != null) await attempt.WaitAsync(DrainBound); }
                        finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
                    }
                }
            }
            finally { await SqlServerTestServer.DropDatabaseAsync(cs); }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerIdentityResourceChecksItsLengthBeforeAcquiring()
    {
        var (store, connect) = Provider(false);
        await store.GetRunnableInstancesAsync(Query); // Initialize schema before using provider SQL.
        await using var hold = await HoldAsync(connect);
        foreach (var statement in new[] { "LockIdentity", "Running" })
        {
            var method = store.GetType().GetMethod("Sql", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var sql = (string)method.Invoke(store, [Enum.Parse(method.GetParameters()[0].ParameterType, statement)])!;
            await hold.Connection.ExecuteScalarAsync<int>(sql, new { InstanceId = new string('x', 238) }, hold.Transaction);
            var error = await Assert.ThrowsAsync<SqlException>(() => hold.Connection.ExecuteAsync(sql,
                new { InstanceId = new string('x', 239) }, hold.Transaction));
            Assert.Equal(51000, error.Number);
        }
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlTrailingSpacesAreDistinctKeysAndIdentities()
    {
        var (store, connect) = Provider(true);
        var instance = Instance();
        var alias = Instance(instance.InstanceId + " ");
        try
        {
            await store.SeedAsync(instance, query: Query);
            await store.SeedAsync(alias, query: Query);
            await using var hold = await HoldAsync(connect);
            await HoldIdentityAsync(hold, instance.InstanceId);
            Assert.Null(await ClaimAsync(store, instance.InstanceId));
            Assert.NotNull(await ClaimAsync(store, alias.InstanceId));
            Assert.NotNull(await store.GetWorkflowInstanceAsync(instance.InstanceId));
            Assert.NotNull(await store.GetWorkflowInstanceAsync(alias.InstanceId));
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
            await store.DeleteWorkflowInstanceAsync(alias.InstanceId);
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerOrdinaryCommitExcludesClaimsAndDeletesByItsRow() => OrdinaryCommitExcludesClaimsAndDeletesByItsRow(false);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlOrdinaryCommitExcludesClaimsAndDeletesByItsRow() => OrdinaryCommitExcludesClaimsAndDeletesByItsRow(true);

    private static async Task OrdinaryCommitExcludesClaimsAndDeletesByItsRow(bool postgres)
    {
        var (store, connect) = Provider(postgres);
        var instance = Instance();
        Task<WorkflowCommitResult>? committing = null;
        Task? deleting = null;
        Task<ExecutionLeaseGrant?>? claiming = null;
        try
        {
            await store.SeedAsync(instance, query: Query);
            var grant = (await ClaimAsync(store, instance.InstanceId))!;
            var commit = CheckpointCommit.Create(instance, grant.Epoch, instance.Revision);
            await using (var hold = await HoldAsync(connect))
            {
                // Stop the real commit after its instance row lock, at its permitted lease wait.
                await hold.Connection.ExecuteAsync(postgres
                    ? "UPDATE ixiflow_workflow_leases SET token=token WHERE instance_id=@Id"
                    : "UPDATE dbo.IxIFlowWorkflowLeases WITH (ROWLOCK) SET Token=Token WHERE InstanceId=@Id",
                    new { Id = instance.InstanceId }, hold.Transaction);
                committing = store.CommitWorkflowInstanceAsync(commit);
                var writer = await WaitForBlockedSessionAsync(connect, hold);
                claiming = ClaimAsync(store, instance.InstanceId);
                Assert.Null(await claiming.WaitAsync(ClaimBound));
                deleting = store.DeleteWorkflowInstanceAsync(instance.InstanceId);
                await WaitForBlockedSessionAsync(connect, writer, postgres);
                Assert.False(deleting.IsCompleted);
            }
            Assert.Equal(WorkflowCommitStatus.Applied, (await committing.WaitAsync(DrainBound)).Status);
            await deleting.WaitAsync(DrainBound);
            Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));
        }
        finally
        {
            try { await Task.WhenAll(new Task[] { committing ?? Task.CompletedTask,
                deleting ?? Task.CompletedTask, claiming ?? Task.CompletedTask }).WaitAsync(DrainBound); }
            finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerOrdinaryCommitAndClaimCommandBudgets() => OrdinaryCommitAndClaimCommandBudgets(false);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlOrdinaryCommitAndClaimCommandBudgets() => OrdinaryCommitAndClaimCommandBudgets(true);

    private static async Task OrdinaryCommitAndClaimCommandBudgets(bool postgres)
    {
        var (store, connect) = Provider(postgres);
        var instance = Instance();
        Task<WorkflowCommitResult>? committing = null;
        try
        {
            await store.SeedAsync(instance, query: Query);
            ExecutionLeaseGrant grant;
            using (var budget = new CommandBudget())
            {
                grant = (await ClaimAsync(store, instance.InstanceId))!;
                Assert.NotNull(grant);
                // Each Dapper command is one round trip; transaction begin/commit are excluded.
                Assert.Equal(7, budget.Commands.Count);
                Assert.Single(budget.Commands, sql => sql.Contains(postgres
                    ? "pg_try_advisory_xact_lock" : "sp_getapplock", StringComparison.Ordinal));
            }
            instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            var commit = CheckpointCommit.Create(instance, grant.Epoch, instance.Revision);
            Assert.Empty(commit.Parts);
            Assert.False(commit.ReplacesParts);
            await using (var hold = await HoldAsync(connect))
            {
                await HoldIdentityAsync(hold, instance.InstanceId);
                using (var budget = new CommandBudget())
                {
                    committing = store.CommitWorkflowInstanceAsync(commit);
                    Assert.Equal(WorkflowCommitStatus.Applied, (await committing.WaitAsync(ClaimBound)).Status);
                    Assert.Equal(12, budget.Commands.Count);
                    Assert.DoesNotContain(budget.Commands, sql => sql.Contains(postgres
                        ? "pg_advisory_xact_lock" : "sp_getapplock", StringComparison.Ordinal));
                }
                // Receipt replay is also an ordinary existing-row commit.
                Assert.Equal(WorkflowCommitStatus.AlreadyApplied,
                    (await store.CommitWorkflowInstanceAsync(commit).WaitAsync(ClaimBound)).Status);
            }
        }
        finally
        {
            try { if (committing != null) await committing.WaitAsync(DrainBound); }
            finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
        }
    }

    // Scope counters through AsyncLocal so parallel tests and ownership/main pools cannot
    // contaminate each other. Count command batches, not statements inside a batch.
    private sealed class CommandBudget : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private static readonly AsyncLocal<CommandBudget?> Current = new();
        private readonly IDisposable _diagnostics;
        private readonly ActivityListener _activities;
        private readonly List<IDisposable> _subscriptions = [];
        internal readonly ConcurrentQueue<string> Commands = new();
        internal CommandBudget()
        {
            Current.Value = this;
            _diagnostics = DiagnosticListener.AllListeners.Subscribe(this);
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    Current.Value == this ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
                ActivityStopped = activity =>
                {
                    if (Current.Value == this && activity.GetTagItem("db.query.text") is string sql &&
                        !sql.Equals("COMMIT", StringComparison.OrdinalIgnoreCase) &&
                        !sql.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase)) Commands.Enqueue(sql);
                }
            };
            ActivitySource.AddActivityListener(_activities);
        }
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "SqlClientDiagnosticListener") _subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Current.Value == this && value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal) &&
                value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) is SqlCommand command)
                Commands.Enqueue(command.CommandText);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            Current.Value = null;
            _diagnostics.Dispose();
            _activities.Dispose();
            foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }

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
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync($"ALTER DATABASE [{connection.Database}] SET READ_COMMITTED_SNAPSHOT ON");
                Assert.True(await connection.ExecuteScalarAsync<bool>(
                    "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
            }
            await NowaitRollsBackWithEitherXactAbortSetting(connectionString);
            await ClaimsSkipLockedRows(false, connectionString);
            await ClaimPassReachesTheFreeInstance(false, connectionString);
            await RecoveryPassReachesTheFreeInstance(false, connectionString);
            await LockedLiveLeaseDoesNotDisplaceAFreeCandidate(connectionString);
        }
        finally { await SqlServerTestServer.DropDatabaseAsync(connectionString); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerUnrelatedAbsentIdsInTheSameGapCanBeClaimed()
    {
        // Gap/page-lock assertions need an otherwise idle database, including under suite load.
        var connectionString = await SqlServerTestServer.CreateDatabaseAsync("IxIFlow_SkipLockedGap");
        try { await UnrelatedAbsentIdsInTheSameGapCanBeClaimed(connectionString); }
        finally { await SqlServerTestServer.DropDatabaseAsync(connectionString); }
    }

    private static async Task UnrelatedAbsentIdsInTheSameGapCanBeClaimed(string connectionString)
    {
        var (store, connect) = Provider(false, connectionString);
        var prefix = "gap-" + Guid.NewGuid().ToString("N");
        var lower = Instance(prefix + "-0");
        var upper = Instance(prefix + "-z");
        var heldId = prefix + "-a";
        Task<ExecutionLeaseGrant?>? attempt = null;
        try
        {
            await store.SeedAsync(lower, query: Query);
            await store.SeedAsync(upper, query: Query);
            foreach (var insert in new[] { false, true })
            {
                var claimedId = prefix + (insert ? "-c" : "-b");
                await using (var hold = await HoldAsync(connect))
                {
                    if (insert) await InsertSqlServerCopyAsync(hold, heldId, lower.InstanceId);
                    else await hold.Connection.ExecuteAsync("""
                        SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                        WHERE InstanceId = @Id
                        """, new { Id = heldId }, hold.Transaction);
                    attempt = ClaimAsync(store, claimedId);
                    Assert.NotNull(await attempt.WaitAsync(ClaimBound));
                }
                Assert.True((await store.ReleaseExecutionLeaseAsync(claimedId, "claim")).Released);
            }
        }
        finally
        {
            try { if (attempt != null) await attempt.WaitAsync(DrainBound); }
            finally
            {
                await store.DeleteWorkflowInstanceAsync(heldId);
                await store.DeleteWorkflowInstanceAsync(prefix + "-b");
                await store.DeleteWorkflowInstanceAsync(prefix + "-c");
                await store.DeleteWorkflowInstanceAsync(lower.InstanceId);
                await store.DeleteWorkflowInstanceAsync(upper.InstanceId);
            }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerUncommittedInsertAndDeleteRefuseTheClaimedKey()
    {
        var (store, connect) = Provider(false);
        var source = Instance();
        var insertedId = Guid.NewGuid().ToString("N");
        Task<ExecutionLeaseGrant?>? attempt = null;
        try
        {
            await store.SeedAsync(source, query: Query);
            foreach (var insert in new[] { true, false })
            {
                var id = insert ? insertedId : source.InstanceId;
                await using (var hold = await HoldAsync(connect))
                {
                    if (insert) await InsertSqlServerCopyAsync(hold, id, source.InstanceId);
                    else await hold.Connection.ExecuteAsync(
                        "DELETE FROM dbo.IxIFlowWorkflowInstances WITH (ROWLOCK) WHERE InstanceId = @Id",
                        new { Id = id }, hold.Transaction);
                    attempt = ClaimAsync(store, id);
                    Assert.Null(await attempt.WaitAsync(ClaimBound));
                }
                Assert.NotNull(await ClaimAsync(store, id));
            }
        }
        finally
        {
            try { if (attempt != null) await attempt.WaitAsync(DrainBound); }
            finally
            {
                await store.DeleteWorkflowInstanceAsync(insertedId);
                await store.DeleteWorkflowInstanceAsync(source.InstanceId);
            }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerNowaitRollsBackWithEitherXactAbortSetting() => NowaitRollsBackWithEitherXactAbortSetting();

    private static async Task NowaitRollsBackWithEitherXactAbortSetting(string? connectionString = null)
    {
        var (store, connect) = Provider(false, connectionString);
        var instance = Instance();
        Task<int>? attempt = null;
        try
        {
            await store.SeedAsync(instance, query: Query);
            await using var hold = await HoldRowAsync(connect, instance.InstanceId, false);
            foreach (var setting in new[] { "ON", "OFF" })
            {
                await using var connection = connect();
                await connection.OpenAsync();
                await connection.ExecuteAsync("SET XACT_ABORT " + setting);
                var begin = store.GetType().GetMethod("BeginTransactionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var operation = Enum.Parse(begin.GetParameters()[1].ParameterType, "LeaseClaim");
                await using var transaction = (await (ValueTask<DbTransaction?>)begin.Invoke(store, [connection, operation])!)!;
                Assert.Equal(IsolationLevel.ReadCommitted, transaction.IsolationLevel);
                // Exercise the provider's exception handler through an actual SqlClient command,
                // on a session whose option is explicit rather than dependent on pool defaults.
                var read = store.GetType().GetMethod("ReadClaimStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                attempt = (Task<int>)read.Invoke(store, [connection, transaction, instance.InstanceId])!;
                Assert.Equal(-1, await attempt.WaitAsync(ClaimBound));
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT @@TRANCOUNT"));
            }
        }
        finally
        {
            try { if (attempt != null) await attempt.WaitAsync(DrainBound); }
            finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
        }
    }

    private static Task InsertSqlServerCopyAsync(LockHold hold, string id, string source) =>
        hold.Connection.ExecuteAsync("""
            INSERT INTO dbo.IxIFlowWorkflowInstances
                (InstanceId, WorkflowName, WorkflowVersion, CreatedAtUtc, Status, RunnableAtUtc, DemandSetId, StateJson, Revision)
            SELECT @Id, WorkflowName, WorkflowVersion, CreatedAtUtc, Status, RunnableAtUtc, DemandSetId,
                JSON_MODIFY(StateJson, '$.InstanceId', @Id), Revision
            FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @Source
            """, new { Id = id, Source = source }, hold.Transaction);

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
        WorkflowDataJson = StoredJson.Inline("{}")
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
                try { if (attempt != null) await attempt.WaitAsync(DrainBound); }
                finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
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
                try { if (pass != null) await pass.WaitAsync(DrainBound); }
                finally
                {
                    await store.DeleteWorkflowInstanceAsync(locked.InstanceId);
                    await store.DeleteWorkflowInstanceAsync(free.InstanceId);
                }
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
                try { await all.WaitAsync(DrainBound); }
                finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
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
            try { if (attempt != null) await attempt.WaitAsync(DrainBound); }
            finally
            {
                await store.DeleteWorkflowInstanceAsync(id);
                await store.DeleteWorkflowInstanceAsync(source.InstanceId);
            }
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
            try { if (attempt != null) await attempt.WaitAsync(DrainBound); }
            finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerLockedLiveLeaseDoesNotDisplaceAFreeCandidate() => LockedLiveLeaseDoesNotDisplaceAFreeCandidate();

    private static async Task LockedLiveLeaseDoesNotDisplaceAFreeCandidate(string? connectionString = null)
    {
        var (store, connect) = Provider(false, connectionString);
        var leased = Instance("0-leased-" + Guid.NewGuid().ToString("N"));
        var free = Instance("1-free-" + Guid.NewGuid().ToString("N"));
        Task<IReadOnlyList<RunnableInstance>>? discovery = null;
        try
        {
            await store.SeedAsync(leased, query: Query);
            await store.SeedAsync(free, query: Query);
            Assert.NotNull(await ClaimAsync(store, leased.InstanceId));
            await using (var hold = await HoldAsync(connect))
            {
                var holder = await hold.Connection.ExecuteScalarAsync<int>("SELECT @@SPID", transaction: hold.Transaction);
                await hold.Connection.ExecuteAsync("""
                    UPDATE dbo.IxIFlowWorkflowLeases WITH (ROWLOCK) SET Token = Token WHERE InstanceId = @Id
                    """, new { Id = leased.InstanceId }, hold.Transaction);
                discovery = store.GetRunnableInstancesAsync(new ClaimQuery(Query.HostId, Query.Capabilities, 1));
                // Observe either completion (versioned lease read) or a real lease-lock wait.
                // A READPAST regression must be checked before releasing the lease holder.
                await using var observer = connect();
                await observer.OpenAsync();
                using var timeout = new CancellationTokenSource(ClaimBound);
                while (!discovery.IsCompleted && !await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.dm_exec_requests
                        WHERE blocking_session_id = @Holder AND wait_type LIKE 'LCK_M%') THEN 1 ELSE 0 END AS bit)
                    """, new { Holder = holder }, cancellationToken: timeout.Token)))
                    await Task.Delay(10, timeout.Token);
                if (discovery.IsCompleted)
                    Assert.Equal(free.InstanceId, Assert.Single(await discovery).InstanceId);
            }
            Assert.Equal(free.InstanceId, Assert.Single(await discovery.WaitAsync(ClaimBound)).InstanceId);
        }
        finally
        {
            try { if (discovery != null) await discovery.WaitAsync(DrainBound); }
            finally
            {
                await store.DeleteWorkflowInstanceAsync(leased.InstanceId);
                await store.DeleteWorkflowInstanceAsync(free.InstanceId);
            }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerRecoveryPassRefillsItsOnlySlot() => RecoveryPassReachesTheFreeInstance(false);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlRecoveryPassRefillsItsOnlySlot() => RecoveryPassReachesTheFreeInstance(true);

    private static async Task RecoveryPassReachesTheFreeInstance(bool postgres, string? connectionString = null)
    {
        var (store, connect) = Provider(postgres, connectionString);
        await RecoveryPassReachesTheFreeInstance(store, async id => await HoldRowAsync(connect, id, false));
    }

    [Fact]
    public async Task MemoryRecoveryPassRefillsItsOnlySlot()
    {
        var store = DispatchProxy.Create<IWorkflowStateRepository, ContendedStore>();
        var proxy = (ContendedStore)(object)store;
        await RecoveryPassReachesTheFreeInstance(store, id =>
        {
            proxy.HeldId = id;
            return Task.FromResult<IAsyncDisposable>(new ReleaseContention(proxy));
        });
    }

    private static async Task RecoveryPassReachesTheFreeInstance(IWorkflowStateRepository store,
        Func<string, Task<IAsyncDisposable>> holdInstance)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => options.Slots = 1);
        services.Replace(ServiceDescriptor.Singleton(store));
        await using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<RecoveryData>("SkipLockedRecovery").Step<RecoveryNoop>().Build();
        await provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var query = await StoreTestSupport.ClaimAsync(provider, 1);
        var (locked, _, _) = WorkflowInstances.NewInstance(definition, new RecoveryData(),
            new() { InstanceId = "0-locked-" + Guid.NewGuid().ToString("N") }, DateTime.UtcNow);
        var (free, _, _) = WorkflowInstances.NewInstance(definition, new RecoveryData(),
            new() { InstanceId = "1-free-" + Guid.NewGuid().ToString("N") }, DateTime.UtcNow);
        using var scanner = new WorkflowRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(), store,
            NullLogger<WorkflowRecoveryService>.Instance, settings: provider.GetRequiredService<HostSettings>());
        Task? pass = null;
        try
        {
            await store.SeedAsync(locked, query: query);
            await store.SeedAsync(free, query: query);
            Assert.Equal(locked.InstanceId, Assert.Single(await store.GetRunnableInstancesAsync(query)).InstanceId);
            // An update lock conflicts with the claim but allows SQL Server's initial engine reads
            // and covering-index discovery, so the real service must refill after a no-grant result.
            await using (var hold = await holdInstance(locked.InstanceId))
            {
                pass = scanner.RecoverOnceAsync(CancellationToken.None);
                await pass.WaitAsync(ClaimBound);
                Assert.Equal(WorkflowStatus.Completed, (await store.GetWorkflowInstanceAsync(free.InstanceId))!.Status);
                Assert.Equal(WorkflowStatus.Running, (await store.GetWorkflowInstanceAsync(locked.InstanceId))!.Status);
            }
            // The next pass retries immediately, with no one-pass refusal exclusion.
            await scanner.RecoverOnceAsync(CancellationToken.None).WaitAsync(ClaimBound);
            Assert.Equal(WorkflowStatus.Completed, (await store.GetWorkflowInstanceAsync(locked.InstanceId))!.Status);
        }
        finally
        {
            try { if (pass != null) await pass.WaitAsync(DrainBound); }
            finally
            {
                await store.DeleteWorkflowInstanceAsync(locked.InstanceId);
                await store.DeleteWorkflowInstanceAsync(free.InstanceId);
            }
        }
    }

    public sealed class RecoveryData;
    public sealed class RecoveryNoop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public class ContendedStore : DispatchProxy
    {
        private readonly IWorkflowStateRepository _inner = new InMemoryWorkflowStateRepository();
        internal string? HeldId;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync) &&
                (string?)args![0] == HeldId)
                return Task.FromResult<ExecutionLeaseGrant?>(null);
            try { return method.Invoke(_inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
    }

    private sealed class ReleaseContention(ContendedStore store) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { store.HeldId = null; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task RecoveryPassTriesCandidatesInOrderOnceAndStopsAtItsBound()
    {
        var candidates = Enumerable.Range(0, 2 * WorkflowRecoveryService.MaxCandidatesPerPass + 2)
            .Select(i => new RunnableInstance(i.ToString(), DateTime.UtcNow)).ToArray();
        var tried = new List<string>();
        var client = new Mock<IWorkflowClient>();
        client.Setup(c => c.RecoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((id, _) =>
            {
                tried.Add(id);
                return Task.FromResult(new WorkflowExecutionResult { InstanceId = id,
                    Status = id == candidates[^1].InstanceId ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Running });
            });
        using var provider = RecoveryProvider(client.Object);
        var discoveries = 0;
        var store = new Mock<IWorkflowStateRepository>();
        store.Setup(s => s.GetRunnableInstancesAsync(It.IsAny<ClaimQuery>())).Returns<ClaimQuery>(query =>
        {
            discoveries++;
            return Task.FromResult<IReadOnlyList<RunnableInstance>>(candidates.Skip((int)query.ScanOffset).Take(query.Limit).ToArray());
        });
        using var scanner = new WorkflowRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(), store.Object,
            NullLogger<WorkflowRecoveryService>.Instance, settings: HostSettings.Default with { Slots = 1 });
        await scanner.RecoverOnceAsync(CancellationToken.None).WaitAsync(ClaimBound);
        Assert.Equal(candidates.Take(WorkflowRecoveryService.MaxCandidatesPerPass).Select(c => c.InstanceId), tried);
        Assert.Equal(1 + (WorkflowRecoveryService.MaxCandidatesPerPass - 1 + WorkflowRecoveryService.RefillPageSize - 1)
            / WorkflowRecoveryService.RefillPageSize, discoveries);
        tried.Clear();
        await scanner.RecoverOnceAsync(CancellationToken.None).WaitAsync(ClaimBound);
        Assert.Equal(candidates.Skip(WorkflowRecoveryService.MaxCandidatesPerPass)
            .Take(WorkflowRecoveryService.MaxCandidatesPerPass).Select(c => c.InstanceId), tried);
        tried.Clear();
        await scanner.RecoverOnceAsync(CancellationToken.None).WaitAsync(ClaimBound);
        Assert.Equal(candidates.Skip(2 * WorkflowRecoveryService.MaxCandidatesPerPass).Select(c => c.InstanceId), tried);
        tried.Clear();
        await scanner.RecoverOnceAsync(CancellationToken.None).WaitAsync(ClaimBound);
        Assert.Equal(candidates.Take(WorkflowRecoveryService.MaxCandidatesPerPass).Select(c => c.InstanceId), tried);
    }

    [Fact]
    public async Task RecoveryPassRefillsWhileAnotherSlotIsStillRunning()
    {
        var candidates = new[] { "slow", "busy", "free", "unused" }.Select(id => new RunnableInstance(id, DateTime.UtcNow)).ToArray();
        var slow = new TaskCompletionSource<WorkflowExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedFree = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedUnused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IWorkflowClient>();
        client.Setup(c => c.RecoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((id, _) =>
            {
                if (id == "slow") return slow.Task;
                if (id == "free") reachedFree.TrySetResult();
                if (id == "unused") reachedUnused.TrySetResult();
                return Task.FromResult(new WorkflowExecutionResult { InstanceId = id,
                    Status = id == "busy" ? WorkflowExecutionStatus.Running : WorkflowExecutionStatus.Success });
            });
        using var provider = RecoveryProvider(client.Object);
        using var scanner = Scanner(provider, candidates, 2);
        var pass = scanner.RecoverOnceAsync(CancellationToken.None);
        try
        {
            await reachedFree.Task.WaitAsync(ClaimBound);
            Assert.False(slow.Task.IsCompleted);
            await scanner.RecoverOnceAsync(CancellationToken.None).WaitAsync(ClaimBound);
            await reachedUnused.Task.WaitAsync(ClaimBound);
            Assert.False(slow.Task.IsCompleted);
        }
        finally
        {
            slow.TrySetResult(new WorkflowExecutionResult { InstanceId = "slow", Status = WorkflowExecutionStatus.Success });
            await pass.WaitAsync(DrainBound);
        }
        client.Verify(c => c.RecoverAsync("busy", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RefillDiscoveryFailureDoesNotCancelASiblingRun()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<WorkflowExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken siblingToken = default;
        var client = new Mock<IWorkflowClient>();
        client.Setup(c => c.RecoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (id, token) =>
            {
                if (id == "slow")
                {
                    siblingToken = token;
                    entered.TrySetResult();
                    return await finish.Task.WaitAsync(token);
                }
                await entered.Task;
                return new WorkflowExecutionResult { InstanceId = id, Status = WorkflowExecutionStatus.Running };
            });
        var store = new Mock<IWorkflowStateRepository>();
        store.SetupSequence(s => s.GetRunnableInstancesAsync(It.IsAny<ClaimQuery>()))
            .ReturnsAsync(new[] { "slow", "busy" }.Select(id => new RunnableInstance(id, DateTime.UtcNow)).ToArray())
            .ThrowsAsync(new InvalidOperationException("refill failed"));
        var log = new RefillLog();
        using var provider = RecoveryProvider(client.Object);
        using var scanner = new WorkflowRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(), store.Object,
            log, settings: HostSettings.Default with { Slots = 2 });
        var pass = scanner.RecoverOnceAsync(CancellationToken.None);
        try
        {
            await log.Failed.Task.WaitAsync(ClaimBound);
            Assert.False(siblingToken.IsCancellationRequested);
            Assert.False(pass.IsCompleted);
        }
        finally
        {
            finish.TrySetResult(new WorkflowExecutionResult { InstanceId = "slow", Status = WorkflowExecutionStatus.Success });
            await pass.WaitAsync(DrainBound);
        }
        Assert.Contains("Could not refill workflows for recovery", Assert.Single(log.Errors));
    }

    private sealed class RefillLog : ILogger<WorkflowRecoveryService>
    {
        internal readonly TaskCompletionSource Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Errors = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (level != LogLevel.Error) return;
            Errors.Enqueue(formatter(state, error));
            Failed.TrySetResult();
        }
    }

    [Fact]
    public async Task OverlappingPassesKeepEachOthersReservations()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRefill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRun = new TaskCompletionSource<WorkflowExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterRun = new TaskCompletionSource<WorkflowExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var client = new Mock<IWorkflowClient>();
        client.Setup(c => c.RecoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((id, _) =>
            {
                if (id == "a")
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                        return Task.FromResult(new WorkflowExecutionResult { InstanceId = id, Status = WorkflowExecutionStatus.Running });
                    laterEntered.TrySetResult();
                    return laterRun.Task;
                }
                firstEntered.TrySetResult();
                return firstRun.Task;
            });
        using var provider = RecoveryProvider(client.Object);
        using var scanner = Scanner(provider,
            new[] { "a", "b" }.Select(id => new RunnableInstance(id, DateTime.UtcNow)).ToArray(), 2,
            query => { if (query.ScanOffset > 0) firstRefill.TrySetResult(); });
        var first = scanner.RecoverOnceAsync(CancellationToken.None);
        Task? later = null;
        try
        {
            await firstEntered.Task.WaitAsync(ClaimBound);
            // Refill starts only after a's no-grant reservation has been released.
            await firstRefill.Task.WaitAsync(ClaimBound);
            later = scanner.RecoverOnceAsync(CancellationToken.None);
            await laterEntered.Task.WaitAsync(ClaimBound);
            firstRun.TrySetResult(new WorkflowExecutionResult { InstanceId = "b", Status = WorkflowExecutionStatus.Success });
            await first.WaitAsync(ClaimBound);
            // Finishing the old pass must leave the later pass's a reservation intact.
            await scanner.RecoverOnceAsync(CancellationToken.None).WaitAsync(ClaimBound);
            Assert.Equal(2, Volatile.Read(ref attempts));
            Assert.False(later.IsCompleted);
        }
        finally
        {
            firstRun.TrySetResult(new WorkflowExecutionResult { Status = WorkflowExecutionStatus.Success });
            laterRun.TrySetResult(new WorkflowExecutionResult { Status = WorkflowExecutionStatus.Success });
            await Task.WhenAll(new[] { first, later ?? Task.CompletedTask }).WaitAsync(DrainBound);
        }
    }

    private static ServiceProvider RecoveryProvider(IWorkflowClient client) => new ServiceCollection()
        .AddSingleton(client).AddSingleton<IWorkflowCatalog>(new WorkflowCatalog()).BuildServiceProvider();

    private static WorkflowRecoveryService Scanner(ServiceProvider provider, IReadOnlyList<RunnableInstance> candidates, int slots,
        Action<ClaimQuery>? discovered = null)
    {
        var store = new Mock<IWorkflowStateRepository>();
        store.Setup(s => s.GetRunnableInstancesAsync(It.IsAny<ClaimQuery>())).Returns<ClaimQuery>(query =>
        {
            discovered?.Invoke(query);
            return Task.FromResult<IReadOnlyList<RunnableInstance>>(candidates.Skip((int)query.ScanOffset).Take(query.Limit).ToArray());
        });
        return new WorkflowRecoveryService(provider.GetRequiredService<IServiceScopeFactory>(), store.Object,
            NullLogger<WorkflowRecoveryService>.Instance, settings: HostSettings.Default with { Slots = slots });
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlChildCreationWaitsForTheIdentityLock() => ChildCreationWaitsForTheIdentityLock(true);

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerChildCreationWaitsForTheIdentityLock() => ChildCreationWaitsForTheIdentityLock(false);

    private static async Task ChildCreationWaitsForTheIdentityLock(bool postgres)
    {
        var (store, connect) = Provider(postgres);
        var (parent, child, commit) = await ChildCreationAsync(store);
        Task<WorkflowCommitResult>? attempt = null;
        try
        {
            await using (var hold = await HoldAsync(connect))
            {
                await HoldIdentityAsync(hold, child.InstanceId);
                attempt = store.CommitWorkflowInstanceAsync(commit);
                await WaitForBlockedSessionAsync(connect, hold);
                Assert.False(attempt.IsCompleted);
                Assert.Null(await store.GetWorkflowInstanceAsync(child.InstanceId));
            }
            Assert.Equal(WorkflowCommitStatus.Applied, (await attempt.WaitAsync(DrainBound)).Status);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
            Assert.NotNull(await ClaimAsync(store, child.InstanceId));
        }
        finally
        {
            try { if (attempt != null) await attempt.WaitAsync(DrainBound); }
            finally
            {
                await store.DeleteWorkflowInstanceAsync(child.InstanceId);
                await store.DeleteWorkflowInstanceAsync(parent.InstanceId);
            }
        }
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlUncommittedParentCreatedChildRefusesClaims()
    {
        var (store, connect) = Provider(true);
        var (parent, child, commit) = await ChildCreationAsync(store);
        var trigger = "skip_locked_" + Guid.NewGuid().ToString("N");
        var gate = "gate-" + trigger;
        Task<WorkflowCommitResult>? creation = null;
        Task<ExecutionLeaseGrant?>? attempt = null;
        await using var observer = connect();
        await observer.OpenAsync();
        try
        {
            // Pause the actual parent transaction after its child insert has happened, rather
            // than simulating a creator which voluntarily takes the missing protection.
            await observer.ExecuteAsync($"""
                CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.instance_id = '{child.InstanceId}' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended('{gate}', 0));
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER {trigger} AFTER INSERT ON ixiflow_workflow_instances
                    FOR EACH ROW EXECUTE FUNCTION {trigger}();
                """);
            await using (var hold = await HoldAsync(connect))
            {
                await HoldPostgreSqlIdentityAsync(hold, gate);
                var holder = await hold.Connection.ExecuteScalarAsync<int>("SELECT pg_backend_pid()", transaction: hold.Transaction);
                creation = store.CommitWorkflowInstanceAsync(commit);
                using var timeout = new CancellationTokenSource(ClaimBound);
                while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @Holder = ANY(pg_blocking_pids(pid)))
                    """, new { Holder = holder }, cancellationToken: timeout.Token)))
                    await Task.Delay(10, timeout.Token);
                Assert.False(creation.IsCompleted);
                attempt = ClaimAsync(store, child.InstanceId);
                Assert.Null(await attempt.WaitAsync(ClaimBound));
            }
            Assert.Equal(WorkflowCommitStatus.Applied, (await creation.WaitAsync(DrainBound)).Status);
            Assert.NotNull(await ClaimAsync(store, child.InstanceId));
        }
        finally
        {
            try
            {
                if (creation != null) await creation.WaitAsync(DrainBound);
                if (attempt != null) await attempt.WaitAsync(DrainBound);
            }
            finally
            {
                try
                {
                    await observer.ExecuteAsync($"DROP TRIGGER IF EXISTS {trigger} ON ixiflow_workflow_instances; DROP FUNCTION IF EXISTS {trigger}();");
                }
                finally
                {
                    await store.DeleteWorkflowInstanceAsync(child.InstanceId);
                    await store.DeleteWorkflowInstanceAsync(parent.InstanceId);
                }
            }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerInsertionAfterTheClaimProbeWaitsWithEitherSnapshotSetting()
    {
        foreach (var snapshot in new[] { false, true })
        {
            var cs = await SqlServerTestServer.CreateDatabaseAsync("IxIFlow_SkipLockedInsertion");
            try
            {
                await using (var connection = new SqlConnection(cs))
                {
                    await connection.OpenAsync();
                    if (snapshot) await connection.ExecuteAsync($"ALTER DATABASE [{connection.Database}] SET READ_COMMITTED_SNAPSHOT ON");
                    Assert.Equal(snapshot, await connection.ExecuteScalarAsync<bool>(
                        "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
                }
                var (store, connect) = Provider(false, cs);
                foreach (var trailingAlias in new[] { false, true })
                {
                    var (parent, child, commit) = await ChildCreationAsync(store, demandingChild: true);
                    var claimId = trailingAlias ? child.InstanceId + " " : child.InstanceId;
                    Task<ExecutionLeaseGrant?>? claim = null;
                    Task<WorkflowCommitResult>? creation = null;
                    try
                    {
                        // A pre-existing, expired lease gives the real claim a permitted wait after
                        // its absent-key probe and eligibility read, while it retains the ID lock.
                        Assert.NotNull(await ClaimAsync(store, claimId));
                        await store.ReleaseExecutionLeaseAsync(child.InstanceId, "claim");
                        await using (var hold = await HoldAsync(connect))
                        {
                            await hold.Connection.ExecuteAsync("""
                                UPDATE dbo.IxIFlowWorkflowLeases WITH (ROWLOCK) SET Token=Token WHERE InstanceId=@Id
                                """, new { Id = child.InstanceId }, hold.Transaction);
                            claim = ClaimAsync(store, claimId);
                            var claimant = await WaitForBlockedSessionAsync(connect, hold);
                            creation = store.CommitWorkflowInstanceAsync(commit);
                            await WaitForBlockedSessionAsync(connect, claimant, postgres: false);
                            Assert.False(creation.IsCompleted);
                        }
                        // Creation cannot insert during the probe/stamp gap. The absent-ID claim
                        // completes first; once the child exists its unmet demand refuses a new claim.
                        Assert.NotNull(await claim.WaitAsync(ClaimBound));
                        Assert.Equal(WorkflowCommitStatus.Applied, (await creation.WaitAsync(DrainBound)).Status);
                        await store.ReleaseExecutionLeaseAsync(child.InstanceId, "claim");
                        Assert.Null(await ClaimAsync(store, claimId));
                    }
                    finally
                    {
                        try { await Task.WhenAll(new Task[] { claim ?? Task.CompletedTask, creation ?? Task.CompletedTask }).WaitAsync(DrainBound); }
                        finally
                        {
                            await store.DeleteWorkflowInstanceAsync(child.InstanceId);
                            await store.DeleteWorkflowInstanceAsync(parent.InstanceId);
                        }
                    }
                }
            }
            finally { await SqlServerTestServer.DropDatabaseAsync(cs); }
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerCreationDeleteAndPurgeWaitForClaimIdentity() => CreationDeleteAndPurgeWaitForClaimIdentity(false);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlCreationDeleteAndPurgeWaitForClaimIdentity() => CreationDeleteAndPurgeWaitForClaimIdentity(true);

    private static async Task CreationDeleteAndPurgeWaitForClaimIdentity(bool postgres)
    {
        var (store, connect) = Provider(postgres);
        foreach (var operation in new[] { "create", "delete", "purge" })
        {
            var instance = Instance();
            Task? writing = null;
            try
            {
                CheckpointCommit? commit = null;
                if (operation == "create")
                {
                    var lease = await StoreTestSupport.LeaseAsync(store, instance.InstanceId, Query);
                    commit = CheckpointCommit.Create(instance, lease.Epoch, 0);
                }
                else
                {
                    if (operation == "purge") instance.Status = WorkflowStatus.Completed;
                    await store.SeedAsync(instance, query: Query);
                    if (operation == "purge")
                    {
                        await using var connection = connect();
                        await connection.OpenAsync();
                        await connection.ExecuteAsync(postgres
                            ? "UPDATE ixiflow_workflow_instances SET ended_at_utc=clock_timestamp()-interval '2 days' WHERE instance_id=@Id"
                            : "UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(day,-2,SYSUTCDATETIME()) WHERE InstanceId=@Id",
                            new { Id = instance.InstanceId });
                    }
                }
                await using (var hold = await HoldAsync(connect))
                {
                    // Hold the provider's ID resource without holding a row. The claim now
                    // combines its zero-timeout ID probe and row read in one command.
                    var sqlMethod = store.GetType().GetMethod("Sql", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var statement = Enum.Parse(sqlMethod.GetParameters()[0].ParameterType, "LockIdentity");
                    var sql = (string)sqlMethod.Invoke(store, [statement])!;
                    await hold.Connection.ExecuteAsync(sql, new { instance.InstanceId }, hold.Transaction);
                    writing = operation switch
                    {
                        "create" => Create(),
                        "delete" => store.DeleteWorkflowInstanceAsync(instance.InstanceId),
                        _ => store.PurgeAsync(TimeSpan.FromDays(1), TimeSpan.FromDays(7), 1000)
                    };
                    await WaitForBlockedSessionAsync(connect, hold);
                    Assert.False(writing.IsCompleted);
                }
                await writing.WaitAsync(DrainBound);
                if (operation == "create") Assert.NotNull(await store.GetWorkflowInstanceAsync(instance.InstanceId));
                else Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));

                async Task Create() => Assert.Equal(WorkflowCommitStatus.Applied,
                    (await store.CommitWorkflowInstanceAsync(commit!)).Status);
            }
            finally
            {
                try { if (writing != null) await writing.WaitAsync(DrainBound); }
                finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
            }
        }
    }

    private static async Task<(WorkflowInstance Parent, WorkflowInstance Child, CheckpointCommit Commit)> ChildCreationAsync(
        IWorkflowStateRepository store, bool demandingChild = false)
    {
        var parent = Instance();
        await store.SeedAsync(parent, query: Query);
        var lease = await StoreTestSupport.LeaseAsync(store, parent.InstanceId, Query);
        var child = Instance();
        if (demandingChild)
        {
            var checkpoint = JsonSerializer.Deserialize<ExecutionCheckpoint>(child.ExecutionStateJson)!;
            checkpoint.Demands = [new WorkflowDemand("gpu", "required")];
            child.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        }
        child.ParentInstanceId = parent.InstanceId;
        child.ParentWaitId = Guid.NewGuid().ToString("N");
        return (parent, child, CheckpointCommit.Create(parent, lease.Epoch, parent.Revision,
            childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), child.ParentWaitId)],
            openedWaits: [new OpenedWait(child.ParentWaitId, WaitKind.Child, "", "IxIFlow.ChildCompleted", null)]));
    }

    private static Task HoldPostgreSqlIdentityAsync(LockHold hold, string id) => hold.Connection.ExecuteAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended(@Id, 0))", new { Id = id }, hold.Transaction);

    private static Task HoldIdentityAsync(LockHold hold, string id) => hold.Connection is NpgsqlConnection
        ? HoldPostgreSqlIdentityAsync(hold, id)
        : hold.Connection.ExecuteAsync("""
            DECLARE @result int, @resource nvarchar(255) = N'IxIFlow.Instance/' + RTRIM(@Id);
            EXEC @result = sys.sp_getapplock @Resource=@resource,
                @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=-1;
            IF @result < 0 THROW 51000, 'Could not hold instance identity', 1;
            """, new { Id = id }, hold.Transaction);

    private static async Task<int> WaitForBlockedSessionAsync(Func<DbConnection> connect, LockHold hold)
    {
        var postgres = hold.Connection is NpgsqlConnection;
        var holder = await hold.Connection.ExecuteScalarAsync<int>(postgres ? "SELECT pg_backend_pid()" : "SELECT @@SPID",
            transaction: hold.Transaction);
        return await WaitForBlockedSessionAsync(connect, holder, postgres);
    }

    private static async Task<int> WaitForBlockedSessionAsync(Func<DbConnection> connect, int holder, bool postgres)
    {
        await using var observer = connect();
        await observer.OpenAsync();
        using var timeout = new CancellationTokenSource(ClaimBound);
        while (true)
        {
            var waiter = await observer.ExecuteScalarAsync<int?>(new CommandDefinition(postgres
                ? "SELECT pid FROM pg_stat_activity WHERE @Holder=ANY(pg_blocking_pids(pid)) LIMIT 1"
                : "SELECT TOP (1) session_id FROM sys.dm_exec_requests WHERE blocking_session_id=@Holder AND wait_type LIKE 'LCK_M%'",
                new { Holder = holder }, cancellationToken: timeout.Token));
            if (waiter is { } session) return session;
            await Task.Delay(10, timeout.Token);
        }
    }

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
            return new LockHold(connection, await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted));
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
