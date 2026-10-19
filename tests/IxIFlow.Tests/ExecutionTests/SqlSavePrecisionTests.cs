using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "C-11")]
public class SqlSavePrecisionTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task CommitRoundTripsRunnableTimeWithSubThreeMillisecondPrecision()
    {
        var repository = new SqlWorkflowStateRepository(SqlTestDatabase<SqlSavePrecisionTests>.ConnectionString);
        var instanceId = Guid.NewGuid().ToString("N");
        var wakeAfter = TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(12345);
        try
        {
            var instance = new WorkflowInstance
            {
                ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = instanceId,
                WorkflowName = "CommitPrecision", Status = WorkflowStatus.Suspended
            };
            var grant = await StoreTestSupport.LeaseAsync(repository, instanceId);
            var commit = CheckpointCommit.Create(instance, grant.Epoch, 0, wakeAfter: wakeAfter,
                openedWaits: [new OpenedWait("clock", WaitKind.Delay, "", "", TimeSpan.FromMinutes(2))]);
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);
            var storeNow = (await repository.GetWaitAsync(instanceId, "clock"))!.OpenedAtUtc;
            var runnableAt = storeNow + wakeAfter;
            var restored = await repository.GetWorkflowInstanceAsync(instanceId);
            Assert.NotNull(restored);
            Assert.Equal(runnableAt.Ticks, restored.RunnableAtUtc?.Ticks);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }
}
