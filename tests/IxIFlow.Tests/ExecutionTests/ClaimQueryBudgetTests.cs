using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>Production SQL, 300,000 due rows, actual plans and a 500 ms warmed scan budget.</summary>
[Trait("Feature", "host")]
[Trait("Spec", "C-09")]
[Trait("Category", "ClaimQueryBudget")]
public sealed class ClaimQueryBudgetTests(ITestOutputHelper output)
{
    private static readonly TimeSpan ScanBudget = TimeSpan.FromMilliseconds(500);

    [DockerBudgetFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerPlansAndBudget() => RunAsync(false);

    [DockerBudgetFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlPlansAndBudget() => RunAsync(true);

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteMaterializesAndIndexesTheSnapshot()
    {
        var connectionString = SqliteTestDatabase<ClaimQueryBudgetTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(connectionString);
        await store.GetWorkflowDemandsAsync("schema");
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var query = new ClaimQuery(Guid.NewGuid(), Enumerable.Range(0, 514)
            .ToDictionary(index => "key-" + index, _ => "value"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sql = (string)store.GetType().GetProperty("RunnableSql", flags)!.GetValue(store)!;
        var parameters = (DynamicParameters)store.GetType().GetMethod("PlacementParameters", flags)!
            .Invoke(store, [query, DateTime.UtcNow, query.Limit])!;
        var plan = (await connection.QueryAsync("EXPLAIN QUERY PLAN " + sql, parameters))
            .Select(row => (string)row.detail).ToArray();
        output.WriteLine(string.Join(Environment.NewLine, plan));
        Assert.Contains(plan, row => row.Contains("MATERIALIZE capabilities", StringComparison.Ordinal));
        Assert.Contains(plan, row => row.Contains("MATERIALIZE blocked", StringComparison.Ordinal));
        Assert.Contains(plan, row => row.Contains("SEARCH capability USING AUTOMATIC", StringComparison.Ordinal));
        Assert.Contains(plan, row => row.Contains("SEARCH blocked USING AUTOMATIC", StringComparison.Ordinal));
    }

    private async Task RunAsync(bool postgres)
    {
        var connectionString = postgres
            ? await PostgreSqlTestServer.CreateDatabaseAsync("claim_budget")
            : await SqlServerTestServer.CreateDatabaseAsync("ClaimBudget");
        try
        {
            IWorkflowStateRepository store = postgres ? new PostgreSqlWorkflowStateRepository(connectionString)
                : new SqlWorkflowStateRepository(connectionString);
            await store.GetWorkflowDemandsAsync("schema");
            await using DbConnection connection = postgres ? new NpgsqlConnection(connectionString) : new SqlConnection(connectionString);
            await connection.OpenAsync();
            var hostId = Guid.NewGuid();
            var otherHost = Guid.NewGuid();
            await connection.ExecuteAsync(postgres ? """
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, workflow_name, status, runnable_at_utc, state_json, revision, last_host_id)
                SELECT n::text, 'budget', 'Running', clock_timestamp() - n * INTERVAL '1 millisecond', '{}', 1,
                    CASE n % 3 WHEN 0 THEN @HostId WHEN 1 THEN NULL ELSE @OtherHost END
                FROM generate_series(1, 300000) n;
                INSERT INTO ixiflow_workflow_demands (instance_id, demand_key, demand_value, expires_at_utc, inherit)
                SELECT instance_id, 'gpu', CASE WHEN instance_id::int % 10000 = 0 THEN 'cuda' ELSE 'metal' END, NULL, false
                FROM ixiflow_workflow_instances;
                INSERT INTO ixiflow_workflow_demands (instance_id, demand_key, demand_value, expires_at_utc, inherit)
                SELECT instance_id, 'region', 'eu', NULL, false FROM ixiflow_workflow_instances;
                INSERT INTO ixiflow_workflow_demands (instance_id, demand_key, demand_value, expires_at_utc, inherit)
                SELECT instance_id, 'host', CAST(@OtherHost AS text), clock_timestamp() - INTERVAL '1 minute', false
                FROM ixiflow_workflow_instances WHERE instance_id::int % 5 = 0;
                INSERT INTO ixiflow_workflow_leases (instance_id, token, expires_at_utc, epoch, crash_count)
                SELECT instance_id, 'live', clock_timestamp() + INTERVAL '1 hour', nextval('ixiflow_execution_epoch'), 0
                FROM ixiflow_workflow_instances WHERE instance_id::int % 10000 = 0 AND instance_id::int <= 50000;
                ANALYZE ixiflow_workflow_instances;
                ANALYZE ixiflow_workflow_demands;
                ANALYZE ixiflow_workflow_leases;
                """ : """
                WITH numbers AS (SELECT TOP (300000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                    FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO dbo.IxIFlowWorkflowInstances
                    (InstanceId, WorkflowName, Status, RunnableAtUtc, StateJson, Revision, LastHostId)
                SELECT CONVERT(nvarchar(100), n), 'budget', 'Running', DATEADD(MILLISECOND, -CONVERT(int, n), SYSUTCDATETIME()), '{}', 1,
                    CASE n % 3 WHEN 0 THEN @HostId WHEN 1 THEN NULL ELSE @OtherHost END FROM numbers;
                INSERT INTO dbo.IxIFlowWorkflowDemands (InstanceId, DemandKey, DemandValue, ExpiresAtUtc, Inherit)
                SELECT InstanceId, 'gpu', CASE WHEN CONVERT(int, InstanceId) % 10000 = 0 THEN 'cuda' ELSE 'metal' END, NULL, 0
                FROM dbo.IxIFlowWorkflowInstances;
                INSERT INTO dbo.IxIFlowWorkflowDemands (InstanceId, DemandKey, DemandValue, ExpiresAtUtc, Inherit)
                SELECT InstanceId, 'region', 'eu', NULL, 0 FROM dbo.IxIFlowWorkflowInstances;
                INSERT INTO dbo.IxIFlowWorkflowDemands (InstanceId, DemandKey, DemandValue, ExpiresAtUtc, Inherit)
                SELECT InstanceId, 'host', CONVERT(nvarchar(100), @OtherHost), DATEADD(MINUTE, -1, SYSUTCDATETIME()), 0
                FROM dbo.IxIFlowWorkflowInstances WHERE CONVERT(int, InstanceId) % 5 = 0;
                INSERT INTO dbo.IxIFlowWorkflowLeases (InstanceId, Token, ExpiresAtUtc, Epoch, CrashCount)
                SELECT InstanceId, 'live', DATEADD(HOUR, 1, SYSUTCDATETIME()), NEXT VALUE FOR dbo.IxIFlowExecutionEpoch, 0
                FROM dbo.IxIFlowWorkflowInstances WHERE CONVERT(int, InstanceId) % 10000 = 0 AND CONVERT(int, InstanceId) <= 50000;
                UPDATE STATISTICS dbo.IxIFlowWorkflowInstances WITH FULLSCAN;
                UPDATE STATISTICS dbo.IxIFlowWorkflowDemands WITH FULLSCAN;
                UPDATE STATISTICS dbo.IxIFlowWorkflowLeases WITH FULLSCAN;
                """, new { HostId = hostId, OtherHost = otherHost }, commandTimeout: 180);
            var count = await connection.ExecuteScalarAsync<int>(postgres
                ? "SELECT count(*) FROM ixiflow_workflow_instances" : "SELECT count(*) FROM dbo.IxIFlowWorkflowInstances");
            Assert.Equal(300000, count);

            var measurements = new List<(string Scenario, TimeSpan Elapsed)>();
            foreach (var scenario in new[] { "zero", "sparse", "large", "dense" })
            {
                var capabilities = new Dictionary<string, string> { ["region"] = "eu" };
                if (scenario != "zero") capabilities["gpu"] = scenario == "dense" ? "metal" : "cuda";
                if (scenario == "large")
                    for (var index = 0; index < 512; index++) capabilities.Add("extra-" + index, "value");
                var query = new ClaimQuery(hostId, capabilities, 32);
                await store.GetRunnableInstancesAsync(query); // warm connection pool and plan
                var clock = Stopwatch.StartNew();
                var found = await store.GetRunnableInstancesAsync(query);
                clock.Stop();
                output.WriteLine($"{scenario}: {capabilities.Count} capabilities, {found.Count} rows, {clock.Elapsed.TotalMilliseconds:F1} ms");
                measurements.Add((scenario, clock.Elapsed));
                Assert.Equal(scenario == "zero" ? 0 : scenario == "dense" ? 32 : 25, found.Count);
                Assert.Equal(found.Count, found.Select(row => row.InstanceId).Distinct().Count());
                await PrintPlanAsync(store, connection, query, scenario, postgres);
            }
            // Include a concurrently locked candidate without changing the providers' execution-lock policy.
            await using var held = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(postgres
                ? "SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = '300000' FOR UPDATE"
                : "SELECT 1 FROM dbo.IxIFlowWorkflowInstances WITH (XLOCK, HOLDLOCK, ROWLOCK) WHERE InstanceId = '300000'",
                transaction: held);
            var lockedClock = Stopwatch.StartNew();
            var lockedScan = store.GetRunnableInstancesAsync(new ClaimQuery(hostId,
                new Dictionary<string, string> { ["gpu"] = "cuda", ["region"] = "eu" }, 32));
            await Task.Delay(100);
            await held.RollbackAsync();
            var lockedRows = await lockedScan;
            lockedClock.Stop();
            Assert.Equal(25, lockedRows.Count);
            output.WriteLine($"locked candidate (released after 100 ms): {lockedClock.Elapsed.TotalMilliseconds:F1} ms");
            measurements.Add(("locked candidate (including 100 ms hold)", lockedClock.Elapsed));
            // Keep every scenario's plans even when an earlier scan exceeds the budget.
            Assert.All(measurements, measurement => Assert.True(measurement.Elapsed < ScanBudget,
                $"{measurement.Scenario} exceeded the 500 ms scan budget: {measurement.Elapsed}"));
        }
        finally
        {
            if (postgres) await PostgreSqlTestServer.DropDatabaseAsync(connectionString);
            else await SqlServerTestServer.DropDatabaseAsync(connectionString);
        }
    }

    // Read the real provider builders so plans cannot silently diverge from the shipping scan.
    private async Task PrintPlanAsync(IWorkflowStateRepository store, DbConnection connection, ClaimQuery query,
        string scenario, bool postgres)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sql = (string)store.GetType().GetProperty("RunnableSql", flags)!.GetValue(store)!;
        var parameters = (DynamicParameters)store.GetType().GetMethod("PlacementParameters", flags)!
            .Invoke(store, [query, DateTime.UtcNow, query.Limit])!;
        output.WriteLine($"{scenario}, complete production scan");
        if (postgres)
        {
            var plan = string.Join(Environment.NewLine, await connection.QueryAsync<string>(
                "EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) " + sql, parameters, commandTimeout: 60));
            Assert.Contains("Execution Time", plan);
            output.WriteLine(plan);
        }
        else
        {
            using var results = await connection.QueryMultipleAsync("SET STATISTICS XML ON; " + sql + "; SET STATISTICS XML OFF;",
                parameters, commandTimeout: 60);
            // Snapshot insertion, blocked-demand materialization and selection each
            // emit a plan. Drain all grids, including the selected instance rows.
            var planCount = 0;
            while (!results.IsConsumed)
                foreach (IDictionary<string, object> row in await results.ReadAsync())
                    foreach (var value in row.Values)
                        if (value is string plan && plan.Contains("<ShowPlanXML", StringComparison.Ordinal))
                        {
                            output.WriteLine(plan);
                            planCount++;
                        }
            Assert.True(planCount >= 3, $"Expected snapshot, demand and selection plans; received {planCount}.");
        }
    }
}

/// <summary>Only the budget tests skip when Docker cannot be reached; ordinary integration tests retain their policy.</summary>
public sealed class DockerBudgetFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process == null) return false;
            if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); return false; }
            return process.ExitCode == 0;
        }
        catch { return false; }
    });

    public DockerBudgetFactAttribute()
    { if (!Available.Value) Skip = "Docker is unavailable; actual query-plan and 300,000-row budget tests require Docker."; }
}
