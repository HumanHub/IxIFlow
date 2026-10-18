using System.Text.Json;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

internal static class InboxAtomicityContract
{
    internal static async Task MixedSettlementsRollBack(IWorkflowStateRepository store)
    {
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "InboxAtomicity", Status = WorkflowStatus.Suspended, WorkflowDataJson = "{\"value\":1}" };
        var child = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "AtomicChild", Status = WorkflowStatus.Running, ParentInstanceId = instance.InstanceId, ParentWaitId = "child" };
        try
        {
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(instance, 0, 0, openedWaits:
                    [new OpenedWait("original", WaitKind.Event, "answer", "answer", null)]))).Status);
            foreach (var id in new[] { "already", "pending" })
                Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(
                    new WorkflowEvent(instance.InstanceId, id, "answer", "{}", "answer"))).Status);
            instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(instance, 0, instance.Revision,
                    settlements: [new EventSettlement("already", EventOutcome.Consumed, "original")]))).Status);
            var before = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            var originalWait = await store.GetWaitAsync(instance.InstanceId, "original");
            var originalEvent = await store.GetEventAsync(instance.InstanceId, "already");
            instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            instance.WorkflowDataJson = "{\"value\":999}";
            var rejected = CheckpointCommit.Create(instance, 0, before.Revision,
                settlements: [new EventSettlement("pending", EventOutcome.Consumed, "original"),
                    new EventSettlement("already", EventOutcome.Consumed, "other")],
                inboxReadThrough: 2,
                openedWaits: [new OpenedWait("child", WaitKind.Child, "child", "child-result", null)],
                closedWaits: [new ClosedWait("original", WaitCloseReason.Answered)],
                childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child")],
                traceEntries: [new ExecutionTraceEntry { ActivityName = "must roll back" }]);
            // Repeating the rejection also proves no success receipt escaped the rollback.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.Equal(WorkflowCommitStatus.EventsChanged, (await store.CommitWorkflowInstanceAsync(rejected)).Status);
                var after = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
                Assert.Equal(before.Revision, after.Revision);
                Assert.Equal(before.WorkflowDataJson, after.WorkflowDataJson);
                Assert.Equal(before.ExecutionStateJson, after.ExecutionStateJson);
                Assert.Equal(before.RunnableAtUtc, after.RunnableAtUtc);
                Assert.Equal(EventOutcome.Pending, (await store.GetEventAsync(instance.InstanceId, "pending"))!.Outcome);
                Assert.Equal(originalEvent, await store.GetEventAsync(instance.InstanceId, "already"));
                Assert.Equal(originalWait, await store.GetWaitAsync(instance.InstanceId, "original"));
                Assert.Null(await store.GetWaitAsync(instance.InstanceId, "child"));
                Assert.Null(await store.GetWorkflowInstanceAsync(child.InstanceId));
                Assert.Empty(await store.GetTraceAsync(instance.InstanceId));
                Assert.Equal("pending", Assert.Single((await store.GetPendingEventsAsync(instance.InstanceId, 0, 10)).Events).EventId);
            }
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(child.InstanceId);
            await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }
}
