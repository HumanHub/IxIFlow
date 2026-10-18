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
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N") };
        var writing = Task.Run(() => store.SaveWorkflowInstanceAsync(instance));
        await time.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<bool> queued;
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
        Assert.False(await queued.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("claim completion")]
    [InlineData("mark completion")]
    [InlineData("release completion")]
    [InlineData("cancel")]
    [InlineData("claim lease")]
    [InlineData("renew lease")]
    [InlineData("release lease")]
    [InlineData("append")]
    [InlineData("page")]
    [InlineData("save")]
    [InlineData("commit")]
    [InlineData("delete")]
    [Trait("Spec", "C-18")]
    public async Task ExternalWriterContentionHasTheSameFailureOnEveryWritePath(string operation)
    {
        await using var store = new SqliteWorkflowStateRepository(ConnectionString);
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N") };
        await store.SaveWorkflowInstanceAsync(instance);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        Func<Task> write = operation switch
        {
            "claim completion" => () => store.ClaimUnpublishedCompletionsAsync(instance.InstanceId, "owner", TimeSpan.FromSeconds(10)),
            "mark completion" => () => store.MarkCompletionPublishedAsync(instance.InstanceId, 0, "owner"),
            "release completion" => () => store.ReleaseCompletionClaimAsync(instance.InstanceId, "owner"),
            "cancel" => () => store.RequestCancellationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "stop" }),
            "claim lease" => () => store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "owner", TimeSpan.FromSeconds(10)),
            "renew lease" => () => store.RenewExecutionLeaseAsync(instance.InstanceId, "owner", TimeSpan.FromSeconds(10)),
            "release lease" => () => store.ReleaseExecutionLeaseAsync(instance.InstanceId, "owner"),
            "append" => () => store.AppendEventAsync(new WorkflowEvent(instance.InstanceId, "event", "test", "{}", "test")),
            "page" => () => store.GetPendingEventsAsync(instance.InstanceId, 0, 10),
            "save" => () => store.SaveWorkflowInstanceAsync(instance),
            "commit" => () => store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0)),
            "delete" => () => store.DeleteWorkflowInstanceAsync(instance.InstanceId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(write);

        Assert.Contains("External writers are unsupported (C-18)", error.Message);
        Assert.Contains(Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode, new[] { 5, 6 });
    }

    private sealed class PausedTimeProvider : TimeProvider, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public override DateTimeOffset GetUtcNow()
        {
            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the writer");
            return base.GetUtcNow();
        }

        public void Dispose() => Release.Dispose();
    }
}
