using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class CatalogRevisionSchemaTests
{
    [Theory, InlineData("counter"), InlineData("stamp"), InlineData("index"), Trait("Category", "SqliteIntegration")]
    public async Task SqliteRequiresRevisionSchema(string missing)
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.CatalogSchema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cs = "Data Source=" + Path.Combine(root, "state.db");
            await VerifyAsync(() => new SqliteWorkflowStateRepository(cs), () => new SqliteConnection(cs + ";Pooling=False"), false, missing);
        }
        finally { await SqliteTestFiles.DeleteAsync(root); }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerRequiresRevisionSchema()
    {
        foreach (var missing in new[] { "counter", "stamp", "index" })
        {
            var cs = await SqlServerTestServer.CreateDatabaseAsync("CatalogRevisionSchema");
            try { await VerifyAsync(() => new SqlWorkflowStateRepository(cs), () => new SqlConnection(cs), true, missing); }
            finally { await SqlServerTestServer.DropDatabaseAsync(cs); }
        }
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlRequiresRevisionSchema()
    {
        foreach (var missing in new[] { "counter", "stamp", "index" })
        {
            var cs = await PostgreSqlTestServer.CreateDatabaseAsync("catalog_revision_schema");
            try { await VerifyAsync(() => new PostgreSqlWorkflowStateRepository(cs), () => new NpgsqlConnection(cs), false, missing); }
            finally { await PostgreSqlTestServer.DropDatabaseAsync(cs); }
        }
    }

    private static async Task VerifyAsync(Func<IWorkflowStateRepository> create, Func<DbConnection> connect,
        bool sqlServer, string missing)
    {
        var store = create();
        try { Assert.Equal(0, await store.GetCatalogRevisionAsync()); }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
        await using (var connection = connect())
        {
            await connection.OpenAsync();
            if (missing == "counter")
                await connection.ExecuteAsync(sqlServer ? "DROP TABLE dbo.IxIFlowCatalogRevision" : "DROP TABLE ixiflow_catalog_revision");
            else
            {
                await connection.ExecuteAsync(sqlServer
                    ? "DROP INDEX ix_workflow_versions_revision ON dbo.IxIFlowWorkflowVersions"
                    : "DROP INDEX ix_workflow_versions_revision");
                if (missing == "stamp") await connection.ExecuteAsync(sqlServer
                    ? "ALTER TABLE dbo.IxIFlowWorkflowVersions DROP COLUMN CatalogRevision"
                    : "ALTER TABLE ixiflow_workflow_versions DROP COLUMN catalog_revision");
            }
        }
        store = create();
        try
        {
            // Any store read validates required columns, rather than silently creating a migration.
            await Assert.ThrowsAsync<WorkflowEngineException>(() => store.GetWorkflowVersionsAsync());
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }
}
