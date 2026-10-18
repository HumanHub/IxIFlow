using System.Collections;
using System.Data.Common;
using System.Reflection;
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
[Trait("Spec", "D-41")]
public sealed class LeaseIncarnationContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MemoryDeletionRemovesLeaseAndFencesIncarnation(bool purge)
    {
        var time = new FakeTimeProvider();
        return Verify(new InMemoryWorkflowStateRepository(time), purge,
            (_, by) => { time.Advance(by); return Task.CompletedTask; });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteDeletionRemovesLeaseAndFencesIncarnation(bool purge)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.LeaseIncarnation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "incarnation.db");
        try
        {
            var time = new FakeTimeProvider();
            await using var store = new SqliteWorkflowStateRepository(connectionString, time);
            await Verify(store, purge, (_, by) => { time.Advance(by); return Task.CompletedTask; },
                () => new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString));
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerDeletionRemovesLeaseAndFencesIncarnation()
    {
        var connectionString = SqlTestDatabase<LeaseIncarnationContractTests>.ConnectionString;
        foreach (var purge in new[] { false, true })
            await Verify(new SqlWorkflowStateRepository(connectionString), purge,
                (id, by) => AgeRows(() => new SqlConnection(connectionString), id, by),
                () => new SqlConnection(connectionString));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlDeletionRemovesLeaseAndFencesIncarnation()
    {
        var connectionString = PostgreSqlTestDatabase<LeaseIncarnationContractTests>.ConnectionString;
        foreach (var purge in new[] { false, true })
            await Verify(new PostgreSqlWorkflowStateRepository(connectionString), purge,
                (id, by) => AgeRows(() => new NpgsqlConnection(connectionString), id, by),
                () => new NpgsqlConnection(connectionString));
    }

    private static async Task Verify(IWorkflowStateRepository store, bool purge,
        Func<string, TimeSpan, Task> advance, Func<DbConnection>? open = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var retention = TimeSpan.FromDays(30);
        var tombstones = TimeSpan.FromDays(90);
        var duration = TimeSpan.FromMinutes(1);
        try
        {
            var original = NewInstance(id);
            await Apply(store, CheckpointCommit.Create(original, 0, 0));
            original = (await store.GetWorkflowInstanceAsync(id))!;
            var originalIncarnation = original.Incarnation;
            Assert.NotEqual(Guid.Empty, originalIncarnation);
            Assert.Equal(1, (await store.TryAcquireExecutionLeaseAsync(id, "old-first", duration))!.Epoch);
            await advance(id, TimeSpan.FromMinutes(2));
            var oldGrant = (await store.TryAcquireExecutionLeaseAsync(id, "old-owner", duration))!;
            Assert.Equal(2, oldGrant.Epoch);
            Assert.Equal(1, oldGrant.CrashCount);

            // These all name revision one and epoch two. The completed one is also a receipt replay.
            var lateRunning = CheckpointCommit.Create(original, oldGrant.Epoch, original.Revision);
            var latePark = CheckpointCommit.Create(original, oldGrant.Epoch, original.Revision,
                parks: true, wakeAfter: TimeSpan.FromHours(1));
            original.Status = WorkflowStatus.Completed;
            var lateCompletion = CheckpointCommit.Create(original, oldGrant.Epoch, original.Revision, "completion");
            await Apply(store, lateCompletion);
            Assert.Equal(1, await LeaseRows(store, id, open));

            if (purge)
            {
                await advance(id, retention + TimeSpan.FromDays(1));
                Assert.Equal(1, (await store.PurgeAsync(retention, tombstones, 100)).InstancesRemoved);
            }
            else
                await store.DeleteWorkflowInstanceAsync(id, tombstones);
            Assert.Null(await store.GetWorkflowInstanceAsync(id));
            Assert.Equal(0, await LeaseRows(store, id, open));

            // Reuse becomes legal when the tombstone expires, without a wall-clock sleep.
            await advance(id, tombstones + TimeSpan.FromDays(1));
            await store.PurgeAsync(retention, tombstones, 100);
            var replacement = NewInstance(id);
            await Apply(store, CheckpointCommit.Create(replacement, 0, 0));
            replacement = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.NotEqual(originalIncarnation, replacement.Incarnation);
            Assert.Equal(original.Revision, replacement.Revision);
            Assert.Equal(replacement.Incarnation, (await store.GetWorkflowInstanceSummaryAsync(id))!.Incarnation);
            Assert.Equal(replacement.Incarnation,
                Assert.Single(await store.GetWorkflowInstancesByNameAsync(replacement.WorkflowName)).Incarnation);

            var first = (await store.TryAcquireExecutionLeaseAsync(id, "new-first", duration))!;
            Assert.Equal(1, first.Epoch);
            Assert.Equal(0, first.CrashCount);
            Assert.True(await store.ReleaseExecutionLeaseAsync(id, "new-first"));
            var current = (await store.TryAcquireExecutionLeaseAsync(id, "new-owner", duration))!;
            Assert.Equal(oldGrant.Epoch, current.Epoch);
            Assert.Equal(0, current.CrashCount);

            foreach (var late in new[] { lateRunning, latePark, lateCompletion })
            {
                var result = await store.CommitWorkflowInstanceAsync(late);
                Assert.Equal(WorkflowCommitStatus.StaleEpoch, result.Status);
                Assert.Equal(replacement.Revision, result.Revision);
                Assert.True(await store.RenewExecutionLeaseAsync(id, "new-owner", duration));
                var unchanged = (await store.GetWorkflowInstanceAsync(id))!;
                Assert.Equal(replacement.Revision, unchanged.Revision);
                Assert.Equal(WorkflowStatus.Running, unchanged.Status);
                Assert.Equal(replacement.Incarnation, unchanged.Incarnation);
            }
            Assert.False(await store.ReleaseExecutionLeaseAsync(id, "old-owner"));
            Assert.False(await store.RenewExecutionLeaseAsync(id, "old-owner", duration));

            // A receipt ID reused by the new incarnation must not admit an old terminal replay either.
            replacement.Status = WorkflowStatus.Completed;
            var normal = CheckpointCommit.Create(replacement, current.Epoch, replacement.Revision, "completion");
            await Apply(store, normal);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(normal)).Status);
            Assert.Equal(WorkflowCommitStatus.StaleEpoch,
                (await store.CommitWorkflowInstanceAsync(lateCompletion)).Status);
            var finished = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(2, finished.Revision);
            Assert.Equal(WorkflowStatus.Completed, finished.Status);
            Assert.Equal(replacement.Incarnation, finished.Incarnation);

            // Epoch zero still uses the lease/revision rule, even with a different incarnation in its payload.
            var unleased = NewInstance(id);
            unleased.Status = WorkflowStatus.Completed;
            await Apply(store, CheckpointCommit.Create(unleased, 0, finished.Revision));
            Assert.Equal(replacement.Incarnation, (await store.GetWorkflowInstanceAsync(id))!.Incarnation);

            // Save also allocates a fresh incarnation when reusing an old object to create a row.
            await store.DeleteWorkflowInstanceAsync(id, tombstones);
            Assert.Equal(0, await LeaseRows(store, id, open));
            await advance(id, tombstones + TimeSpan.FromDays(1));
            await store.PurgeAsync(retention, tombstones, 100);
            original.Revision = 0;
            original.Status = WorkflowStatus.Running;
            await store.SaveWorkflowInstanceAsync(original);
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.NotEqual(originalIncarnation, saved.Incarnation);
            Assert.NotEqual(replacement.Incarnation, saved.Incarnation);
            var update = NewInstance(id);
            await store.SaveWorkflowInstanceAsync(update);
            Assert.Equal(saved.Incarnation, (await store.GetWorkflowInstanceAsync(id))!.Incarnation);
        }
        finally { await store.DeleteWorkflowInstanceAsync(id); }
    }

    private static WorkflowInstance NewInstance(string id) => new()
    {
        InstanceId = id, WorkflowName = "LeaseIncarnation" + id,
        Status = WorkflowStatus.Running, WorkflowDataJson = "{}"
    };

    private static async Task Apply(IWorkflowStateRepository store, CheckpointCommit commit) =>
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);

    private static async Task<int> LeaseRows(IWorkflowStateRepository store, string id, Func<DbConnection>? open)
    {
        if (open == null)
        {
            var leases = (IDictionary)typeof(InMemoryWorkflowStateRepository)
                .GetField("_leases", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
            return leases.Contains(id) ? 1 : 0;
        }
        await using var connection = open();
        return await connection.ExecuteScalarAsync<int>(connection is SqlConnection
            ? "SELECT COUNT(*) FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId=@Id"
            : "SELECT COUNT(*) FROM ixiflow_workflow_leases WHERE instance_id=@Id", new { Id = id });
    }

    private static async Task AgeRows(Func<DbConnection> open, string id, TimeSpan by)
    {
        await using var connection = open();
        await connection.ExecuteAsync(connection is SqlConnection ? """
            UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(SECOND, -@Seconds, EndedAtUtc) WHERE InstanceId=@Id;
            UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc=DATEADD(SECOND, -@Seconds, ExpiresAtUtc)
                WHERE InstanceId=@Id AND ExpiresAtUtc>CAST('0001-01-01' AS DATETIME2);
            UPDATE dbo.IxIFlowWorkflowTombstones SET ExpiresAtUtc=DATEADD(SECOND, -@Seconds, ExpiresAtUtc) WHERE InstanceId=@Id;
            """ : """
            UPDATE ixiflow_workflow_instances SET ended_at_utc=ended_at_utc - @Seconds * INTERVAL '1 second' WHERE instance_id=@Id;
            UPDATE ixiflow_workflow_leases SET expires_at_utc=expires_at_utc - @Seconds * INTERVAL '1 second'
                WHERE instance_id=@Id AND expires_at_utc>'0001-01-01'::timestamptz;
            UPDATE ixiflow_workflow_tombstones SET expires_at_utc=expires_at_utc - @Seconds * INTERVAL '1 second' WHERE instance_id=@Id;
            """, new { Id = id, Seconds = (int)by.TotalSeconds });
    }
}
