using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "C-11")]
public class SqlSavePrecisionTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SaveRoundTripsRunnableTimeWithSubThreeMillisecondPrecision()
    {
        var repository = new SqlWorkflowStateRepository(SqlTestDatabase<SqlSavePrecisionTests>.ConnectionString);
        var instanceId = Guid.NewGuid().ToString("N");
        var runnableAt = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(12345);
        try
        {
            await repository.SeedAsync(new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId,
                WorkflowName = "SavePrecision",
                Status = WorkflowStatus.Suspended,
                RunnableAtUtc = runnableAt
            });
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
