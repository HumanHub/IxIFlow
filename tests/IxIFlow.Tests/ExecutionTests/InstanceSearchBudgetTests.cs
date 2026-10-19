using System.Diagnostics;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class InstanceSearchBudgetTests
{
    [DockerSearchFact]
    [Trait("Category", "Docker")]
    public async Task PostgreSqlSearchUsesBoundedIndexedPagesAtThreeHundredThousandRows()
    {
        var cs = await PostgreSqlTestServer.CreateDatabaseAsync("SearchBudget");
        try
        {
            var store = new PostgreSqlWorkflowStateRepository(cs);
            await store.QueryInstancesAsync(new InstanceQuery()); // create the production schema
            await using var connection = new NpgsqlConnection(cs);
            await connection.ExecuteAsync("""
                INSERT INTO ixiflow_workflow_instances
                    (instance_id, incarnation, workflow_name, workflow_version, created_at_utc, status,
                     correlation_id, parent_instance_id, ended_at_utc, stop_kind, stop_crash_count, state_json, revision)
                SELECT 'row-' || lpad(i::text, 9, '0'), md5(i::text)::uuid, 'workflow-' || (i % 100), i % 5,
                    '2026-01-01'::timestamptz + (i / 10) * interval '1 second',
                    CASE WHEN i % 5 = 0 THEN 'Completed' WHEN i % 7 = 0 THEN 'NeedsResolution' ELSE 'Suspended' END,
                    'correlation-' || (i % 100), 'parent-' || (i % 1000),
                    CASE WHEN i % 5 = 0 THEN '2026-01-01'::timestamptz + (i / 10) * interval '1 second' END,
                    CASE WHEN i % 7 = 0 THEN 'Quarantine' END, CASE WHEN i % 7 = 0 THEN 7 END,
                    'intentionally unread', 1
                FROM generate_series(1, 300000) AS i;
                ANALYZE ixiflow_workflow_instances;
                """, commandTimeout: 120);
            string[] filters = ["true", "workflow_name='workflow-2'", "status='Suspended'",
                "status=ANY(ARRAY['Suspended','NeedsResolution'])", "workflow_version=2",
                "workflow_name='workflow-2' AND workflow_version=2", "stop_kind='Quarantine'",
                "correlation_id='correlation-2'", "parent_instance_id='parent-2'",
                "ended_at_utc >= '2026-01-01'::timestamptz",
                "created_at_utc <= '2026-01-01 04:10:00+00'::timestamptz AND (created_at_utc < '2026-01-01 04:10:00+00'::timestamptz OR (created_at_utc = '2026-01-01 04:10:00+00'::timestamptz AND instance_id > 'row-000150005'))"];
            foreach (var filter in filters)
            {
                var json = await connection.ExecuteScalarAsync<string>($"""
                    EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
                    SELECT instance_id, workflow_name, workflow_version, status, stop_kind, stop_crash_count,
                        created_at_utc, ended_at_utc, correlation_id, parent_instance_id, runnable_at_utc, revision, incarnation
                    FROM ixiflow_workflow_instances WHERE {filter}
                    ORDER BY created_at_utc DESC, instance_id ASC LIMIT 101
                    """);
                using var plan = JsonDocument.Parse(json!);
                var root = plan.RootElement[0].GetProperty("Plan");
                Assert.DoesNotContain("Seq Scan", root.GetRawText());
                Assert.True(root.GetProperty("Shared Hit Blocks").GetInt32() + root.GetProperty("Shared Read Blocks").GetInt32() < 2500,
                    $"Search exceeded 2500 buffer accesses: {filter}: {root}");
            }
            var first = await store.QueryInstancesAsync(new InstanceQuery(Limit: 100));
            var second = await store.QueryInstancesAsync(new InstanceQuery(After: first.NextCursor, Limit: 100));
            Assert.Equal(100, second.Items.Count);
            Assert.Empty(first.Items.Select(row => row.InstanceId).Intersect(second.Items.Select(row => row.InstanceId)));
        }
        finally { await PostgreSqlTestServer.DropDatabaseAsync(cs); }
    }
}

/// <summary>The large search measurement is scheduled only when the Docker daemon is reachable.</summary>
public sealed class DockerSearchFactAttribute : FactAttribute
{
    public DockerSearchFactAttribute()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (process == null || !process.WaitForExit(2000))
            {
                if (process != null) process.Kill();
                Skip = "Docker is unavailable";
            }
            else if (process.ExitCode != 0) Skip = "Docker is unavailable";
        }
        catch { Skip = "Docker is unavailable"; }
    }
}
