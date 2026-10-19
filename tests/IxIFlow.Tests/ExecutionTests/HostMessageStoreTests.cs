using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "A-02")]
public sealed class HostMessageStoreTests : IClassFixture<SqliteTestDatabase<HostMessageStoreTests>>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitAndReplayReturnTheStoredRunnableTimeAndAFreshClockSample(bool sqlite)
    {
        var time = new FakeTimeProvider();
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<HostMessageStoreTests>.ConnectionString, time)
            : new InMemoryWorkflowStateRepository(time);
        try { await VerifyReceiptAsync(store, () => time.Advance(TimeSpan.FromSeconds(20))); }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerReturnsSchedulingInCommitAndReplay()
    {
        var store = new SqlWorkflowStateRepository(SqlTestDatabase<HostMessageStoreTests>.ConnectionString);
        await VerifyReceiptAsync(store, () => { });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlReturnsSchedulingInCommitAndReplay()
    {
        var store = new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<HostMessageStoreTests>.ConnectionString);
        await VerifyReceiptAsync(store, () => { });
    }

    private static async Task VerifyReceiptAsync(IWorkflowStateRepository store, Action advance)
    {
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "StoreReceipt",
            WorkflowDataJson = "{}", ExecutionStateJson = StoreTestSupport.CheckpointJson,
            Status = WorkflowStatus.Suspended
        };
        var grant = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "owner", TimeSpan.FromMinutes(5)))!;
        var commit = CheckpointCommit.Create(instance, grant.Epoch, 0,
            openedWaits: [new OpenedWait("timer", WaitKind.Delay, "timer", "", TimeSpan.FromMinutes(1))]);
        var applied = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
        Assert.NotEqual(default, applied.StoreTimeUtc);
        Assert.Equal(applied.StoreTimeUtc.AddMinutes(1), applied.RunnableAtUtc);
        Assert.Equal(applied.RunnableAtUtc, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.RunnableAtUtc);
        advance();
        var replay = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, replay.Status);
        Assert.Equal(applied.RunnableAtUtc, replay.RunnableAtUtc);
        Assert.True(replay.StoreTimeUtc > applied.StoreTimeUtc);
        // A replay must preserve the original deadline while reducing the remaining delay.
        Assert.True(replay.RunnableAtUtc - replay.StoreTimeUtc < applied.RunnableAtUtc - applied.StoreTimeUtc);

        var next = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "next", TimeSpan.FromMinutes(5)))!;
        instance.Status = WorkflowStatus.Completed;
        var completed = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, next.Epoch, applied.Revision));
        Assert.Equal(WorkflowCommitStatus.Applied, completed.Status);
        Assert.Null(completed.RunnableAtUtc);
        Assert.True(completed.StoreTimeUtc >= replay.StoreTimeUtc);
    }

    [Fact]
    public async Task ALeaseDeliversCancellationAndLossThroughItsAcquisitionCallbacks()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "LeaseCallbacks",
            WorkflowDataJson = "{}", ExecutionStateJson = StoreTestSupport.CheckpointJson,
            Status = WorkflowStatus.Running
        };
        await store.SeedAsync(instance);
        await using var watch = new CancellationWatch(store, time);
        var cancellation = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = await WorkflowExecutionLease.TryAcquireAsync(store, instance,
            ExecutionLeaseSettings.Default, watch, error => loss.TrySetResult(error),
            reason => cancellation.TrySetResult(reason), _ => { }, time);
        Assert.NotNull(lease);
        lease.ObserveCancellation(new CancellationReason { ReasonCode = "requested" });
        Assert.Equal("requested", (await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(5))).ReasonCode);
        time.Advance(TimeSpan.FromSeconds(31));
        await loss.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ExecutionOwnershipLostException>(lease.ThrowIfLost);
    }
}


