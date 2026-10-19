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
            await Exercise(store, connection, postgres ? "postgres" : "sqlserver");
        }
        finally
        {
            if (postgres) await PostgreSqlTestServer.DropDatabaseAsync(cs);
            else await SqlServerTestServer.DropDatabaseAsync(cs);
        }
    }

    private async Task Exercise(IWorkflowStateRepository store, DbConnection connection, string provider)
    {
        var sqlserver = provider == "sqlserver";
        var postgres = provider == "postgres";
        var host = Guid.NewGuid();
        var cuda = DemandSet.Create([new("gpu", "cuda"), new("region", "eu")]);
        var metal = DemandSet.Create([new("gpu", "metal"), new("region", "eu")]);
        await store.RegisterMemberAsync(UnschedulableTests.Host(host, new Dictionary<string, string>
            { ["gpu"] = "metal", ["region"] = "eu", ["host"] = host.ToString("D") }, TimeSpan.FromMinutes(20)));
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
                    (InstanceId, WorkflowName, Status, RunnableAtUtc, StateJson, Revision, DemandSetId, PinnedHost, PinExpiresAtUtc)
                SELECT CONCAT(@Prefix, n), 'budget', 'Running', DATEADD(MILLISECOND, -CONVERT(int, n), SYSUTCDATETIME()), '{"WorkflowVersion":1}', 1,
                    CASE WHEN @Growth = 0 AND n % 10000 = 0 THEN @Cuda ELSE @Metal END,
                    CASE WHEN n % 5 = 0 THEN @Host END,
                    CASE WHEN n % 5 = 0 THEN DATEADD(MINUTE, -1, SYSUTCDATETIME()) END FROM numbers;
                UPDATE STATISTICS dbo.IxIFlowWorkflowInstances WITH FULLSCAN;
                """ : postgres ? """
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, workflow_name, status, runnable_at_utc, state_json, revision, demand_set_id, pinned_host, pin_expires_at_utc)
                SELECT @Prefix || n::text, 'budget', 'Running', clock_timestamp() - n * INTERVAL '1 millisecond', '{"WorkflowVersion":1}', 1,
                    CASE WHEN NOT @Growth AND n % 10000 = 0 THEN @Cuda ELSE @Metal END,
                    CASE WHEN n % 5 = 0 THEN @Host END,
                    CASE WHEN n % 5 = 0 THEN clock_timestamp() - INTERVAL '1 minute' END FROM generate_series(1, 300000) n;
                ANALYZE ixiflow_workflow_instances;
                """ : """
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 300000)
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, incarnation, workflow_name, status, runnable_at_utc, state_json, revision, demand_set_id, pinned_host, pin_expires_at_utc)
                SELECT @Prefix || n, '00000000-0000-0000-0000-000000000001', 'budget', 'Running', '2020-01-01 00:00:00.0000000', '{"WorkflowVersion":1}', 1,
                    CASE WHEN @Growth = 0 AND n % 10000 = 0 THEN @Cuda ELSE @Metal END,
                    CASE WHEN n % 5 = 0 THEN @Host END, CASE WHEN n % 5 = 0 THEN '2020-01-01 00:00:00.0000000' END FROM numbers;
                ANALYZE ixiflow_workflow_instances;
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
            await store.GetUnschedulableAsync(32); // warm the complete production call
            var start = Stopwatch.GetTimestamp();
            var result = await store.GetUnschedulableAsync(32);
            var elapsed = Stopwatch.GetElapsedTime(start);
            output.WriteLine($"{provider}: {scenario}: {elapsed.TotalMilliseconds:F2} ms");
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
                Assert.Contains(plan, line => line.Contains("ix_ixiflow_instances_set_runnable (demand_set_id=?"));
                Assert.Contains(plan, line => line.Contains("ix_ixiflow_instances_set_pin (demand_set_id=? AND pin_expires_at_utc"));
                Assert.DoesNotContain(plan, line => line.StartsWith("SCAN instance"));
            }
            else if (postgres)
            {
                output.WriteLine($"{scenario}, complete production unschedulable scan");
                var plan = string.Join(Environment.NewLine, await connection.QueryAsync<string>(
                    "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + sql, parameters, commandTimeout: 60));
                output.WriteLine(plan);
                Assert.Contains("Execution Time", plan);
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
}
