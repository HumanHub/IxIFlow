using System.Data.Common;
using System.Diagnostics;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class DatabasePoolAlgorithmsTests
{
    [Fact]
    public void DeleteOrderPreservesForeignKeysAndRejectsCycles()
    {
        var order = DatabaseSchemaFingerprint.DeleteOrder(["parent", "child", "leaf", "unrelated"],
            [("leaf", "child"), ("child", "parent"), ("parent", "parent")]);

        Assert.Equal(4, order.Distinct().Count());
        Assert.True(Array.IndexOf(order, "leaf") < Array.IndexOf(order, "child"));
        Assert.True(Array.IndexOf(order, "child") < Array.IndexOf(order, "parent"));
        Assert.Throws<InvalidOperationException>(() => DatabaseSchemaFingerprint.DeleteOrder(["a", "b"], [("a", "b"), ("b", "a")]));
    }

    [Fact]
    public void RowFingerprintPreservesTypesDuplicatesNullsAndColumnBoundaries()
    {
        var baseline = DatabaseSchemaFingerprint.Canonical([[1, "a|b", null], [2, "c", new byte[] { 0, 255 }]]);
        Assert.Equal(baseline, DatabaseSchemaFingerprint.Canonical([[2, "c", new byte[] { 0, 255 }], [1, "a|b", null]]));
        Assert.NotEqual(baseline, DatabaseSchemaFingerprint.Canonical([["1", "a|b", null], [2, "c", new byte[] { 0, 255 }]]));
        Assert.NotEqual(DatabaseSchemaFingerprint.Canonical([[1]]), DatabaseSchemaFingerprint.Canonical([[1], [1]]));
        Assert.NotEqual(DatabaseSchemaFingerprint.Canonical([[null]]), DatabaseSchemaFingerprint.Canonical([[""]]));
        Assert.NotEqual(DatabaseSchemaFingerprint.Canonical([["a|b", "c"]]), DatabaseSchemaFingerprint.Canonical([["a", "b|c"]]));
    }
}

[CollectionDefinition("Database pool verification", DisableParallelization = true)]
public sealed class DatabasePoolVerificationCollection;

[Collection("Database pool verification")]
[Trait("Feature", "durability")]
public sealed class DatabasePoolServerTests
{
    [SqlServerFact, Trait("Category", "PoolServer")]
    public Task SqlServerResetIsolation() => VerifyAsync(true);

    [PostgreSqlFact, Trait("Category", "PoolServer")]
    public Task PostgreSqlResetIsolation() => VerifyAsync(false);

    private static async Task VerifyAsync(bool sql)
    {
        Task<string> Lease(bool empty = false) => sql
            ? SqlServerTestServer.CreateDatabaseAsync("PoolVerification", empty)
            : PostgreSqlTestServer.CreateDatabaseAsync("pool_verification", empty);

        Task Release(string cs) => sql ? SqlServerTestServer.DropDatabaseAsync(cs) : PostgreSqlTestServer.DropDatabaseAsync(cs);

        DbConnection Connect(string cs) => sql
            ? new SqlConnection(new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString)
            : new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);

        var mutations = sql
            ? new[]
            {
                "UPDATE dbo.IxIFlowCatalogRevision SET Revision=42; INSERT INTO dbo.IxIFlowOutboxPublishers (Name,TypesJson) VALUES ('pool-probe','[]'); SELECT NEXT VALUE FOR dbo.IxIFlowExecutionEpoch;",
                "DROP INDEX ix_workflow_versions_revision ON dbo.IxIFlowWorkflowVersions;",
                "ALTER TABLE dbo.IxIFlowWorkflowVersions DROP COLUMN DescriptorJson;",
                "DROP TABLE dbo.IxIFlowCatalogRevision;",
                "DECLARE @key sysname=(SELECT name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.IxIFlowCatalogRevision') AND type='PK'); EXEC('ALTER TABLE dbo.IxIFlowCatalogRevision DROP CONSTRAINT ['+@key+']');"
            }
            : new[]
            {
                "UPDATE ixiflow_catalog_revision SET revision=42; INSERT INTO ixiflow_outbox_publishers (name,types_json) VALUES ('pool-probe','[]'); SELECT nextval('ixiflow_execution_epoch');",
                "DROP INDEX ix_workflow_versions_revision;",
                "ALTER TABLE ixiflow_workflow_versions DROP COLUMN descriptor_json;",
                "DROP TABLE ixiflow_catalog_revision;",
                "ALTER TABLE ixiflow_catalog_revision DROP CONSTRAINT ixiflow_catalog_revision_pkey;",

                // Normalizing the generated FK trigger names must still detect disabled enforcement.
                "ALTER TABLE ixiflow_demand_set_rows DISABLE TRIGGER ALL;"
            };

        DatabaseSchemaFingerprint? reference = null;
        string? databaseName = null;
        long? objectId = null;
        var mutationIndex = 0;

        foreach (var mutation in mutations)
        {
            var cs = await Lease();

            try
            {
                await using var connection = Connect(cs);
                await connection.OpenAsync();
                await using var identity = connection.CreateCommand();

                identity.CommandText = sql
                    ? "SELECT CAST(OBJECT_ID('dbo.IxIFlowOutboxPublishers') AS bigint)"
                    : "SELECT 'ixiflow_outbox_publishers'::regclass::oid::bigint";

                var currentId = (long)(await identity.ExecuteScalarAsync())!;

                if (databaseName != null)
                {
                    Assert.Equal(databaseName, connection.Database); // Failed resets must not silently pass via a replacement DB.

                    if (mutationIndex == 1 && Environment.GetEnvironmentVariable("IXIFLOW_TEST_KEEP_SCHEMA") != "0")
                        Assert.Equal(objectId, currentId); // Rows only: keep the existing schema.
                    else
                        Assert.NotEqual(objectId, currentId); // DDL mutations: full reset.
                }

                databaseName = connection.Database;
                objectId = currentId;
                var actual = await DatabaseSchemaFingerprint.CaptureAsync(connection, sql);
                reference ??= actual;
                Assert.Equal(reference.Schema, actual.Schema);
                Assert.Equal(reference.Data, actual.Data);
                await DatabaseSchemaFingerprint.ExecuteAsync(connection, mutation);
            }
            finally
            {
                await Release(cs);
            }

            mutationIndex++;
        }

        var last = await Lease();

        try
        {
            await using var connection = Connect(last);
            await connection.OpenAsync();
            var actual = await DatabaseSchemaFingerprint.CaptureAsync(connection, sql);
            Assert.Equal(reference!.Schema, actual.Schema);
            Assert.Equal(reference.Data, actual.Data);
        }
        finally
        {
            await Release(last);
        }

        var empty = await Lease(empty: true);

        try
        {
            await using var connection = Connect(empty);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();

            command.CommandText = sql
                ? "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0"
                : "SELECT count(*)::int FROM pg_tables WHERE schemaname='public'";

            Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            await Release(empty);
        }

        // An object the old full reset cannot remove must retire this database, never publish it.
        var poisoned = await Lease();
        string poisonedName;

        await using (var connection = Connect(poisoned))
        {
            await connection.OpenAsync();
            poisonedName = connection.Database;

            await DatabaseSchemaFingerprint.ExecuteAsync(connection, sql
                ? "CREATE VIEW dbo.PoolRetirement AS SELECT 1 AS value;"
                : "CREATE PUBLICATION ixiflow_pool_retirement;");
        }

        await Release(poisoned);
        var replacement = await Lease();

        try
        {
            await using var connection = Connect(replacement);
            await connection.OpenAsync();
            Assert.NotEqual(poisonedName, connection.Database);
            Assert.True(await reference!.MatchesFreshAsync(connection));
        }
        finally
        {
            await Release(replacement);
        }
    }
}

/// <summary>Run this alone before the full runner. No speed thresholds or test-time assertion changes.</summary>
[Collection("Database pool verification")]
[Trait("Feature", "durability")]
public sealed class DatabaseResetTimingProbe(ITestOutputHelper output)
{
    [SqlServerFact, Trait("Category", "Timing")]
    public Task SqlServer() => MeasureAsync(true);

    [PostgreSqlFact, Trait("Category", "Timing")]
    public Task PostgreSql() => MeasureAsync(false);

    private async Task MeasureAsync(bool sql)
    {
        var samples = new List<(double Create, double Reopen, double Wipe)>();

        async Task OpenStore(string cs)
        {
            if (sql)
            {
                var store = new SqlServerWorkflowStateRepository(cs);
                await store.GetCatalogRevisionAsync();
            }
            else
            {
                var store = new PostgreSqlWorkflowStateRepository(cs);
                await store.GetCatalogRevisionAsync();
            }
        }

        // The physical create/drop, reference capture and connection opening are outside measured intervals.
        // Iteration zero warms JIT/provider caches; five subsequent samples are reported individually.
        for (var iteration = 0; iteration <= 5; iteration++)
        {
            var cs = sql
                ? await SqlServerTestServer.CreateFreshDatabaseAsync("SchemaTiming")
                : await PostgreSqlTestServer.CreateFreshDatabaseAsync("schema_timing");

            try
            {
                var watch = Stopwatch.StartNew();
                await OpenStore(cs);
                var create = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                await OpenStore(cs);
                var reopen = watch.Elapsed.TotalMilliseconds;

                await using DbConnection connection = sql
                    ? new SqlConnection(new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString)
                    : new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);

                await connection.OpenAsync();
                var reference = await DatabaseSchemaFingerprint.CaptureAsync(connection, sql);

                await DatabaseSchemaFingerprint.ExecuteAsync(connection, sql
                    ? "UPDATE dbo.IxIFlowCatalogRevision SET Revision=42; INSERT INTO dbo.IxIFlowOutboxPublishers (Name,TypesJson) VALUES ('timing','[]'); SELECT NEXT VALUE FOR dbo.IxIFlowExecutionEpoch;"
                    : "UPDATE ixiflow_catalog_revision SET revision=42; INSERT INTO ixiflow_outbox_publishers (name,types_json) VALUES ('timing','[]'); SELECT nextval('ixiflow_execution_epoch');");

                watch.Restart();
                await DatabaseSchemaFingerprint.ExecuteAsync(connection, "BEGIN TRANSACTION;");
                Assert.True(await reference.TryWipeAsync(connection));
                await DatabaseSchemaFingerprint.ExecuteAsync(connection, "COMMIT;");
                var wipe = watch.Elapsed.TotalMilliseconds;

                if (iteration != 0)
                {
                    samples.Add((create, reopen, wipe));

                    output.WriteLine(
                        $"{(sql ? "sqlserver" : "postgresql")} sample={iteration} create_ms={create:F3} reopen_ms={reopen:F3} verified_row_wipe_ms={wipe:F3} schema={reference.Schema} seeds={reference.Data}");
                }
            }
            finally
            {
                if (sql)
                    await SqlServerTestServer.DropDatabaseAsync(cs);
                else
                    await PostgreSqlTestServer.DropDatabaseAsync(cs);
            }
        }

        output.WriteLine(
            $"mean_ms create={samples.Average(sample => sample.Create):F3} reopen={samples.Average(sample => sample.Reopen):F3} verified_row_wipe={samples.Average(sample => sample.Wipe):F3}");
    }
}