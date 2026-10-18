using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "D-41")]
[Trait("Spec", "D-05")]
[Trait("Spec", "H-05")]
public sealed class RetentionStoreTests
{
    public static TheoryData<RetentionScenario> Scenarios
    {
        get
        {
            var data = new TheoryData<RetentionScenario>();
            foreach (var scenario in Enum.GetValues<RetentionScenario>()) data.Add(scenario);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public Task MemoryRetentionContract(RetentionScenario scenario)
    {
        var time = new FakeTimeProvider();
        return new RetentionStoreContract(new InMemoryWorkflowStateRepository(time), (_, by) =>
            { time.Advance(by); return Task.CompletedTask; }).VerifyAsync(scenario);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteRetentionContract(RetentionScenario scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "retention.db");
        try
        {
            var time = new FakeTimeProvider();
            await using var store = new SqliteWorkflowStateRepository(connectionString, time);
            await new RetentionStoreContract(store, (_, by) => { time.Advance(by); return Task.CompletedTask; },
                () => new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString)).VerifyAsync(scenario);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerRetentionContract()
    {
        var connectionString = SqlTestDatabase<RetentionStoreTests>.ConnectionString;
        foreach (var scenario in Enum.GetValues<RetentionScenario>())
            await new RetentionStoreContract(new SqlWorkflowStateRepository(connectionString),
                (ids, by) => AgeRows(() => new SqlConnection(connectionString), ids, by),
                () => new SqlConnection(connectionString)).VerifyAsync(scenario);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlRetentionContract()
    {
        var connectionString = PostgreSqlTestDatabase<RetentionStoreTests>.ConnectionString;
        foreach (var scenario in Enum.GetValues<RetentionScenario>())
            await new RetentionStoreContract(new PostgreSqlWorkflowStateRepository(connectionString),
                (ids, by) => AgeRows(() => new NpgsqlConnection(connectionString), ids, by),
                () => new NpgsqlConnection(connectionString)).VerifyAsync(scenario);
    }

    // Database clocks are authoritative. Advance the age of this scenario's rows, never a server's
    // clock and never by sleeping. The same scenarios use exact FakeTimeProvider time locally.
    private static async Task AgeRows(Func<DbConnection> open, IReadOnlyCollection<string> ids, TimeSpan by)
    {
        await using var connection = open();
        var sql = connection is SqlConnection ? """
            UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(SECOND, -@Seconds, EndedAtUtc) WHERE InstanceId IN @Ids;
            UPDATE dbo.IxIFlowWorkflowTombstones SET RemovedAtUtc=DATEADD(SECOND, -@Seconds, RemovedAtUtc),
                ExpiresAtUtc=DATEADD(SECOND, -@Seconds, ExpiresAtUtc) WHERE InstanceId IN @Ids;
            """ : """
            UPDATE ixiflow_workflow_instances SET ended_at_utc=ended_at_utc - @Seconds * INTERVAL '1 second' WHERE instance_id=ANY(@Ids);
            UPDATE ixiflow_workflow_tombstones SET removed_at_utc=removed_at_utc - @Seconds * INTERVAL '1 second',
                expires_at_utc=expires_at_utc - @Seconds * INTERVAL '1 second' WHERE instance_id=ANY(@Ids);
            """;
        await connection.ExecuteAsync(sql, new { Ids = ids.ToArray(), Seconds = (int)by.TotalSeconds });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactRetentionAndExpiryBoundariesUseStoreTime(bool sqlite)
    {
        var time = new FakeTimeProvider();
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = sqlite ? (IWorkflowStateRepository)new SqliteWorkflowStateRepository(
            "Data Source=" + Path.Combine(folder, "boundaries.db"), time) : new InMemoryWorkflowStateRepository(time);
        try
        {
            var instance = new WorkflowInstance { InstanceId = "boundary", WorkflowName = "Boundary", Status = WorkflowStatus.Completed };
            await store.SaveWorkflowInstanceAsync(instance);
            var retention = store;
            time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromTicks(1));
            Assert.Equal(0, (await retention.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 1)).InstancesRemoved);
            time.Advance(TimeSpan.FromTicks(1));
            Assert.Equal(1, (await retention.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 1)).InstancesRemoved);
            time.Advance(TimeSpan.FromDays(90) - TimeSpan.FromTicks(1));
            Assert.NotNull(await store.GetTombstoneAsync(instance.InstanceId));
            time.Advance(TimeSpan.FromTicks(1));
            Assert.Null(await store.GetTombstoneAsync(instance.InstanceId));
            Assert.Equal(1, (await retention.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 1)).TombstonesRemoved);
        }
        finally
        {
            if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }
}
