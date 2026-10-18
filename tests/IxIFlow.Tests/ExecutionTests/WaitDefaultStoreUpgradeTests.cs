using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "waits")]
[Trait("Spec", "D-14")]
[Trait("Category", "SqliteIntegration")]
public sealed class WaitDefaultStoreUpgradeTests : IClassFixture<SqliteTestDatabase<WaitDefaultStoreUpgradeTests>>
{
    [Fact]
    public async Task ExistingSqliteWaitsHaveNoDefaultAfterTheSchemaUpgrade()
    {
        var connectionString = SqliteTestDatabase<WaitDefaultStoreUpgradeTests>.ConnectionString;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ixiflow_workflow_waits (
                    instance_id TEXT NOT NULL, wait_id TEXT NOT NULL, kind INTEGER NOT NULL,
                    wait_key TEXT NOT NULL, event_type TEXT NOT NULL, deadline_utc TEXT NULL,
                    opened_sequence INTEGER NOT NULL, opened_at_utc TEXT NOT NULL,
                    close_reason INTEGER NULL, closed_at_utc TEXT NULL,
                    PRIMARY KEY (instance_id, wait_id)
                );
                INSERT INTO ixiflow_workflow_waits
                    (instance_id, wait_id, kind, wait_key, event_type, opened_sequence, opened_at_utc)
                VALUES ('legacy', 'approval', 1, 'approve', 'Approval', 0, '2026-10-18T00:00:00.0000000Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteWorkflowStateRepository(connectionString);
        var wait = Assert.Single(await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: "legacy")));
        Assert.Equal("approval", wait.WaitId);
        Assert.Null(wait.DefaultPayloadJson);
    }
}
