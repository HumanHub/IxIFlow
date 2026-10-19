using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    internal sealed partial class InstanceOperations
    {
        public async Task<DeliverySummary?> GetDeliveryAsync(string eventId, string publisherName) =>
            await _store.GetDeliveryAsync(eventId, publisherName) is { } delivery ? DeliverySummary.From(delivery) : null;
        public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query) => _store.ListDeliveriesAsync(query);
        public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor) => _store.RetryDeliveryAsync(eventId, publisherName, actor);
        public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync() => _store.ListPublishersAsync();
        public Task<bool> EnablePublisherAsync(string publisherName, string actor) => _store.EnablePublisherAsync(publisherName, actor);
        public Task UnregisterPublisherAsync(string publisherName, string actor) => _store.UnregisterPublisherAsync(publisherName, actor);
        private static WorkflowExecutionResult TerminatedResult(string instanceId) =>
            new() { InstanceId = instanceId, Status = WorkflowExecutionStatus.Terminated };

        public Task<InstancePage> SearchInstancesAsync(InstanceQuery query) => _store.QueryInstancesAsync(query);
        public Task<IReadOnlyList<StoredTraceEntry>> GetTracePageAsync(string instanceId, long after, int limit) =>
            _store.GetTraceAsync(instanceId, after, limit);

        public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, string reasonCode, string? description, string? actor) =>
            _store.RequestTerminationAsync(instanceId, new CancellationReason
            {
                Mode = WorkflowControlMode.Terminate, ReasonCode = reasonCode, Description = description ?? "",
                RequestedBy = actor ?? _defaultActor ?? "", RequestedAt = Now
            });

        // Also used by recovery before looking up a definition or deserializing user data. A remote owner
        // stops renewing after termination, so an activity that hangs cannot keep its lease indefinitely.
        public async Task<bool> FinishTerminationAsync(string instanceId, Guid? expectedIncarnation = null)
        {
            var reason = await _store.GetCancellationRequestAsync(instanceId);
            if (reason?.Mode != WorkflowControlMode.Terminate) return false;
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance == null || expectedIncarnation is { } identity && instance.Incarnation != identity ||
                WorkflowStoreRules.IsTerminal(instance.Status)) return false;
            using var signals = Executor.PrepareAssignment(Guid.NewGuid());
            await using var owner = await AcquireAsync(instance, signals);
            if (owner == null) return false;
            using var assignment = Admit(instance, owner, signals.AssignmentId);
            var commit = await PrepareTerminationAsync(instance, owner.Epoch);
            if (commit == null) return false;
            for (var attempt = 1; ; attempt++)
            {
                var reply = await assignment.CheckpointAsync(new(signals.AssignmentId, commit));
                if (reply.Kind == CheckpointReplyKind.Ack)
                {
                    _control.Unschedule(instanceId);
                    return true;
                }
                if (reply.Kind is not (CheckpointReplyKind.Retry or CheckpointReplyKind.Unknown) || attempt == 3)
                    return false;
            }
        }

        // Called inside the assignment's commit queue. Only saved state is used: termination does
        // not require a loaded definition or deserializable user data.
        private async Task<CheckpointCommit?> PrepareTerminationAsync(WorkflowInstance assigned, long epoch)
        {
            var instanceId = assigned.InstanceId;
            var reason = await _store.GetCancellationRequestAsync(instanceId);
            if (reason?.Mode != WorkflowControlMode.Terminate) return null;
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance == null || instance.Incarnation != assigned.Incarnation || WorkflowStoreRules.IsTerminal(instance.Status)) return null;
            // A command blocks all further ordinary commits, including child starts. Every child
            // committed before acceptance is therefore visible to this scan. Detached children follow
            // the same subtree policy: their work is hard-stopped too, even after the parent stopped waiting.
            var children = new List<ChildCancellation>();
            var parents = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { instanceId };
            parents.Enqueue(instanceId);
            while (parents.TryDequeue(out var parentId))
            {
                string? after = null;
                do
                {
                    var page = await _store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: parentId, After: after, Limit: 1000));
                    foreach (var child in page.Items)
                    {
                        if (!visited.Add(child.InstanceId)) continue;
                        if (WorkflowStoreRules.IsTerminal(child.Status))
                        {
                            // A completed child can still have detached descendants. Its result stays
                            // intact, while administrative subtree termination reaches their work.
                            parents.Enqueue(child.InstanceId);
                            continue;
                        }
                        var saved = await _store.GetWorkflowInstanceSummaryAsync(child.InstanceId);
                        if (saved != null)
                            children.Add(new ChildCancellation(child.InstanceId, reason, saved.ParentWaitId));
                    }
                    after = page.NextCursor;
                } while (after != null);
            }
            instance.Status = WorkflowStatus.Terminated;
            instance.CompletedAt = Now;
            instance.CancellationReason = reason;
            instance.LastError = null;
            instance.LastErrorStackTrace = null;
            var state = JsonNode.Parse(instance.ExecutionStateJson)!;
            state["OperatorStop"] = null;
            instance.ExecutionStateJson = state.ToJsonString();
            instance.Stored = null;
            var trace = new ExecutionTraceEntry
            {
                Timestamp = Now, EntryType = TraceEntryType.WorkflowTerminated,
                Payload = new CancellationTrace(reason.RequestedBy, reason.RequestedAt, reason.ReasonCode)
            };
            var checkpoint = ExecutionCheckpoint.ReadTrunk(instance.ExecutionStateJson);
            return CheckpointCommit.Create(instance, epoch, instance.Revision,
                childCancellations: children, parentNotice: ChildCompleted.NoticeOf(instance, checkpoint), traceEntries: [trace]);
        }
    }
}
