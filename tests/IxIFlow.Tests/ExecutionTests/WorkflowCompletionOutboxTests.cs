using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Dapper;
using Microsoft.Data.SqlClient;

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
        await VerifyExpiredClaimCanBeRecovered(new InMemoryWorkflowStateRepository());
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task ExpiredSqlOutboxClaimCanBeRecovered()
    {
        var repository = new SqlWorkflowStateRepository(
            SqlServerTestServer.ConnectionString);
        await VerifyExpiredClaimCanBeRecovered(repository);
    }

    private static async Task VerifyExpiredClaimCanBeRecovered(IWorkflowStateRepository repository)
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
                instance.InstanceId, "dead-host", TimeSpan.FromMilliseconds(50)));
            Assert.Empty(await outbox.ClaimUnpublishedCompletionsAsync(
                instance.InstanceId, "new-host", TimeSpan.FromSeconds(5)));

            await Task.Delay(200);

            var recovered = Assert.Single(await outbox.ClaimUnpublishedCompletionsAsync(
                instance.InstanceId, "new-host", TimeSpan.FromSeconds(5)));
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
        var masterConnectionString = SqlServerTestServer.ConnectionString;
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
