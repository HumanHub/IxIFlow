using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    internal sealed partial class InstanceOperations
    {
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
        public async Task<bool> FinishTerminationAsync(string instanceId, long? ownedEpoch = null, Guid? expectedIncarnation = null)
        {
            var reason = await _store.GetCancellationRequestAsync(instanceId);
            if (reason?.Mode != WorkflowControlMode.Terminate) return false;
            var token = Guid.NewGuid().ToString("N");
            var epoch = ownedEpoch ?? (await _store.TryAcquireExecutionLeaseAsync(instanceId, token, _leaseSettings.Duration))?.Epoch;
            if (epoch == null) return false;
            try
            {
                var instance = await _store.GetWorkflowInstanceAsync(instanceId);
                if (instance == null || expectedIncarnation is { } identity && instance.Incarnation != identity || WorkflowStoreRules.IsTerminal(instance.Status)) return false;
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
                var commit = CheckpointCommit.Create(instance, epoch.Value, instance.Revision,
                    childCancellations: children, parentNotice: ChildCompleted.NoticeOf(instance, checkpoint), traceEntries: [trace]);
                WorkflowCommitResult receipt;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        receipt = await _store.CommitWorkflowInstanceAsync(commit);
                        if (receipt.Status != WorkflowCommitStatus.Retry || attempt == 3) break;
                    }
                    catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.Infrastructure && attempt < 3)
                    {
                        // An unknown outcome resends the immutable commit and learns its receipt.
                    }
                }
                if (receipt.Status is not (WorkflowCommitStatus.Applied or WorkflowCommitStatus.AlreadyApplied)) return false;
                foreach (var runnable in receipt.MadeRunnable) _control.Runnable(runnable);
                return true;
            }
            finally
            {
                if (ownedEpoch == null) await _store.ReleaseExecutionLeaseAsync(instanceId, token);
            }
        }
    }
}
