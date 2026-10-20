using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "G-11")]
public sealed class StoredTraceRevisionTests : IClassFixture<SqliteTestDatabase<StoredTraceRevisionTests>>
{
    [Fact]
    public Task MemoryKeepsCommitMetadataAcrossPagingRetriesAndInboxAppends() =>
        Verify(new InMemoryWorkflowStateRepository());

    [Fact]
    public async Task SqliteKeepsCommitMetadataAcrossPagingRetriesAndInboxAppends()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<StoredTraceRevisionTests>.ConnectionString);
        await Verify(store);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerKeepsCommitMetadataAcrossPagingRetriesAndInboxAppends() =>
        Verify(new SqlWorkflowStateRepository(SqlTestDatabase<StoredTraceRevisionTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlKeepsCommitMetadataAcrossPagingRetriesAndInboxAppends() =>
        Verify(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<StoredTraceRevisionTests>.ConnectionString));

    private static async Task Verify(IWorkflowStateRepository store)
    {
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "TraceRevisions",
            ExecutionStateJson = StoreTestSupport.CheckpointJson, Status = WorkflowStatus.Running };
        var epoch = (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch;
        var first = CheckpointCommit.Create(instance, epoch, 0, traceEntries:
        [new() { EntryType = TraceEntryType.ActivityStarted }, new() { EntryType = TraceEntryType.ActivityCompleted }]);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(first)).Status);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(first)).Status);
        var page = Assert.Single(await store.GetTraceAsync(instance.InstanceId, limit: 1));
        Assert.Equal(1, page.Revision); Assert.Equal(2, page.CommitEndSequence); Assert.Equal(WorkflowStatus.Running, page.CommitStatus);
        // Responses own their mutable entries; storage must not retain the caller's graph.
        page.Entry.ActivityName = "caller mutation";
        Assert.Empty((await store.GetTraceAsync(instance.InstanceId))[0].Entry.ActivityName);
        instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        var silent = CheckpointCommit.Create(instance, epoch, instance.Revision);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(silent)).Status);
        Assert.Equal(2, (await store.GetTraceAsync(instance.InstanceId)).Count);
        instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        instance.Status = WorkflowStatus.Completed;
        var last = CheckpointCommit.Create(instance, epoch, instance.Revision, traceEntries:
            [new() { EntryType = TraceEntryType.WorkflowCompleted }]);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(last)).Status);
        var completed = Assert.Single(await store.GetTraceAsync(instance.InstanceId, afterSequence: 2));
        Assert.Equal(3, completed.Sequence); Assert.Equal(3, completed.Revision);
        Assert.Equal(3, completed.CommitEndSequence); Assert.Equal(WorkflowStatus.Completed, completed.CommitStatus);
        var refused = await store.AppendEventAsync(new WorkflowEvent(instance.InstanceId, "late", "late", "{}", Key: "late"));
        Assert.Equal(EventAppendStatus.Closed, refused.Status);
        var inbox = Assert.Single(await store.GetTraceAsync(instance.InstanceId, afterSequence: 3));
        Assert.Equal(4, inbox.Sequence); Assert.Equal(3, inbox.Revision);
        Assert.Null(inbox.CommitEndSequence); Assert.Null(inbox.CommitStatus); Assert.Equal(TraceClock.Store, inbox.Entry.Clock);
        Assert.Equal(TraceEntryType.EventRefused, inbox.Entry.EntryType);
        var roundTrip = JsonSerializer.Deserialize<StoredTraceEntry>(JsonSerializer.Serialize(completed))!;
        Assert.Equal(completed.Revision, roundTrip.Revision); Assert.Equal(completed.CommitEndSequence, roundTrip.CommitEndSequence);
        Assert.Equal(completed.CommitStatus, roundTrip.CommitStatus);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("commit_end_sequence")]
    [InlineData("commit_status")]
    public async Task SqliteRefusesAnEarlierTraceSchemaWithoutAlteringIt(string column)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ixiflow-trace-schema-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;
        try
        {
            await using (var store = new SqliteWorkflowStateRepository(connectionString)) await store.GetTraceAsync("absent");
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = $"ALTER TABLE ixiflow_workflow_traces DROP COLUMN {column}";
                await command.ExecuteNonQueryAsync();
            }
            await using var old = new SqliteWorkflowStateRepository(connectionString);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => old.GetTraceAsync("absent"));
            Assert.Contains("earlier build", error.Message);
        }
        finally { File.Delete(path); }
    }
}
