using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "J-06")]
public class SqlWorkflowStateRepositoryTests : IClassFixture<SqliteTestDatabase<SqlWorkflowStateRepositoryTests>>
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SuspendedDeadlineAppearsInRecoveryScanWhenDue() => StateRepositoryContract.VerifySuspendedDeadlineRecoveryScan(new SqlWorkflowStateRepository(SqlTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteSuspendedDeadlineAppearsInRecoveryScanWhenDue()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString);
        await StateRepositoryContract.VerifySuspendedDeadlineRecoveryScan(repository);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "A-08")]
    public Task ACommitIsStoredAsItCameAndReadsTakeTheRevisionAndRunnableTimeFromTheirColumns() => StateRepositoryContract.VerifyCommitPayloadAndColumns(new SqlWorkflowStateRepository(SqlTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString), () => new Microsoft.Data.SqlClient.SqlConnection(SqlTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "A-08")]
    public async Task SqliteACommitIsStoredAsItCameAndReadsTakeTheRevisionAndRunnableTimeFromTheirColumns()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString);
        await StateRepositoryContract.VerifyCommitPayloadAndColumns(repository, () => new Microsoft.Data.Sqlite.SqliteConnection(SqliteTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString));
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SuspendedInstance_AppliesOneDeliveryOnceAcrossRepositories() => StateRepositoryContract.VerifyDeliveryOnceAcrossRepositories(() => new SqlWorkflowStateRepository(SqlTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString));

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SuspendedInstance_SurvivesRepositoryRecreation() => StateRepositoryContract.VerifyRepositoryRecreation(() => new SqlWorkflowStateRepository(SqlTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteSuspendedInstance_SurvivesRepositoryRecreation()
    {
        var instanceId = Guid.NewGuid().ToString("N");
        await using var firstRepository = new SqliteWorkflowStateRepository(SqliteTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString);
        SqliteWorkflowStateRepository? secondRepository = null;
        try
        {
            var instance = new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId,
                WorkflowName = "DurableTest",
                WorkflowVersion = 2,
                Status = WorkflowStatus.Suspended,
                CorrelationId = "correlation-1",
                WorkflowDataJson = StoredJson.Inline("{\"Value\":42}")
            };
            await firstRepository.SeedAsync(instance);

            await firstRepository.DisposeAsync();
            secondRepository = new SqliteWorkflowStateRepository(SqliteTestDatabase<SqlWorkflowStateRepositoryTests>.ConnectionString);
            var restored = await secondRepository.GetWorkflowInstanceAsync(instanceId);
            Assert.NotNull(restored);
            Assert.Equal(WorkflowStatus.Suspended, restored.Status);
            Assert.Equal("{\"Value\":42}", restored.WorkflowDataJson.InlineText);
            Assert.Contains(await Task.WhenAll((await secondRepository.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Suspended], Limit: int.MaxValue))).Items.Select(async row => (await secondRepository.GetWorkflowInstanceAsync(row.InstanceId))!)),
                candidate => candidate.InstanceId == instanceId);

            restored.Status = WorkflowStatus.Completed;
            await secondRepository.SeedAsync(restored);
            Assert.DoesNotContain(await Task.WhenAll((await secondRepository.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Suspended], Limit: int.MaxValue))).Items.Select(async row => (await secondRepository.GetWorkflowInstanceAsync(row.InstanceId))!)),
                candidate => candidate.InstanceId == instanceId);
            Assert.Contains(await Task.WhenAll((await secondRepository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "DurableTest", Limit: int.MaxValue))).Items.Select(async row => (await secondRepository.GetWorkflowInstanceAsync(row.InstanceId))!)),
                candidate => candidate.InstanceId == instanceId);
        }
        finally
        {
            if (secondRepository != null)
            {
                try
                {
                    await secondRepository.DeleteWorkflowInstanceAsync(instanceId);
                }
                finally
                {
                    await secondRepository.DisposeAsync();
                }
            }
        }
    }
}
