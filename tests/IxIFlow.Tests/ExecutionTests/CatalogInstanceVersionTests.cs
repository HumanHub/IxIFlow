using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-10")]
[Trait("Spec", "F-11")]
[Trait("Spec", "F-15")]
[Trait("Spec", "D-10")]
public sealed class CatalogInstanceVersionTests
{
    public enum Scenario { Writes, QueryPlan, MissingColumn }
    public static TheoryData<Scenario> Scenarios => new(Enum.GetValues<Scenario>());

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "SqlIntegration")]
    public Task SqlServer(Scenario scenario) => VerifyAsync("sqlserver", scenario);

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSql(Scenario scenario) => VerifyAsync("postgresql", scenario);

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "SqliteIntegration")]
    public Task Sqlite(Scenario scenario) => VerifyAsync("sqlite", scenario);

    private static async Task VerifyAsync(string profile, Scenario scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.CatalogVersion", Guid.NewGuid().ToString("N"));
        var cs = profile switch
        {
            "sqlserver" => await SqlServerTestServer.CreateDatabaseAsync("IxIFlowCatalogVersion"),
            "postgresql" => await PostgreSqlTestServer.CreateDatabaseAsync("ixiflow_catalog_version"),
            _ => SqliteConnectionString(folder)
        };
        try
        {
            var store = Repository(profile, cs);
            try
            {
                Assert.Null(await store.GetWorkflowInstanceAsync("missing"));
                await using DbConnection connection = profile switch
                {
                    "sqlserver" => new SqlConnection(cs),
                    "postgresql" => new NpgsqlConnection(cs),
                    _ => new SqliteConnection(cs + ";Pooling=False")
                };
                await connection.OpenAsync();
                switch (scenario)
                {
                    case Scenario.Writes: await WritesAsync(store, connection, profile); break;
                    case Scenario.QueryPlan: await QueryPlanAsync(store, connection, profile); break;
                    case Scenario.MissingColumn:
                        if (store is IAsyncDisposable owner) await owner.DisposeAsync();
                        await MissingColumnAsync(connection, profile, cs);
                        break;
                }
            }
            finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
        }
        finally
        {
            if (profile == "sqlserver") await SqlServerTestServer.DropDatabaseAsync(cs);
            else if (profile == "postgresql") await PostgreSqlTestServer.DropDatabaseAsync(cs);
            else await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    private static string SqliteConnectionString(string folder)
    {
        Directory.CreateDirectory(folder);
        return "Data Source=" + Path.Combine(folder, "catalog.db");
    }

    private static IWorkflowStateRepository Repository(string profile, string cs) => profile switch
    {
        "sqlserver" => new SqlWorkflowStateRepository(cs),
        "postgresql" => new PostgreSqlWorkflowStateRepository(cs),
        _ => new SqliteWorkflowStateRepository(cs)
    };

    private static async Task WritesAsync(IWorkflowStateRepository store, DbConnection connection, string profile)
    {
        var name = "VersionWrites";
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddSingleton(Workflow.Create<Data>(name, 3).Step<Noop>().Build());
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(name, 3, new Data());
        await AssertVersionAsync(id, 3);
        await client.AwaitAsync(id);

        var parent = (await store.GetWorkflowInstanceAsync(id))!;
        parent.Status = WorkflowStatus.Suspended;
        parent.ExecutionStateJson = StoreTestSupport.CheckpointJson;
        var lease = await StoreTestSupport.LeaseAsync(store, id);
        var committed = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, lease.Epoch, parent.Revision));
        Assert.Equal(WorkflowCommitStatus.Applied, committed.Status);
        parent.Revision = committed.Revision;
        await AssertVersionAsync(id, 3);

        // A changed payload version also has to replace the column on an ordinary commit.
        parent.WorkflowVersion = 8;
        var child = Instance("VersionChild", 11);
        child.ParentInstanceId = id;
        child.ParentWaitId = "child-wait";
        child.Status = WorkflowStatus.Running;
        parent.Status = WorkflowStatus.Suspended;
        lease = await StoreTestSupport.LeaseAsync(store, id);
        committed = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, lease.Epoch, parent.Revision,
            openedWaits: [new("child-wait", WaitKind.Child, "child", ChildCompleted.EventType, null)],
            childStarts: [new(child.InstanceId, JsonSerializer.Serialize(child), "child-wait")]));
        Assert.Equal(WorkflowCommitStatus.Applied, committed.Status);
        await AssertVersionAsync(id, 8);
        await AssertVersionAsync(child.InstanceId, 11);

        await store.DeleteWorkflowInstanceAsync(id);
        await connection.ExecuteAsync(profile == "sqlserver"
            ? "DELETE FROM dbo.IxIFlowWorkflowTombstones WHERE InstanceId=@Id"
            : "DELETE FROM ixiflow_workflow_tombstones WHERE instance_id=@Id", new { Id = id });
        var recreated = Instance(name, 19);
        recreated.InstanceId = id;
        await store.SeedAsync(recreated);
        await AssertVersionAsync(id, 19);

        async Task AssertVersionAsync(string instanceId, int expected)
        {
            var row = await connection.QuerySingleAsync<VersionRow>(profile == "sqlserver"
                ? "SELECT WorkflowVersion AS Version, StateJson AS Json FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId=@Id"
                : "SELECT workflow_version AS Version, state_json AS Json FROM ixiflow_workflow_instances WHERE instance_id=@Id",
                new { Id = instanceId });
            Assert.Equal(expected, row.Version);
            using var payload = JsonDocument.Parse(row.Json);
            Assert.Equal(row.Version, payload.RootElement.GetProperty("WorkflowVersion").GetInt32());
            Assert.Equal(row.Version, (await store.GetWorkflowInstanceAsync(instanceId))!.WorkflowVersion);
        }
    }

    private static async Task MissingColumnAsync(DbConnection connection, string profile, string cs)
    {
        await connection.ExecuteAsync(profile == "sqlserver" ? """
            DROP INDEX IX_IxIFlowWorkflowInstances_UnfinishedWorkflow ON dbo.IxIFlowWorkflowInstances;
            DROP INDEX IX_IxIFlowWorkflowInstances_version_creation ON dbo.IxIFlowWorkflowInstances;
            DROP INDEX IX_IxIFlowWorkflowInstances_workflow_version_creation ON dbo.IxIFlowWorkflowInstances;
            ALTER TABLE dbo.IxIFlowWorkflowInstances DROP COLUMN WorkflowVersion;
            """ : """
            DROP INDEX ix_ixiflow_workflow_instances_unfinished_workflow;
            DROP INDEX ix_ixiflow_instances_version_creation;
            DROP INDEX ix_ixiflow_instances_workflow_version_creation;
            ALTER TABLE ixiflow_workflow_instances DROP COLUMN workflow_version;
            """);
        var reopened = Repository(profile, cs);
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetWorkflowInstanceAsync("missing"));
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains("earlier build", error.Message);
            Assert.Contains("must be recreated", error.Message);
            Assert.Contains(profile == "sqlserver" ? "WorkflowVersion" : "workflow_version", error.InnerException!.Message);
        }
        finally { if (reopened is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    private static async Task QueryPlanAsync(IWorkflowStateRepository store, DbConnection connection, string profile)
    {
        const string caller = "DependingCaller";
        string[] terminals = ["Completed", "Failed", "Cancelled", "Terminated", "TimedOut"];
        var rows = Enumerable.Range(0, 18000).Select(i => new
        {
            Id = "row-" + i,
            Name = i < 6000 || i >= 12000 ? caller : "UnrelatedCaller",
            Version = i >= 12000 ? 8 : 3,
            Status = i < 6000 ? terminals[i % terminals.Length] : "Suspended"
        });
        // Malformed JSON makes any accidental payload read fail, even on a matching caller name.
        await connection.ExecuteAsync(profile switch
        {
            "sqlserver" => """
                INSERT INTO dbo.IxIFlowWorkflowInstances (InstanceId, WorkflowName, WorkflowVersion, CreatedAtUtc, Status, StateJson, Revision)
                SELECT Id, Name, Version, '2026-01-01T00:00:00', Status, 'not-json', 1
                FROM OPENJSON(@Rows) WITH (Id nvarchar(100), Name nvarchar(200), Version int, Status nvarchar(50));
                UPDATE STATISTICS dbo.IxIFlowWorkflowInstances WITH FULLSCAN;
                """,
            "postgresql" => """
                INSERT INTO ixiflow_workflow_instances (instance_id, workflow_name, workflow_version, created_at_utc, status, state_json, revision)
                SELECT "Id", "Name", "Version", TIMESTAMPTZ '2026-01-01 00:00:00+00', "Status", 'not-json', 1
                FROM jsonb_to_recordset(CAST(@Rows AS jsonb)) AS row("Id" text, "Name" text, "Version" integer, "Status" text);
                ANALYZE ixiflow_workflow_instances;
                """,
            _ => """
                INSERT INTO ixiflow_workflow_instances (instance_id, incarnation, workflow_name, workflow_version, created_at_utc, status, state_json, revision)
                SELECT json_extract(value, '$.Id'), @Incarnation, json_extract(value, '$.Name'),
                    json_extract(value, '$.Version'), '2026-01-01 00:00:00.0000000', json_extract(value, '$.Status'), 'not-json', 1 FROM json_each(@Rows);
                ANALYZE ixiflow_workflow_instances;
                """
        }, new { Rows = JsonSerializer.Serialize(rows), Incarnation = Guid.NewGuid().ToString() });

        var method = store.GetType().GetMethod("CatalogSql", BindingFlags.Static | BindingFlags.NonPublic)!;
        var statement = Enum.Parse(method.GetParameters()[0].ParameterType, "CatalogUnfinishedCallers");
        var sql = (string)method.Invoke(null, [statement])!;
        var pairs = new WorkflowVersionReference[] { new(caller, 3), new("UnrelatedCaller", 8) };
        var parameters = new { Callers = JsonSerializer.Serialize(pairs) };
        await AssertQueryAsync(false);
        await AssertPlanAsync();

        var matching = Instance(caller, 3);
        await store.SeedAsync(matching);
        await AssertQueryAsync(true);
        await AssertPlanAsync();
        // The seek comparison must not let the database's case-insensitive collation change identity.
        Assert.Empty(await connection.QueryAsync<CallerRow>(sql,
            new { Callers = JsonSerializer.Serialize(new[] { new WorkflowVersionReference(caller.ToLowerInvariant(), 3) }) }));
        foreach (var terminal in Enum.GetValues<WorkflowStatus>().Where(status => terminals.Contains(status.ToString())))
        {
            matching.Status = terminal;
            await store.SeedAsync(matching);
            await AssertQueryAsync(false);
        }

        async Task AssertQueryAsync(bool found)
        {
            var result = (await connection.QueryAsync<CallerRow>(sql, parameters)).ToArray();
            if (found)
            {
                var row = Assert.Single(result);
                Assert.Equal(caller, row.Name);
                Assert.Equal(3, row.Version);
            }
            else Assert.Empty(result);
        }

        async Task AssertPlanAsync()
        {
            if (profile == "sqlite")
            {
                var plan = (await connection.QueryAsync<SqlitePlanRow>("EXPLAIN QUERY PLAN " + sql, parameters)).ToArray();
                var search = Assert.Single(plan, row => row.Detail.StartsWith("SEARCH instance ", StringComparison.Ordinal));
                Assert.Contains("ix_ixiflow_workflow_instances_unfinished_workflow", search.Detail);
                Assert.Contains("workflow_name=? AND workflow_version=?", search.Detail);
                Assert.DoesNotContain(plan, row => row.Detail.StartsWith("SCAN instance", StringComparison.Ordinal));
            }
            else if (profile == "postgresql")
            {
                var json = await connection.QuerySingleAsync<string>("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, parameters);
                using var plan = JsonDocument.Parse(json);
                var nodes = PostgreSqlNodes(plan.RootElement[0].GetProperty("Plan")).ToArray();
                var access = Assert.Single(nodes, node => node.TryGetProperty("Relation Name", out var relation)
                    && relation.GetString() == "ixiflow_workflow_instances");
                Assert.Contains(access.GetProperty("Node Type").GetString(), new[] { "Index Scan", "Index Only Scan" });
                Assert.Equal("ix_ixiflow_workflow_instances_unfinished_workflow", access.GetProperty("Index Name").GetString());
                var condition = access.GetProperty("Index Cond").GetString()!;
                Assert.Contains("workflow_name", condition);
                Assert.Contains("workflow_version", condition);
            }
            else
            {
                // STATISTICS XML is the executed statement's actual plan, not a SQL-text assertion.
                await using var command = connection.CreateCommand();
                command.CommandText = "SET STATISTICS XML ON;\n" + sql + ";\nSET STATISTICS XML OFF;";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@Callers";
                parameter.Value = parameters.Callers;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync();
                XDocument? plan = null;
                do
                {
                    while (await reader.ReadAsync())
                        if (reader.FieldCount == 1 && reader.GetValue(0).ToString() is { } xml && xml.Contains("<ShowPlanXML"))
                            plan = XDocument.Parse(xml);
                } while (await reader.NextResultAsync());
                Assert.NotNull(plan);
                XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                var accesses = plan.Descendants(ns + "RelOp").Where(op => op.Element(ns + "IndexScan")?
                    .Element(ns + "Object")?.Attribute("Table")?.Value == "[IxIFlowWorkflowInstances]").ToArray();
                var seek = Assert.Single(accesses);
                Assert.Equal("Index Seek", seek.Attribute("PhysicalOp")?.Value);
                Assert.Equal("[IX_IxIFlowWorkflowInstances_UnfinishedWorkflow]",
                    seek.Element(ns + "IndexScan")!.Element(ns + "Object")!.Attribute("Index")?.Value);
                var columns = seek.Descendants(ns + "SeekPredicates").Descendants(ns + "ColumnReference")
                    .Select(column => column.Attribute("Column")?.Value).ToArray();
                Assert.Contains("WorkflowName", columns);
                Assert.Contains("WorkflowVersion", columns);
            }
        }
    }

    private static IEnumerable<JsonElement> PostgreSqlNodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in PostgreSqlNodes(child)) yield return descendant;
    }

    private static WorkflowInstance Instance(string name, int version) => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = name, WorkflowVersion = version,
        Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
        WorkflowDataJson = StoredJson.Inline("{}"), WorkflowDataType = typeof(Data).AssemblyQualifiedName!
    };

    private sealed class CallerRow { public string Name { get; set; } = ""; public int Version { get; set; } }
    private sealed class VersionRow { public int Version { get; set; } public string Json { get; set; } = ""; }
    private sealed class SqlitePlanRow { public string Detail { get; set; } = ""; }
    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
