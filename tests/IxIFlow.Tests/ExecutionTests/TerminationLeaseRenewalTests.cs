using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "H-04")]
public sealed class TerminationLeaseRenewalTests : IClassFixture<SqliteTestDatabase<TerminationLeaseRenewalTests>>
{
    [Fact]
    public Task Memory()
    {
        var time = new FakeTimeProvider();
        return VerifyAsync(new InMemoryWorkflowStateRepository(time), _ =>
        { time.Advance(TimeSpan.FromSeconds(31)); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Sqlite()
    {
        var time = new FakeTimeProvider();
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<TerminationLeaseRenewalTests>.ConnectionString, time);
        await VerifyAsync(store, _ => { time.Advance(TimeSpan.FromSeconds(31)); return Task.CompletedTask; });
    }

    [SqlServerFact]
    public Task SqlServer()
    {
        var connectionString = SqlTestDatabase<TerminationLeaseRenewalTests>.ConnectionString;
        return VerifyAsync(new SqlWorkflowStateRepository(connectionString), async id =>
        {
            await using var connection = new SqlConnection(connectionString);
            Assert.Equal(1, await connection.ExecuteAsync("UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc=DATEADD(SECOND,-1,SYSUTCDATETIME()) WHERE InstanceId=@Id", new { Id = id }));
        });
    }

    [PostgreSqlFact]
    public Task PostgreSql()
    {
        var connectionString = PostgreSqlTestDatabase<TerminationLeaseRenewalTests>.ConnectionString;
        return VerifyAsync(new PostgreSqlWorkflowStateRepository(connectionString), async id =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            Assert.Equal(1, await connection.ExecuteAsync("UPDATE ixiflow_workflow_leases SET expires_at_utc=clock_timestamp()-INTERVAL '1 second' WHERE instance_id=@Id", new { Id = id }));
        });
    }

    private static async Task VerifyAsync(IWorkflowStateRepository store, Func<string, Task> expire)
    {
        var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        try
        {
            const string owner = "original-owner";
            var duration = TimeSpan.FromSeconds(30);
            var original = await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, owner, duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
            Assert.NotNull(original);
            Assert.True(await store.RenewExecutionLeaseAsync(instance.InstanceId, owner, duration));
            Assert.True((await store.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "hard-stop" })).Accepted);
            Assert.False(await store.RenewExecutionLeaseAsync(instance.InstanceId, owner, duration));
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "takeover", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
            await expire(instance.InstanceId); // Advance the store clock/expiry; never release the original lease.
            var takeover = await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "takeover", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
            Assert.NotNull(takeover);
            Assert.True(takeover.Epoch > original.Epoch);
            var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            saved.Status = WorkflowStatus.Terminated;
            saved.CancellationReason = (await store.GetCancellationRequestAsync(instance.InstanceId))!;
            Assert.Equal(WorkflowCommitStatus.StaleEpoch, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(saved, original.Epoch, saved.Revision))).Status);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(saved, takeover.Epoch, saved.Revision))).Status);
        }
        finally { await store.DeleteWorkflowInstanceAsync(instance.InstanceId); }
    }
}
