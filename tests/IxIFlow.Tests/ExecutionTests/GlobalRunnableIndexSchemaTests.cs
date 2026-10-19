using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class GlobalRunnableIndexSchemaTests : IClassFixture<SqliteTestDatabase<GlobalRunnableIndexSchemaTests>>
{
    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteCreatesNoGlobalOrderingIndex()
    {
        var cs = SqliteTestDatabase<GlobalRunnableIndexSchemaTests>.ConnectionString;
        await using var connection = new SqliteConnection(cs);
        await Verify(() => new SqliteWorkflowStateRepository(cs), connection, false, false);
    }

    [Fact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlCreatesNoGlobalOrderingIndex()
    {
        var cs = await PostgreSqlTestServer.CreateDatabaseAsync("global_index_schema");
        try
        {
            await using var connection = new NpgsqlConnection(cs);
            await Verify(() => new PostgreSqlWorkflowStateRepository(cs), connection, false, true);
        }
        finally { await PostgreSqlTestServer.DropDatabaseAsync(cs); }
    }

    [Fact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerCreatesNoGlobalOrderingIndex()
    {
        var cs = await SqlServerTestServer.CreateDatabaseAsync("GlobalIndexSchema");
        try
        {
            await using var connection = new SqlConnection(cs);
            await Verify(() => new SqlWorkflowStateRepository(cs), connection, true, false);
        }
        finally { await SqlServerTestServer.DropDatabaseAsync(cs); }
    }

    // Every instance has a demand set, so claim and backlog reads seek their set. A global runnable or
    // affinity order would let the planner walk unrelated sets when sampled statistics misjudge a set's size.
    private static async Task Verify(Func<IWorkflowStateRepository> create, DbConnection connection, bool sqlServer, bool postgres)
    {
        var store = create();
        try { Assert.Null(await store.GetWorkflowInstanceAsync("missing")); }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
        await connection.OpenAsync();
        var indexes = (await connection.QueryAsync<string>(sqlServer
            ? "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IxIFlowWorkflowInstances')"
            : postgres
                ? "SELECT indexname FROM pg_indexes WHERE tablename = 'ixiflow_workflow_instances' AND schemaname = current_schema()"
                : "SELECT name FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'ixiflow_workflow_instances'")).ToArray();
        foreach (var name in sqlServer
            ? new[] { "IX_IxIFlowWorkflowInstances_Runnable", "IX_IxIFlowWorkflowInstances_Affinity" }
            : new[] { "ix_ixiflow_workflow_instances_runnable", "ix_ixiflow_workflow_instances_affinity" })
            Assert.DoesNotContain(name, indexes);
    }
}
