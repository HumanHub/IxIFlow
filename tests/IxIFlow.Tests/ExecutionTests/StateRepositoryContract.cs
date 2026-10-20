using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

internal static class StateRepositoryContract
{
    internal static async Task VerifySuspendedDeadlineRecoveryScan(IWorkflowStateRepository repository, Func<string, Task> makeDue)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            var instance = new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId,
                Status = WorkflowStatus.Suspended,
                RunnableAtUtc = DateTime.UtcNow.AddMinutes(1)
            };
            await repository.SeedAsync(instance, TimeSpan.FromMinutes(1));
            Assert.DoesNotContain(await repository.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 100)),
                candidate => candidate.InstanceId == instanceId);

            await makeDue(instanceId);
            Assert.Contains(await repository.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 100)),
                candidate => candidate.InstanceId == instanceId);

            instance.Status = WorkflowStatus.NeedsResolution;
            await repository.SeedAsync(instance, TimeSpan.Zero);
            Assert.Contains(await repository.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 100)),
                candidate => candidate.InstanceId == instanceId);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }


    internal static async Task VerifyCommitPayloadAndColumns(IWorkflowStateRepository repository, Func<System.Data.Common.DbConnection> newConnection)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId, WorkflowName = "Columns", Status = WorkflowStatus.Suspended,
                ExecutionStateJson = StoreTestSupport.CheckpointJson, Revision = 0, RunnableAtUtc = DateTime.UtcNow.AddYears(5)
            };
            var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0);
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);

            await using (var connection = newConnection())
            {
                await connection.OpenAsync();
                await using var read = connection.CreateCommand();
                read.CommandText = connection is Microsoft.Data.SqlClient.SqlConnection
                    ? "SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId"
                    : "SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId";
                var parameter = read.CreateParameter();
                parameter.ParameterName = connection is Npgsql.NpgsqlConnection ? "InstanceId" : "@InstanceId";
                parameter.Value = instanceId;
                read.Parameters.Add(parameter);
                // Placement is interned; every other payload field is retained exactly.
                var expected = System.Text.Json.Nodes.JsonNode.Parse(commit.Payload)!;
                expected["ExecutionStateJson"]!.AsObject().Remove("Demands");
                Assert.Equal(expected.ToJsonString(), (string?)await read.ExecuteScalarAsync());
            }
            // The payload says revision 0 and a runnable time years away; the columns say what the store set.
            var stored = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            Assert.Equal(1, stored.Revision);
            Assert.Null(stored.RunnableAtUtc);
            Assert.Equal(1, Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "Columns", Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)),
                candidate => candidate.InstanceId == instanceId).Revision);
            Assert.Equal(instance.ExecutionStateJson, stored.ExecutionStateJson);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }


    internal static async Task VerifyDeliveryOnceAcrossRepositories(Func<IWorkflowStateRepository> newRepository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = newRepository();
        var secondRepository = newRepository();
        try
        {
            await ResumeOnceScenario.RunAsync(firstRepository, secondRepository, instanceId);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }


    internal static async Task VerifyRepositoryRecreation(Func<IWorkflowStateRepository> newRepository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = newRepository();
        try
        {
            var instance = new WorkflowInstance
            { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                InstanceId = instanceId,
                WorkflowName = "DurableTest",
                WorkflowVersion = 2,
                Status = WorkflowStatus.Suspended,
                CorrelationId = "correlation-1",
                WorkflowDataJson = "{\"Value\":42}"
            };
            await firstRepository.SeedAsync(instance, TimeSpan.Zero);

            var secondRepository = newRepository();
            var restored = await secondRepository.GetWorkflowInstanceAsync(instanceId);
            Assert.NotNull(restored);
            Assert.Equal(WorkflowStatus.Suspended, restored.Status);
            Assert.Equal("{\"Value\":42}", restored.WorkflowDataJson);
            Assert.Contains(await Task.WhenAll((await secondRepository.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Suspended], Limit: int.MaxValue))).Items.Select(async row => (await secondRepository.GetWorkflowInstanceAsync(row.InstanceId))!)),
                candidate => candidate.InstanceId == instanceId);

            restored.Status = WorkflowStatus.Completed;
            await secondRepository.SeedAsync(restored);
            Assert.DoesNotContain(await Task.WhenAll((await firstRepository.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Suspended], Limit: int.MaxValue))).Items.Select(async row => (await firstRepository.GetWorkflowInstanceAsync(row.InstanceId))!)),
                candidate => candidate.InstanceId == instanceId);
            Assert.Contains(await Task.WhenAll((await firstRepository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "DurableTest", Limit: int.MaxValue))).Items.Select(async row => (await firstRepository.GetWorkflowInstanceAsync(row.InstanceId))!)),
                candidate => candidate.InstanceId == instanceId);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

}
