using System.Diagnostics;
using System.Text;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B_ExecutorBacksOffBeforeResendingContendedCommits(bool sqlite)
    {
        RetryStore retry = null!;
        await using var h = new Harness(sqlite, s => retry = new(s));
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Client.RunAsync(
            Workflow.Create<Data>("retry-delay").Step<Noop>().Build(), new Data { Text = Large })).Status);
        Assert.True(retry.Elapsed >= TimeSpan.FromMilliseconds(50), $"Retries took {retry.Elapsed.TotalMilliseconds}ms");
    }

    private sealed class RetryStore(IWorkflowStateRepository inner) : FaultInjectingRepository(inner)
    {
        private readonly Stopwatch _time = new();
        private int _attempt;
        public TimeSpan Elapsed { get; private set; }
        public override Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            if (++_attempt == 1) _time.Start();
            if (_attempt <= 2) return Task.FromResult(new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0));
            if (_attempt == 3) Elapsed = _time.Elapsed;
            return base.CommitWorkflowInstanceAsync(commit);
        }
    }
}

public sealed class DebugPayloadContentionServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task B_SqlServerSharedDigestWaitsForTheOtherTransaction() =>
        Verify(new SqlWorkflowStateRepository(SqlTestDatabase<DebugPayloadContentionServerTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task B_PostgreSqlSharedDigestWaitsForTheOtherTransaction() =>
        Verify(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<DebugPayloadContentionServerTests>.ConnectionString));

    private static async Task Verify(IWorkflowStateRepository store)
    {
        var reference = await store.PayloadStore.PutAsync(Encoding.UTF8.GetBytes("\"shared-" + Guid.NewGuid().ToString("N") + "\""));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = store.PayloadStore.InTransactionAsync(async (tx, ct) =>
        {
            await tx.PinAsync(new("test", Guid.NewGuid().ToString("N")), reference, ct);
            held.SetResult();
            await release.Task;
        }).AsTask();
        await held.Task;
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var lease = await StoreTestSupport.LeaseAsync(store, id);
            var instance = new WorkflowInstance { InstanceId = id, Status = WorkflowStatus.Running,
                WorkflowDataJson = StoredJson.FromReference(reference), ExecutionStateJson = StoreTestSupport.CheckpointJson };
            var pending = store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, lease.Epoch, 0));
            await Task.Delay(150);
            Assert.False(pending.IsCompleted, "A shared digest should wait for the bounded lock interval");
            release.TrySetResult();
            Assert.Equal(WorkflowCommitStatus.Applied, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        }
        finally { release.TrySetResult(); await holding; }
    }
}
