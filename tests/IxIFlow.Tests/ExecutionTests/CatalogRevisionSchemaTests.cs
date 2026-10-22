using System.Data.Common;
using System.Diagnostics;
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
    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerDeltaWatermarkPrecedesRowsAcrossACommit()
    {
        foreach (var snapshot in new[] { false, true })
        {
            var cs = await SqlServerTestServer.CreateDatabaseAsync("CatalogDeltaBoundary");
            try
            {
                await using var holder = new SqlConnection(cs);
                await holder.OpenAsync();
                await holder.ExecuteAsync($"ALTER DATABASE [{holder.Database}] SET READ_COMMITTED_SNAPSHOT {(snapshot ? "ON" : "OFF")}");
                Assert.Equal(snapshot, await holder.ExecuteScalarAsync<bool>(
                    "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
                var writer = new SqlWorkflowStateRepository(cs);
                await writer.RegisterWorkflowVersionAsync(new("BeforeBoundary", 1, "before"));
                var watermark = await writer.GetCatalogRevisionAsync();
                var application = "catalog-delta-" + Guid.NewGuid().ToString("N");
                var reader = new SqlWorkflowStateRepository(new SqlConnectionStringBuilder(cs) { ApplicationName = application }.ConnectionString);
                await reader.GetCatalogRevisionAsync(); // Admit schema before observing provider commands.
                var resource = "catalog-row-barrier-" + Guid.NewGuid().ToString("N");
                var session = await holder.ExecuteScalarAsync<int>("SELECT @@SPID");
                await using var transaction = await holder.BeginTransactionAsync();
                await holder.ExecuteAsync("""
                    DECLARE @r int;
                    EXEC @r=sys.sp_getapplock @Resource=@Resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
                    IF @r<0 THROW 51000, 'Cannot acquire catalog read barrier', 1;
                    """, new { Resource = resource }, transaction);
                using var pause = new DeltaRowPause(application, resource);
                var reading = reader.GetWorkflowVersionChangesAsync(0);
                try
                {
                    await using var observer = new SqlConnection(cs);
                    await observer.OpenAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                        SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.dm_exec_requests WHERE blocking_session_id=@Session)
                        THEN 1 ELSE 0 END AS bit)
                        """, new { Session = session }, cancellationToken: timeout.Token))) await Task.Delay(10, timeout.Token);
                    Assert.Equal(1, pause.Hits);
                    Assert.False(reading.IsCompleted);
                    // The provider has selected its rows but has not returned the delta. A rows-first
                    // regression would now read the later counter and permanently skip this commit.
                    await writer.RegisterWorkflowVersionAsync(new("AfterBoundary", 1, "after"));
                }
                finally { await transaction.RollbackAsync(); }
                var delta = await reading.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(watermark, delta.Revision);
                Assert.Equal("BeforeBoundary", Assert.Single(delta.Versions).Name);
                var next = await reader.GetWorkflowVersionChangesAsync(delta.Revision);
                Assert.Equal(watermark + 1, next.Revision);
                Assert.Equal("AfterBoundary", Assert.Single(next.Versions).Name);
                Assert.Empty((await reader.GetWorkflowVersionChangesAsync(next.Revision)).Versions);
            }
            finally { await SqlServerTestServer.DropDatabaseAsync(cs); }
        }
    }

    // Append a transaction barrier after the real row SELECT, using SqlClient's public
    // diagnostic stream. Dapper drains every result before returning its buffered rows.
    private sealed class DeltaRowPause : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _application, _resource;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _all;
        private int _hits;
        internal int Hits => Volatile.Read(ref _hits);
        internal DeltaRowPause(string application, string resource)
        { _application = application; _resource = resource; _all = DiagnosticListener.AllListeners.Subscribe(this); }
        public void OnNext(DiagnosticListener listener)
        { if (listener.Name == "SqlClientDiagnosticListener") _subscriptions.Add(listener.Subscribe(this)); }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal) || value.Value == null) return;
            if (value.Value.GetType().GetProperty("Command")?.GetValue(value.Value) is not SqlCommand command ||
                command.Connection == null || new SqlConnectionStringBuilder(command.Connection.ConnectionString).ApplicationName != _application ||
                !command.CommandText.Contains("FROM dbo.IxIFlowWorkflowVersions WHERE CatalogRevision>@AfterRevision", StringComparison.Ordinal) ||
                Interlocked.CompareExchange(ref _hits, 1, 0) != 0) return;
            command.CommandText += $"""
                ;
                DECLARE @barrier int;
                EXEC @barrier=sys.sp_getapplock @Resource=N'{_resource}', @LockMode='Shared', @LockOwner='Session', @LockTimeout=20000;
                IF @barrier<0 THROW 51000, 'Catalog read barrier timed out', 1;
                EXEC sys.sp_releaseapplock @Resource=N'{_resource}', @LockOwner='Session';
                """;
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { _all.Dispose(); foreach (var subscription in _subscriptions) subscription.Dispose(); }
    }

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

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteRequiresCounterRow()
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.CatalogSchema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cs = "Data Source=" + Path.Combine(root, "state.db");
            await using var store = new SqliteWorkflowStateRepository(cs);
            await VerifyMissingCounterRowAsync(store, () => new SqliteConnection(cs + ";Pooling=False"), false);
        }
        finally { await SqliteTestFiles.DeleteAsync(root); }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerRequiresCounterRow()
    {
        var cs = await SqlServerTestServer.CreateDatabaseAsync("CatalogRevisionRow");
        try { await VerifyMissingCounterRowAsync(new SqlWorkflowStateRepository(cs), () => new SqlConnection(cs), true); }
        finally { await SqlServerTestServer.DropDatabaseAsync(cs); }
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlRequiresCounterRow()
    {
        var cs = await PostgreSqlTestServer.CreateDatabaseAsync("catalog_revision_row");
        try { await VerifyMissingCounterRowAsync(new PostgreSqlWorkflowStateRepository(cs), () => new NpgsqlConnection(cs), false); }
        finally { await PostgreSqlTestServer.DropDatabaseAsync(cs); }
    }

    private static async Task VerifyMissingCounterRowAsync(IWorkflowStateRepository store, Func<DbConnection> connect, bool sqlServer)
    {
        Assert.Equal(0, await store.GetCatalogRevisionAsync());
        await using (var connection = connect())
        {
            await connection.OpenAsync();
            Assert.Equal(1, await connection.ExecuteAsync(sqlServer
                ? "DELETE FROM dbo.IxIFlowCatalogRevision" : "DELETE FROM ixiflow_catalog_revision"));
        }
        const string message = "The catalog revision row is missing; recreate the catalog schema";
        foreach (var action in new Func<Task>[]
        {
            async () => { await store.GetCatalogRevisionAsync(); },
            async () => { await store.GetWorkflowVersionChangesAsync(0); },
            () => store.RegisterWorkflowVersionAsync(new("MissingCounter", 1, "hash"))
        })
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(action);
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Equal(message, error.Message);
        }
        Assert.Empty(await store.GetWorkflowVersionsAsync());
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
