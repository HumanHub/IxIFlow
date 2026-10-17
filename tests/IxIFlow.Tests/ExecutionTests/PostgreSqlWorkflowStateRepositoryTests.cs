using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "J-06")]
public class PostgreSqlWorkflowStateRepositoryTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SuspendedDeadlineAppearsInRecoveryScanWhenDue()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId,
                Status = WorkflowStatus.Suspended,
                RunnableAtUtc = DateTime.UtcNow.AddMinutes(1)
            };
            await repository.SaveWorkflowInstanceAsync(instance);
            Assert.DoesNotContain(await repository.GetRunnableInstancesAsync(100),
                candidate => candidate.InstanceId == instanceId);

            instance.RunnableAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await repository.SaveWorkflowInstanceAsync(instance);
            Assert.Contains(await repository.GetRunnableInstancesAsync(100),
                candidate => candidate.InstanceId == instanceId);

            instance.Status = WorkflowStatus.NeedsResolution;
            await repository.SaveWorkflowInstanceAsync(instance);
            Assert.Contains(await repository.GetRunnableInstancesAsync(100),
                candidate => candidate.InstanceId == instanceId);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "A-08")]
    public async Task ACommitIsStoredAsItCameAndReadsTakeTheRevisionAndRunnableTimeFromTheirColumns()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId, WorkflowName = "Columns", Status = WorkflowStatus.Suspended,
                ExecutionStateJson = "{\"Waits\":[]}", Revision = 0, RunnableAtUtc = DateTime.UtcNow.AddYears(5)
            };
            var commit = CheckpointCommit.Create(instance, 0, 0);
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);

            await using (var connection = new Npgsql.NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var read = new Npgsql.NpgsqlCommand(
                    "SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId", connection);
                read.Parameters.AddWithValue("InstanceId", instanceId);
                Assert.Equal(commit.Payload, (string?)await read.ExecuteScalarAsync());
            }
            // The payload says revision 0 and a runnable time years away; the columns say what the store set.
            var stored = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            Assert.Equal(1, stored.Revision);
            Assert.Null(stored.RunnableAtUtc);
            Assert.Equal(1, Assert.Single(await repository.GetWorkflowInstancesByNameAsync("Columns"),
                candidate => candidate.InstanceId == instanceId).Revision);
            Assert.Equal("{\"Waits\":[]}", stored.ExecutionStateJson);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    // Rows are deleted by key in every table; the lease row stays, released, so a later owner gets a higher epoch.
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "C-11")]
    public async Task DeletingAnInstanceDeletesItsRowsInEveryTable()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        var instance = new WorkflowInstance
        {
            InstanceId = instanceId, WorkflowName = "Deleted", Status = WorkflowStatus.Suspended,
            WorkflowDataJson = "{\"Value\":1}"
        };
        Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "owner", TimeSpan.FromMinutes(1)));
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, 1, 0,
                openedWaits: [new OpenedWait("approval", WaitKind.Event, "approval", "Approval", null)],
                traceEntries: [new ExecutionTraceEntry { ActivityName = "first" }]))).Status);
        Assert.Equal(EventAppendStatus.Stored, (await repository.AppendEventAsync(
            new WorkflowEvent(instanceId, "event-1", "Approval", "{}", Key: "approval"))).Status);
        Assert.Equal(6, await CountRowsAsync(connectionString, instanceId));

        await repository.DeleteWorkflowInstanceAsync(instanceId);

        Assert.Equal(0, await CountRowsAsync(connectionString, instanceId));
        Assert.Equal(2, (await repository.TryAcquireExecutionLeaseAsync(instanceId, "next", TimeSpan.FromMinutes(1)))!
            .Epoch);
        await repository.ReleaseExecutionLeaseAsync(instanceId, "next");
    }

    // One row in each of the instance, parts, traces, waits, events and commits tables while the instance exists.
    private static async Task<long> CountRowsAsync(string connectionString, string instanceId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var count = new Npgsql.NpgsqlCommand("""
            SELECT (SELECT count(*) FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_waits WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_events WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_commits WHERE instance_id = @InstanceId)
            """, connection);
        count.Parameters.AddWithValue("InstanceId", instanceId);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SuspendedInstance_AppliesOneDeliveryOnceAcrossRepositories()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = new PostgreSqlWorkflowStateRepository(connectionString);
        var secondRepository = new PostgreSqlWorkflowStateRepository(connectionString);
        try
        {
            await ResumeOnceScenario.RunAsync(firstRepository, secondRepository, instanceId);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SuspendedInstance_SurvivesRepositoryRecreation()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = new PostgreSqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = "DurableTest",
                WorkflowVersion = 2,
                Status = WorkflowStatus.Suspended,
                CorrelationId = "correlation-1",
                WorkflowDataJson = "{\"Value\":42}"
            };
            await firstRepository.SaveWorkflowInstanceAsync(instance);

            var secondRepository = new PostgreSqlWorkflowStateRepository(connectionString);
            var restored = await secondRepository.GetWorkflowInstanceAsync(instanceId);
            Assert.NotNull(restored);
            Assert.Equal(WorkflowStatus.Suspended, restored.Status);
            Assert.Equal("{\"Value\":42}", restored.WorkflowDataJson);
            Assert.Contains(await secondRepository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Suspended),
                candidate => candidate.InstanceId == instanceId);

            restored.Status = WorkflowStatus.Completed;
            await secondRepository.SaveWorkflowInstanceAsync(restored);
            Assert.DoesNotContain(await firstRepository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Suspended),
                candidate => candidate.InstanceId == instanceId);
            Assert.Contains(await firstRepository.GetWorkflowInstancesByNameAsync("DurableTest"),
                candidate => candidate.InstanceId == instanceId);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }
}
