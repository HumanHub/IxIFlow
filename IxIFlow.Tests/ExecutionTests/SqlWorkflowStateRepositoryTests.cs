using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

public class SqlWorkflowStateRepositoryTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SuspendedInstance_SurvivesRepositoryRecreation()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
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
                SuspensionInfo = new SuspensionInfo { SuspendReason = "approval" },
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
            Assert.Equal("approval", restored.SuspensionInfo?.SuspendReason);
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
