using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "C-10")]
[Trait("Category", "UnschedulableQueryBudget")]
public sealed class UnschedulableQueryBudgetTests(ITestOutputHelper output)
{
    [DockerBudgetFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerBudgetAndScaling() => Relational(false);

    [DockerBudgetFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlBudgetAndScaling() => Relational(true);

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteBudgetAndScaling()
    {
        var cs = SqliteTestDatabase<UnschedulableQueryBudgetTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(cs);
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync();
        await Exercise(store, connection, "sqlite");
    }

    private async Task Relational(bool postgres)
    {
        var cs = postgres ? await PostgreSqlTestServer.CreateDatabaseAsync("unsched_budget") :
            await SqlServerTestServer.CreateDatabaseAsync("UnschedBudget");
        try
        {
            IWorkflowStateRepository store = postgres ? new PostgreSqlWorkflowStateRepository(cs) : new SqlWorkflowStateRepository(cs);
            await using DbConnection connection = postgres ? new NpgsqlConnection(cs) : new SqlConnection(cs);
            await connection.OpenAsync();
            await Exercise(store, connection, postgres ? "postgres" : "sqlserver", cs);
        }
        finally
        {
            if (postgres) await PostgreSqlTestServer.DropDatabaseAsync(cs);
            else await SqlServerTestServer.DropDatabaseAsync(cs);
        }
    }

    private async Task Exercise(IWorkflowStateRepository store, DbConnection connection, string provider, string? connectionString = null)
    {
        var sqlserver = provider == "sqlserver";
        var postgres = provider == "postgres";
        var host = Guid.NewGuid();
        var cuda = DemandSet.Create([new("gpu", "cuda"), new("region", "eu")]);
        var metal = DemandSet.Create([new("gpu", "metal"), new("region", "eu")]);
        await store.RegisterMemberAsync(UnschedulableTests.Host(host, new Dictionary<string, string>
            { ["gpu"] = "metal", ["region"] = "eu", ["host"] = host.ToString("D") }, TimeSpan.FromMinutes(20)));
        if (postgres) await VerifyPostgreSqlSchemaAndJitAsync(store, connection, connectionString!);
        foreach (var set in new[] { cuda, metal })
        {
            await connection.ExecuteAsync(sqlserver ? "INSERT INTO dbo.IxIFlowDemandSets (DemandSetId) VALUES (@Id)" :
                "INSERT INTO ixiflow_demand_sets (demand_set_id) VALUES (@Id)", new { set.Id });
            foreach (var demand in set.Rows)
                await connection.ExecuteAsync(sqlserver ? """
                    INSERT INTO dbo.IxIFlowDemandSetRows (DemandSetId, DemandKey, DemandValue, Inherit) VALUES (@Id, @Key, @Value, 0)
                    """ : $"""
                    INSERT INTO ixiflow_demand_set_rows (demand_set_id, demand_key, demand_value, inherit) VALUES (@Id, @Key, @Value, {(postgres ? "false" : "0")})
                    """, new { set.Id, demand.Key, demand.Value });
        }
        async Task Insert(bool growth)
        {
            var sql = sqlserver ? """
                WITH numbers AS (SELECT TOP (300000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO dbo.IxIFlowWorkflowInstances
                    (InstanceId, WorkflowName, WorkflowVersion, CreatedAtUtc, Status, RunnableAtUtc, StateJson, Revision, DemandSetId, PinnedHost, PinExpiresAtUtc)
                SELECT CONCAT(@Prefix, n), 'budget', 1, SYSUTCDATETIME(), 'Running', DATEADD(MILLISECOND, -CONVERT(int, n), SYSUTCDATETIME()), '{"WorkflowVersion":1}', 1,
                    CASE WHEN @Growth = 0 AND n % 10000 = 0 THEN @Cuda ELSE @Metal END,
                    CASE WHEN n % 5 = 0 THEN @Host END,
                    CASE WHEN n % 5 = 0 THEN DATEADD(MINUTE, -1, SYSUTCDATETIME()) END FROM numbers;
                """ : postgres ? """
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, workflow_name, workflow_version, created_at_utc, status, runnable_at_utc, state_json, revision, demand_set_id, pinned_host, pin_expires_at_utc)
                SELECT @Prefix || n::text, 'budget', 1, clock_timestamp(), 'Running', clock_timestamp() - n * INTERVAL '1 millisecond', '{"WorkflowVersion":1}', 1,
                    CASE WHEN NOT @Growth AND n % 10000 = 0 THEN @Cuda ELSE @Metal END,
                    CASE WHEN n % 5 = 0 THEN @Host END,
                    CASE WHEN n % 5 = 0 THEN clock_timestamp() - INTERVAL '1 minute' END FROM generate_series(1, 300000) n;
                """ : """
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 300000)
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, incarnation, workflow_name, workflow_version, created_at_utc, status, runnable_at_utc, state_json, revision, demand_set_id, pinned_host, pin_expires_at_utc)
                SELECT @Prefix || n, '00000000-0000-0000-0000-000000000001', 'budget', 1, '2020-01-01 00:00:00.0000000', 'Running', '2020-01-01 00:00:00.0000000', '{"WorkflowVersion":1}', 1,
                    CASE WHEN @Growth = 0 AND n % 10000 = 0 THEN @Cuda ELSE @Metal END,
                    CASE WHEN n % 5 = 0 THEN @Host END, CASE WHEN n % 5 = 0 THEN '2020-01-01 00:00:00.0000000' END FROM numbers;
                """;
            await connection.ExecuteAsync(sql, new { Prefix = growth ? "growth-" : "base-", Growth = growth,
                Cuda = cuda.Id, Metal = metal.Id, Host = host.ToString("D") }, commandTimeout: 180);
        }
        await Insert(false);
        Assert.Equal(4, await connection.ExecuteScalarAsync<int>(sqlserver ? "SELECT count(*) FROM dbo.IxIFlowDemandSetRows" : "SELECT count(*) FROM ixiflow_demand_set_rows"));
        Assert.Equal(60000, await connection.ExecuteScalarAsync<int>(sqlserver ? "SELECT count(*) FROM dbo.IxIFlowWorkflowInstances WHERE PinnedHost IS NOT NULL" :
            "SELECT count(*) FROM ixiflow_workflow_instances WHERE pinned_host IS NOT NULL"));
        // Five live leases leave exactly 25 blocked rows in the sparse failed set.
        var clock = DateTime.UtcNow.AddHours(1);
        foreach (var n in Enumerable.Range(1, 5))
            await connection.ExecuteAsync(sqlserver ? """
                INSERT INTO dbo.IxIFlowWorkflowLeases (InstanceId, Token, ExpiresAtUtc, Epoch, CrashCount)
                VALUES (@Id, 'live', @Expiry, NEXT VALUE FOR dbo.IxIFlowExecutionEpoch, 0)
                """ : postgres ? """
                INSERT INTO ixiflow_workflow_leases (instance_id, token, expires_at_utc, epoch, crash_count)
                VALUES (@Id, 'live', @Expiry, nextval('ixiflow_execution_epoch'), 0)
                """ : """
                INSERT INTO ixiflow_workflow_leases (instance_id, token, expires_at_utc, epoch, crash_count)
                VALUES (@Id, 'live', @Expiry, @Epoch, 0)
                """, new { Id = "base-" + n * 10000, Expiry = provider == "sqlite" ? (object)clock.ToString("yyyy-MM-dd HH:mm:ss.fffffff") : clock, Epoch = n });
        var before = await Measure("300,000 instances");
        await Insert(true);
        var after = await Measure("600,000 instances; growth entirely in a schedulable set");
        if (postgres) await PrintPostgreSqlPeriodicCostsAsync(store, connection, host, after.Page.StoreTimeUtc);
        Assert.Equal(before.Page.Items.Select(r => r.InstanceId), after.Page.Items.Select(r => r.InstanceId));
        // Print both volumes' plans before enforcing either timing budget.
        Assert.True(before.Elapsed < TimeSpan.FromMilliseconds(500),
            $"{provider}: 300,000 instances: {before.Elapsed.TotalMilliseconds:F2} ms exceeds 500 ms");
        Assert.True(after.Elapsed < TimeSpan.FromMilliseconds(500),
            $"{provider}: 600,000 instances: {after.Elapsed.TotalMilliseconds:F2} ms exceeds 500 ms");
        var growthBudget = before.Elapsed * 2 + TimeSpan.FromMilliseconds(20);
        Assert.True(after.Elapsed <= growthBudget,
            $"{provider}: schedulable-set growth: {after.Elapsed.TotalMilliseconds:F2} ms exceeds twice " +
            $"{before.Elapsed.TotalMilliseconds:F2} ms plus 20 ms ({growthBudget.TotalMilliseconds:F2} ms)");

        async Task<(UnschedulablePage Page, TimeSpan Elapsed)> Measure(string scenario)
        {
            // Refresh every backlog input after loading (including leases), at both volumes.
            foreach (var table in sqlserver
                ? new[] { "IxIFlowWorkflowInstances", "IxIFlowDemandSets", "IxIFlowDemandSetRows", "IxIFlowWorkflowLeases", "IxIFlowHosts", "IxIFlowHostCapabilities" }
                : new[] { "ixiflow_workflow_instances", "ixiflow_demand_sets", "ixiflow_demand_set_rows", "ixiflow_workflow_leases", "ixiflow_hosts", "ixiflow_host_capabilities" })
                await connection.ExecuteAsync(sqlserver ? $"UPDATE STATISTICS dbo.{table} WITH FULLSCAN" : $"ANALYZE {table}", commandTimeout: 180);
            var failedSetSize = await connection.ExecuteScalarAsync<int>(sqlserver
                ? "SELECT count(*) FROM dbo.IxIFlowWorkflowInstances WHERE DemandSetId = @Id"
                : "SELECT count(*) FROM ixiflow_workflow_instances WHERE demand_set_id = @Id", new { cuda.Id });
            Assert.Equal(30, failedSetSize);
            await store.GetUnschedulableAsync(32); // warm the complete production call
            // The median of several warmed calls, so one scheduling stall on a busy machine is not the measurement.
            var samples = new List<TimeSpan>();
            UnschedulablePage result = null!;
            for (var sample = 0; sample < 7; sample++)
            {
                var start = Stopwatch.GetTimestamp();
                result = await store.GetUnschedulableAsync(32);
                samples.Add(Stopwatch.GetElapsedTime(start));
            }
            var elapsed = samples.Order().ElementAt(samples.Count / 2);
            output.WriteLine($"{provider}: {scenario}: {elapsed.TotalMilliseconds:F2} ms (median of {samples.Count})");
            Assert.Equal(25, result.Items.Count);
            Assert.All(result.Items, row => Assert.Equal(new WorkflowDemand("gpu", "cuda"), Assert.Single(row.UnmetDemands)));
            var sql = (string)store.GetType().GetProperty("UnschedulableSql", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            var sets = JsonSerializer.Serialize(new[] { new { Id = cuda.Id, Failed = true, Pins = Array.Empty<string>() },
                new { Id = metal.Id, Failed = false, Pins = new[] { host.ToString("D") } } });
            var parameters = new { Sets = sets, Limit = 33, AfterId = (string?)null,
                AfterTime = (DateTime?)null, Now = provider == "sqlite" ? (object)result.StoreTimeUtc.ToString("yyyy-MM-dd HH:mm:ss.fffffff") : result.StoreTimeUtc };
            if (provider == "sqlite")
            {
                var plan = (await connection.QueryAsync("EXPLAIN QUERY PLAN " + sql, parameters)).Select(row => (string)row.detail).ToArray();
                output.WriteLine(string.Join(Environment.NewLine, plan));
                Assert.Contains(plan, line => line.StartsWith("SEARCH instance ") && line.Contains("(demand_set_id=?"));
                // SQLite renders IS NULL as an equality seek, separately from the future range.
                Assert.Contains(plan, line => line.StartsWith("SEARCH instance ") && line.Contains("(demand_set_id=? AND pin_expires_at_utc=?"));
                Assert.Contains(plan, line => line.StartsWith("SEARCH instance ") && line.Contains("(demand_set_id=? AND pin_expires_at_utc>?"));
                Assert.DoesNotContain(plan, line => line.StartsWith("SCAN instance"));
            }
            else if (postgres)
            {
                output.WriteLine($"{scenario}, complete production unschedulable scan");
                await using var transaction = await PostgreSqlPeriodicPlan.BeginAsync(store, connection, "Registry");
                var plan = string.Join(Environment.NewLine, await connection.QueryAsync<string>(
                    "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + sql, parameters, transaction, commandTimeout: 60));
                output.WriteLine(plan);
                Assert.Contains("Execution Time", plan);
                Assert.DoesNotContain("JIT:", plan);
                var json = await connection.QuerySingleAsync<string>(
                    "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, parameters, transaction, commandTimeout: 60);
                using var document = JsonDocument.Parse(json);
                Assert.False(document.RootElement[0].TryGetProperty("JIT", out _));
                var scans = Nodes(document.RootElement[0].GetProperty("Plan"))
                    .Where(node => node.TryGetProperty("Relation Name", out var relation) &&
                        relation.GetString() == "ixiflow_workflow_instances").ToArray();
                Assert.NotEmpty(scans);
                Assert.All(scans, node =>
                {
                    Assert.Contains(node.GetProperty("Node Type").GetString(), new[] { "Index Scan", "Index Only Scan" });
                    var condition = node.GetProperty("Index Cond").GetString()!;
                    // Candidate accesses seek the set; final hydration instead seeks each page ID.
                    // Neither assertion depends on which suitable index PostgreSQL chooses.
                    if (condition.Contains("page.instance_id")) Assert.Contains("instance_id", condition);
                    else Assert.Contains("demand_set_id", condition);
                    var rows = node.GetProperty("Actual Rows").GetDouble();
                    if (node.TryGetProperty("Rows Removed by Filter", out var removed)) rows += removed.GetDouble();
                    if (node.TryGetProperty("Rows Removed by Index Recheck", out var rechecked)) rows += rechecked.GetDouble();
                    // EXPLAIN reports rows per execution: count repeated probes as well.
                    var rowsRead = rows * node.GetProperty("Actual Loops").GetDouble();
                    var bound = condition.Contains("page.instance_id") ? parameters.Limit : failedSetSize + parameters.Limit;
                    Assert.True(rowsRead <= bound, $"An instance access read {rowsRead} rows, exceeding {bound}: {node}");
                });
            }
            else
            {
                using var plans = await connection.QueryMultipleAsync("SET STATISTICS XML ON; " + sql + " SET STATISTICS XML OFF;", parameters);
                while (!plans.IsConsumed)
                    foreach (IDictionary<string, object> row in await plans.ReadAsync())
                        foreach (var value in row.Values)
                            if (value is string plan && plan.Contains("<ShowPlanXML")) output.WriteLine(plan);
            }
            return (result, elapsed);
        }
    }

    private static async Task VerifyPostgreSqlSchemaAndJitAsync(IWorkflowStateRepository store, DbConnection connection, string connectionString)
    {
        var originalJit = await connection.ExecuteScalarAsync<string>("SHOW jit");
        await connection.ExecuteAsync("SET jit = on");
        try
        {
            foreach (var operation in new[] { "Registry", "RunnableRead", "RetentionRead", "OutboxClaim", "Purge" })
            {
                await using (await PostgreSqlPeriodicPlan.BeginAsync(store, connection, operation)) { }
                Assert.Equal("on", await connection.ExecuteScalarAsync<string>("SHOW jit"));
            }
        }
        finally { await connection.ExecuteAsync(originalJit == "on" ? "SET jit = on" : "SET jit = off"); }

        foreach (var index in new[] { "ix_ixiflow_instances_set_runnable", "ix_ixiflow_instances_set_pin", "ix_ixiflow_instances_set_pin_no_expiry" })
        {
            var definition = await connection.QuerySingleAsync<string>("SELECT pg_get_indexdef(CAST(@Index AS regclass))", new { Index = index });
            var invalid = new List<string> { definition.Replace("instance_id)", "instance_id COLLATE \"POSIX\")") };
            if (index.Contains("set_pin"))
            {
                invalid.Add(definition.Replace(" INCLUDE (pinned_host)", ""));
                var expiry = index.EndsWith("no_expiry") ? "(pin_expires_at_utc IS NULL)" : "(pin_expires_at_utc IS NOT NULL)";
                invalid.Add(definition.Replace(expiry, "true"));
                invalid.Add(definition.Replace(expiry, index.EndsWith("no_expiry")
                    ? "(pin_expires_at_utc IS NOT NULL)" : "(pin_expires_at_utc IS NULL)"));
                try
                {
                    await connection.ExecuteAsync($"DROP INDEX {index}");
                    var reopened = new PostgreSqlWorkflowStateRepository(connectionString);
                    var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetWorkflowInstanceAsync("missing"));
                    Assert.Contains("must be recreated", error.Message);
                }
                finally { await connection.ExecuteAsync(definition); }
            }
            foreach (var sql in invalid)
            {
                Assert.NotEqual(definition, sql);
                try
                {
                    await connection.ExecuteAsync($"DROP INDEX {index}; " + sql);
                    var reopened = new PostgreSqlWorkflowStateRepository(connectionString);
                    var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetWorkflowInstanceAsync("missing"));
                    Assert.Contains("must be recreated", error.Message);
                }
                finally { await connection.ExecuteAsync($"DROP INDEX {index}; " + definition); }
            }
        }
        Assert.Null(await new PostgreSqlWorkflowStateRepository(connectionString).GetWorkflowInstanceAsync("missing"));
    }

    private async Task PrintPostgreSqlPeriodicCostsAsync(IWorkflowStateRepository store, DbConnection connection, Guid host, DateTime now)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var runnable = (string)store.GetType().GetProperty("RunnableSql", flags)!.GetValue(store)!;
        var claim = new ClaimQuery(host, new Dictionary<string, string> { ["gpu"] = "metal", ["region"] = "eu" }, 32);
        var placement = store.GetType().GetMethod("PlacementParameters", flags)!.Invoke(store, [claim, now, 33]);
        await Print("claim scan", "RunnableRead", runnable, placement);
        var method = store.GetType().GetMethod("Sql", flags)!;
        foreach (var statement in new[] { "RetentionCandidates", "RetentionBlockedPublishers", "ClaimDeliveries", "ExpireUnattemptedDeliveries" })
        {
            var kind = Enum.Parse(method.GetParameters()[0].ParameterType, statement);
            var sql = (string)method.Invoke(store, [kind])!;
            await Print(statement, statement == "ClaimDeliveries" ? "OutboxClaim" :
                statement.StartsWith("Retention") ? "RetentionRead" : "Purge", sql,
                new { Now = now, Cutoff = now.AddDays(-30), Limit = 33, Name = "missing", Error = "expired" });
        }
        await Print("demand-set collection", "Purge",
            (string)store.GetType().GetProperty("CollectDemandSetsSql", flags)!.GetValue(store)!, new { Limit = 33 });

        async Task Print(string label, string operation, string sql, object? parameters)
        {
            await using var transaction = await PostgreSqlPeriodicPlan.BeginAsync(store, connection, operation);
            // EXPLAIN without ANALYZE neither executes mutations nor pays for compilation. Report
            // the unmodified cost and threshold so high estimates in other periodic queries are visible.
            var json = await connection.QuerySingleAsync<string>("EXPLAIN (FORMAT JSON) " + sql, parameters, transaction);
            using var document = JsonDocument.Parse(json);
            var cost = document.RootElement[0].GetProperty("Plan").GetProperty("Total Cost").GetDouble();
            var threshold = await connection.ExecuteScalarAsync<double>("SHOW jit_above_cost", transaction: transaction);
            output.WriteLine($"{label}: estimated cost {cost:F2}, jit_above_cost {threshold:F2}, jit=off for production transaction");
            Assert.False(document.RootElement[0].TryGetProperty("JIT", out _));
        }
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Nodes(child)) yield return descendant;
    }
}
