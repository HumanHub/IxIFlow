using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Category", "SqliteIntegration")]
[Trait("Spec", "C-11")]
public sealed class SqliteWriterTests : IClassFixture<SqliteTestDatabase<SqliteWriterTests>>
{
    private static readonly string ConnectionString = SqliteTestDatabase<SqliteWriterTests>.ConnectionString;

    [Fact]
    public async Task WritersForDifferentInstancesQueueWithoutBlockingTheCaller()
    {
        using var time = new PausedTimeProvider();
        await using var store = new SqliteWorkflowStateRepository(ConnectionString, time);
        var instance = new WorkflowInstance { Status = WorkflowStatus.Running, ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N") };
        var grant = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        var commit = CheckpointCommit.Create(instance, grant.Epoch, 0);
        time.Pause = true;
        var writing = Task.Run(() => store.CommitWorkflowInstanceAsync(commit));
        await time.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<CancellationRequestResult> queued;
        try
        {
            queued = store.RequestCancellationAsync("another-instance", new CancellationReason { ReasonCode = "stop" });
            Assert.False(writing.IsCompleted);
            Assert.False(queued.IsCompleted);
        }
        finally
        {
            time.Release.Set();
        }
        await writing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await queued.WaitAsync(TimeSpan.FromSeconds(10))).Accepted);
    }

    [Theory]
    [InlineData("register publisher")]
    [InlineData("claim delivery")]
    [InlineData("settle delivery")]
    [InlineData("renew delivery lease")]
    [InlineData("cancel")]
    [InlineData("claim lease")]
    [InlineData("renew lease")]
    [InlineData("release lease")]
    [InlineData("append")]
    [InlineData("page")]
    [InlineData("commit")]
    [InlineData("delete")]
    [Trait("Spec", "C-18")]
    public async Task ExternalWriterContentionHasTheSameFailureOnEveryWritePath(string operation)
    {
        await using var store = new SqliteWorkflowStateRepository(ConnectionString);
        var instance = new WorkflowInstance { Status = WorkflowStatus.Running, ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N") };
        await store.SeedAsync(instance);
        var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, instance.Revision);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        Func<Task> write = operation switch
        {
            "register publisher" => () => store.RegisterPublisherAsync("publisher", [OutboxEvent.WorkflowCompleted]),
            "claim delivery" => () => store.ClaimDeliveriesAsync("publisher", "owner", TimeSpan.FromSeconds(10), 1),
            "settle delivery" => () => store.SettleDeliveryAsync("event", "publisher", "owner", OutboxPublishResult.Delivered()),
            "renew delivery lease" => () => store.RenewDeliveryLeaseAsync("event", "publisher", "owner", TimeSpan.FromSeconds(10)),
            "cancel" => () => store.RequestCancellationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "stop" }),
            "claim lease" => () => store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "owner", TimeSpan.FromSeconds(10), new ClaimQuery(Guid.Empty, new Dictionary<string, string>())),
            "renew lease" => () => store.RenewExecutionLeaseAsync(instance.InstanceId, "owner", TimeSpan.FromSeconds(10)),
            "release lease" => () => store.ReleaseExecutionLeaseAsync(instance.InstanceId, "owner"),
            "append" => () => store.AppendEventAsync(new WorkflowEvent(instance.InstanceId, "event", "test", "{}", "test")),
            "page" => () => store.GetPendingEventsAsync(instance.InstanceId, 0, 10),
            "commit" => () => store.CommitWorkflowInstanceAsync(commit),
            "delete" => () => store.DeleteWorkflowInstanceAsync(instance.InstanceId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(write);

        Assert.Contains("External writers are unsupported", error.Message);
        Assert.Contains(Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode, new[] { 5, 6 });
    }

    private sealed class PausedTimeProvider : TimeProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public bool Pause { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (!Pause) return base.GetUtcNow();
            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the writer");
            return base.GetUtcNow();
        }

        public void Dispose() => Release.Dispose();
    }
}
