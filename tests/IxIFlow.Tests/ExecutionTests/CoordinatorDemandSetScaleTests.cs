using System.Diagnostics;
using System.Reflection;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
public sealed class CoordinatorDemandSetScaleTests(ITestOutputHelper output) : IClassFixture<SqliteTestDatabase<CoordinatorDemandSetScaleTests>>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DormantSetsAreExcludedBeforeDemandRowsAreRead(bool sqlite)
    {
        var cs = SqliteTestDatabase<CoordinatorDemandSetScaleTests>.ConnectionString;
        IWorkflowStateRepository store = sqlite ? new SqliteWorkflowStateRepository(cs) : new InMemoryWorkflowStateRepository();
        try
        {
            var (due, checkpoint, scopes) = WorkflowInstances.NewInstance(
                Workflow.Create<Data>("Scale").Demand("due", "yes").Step<Noop>().Build(), new Data(), new(), DateTime.UtcNow);
            ExecutionCheckpointWriter.Prepare(due, checkpoint, new Data(), scopes, DateTime.UtcNow);
            await store.SeedAsync(due, TimeSpan.Zero);
            var query = new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>
                { ["due"] = "yes", ["workflow:Scale@1"] = scopes.StructureHash });
            var before = await Measure();
            if (sqlite)
            {
                await using var connection = new SqliteConnection(cs);
                await connection.OpenAsync();
                await connection.ExecuteAsync("""
                    WITH RECURSIVE n(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM n WHERE value<4096)
                    INSERT INTO ixiflow_demand_sets SELECT 'dormant-' || value FROM n;
                    INSERT INTO ixiflow_demand_set_rows (demand_set_id, demand_key, demand_value, inherit)
                    SELECT demand_set_id, 'dormant', demand_set_id, 0 FROM ixiflow_demand_sets WHERE demand_set_id LIKE 'dormant-%';
                    INSERT INTO ixiflow_workflow_instances
                        (instance_id, incarnation, workflow_name, workflow_version, created_at_utc, state_json, status,
                         runnable_at_utc, demand_set_id, revision)
                    SELECT sets.demand_set_id, source.incarnation, source.workflow_name, source.workflow_version,
                        source.created_at_utc, source.state_json, source.status, NULL, sets.demand_set_id, source.revision
                    FROM ixiflow_demand_sets AS sets CROSS JOIN ixiflow_workflow_instances AS source
                    WHERE sets.demand_set_id LIKE 'dormant-%' AND source.instance_id = @Id;
                    ANALYZE;
                    """, new { Id = due.InstanceId });
                var after = await Measure();
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var sql = (string)store.GetType().GetProperty("AllDemandSetsSql", flags)!.GetValue(store)!;
                var rows = (await connection.QueryAsync(sql, new { Now = DateTime.UtcNow })).ToArray();
                output.WriteLine($"SQLite candidate demand rows with 4096 dormant sets: {rows.Length}");
                output.WriteLine($"SQLite median claim/backlog: {before.TotalMilliseconds:F2} -> {after.TotalMilliseconds:F2} ms");
                Assert.Equal(2, rows.Length); // due tag and immutable workflow hash; no dormant rows
                var plans = (await connection.QueryAsync("EXPLAIN QUERY PLAN " + sql, new { Now = DateTime.UtcNow }))
                    .Select(row => (string)row.detail).ToArray();
                output.WriteLine(string.Join(Environment.NewLine, plans));
                Assert.Contains(plans, row => row.Contains("ix_ixiflow_instances_set_runnable", StringComparison.Ordinal)
                    && row.Contains("demand_set_id=?", StringComparison.Ordinal));
            }
            else
            {
                for (var i = 0; i < 4096; i++)
                {
                    var (instance, state, catalog) = WorkflowInstances.NewInstance(
                        Workflow.Create<Data>("Dormant").Demand("dormant", i.ToString()).Step<Noop>().Build(), new Data(), new(), DateTime.UtcNow);
                    ExecutionCheckpointWriter.Prepare(instance, state, new Data(), catalog, DateTime.UtcNow);
                    instance.Status = WorkflowStatus.Completed;
                    await store.SeedAsync(instance);
                }
                var after = await Measure();
                output.WriteLine($"Memory median claim/backlog with 4096 dormant sets: {before.TotalMilliseconds:F2} -> {after.TotalMilliseconds:F2} ms");
            }
            Assert.Equal(due.InstanceId, Assert.Single(await store.GetRunnableInstancesAsync(query)).InstanceId);
            Assert.Equal(due.InstanceId, Assert.Single((await store.GetUnschedulableAsync()).Items).InstanceId);

            async Task<TimeSpan> Measure()
            {
                var samples = new List<TimeSpan>();
                for (var i = 0; i < 7; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    await store.GetRunnableInstancesAsync(query);
                    await store.GetUnschedulableAsync();
                    samples.Add(Stopwatch.GetElapsedTime(start));
                }
                return samples.Order().ElementAt(3);
            }
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }
    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
