using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

public class SqlWorkflowStateRepositoryTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SuspendedDeadlineAppearsInRecoveryScanWhenDue()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new SqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId,
                Status = WorkflowStatus.Suspended,
                NextDueAtUtc = DateTime.UtcNow.AddMinutes(1)
            };
            await repository.SaveWorkflowInstanceAsync(instance);
            Assert.DoesNotContain(await repository.GetWorkflowsRequiringRecoveryAsync(),
                candidate => candidate.InstanceId == instanceId);

            instance.NextDueAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await repository.SaveWorkflowInstanceAsync(instance);
            Assert.Contains(await repository.GetWorkflowsRequiringRecoveryAsync(),
                candidate => candidate.InstanceId == instanceId);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SuspendedInstance_HasOneClaimAcrossRepositories()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var firstWait = new SuspensionInfo { SuspendReason = "approval" };

        WorkflowInstance Claim(SuspensionInfo wait) => new()
        {
            InstanceId = instanceId,
            Status = WorkflowStatus.Running,
            SuspensionInfo = wait
        };

        try
        {
            await firstRepository.SaveWorkflowInstanceAsync(new WorkflowInstance
            {
                InstanceId = instanceId,
                Status = WorkflowStatus.Suspended,
                SuspensionInfo = firstWait
            });

            var claims = await Task.WhenAll(
                firstRepository.TryClaimSuspendedWorkflowAsync(Claim(firstWait)),
                secondRepository.TryClaimSuspendedWorkflowAsync(Claim(firstWait)));
            Assert.Single(claims, claimed => claimed);
            Assert.Equal(WorkflowStatus.Running,
                (await firstRepository.GetWorkflowInstanceAsync(instanceId))!.Status);

            var secondWait = new SuspensionInfo { SuspendReason = "approval" };
            await secondRepository.SaveWorkflowInstanceAsync(new WorkflowInstance
            {
                InstanceId = instanceId,
                Status = WorkflowStatus.Suspended,
                SuspensionInfo = secondWait
            });
            Assert.False(await firstRepository.TryClaimSuspendedWorkflowAsync(Claim(firstWait)));
            Assert.True(await secondRepository.TryClaimSuspendedWorkflowAsync(Claim(secondWait)));
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
