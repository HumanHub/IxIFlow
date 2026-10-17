using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The completion outbox at store level: a finished instance's completion waits, claimed by one publisher at a time,
/// until it is marked published (D-11). Publishing it is D-11's.
/// </summary>
[Trait("Feature", "durability")]
public sealed class WorkflowCompletionOutboxTests
{
    [Fact]
    public async Task ExpiredMemoryOutboxClaimCanBeRecovered()
    {
        var time = new FakeTimeProvider();
        await VerifyExpiredClaimCanBeRecovered(new InMemoryWorkflowStateRepository(time),
            TimeSpan.FromMilliseconds(50), reclaim =>
            {
                time.Advance(TimeSpan.FromMilliseconds(200));
                return reclaim();
            });
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task ExpiredSqlOutboxClaimCanBeRecovered()
    {
        var repository = new SqlWorkflowStateRepository(
            SqlTestDatabase<WorkflowCompletionOutboxTests>.ConnectionString);
        // Long enough that the refusal right after the claim cannot land after it on SQL Server's clock; the
        // new host then claims again until the dead host's claim has expired.
        await VerifyExpiredClaimCanBeRecovered(repository, TimeSpan.FromSeconds(2), async reclaim =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                var claimed = (await reclaim()).ToList();
                if (claimed.Count > 0)
                    return claimed;
                Assert.True(DateTime.UtcNow < deadline, "The expired claim was never recovered");
                await Task.Delay(100);
            }
        });
    }

    // The dead host's claim lasts deadHostClaim; reclaimOnceExpired lets it expire and claims for the new host.
    private static async Task VerifyExpiredClaimCanBeRecovered(IWorkflowStateRepository repository,
        TimeSpan deadHostClaim,
        Func<Func<Task<IEnumerable<WorkflowInstance>>>, Task<IEnumerable<WorkflowInstance>>> reclaimOnceExpired)
    {
        var outbox = Assert.IsAssignableFrom<IWorkflowCompletionOutbox>(repository);
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "ExpiredOutboxClaim",
            Status = WorkflowStatus.Completed
        };
        try
        {
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0,
                    Guid.NewGuid().ToString("N")))).Status);
            Assert.Single(await outbox.ClaimUnpublishedCompletionsAsync(
                instance.InstanceId, "dead-host", deadHostClaim));
            Assert.Empty(await outbox.ClaimUnpublishedCompletionsAsync(
                instance.InstanceId, "new-host", TimeSpan.FromSeconds(5)));

            var recovered = Assert.Single(await reclaimOnceExpired(() => outbox.ClaimUnpublishedCompletionsAsync(
                instance.InstanceId, "new-host", TimeSpan.FromSeconds(5))));
            Assert.False(await outbox.MarkCompletionPublishedAsync(
                instance.InstanceId, recovered.Revision, "dead-host"));
            Assert.True(await outbox.MarkCompletionPublishedAsync(
                instance.InstanceId, recovered.Revision, "new-host"));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task AddingOutboxColumnDoesNotRepublishHistoricalCompletions()
    {
        var masterConnectionString = SqlTestDatabase<WorkflowCompletionOutboxTests>.ConnectionString;
        var databaseName = "IxIFlowOutboxMigration_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        };
        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync();
        await master.ExecuteAsync($"CREATE DATABASE [{databaseName}]");
        try
        {
            await using (var connection = new SqlConnection(builder.ConnectionString))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync("""
                    CREATE TABLE dbo.IxIFlowWorkflowInstances (
                        InstanceId NVARCHAR(100) NOT NULL PRIMARY KEY,
                        WorkflowName NVARCHAR(200) NOT NULL,
                        Status NVARCHAR(50) NOT NULL,
                        CorrelationId NVARCHAR(100) NULL,
                        SuspensionExpiresAt DATETIME2 NULL,
                        StateJson NVARCHAR(MAX) NOT NULL
                    );
                    """);
                var historical = new WorkflowInstance
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    WorkflowName = "Historical",
                    Status = WorkflowStatus.Completed
                };
                await connection.ExecuteAsync("""
                    INSERT INTO dbo.IxIFlowWorkflowInstances
                        (InstanceId, WorkflowName, Status, StateJson)
                    VALUES (@InstanceId, @WorkflowName, @Status, @StateJson)
                    """, new { historical.InstanceId, historical.WorkflowName,
                        Status = historical.Status.ToString(),
                        StateJson = System.Text.Json.JsonSerializer.Serialize(historical) });
            }

            var repository = new SqlWorkflowStateRepository(builder.ConnectionString);
            Assert.Empty(await repository.GetUnpublishedCompletionsAsync());
        }
        finally
        {
            await master.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            await master.ExecuteAsync($"DROP DATABASE [{databaseName}]");
        }
    }
}
