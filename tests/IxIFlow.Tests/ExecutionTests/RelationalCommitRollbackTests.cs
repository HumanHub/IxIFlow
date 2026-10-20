using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability"), Trait("Spec", "D-04"), Trait("Spec", "A-03")]
public sealed class RelationalCommitRollbackTests : IClassFixture<SqliteTestDatabase<RelationalCommitRollbackTests>>
{
    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteLateFailureRollsBackEveryCommitWrite()
    {
        var cs = SqliteTestDatabase<RelationalCommitRollbackTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(cs);
        await Verify(store, () => new SqliteConnection(cs));
    }
    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerLateFailureRollsBackEveryCommitWrite()
    {
        var cs = SqlTestDatabase<RelationalCommitRollbackTests>.ConnectionString;
        return Verify(new SqlWorkflowStateRepository(cs), () => new SqlConnection(cs));
    }
    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlLateFailureRollsBackEveryCommitWrite()
    {
        var cs = PostgreSqlTestDatabase<RelationalCommitRollbackTests>.ConnectionString;
        return Verify(new PostgreSqlWorkflowStateRepository(cs), () => new NpgsqlConnection(cs));
    }

    private static async Task Verify(IWorkflowStateRepository store, Func<DbConnection> connect)
    {
        var parent = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "NoticeParent",
            Status = WorkflowStatus.Suspended };
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "LateRollback",
            Status = WorkflowStatus.Suspended, ParentInstanceId = parent.InstanceId, ParentWaitId = "notice",
            WorkflowDataJson = StoredJson.Inline("{\"before\":true}") };
        var child = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "RollbackChild",
            Status = WorkflowStatus.Running, ParentInstanceId = instance.InstanceId, ParentWaitId = "child" };
        var publisher = "rollback-" + instance.InstanceId;
        var trigger = "fail_" + instance.InstanceId;
        await store.RegisterPublisherAsync(publisher, [OutboxEvent.WorkflowCompleted]);
        await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, 0, openedWaits:
            [new OpenedWait("notice", WaitKind.Child, "child", "child-result", null)]));
        await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0, openedWaits:
            [new OpenedWait("original", WaitKind.Event, "answer", "answer", null)]));
        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(
            new WorkflowEvent(instance.InstanceId, "pending", "answer", "{}", "answer"))).Status);
        var before = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        var originalWait = await store.GetWaitAsync(instance.InstanceId, "original");
        instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        instance.Status = WorkflowStatus.Completed;
        instance.WorkflowDataJson = StoredJson.Inline("{\"after\":true}");
        var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, before.Revision,
            settlements: [new EventSettlement("pending", EventOutcome.Consumed, "original")],
            openedWaits: [new OpenedWait("child", WaitKind.Child, "child", "child-result", null)],
            closedWaits: [new ClosedWait("original", WaitCloseReason.Answered)], inboxReadThrough: 1,
            childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child")],
            parentNotice: new ParentNotice(parent.InstanceId, "notice", "fail-notice", "child-result", "{}"),
            traceEntries: [new ExecutionTraceEntry { ActivityName = "rollback" }]);
        await using var connection = connect();
        await connection.OpenAsync();
        var sqlServer = connection is SqlConnection;
        var postgres = connection is NpgsqlConnection;
        // Parent notice is appended after the trunk, parts, receipt, waits, outbox, trace and child inserts.
        var install = sqlServer ? $"""
            CREATE TRIGGER dbo.{trigger} ON dbo.IxIFlowWorkflowEvents AFTER INSERT AS
            IF EXISTS (SELECT 1 FROM inserted WHERE InstanceId='{parent.InstanceId}' AND EventId='fail-notice')
                THROW 51000, 'injected late commit failure', 1;
            """ : postgres ? $"""
            CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.instance_id='{parent.InstanceId}' AND NEW.event_id='fail-notice' THEN
                RAISE EXCEPTION 'injected late commit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON ixiflow_workflow_events FOR EACH ROW EXECUTE FUNCTION {trigger}();
            """ : $"""
            CREATE TRIGGER {trigger} BEFORE INSERT ON ixiflow_workflow_events
            WHEN NEW.instance_id='{parent.InstanceId}' AND NEW.event_id='fail-notice'
            BEGIN SELECT RAISE(ABORT, 'injected late commit failure'); END;
            """;
        var drop = sqlServer ? $"DROP TRIGGER dbo.{trigger}" : postgres
            ? $"DROP TRIGGER {trigger} ON ixiflow_workflow_events; DROP FUNCTION {trigger}()" : $"DROP TRIGGER {trigger}";
        await connection.ExecuteAsync(install);
        try
        {
            var error = await Assert.ThrowsAnyAsync<DbException>(() => store.CommitWorkflowInstanceAsync(commit));
            Assert.Contains("injected late commit failure", error.Message);
            var after = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.WorkflowDataJson, after.WorkflowDataJson);
            Assert.Equal(before.ExecutionStateJson, after.ExecutionStateJson);
            Assert.Equal(originalWait, await store.GetWaitAsync(instance.InstanceId, "original"));
            Assert.Null(await store.GetWaitAsync(instance.InstanceId, "child"));
            Assert.Null(await store.GetWorkflowInstanceAsync(child.InstanceId));
            Assert.Empty(await store.GetTraceAsync(instance.InstanceId));
            Assert.Equal(EventOutcome.Pending, (await store.GetEventAsync(instance.InstanceId, "pending"))!.Outcome);
            Assert.Null(await store.GetEventAsync(parent.InstanceId, "fail-notice"));
            Assert.Empty(await store.ClaimDeliveriesAsync(publisher, "reader", TimeSpan.FromMinutes(1), 100));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(sqlServer
                ? "SELECT COUNT(*) FROM dbo.IxIFlowOutboxEvents WHERE InstanceId=@Id"
                : "SELECT COUNT(*) FROM ixiflow_outbox_events WHERE instance_id=@Id", new { Id = instance.InstanceId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(sqlServer
                ? "SELECT COUNT(*) FROM dbo.IxIFlowOutboxDeliveries WHERE PublisherName=@Publisher"
                : "SELECT COUNT(*) FROM ixiflow_outbox_deliveries WHERE publisher_name=@Publisher", new { Publisher = publisher }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(sqlServer
                ? "SELECT COUNT(*) FROM dbo.IxIFlowWorkflowCommits WHERE InstanceId=@Id AND CommitId=@CommitId"
                : "SELECT COUNT(*) FROM ixiflow_workflow_commits WHERE instance_id=@Id AND commit_id=@CommitId",
                new { Id = instance.InstanceId, commit.CommitId }));
        }
        finally { await connection.ExecuteAsync(drop); }
        // The identical transition must still be admissible after the command failure.
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        await store.UnregisterPublisherAsync(publisher);
        await store.DeleteWorkflowInstanceAsync(child.InstanceId);
        await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
        await store.DeleteWorkflowInstanceAsync(parent.InstanceId);
    }
}
