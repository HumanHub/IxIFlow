using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "J-06")]
public class SqlWorkflowStateRepositoryTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SuspendedDeadlineAppearsInRecoveryScanWhenDue()
    {
        var connectionString = SqlServerTestServer.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new SqlWorkflowStateRepository(connectionString);
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

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "A-08")]
    public async Task ACommitIsStoredAsItCameAndReadsTakeTheRevisionAndRunnableTimeFromTheirColumns()
    {
        var connectionString = SqlServerTestServer.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new SqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId, WorkflowName = "Columns", Status = WorkflowStatus.Suspended,
                ExecutionStateJson = "{\"Waits\":[]}", Revision = 0, RunnableAtUtc = DateTime.UtcNow.AddYears(5)
            };
            var commit = CheckpointCommit.Create(instance, 0, 0);
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);

            await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var read = new Microsoft.Data.SqlClient.SqlCommand(
                    "SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId", connection);
                read.Parameters.AddWithValue("@InstanceId", instanceId);
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

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SuspendedInstance_AppliesOneDeliveryOnceAcrossRepositories()
    {
        var connectionString = SqlServerTestServer.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        try
        {
            await ResumeOnceScenario.RunAsync(firstRepository, secondRepository, instanceId);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SuspendedInstance_SurvivesRepositoryRecreation()
    {
        var connectionString = SqlServerTestServer.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = "DurableTest",
                WorkflowVersion = 2,
                Status = WorkflowStatus.Suspended,
                CorrelationId = "correlation-1",
                WorkflowDataJson = "{\"Value\":42}",
                ExecutionSnapshot = new WorkflowExecutionSnapshot
                {
                    InstanceId = instanceId,
                    Frames = [new ExecutionFrame { StepId = "try-1", Kind = "TryCatch" }]
                }
            };
            await firstRepository.SaveWorkflowInstanceAsync(instance);

            var secondRepository = new SqlWorkflowStateRepository(connectionString);
            var restored = await secondRepository.GetWorkflowInstanceAsync(instanceId);
            Assert.NotNull(restored);
            Assert.Equal(WorkflowStatus.Suspended, restored.Status);
            Assert.Equal("{\"Value\":42}", restored.WorkflowDataJson);
            Assert.Equal("try-1", Assert.Single(restored.ExecutionSnapshot!.Frames).StepId);
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
