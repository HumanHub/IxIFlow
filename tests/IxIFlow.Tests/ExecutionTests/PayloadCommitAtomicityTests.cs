using System.Text;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class PayloadCommitAtomicityTests
{
    [Fact]
    public Task MemoryBodiesShareTheCheckpointTransaction() => Verify(new InMemoryWorkflowStateRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteBodiesShareTheCheckpointTransaction()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlowPayloadAtomicity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var repository = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "state.db"));
            await Verify(repository);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerBodiesShareTheCheckpointTransaction() =>
        Verify(new SqlWorkflowStateRepository(SqlTestDatabase<PayloadCommitAtomicityTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlBodiesShareTheCheckpointTransaction() =>
        Verify(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<PayloadCommitAtomicityTests>.ConnectionString));

    private static async Task Verify(IWorkflowStateRepository repository)
    {
        var id = "payload-atomic-" + Guid.NewGuid().ToString("N");
        var leafBytes = Encoding.UTF8.GetBytes("\"" + id + "\"");
        var leaf = PayloadReference.Of(leafBytes);
        var rootBytes = Encoding.UTF8.GetBytes("{\"leaf\":\"" + leaf.Digest + "\"}");
        var root = PayloadReference.Of(rootBytes);
        PayloadBody[] bodies = [new(leaf, leafBytes, []), new(root, rootBytes, [leaf])];
        var instance = new WorkflowInstance { InstanceId = id, Status = WorkflowStatus.Running,
            WorkflowDataJson = StoredJson.FromReference(root), ExecutionStateJson = StoreTestSupport.CheckpointJson };
        var lease = await StoreTestSupport.LeaseAsync(repository, id);
        var rejected = CheckpointCommit.Create(instance, lease.Epoch, 1).WithPayloadBodies(bodies);
        Assert.Equal(WorkflowCommitStatus.Conflict, (await repository.CommitWorkflowInstanceAsync(rejected)).Status);
        Assert.Null(await repository.GetWorkflowInstanceAsync(id));
        Assert.Null(await repository.PayloadStore.GetAsync(root));
        Assert.Null(await repository.PayloadStore.GetAsync(leaf));

        var commit = CheckpointCommit.Create(instance, lease.Epoch, 0).WithPayloadBodies(bodies);
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);
        Assert.Equal(root, (await repository.GetWorkflowInstanceAsync(id))!.WorkflowDataJson.Reference);
        Assert.Equal(rootBytes, (await repository.PayloadStore.GetAsync(root))!.Value.ToArray());
        Assert.False(await repository.PayloadStore.CollectAsync(root, TimeSpan.Zero));
        Assert.False(await repository.PayloadStore.CollectAsync(leaf, TimeSpan.Zero));
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);

        var saved = (await repository.GetWorkflowInstanceAsync(id))!;
        saved.WorkflowDataJson = StoredJson.Inline("null");
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, lease.Epoch, saved.Revision))).Status);
        Assert.True(await repository.PayloadStore.CollectAsync(root, TimeSpan.Zero));
        Assert.True(await repository.PayloadStore.CollectAsync(leaf, TimeSpan.Zero));
        // Replaying an old receipt must not recreate bodies or pins already retired by a later transaction.
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);
        Assert.Null(await repository.PayloadStore.GetAsync(root));
        Assert.Null(await repository.PayloadStore.GetAsync(leaf));
    }
}
