using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "F-21")]
public sealed class InstalledActivityPackageDatabaseTests
{
    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteSchemaAndTransactionContract()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.InstalledSchema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "installed.db") + ";Pooling=False";
        try
        {
            await Verify(() => new SqliteWorkflowStateRepository(cs), () => new SqliteConnection(cs));
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerSchemaAndTransactionContract()
    {
        var cs = SqlTestDatabase<InstalledActivityPackageDatabaseTests>.ConnectionString;
        return Verify(() => new SqlWorkflowStateRepository(cs), () => new SqlConnection(cs));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlSchemaAndTransactionContract()
    {
        var cs = PostgreSqlTestDatabase<InstalledActivityPackageDatabaseTests>.ConnectionString;
        return Verify(() => new PostgreSqlWorkflowStateRepository(cs), () => new NpgsqlConnection(cs));
    }

    private static async Task Dispose(IWorkflowStateRepository store)
    {
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
    }

    private static async Task Verify(Func<IWorkflowStateRepository> create, Func<DbConnection> open)
    {
        var initial = create();
        await initial.GetInstalledPackagesAsync();
        await Dispose(initial);
        await using var connection = open();
        await connection.OpenAsync();
        var sqlServer = connection is SqlConnection;
        var installed = sqlServer ? "dbo.IxIFlowInstalledPackages" : "ixiflow_installed_packages";
        var hosts = sqlServer ? "dbo.IxIFlowHostPackages" : "ixiflow_host_packages";
        foreach (var (table, column) in new[] { (installed, sqlServer ? "ActivitiesJson" : "activities_json"),
                     (hosts, sqlServer ? "Fingerprint" : "fingerprint") })
        foreach (var missingTable in new[] { true, false })
        {
            string Rename(bool restore)
            {
                var original = missingTable ? table : column;
                var source = restore ? original + "_F21" : original;
                var target = restore ? original : original + "_F21";
                if (sqlServer)
                    return missingTable ? $"EXEC sp_rename N'{source}', N'{target.Replace("dbo.", "")}'"
                        : $"EXEC sp_rename N'{table}.{source}', N'{target}', N'COLUMN'";
                return missingTable ? $"ALTER TABLE {source} RENAME TO {target}"
                    : $"ALTER TABLE {table} RENAME COLUMN {source} TO {target}";
            }
            await connection.ExecuteAsync(Rename(false));
            try
            {
                var reopened = create();
                try
                {
                    var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetInstalledPackagesAsync());
                    Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
                    Assert.Contains("must be recreated", error.Message);
                }
                finally { await Dispose(reopened); }
            }
            finally { await connection.ExecuteAsync(Rename(true)); }
        }

        var store = create();
        var id = "installed.rollback." + Guid.NewGuid().ToString("N");
        var prior = InstalledActivityPackageTests.Package(id, "[]");
        var next = InstalledActivityPackageTests.Package(id, "[{\"Key\":\"next\"}]");
        var member = MemberRegistryContract.Registration() with { InstalledPackages = [prior] };
        await store.RegisterMemberAsync(member);
        var trigger = sqlServer
            ? "CREATE TRIGGER dbo.F21RejectMark ON dbo.IxIFlowHostPackages AFTER INSERT AS THROW 51000, 'Injected package mark failure', 1;"
            : connection is SqliteConnection
                ? "CREATE TRIGGER f21_reject_mark BEFORE INSERT ON ixiflow_host_packages BEGIN SELECT RAISE(ABORT, 'Injected package mark failure'); END;"
                : """
                    CREATE FUNCTION f21_reject_mark() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Injected package mark failure'; END $$;
                    CREATE TRIGGER f21_reject_mark BEFORE INSERT ON ixiflow_host_packages FOR EACH ROW EXECUTE FUNCTION f21_reject_mark();
                    """;
        await connection.ExecuteAsync(trigger);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => store.RegisterMemberAsync(member with
            { Name = "failed replacement", Capabilities = new Dictionary<string, string> { ["unexpected"] = "value" }, InstalledPackages = [next] }));
            var saved = Assert.Single(await store.GetInstalledPackagesAsync(id));
            Assert.Equal(prior.Fingerprint, saved.Fingerprint);
            Assert.Equal(member.Id, Assert.Single(saved.Hosts));
            var host = Assert.Single(await store.GetLiveMembersAsync(), row => row.Id == member.Id);
            Assert.Equal(member.Name, host.Name);
            Assert.Equal(member.Capabilities.OrderBy(pair => pair.Key), host.Capabilities.OrderBy(pair => pair.Key));
        }
        finally
        {
            await connection.ExecuteAsync(sqlServer ? "DROP TRIGGER dbo.F21RejectMark"
                : connection is SqliteConnection ? "DROP TRIGGER f21_reject_mark"
                : "DROP TRIGGER f21_reject_mark ON ixiflow_host_packages; DROP FUNCTION f21_reject_mark();");
            await store.UnregisterMemberAsync(member.Id);
            await Dispose(store);
        }
    }
}
