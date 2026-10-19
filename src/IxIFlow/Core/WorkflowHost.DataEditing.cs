using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    internal sealed partial class InstanceOperations
    {
        public async Task<DataEditResult> EditDataAsync(string instanceId, string editId, long expectedRevision,
            DataEditTarget target, IReadOnlyList<DataPatch> patches, string actor, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(editId);
            ArgumentException.ThrowIfNullOrWhiteSpace(actor);
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentOutOfRangeException.ThrowIfNegative(target.PauseGeneration);
            ArgumentNullException.ThrowIfNull(patches);
            if (patches.Count == 0) throw new ArgumentException("An edit requires at least one patch", nameof(patches));
            if (!(hostSettings ?? HostSettings.Default).Debugging) throw EditRefused("Debugging is disabled on this host");
            // Own every path and JSON value across awaits; the caller may reuse its input buffers.
            var request = JsonSerializer.Deserialize<DataEditRequest>(JsonSerializer.Serialize(new DataEditRequest(expectedRevision, target, patches)))!;
            using var entered = await gate.EnterAsync(instanceId, cancellationToken);
            return await EditUnderGateAsync(instanceId, editId, request, actor, cancellationToken);
        }

        private async Task<DataEditResult> EditUnderGateAsync(string instanceId, string editId, DataEditRequest request,
            string actor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = (await _store.GetDebugStatesAsync([instanceId])).GetValueOrDefault(instanceId);
            if (EditReceipt(state, editId, request, actor) is { } receipt) return receipt;
            var key = (hostSettings ?? HostSettings.Default).DataEditAuditKey;
            if (key is not { Length: >= 32 }) throw EditRefused("Data edits require a configured audit key of at least 32 bytes");
            var instance = await _store.GetWorkflowInstanceAsync(instanceId)
                ?? throw EditRefused("Workflow instance does not exist");
            using var signals = Executor.PrepareAssignment(Guid.NewGuid());
            await using var owner = await AcquireAsync(instance, signals);
            if (owner == null)
            {
                var concurrent = (await _store.GetDebugStatesAsync([instanceId])).GetValueOrDefault(instanceId);
                return EditReceipt(concurrent, editId, request, actor)
                    ?? throw EditRefused("The instance is owned by another host; retry the same edit ID");
            }
            using var assignment = Admit(instance, owner, signals.AssignmentId);
            var latest = (await _store.GetDebugStatesAsync([instanceId]))[instanceId];
            if (EditReceipt(latest, editId, request, actor) is { } committed) return committed;
            var current = await _store.GetWorkflowInstanceAsync(instanceId);
            if (current == null || current.Incarnation != instance.Incarnation ||
                current.Status != WorkflowStatus.Paused || current.Revision != request.ExpectedRevision)
                throw EditRefused("The edit requires the expected paused revision");
            var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(current));
            if (checkpoint.Debug.PauseGeneration != request.Target.PauseGeneration)
                throw EditRefused("The pause generation changed");
            var definition = await registry.GetWorkflowDefinitionAsync(current.WorkflowName, current.WorkflowVersion)
                ?? throw EditRefused("Workflow definition is unavailable");
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            if (scopes.StructureHash != checkpoint.StructureHash) throw EditRefused("Workflow structure differs");
            var first = latest.Commands.FirstOrDefault(c => c.AcknowledgedRevision == null);
            if (first != null && first.Id != editId) throw EditRefused("An earlier debug command must be acknowledged first");
            if (await _store.GetCancellationRequestAsync(instanceId) != null) throw EditRefused("Cancellation is pending");

            var order = checked((latest.Commands.LastOrDefault()?.Order ?? 0) + 1);
            if (first != null) order = first.Order; // Recovery of an accepted, unacknowledged edit.
            checkpoint.Debug = checkpoint.Debug with { AppliedOrder = order };
            IReadOnlyList<DataEditDigest> digests;
            StoredCheckpoint prepared;
            try
            {
                checkpoint.RootData = BranchData.Copy(current.WorkflowDataJson.InlineText, definition.WorkflowDataType);
                var branch = request.Target.ContinuationId is { } id ? checkpoint.ContinuationById(id) : null;
                if (request.Target.ContinuationId != null && (branch == null ||
                    branch.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled))
                    throw EditRefused("The selected continuation is not live");
                var data = branch == null ? checkpoint.RootData : checkpoint.DataOf(branch);
                var patched = BranchData.Patch(data, request.Patches);
                digests = EditDigests(instanceId, editId, data, patched, request.Patches, key);
                if (branch?.ParentJoinId != null)
                {
                    branch.DataChanges = BranchData.Changes(checkpoint.DataOf(checkpoint.ForkParent(branch)), patched);
                    branch.DataChanged = false;
                }
                else checkpoint.RootData = patched;
                // Drop ALL cached copies. Rebuild recursively through every still-retained join,
                // including completed children whose changes have yet to merge.
                foreach (var child in checkpoint.Continuations) child.Data = null;
                foreach (var child in checkpoint.Continuations.Where(c => c.ParentJoinId != null))
                    _ = checkpoint.DataOf(child);
                checkpoint.TouchAll();
                checkpoint.Signals = signals;
                checkpoint.Trace.Configure(current, checkpoint, _limits, _time, scopes);
                // Prepare before accepting the command: serialization and size refusals change nothing.
                checkpoint.CloseEndedRequests();
                prepared = ExecutionCheckpointWriter.Prepare(current, checkpoint, checkpoint.RootData, scopes, Now,
                    verify: _limits.VerifyCheckpointParts, preserveContinuationState: true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Converters and setters can include values in exception text; keep them out of diagnostics.
                throw EditRefused("The data contract refused the patch or a descendant could not be rebuilt");
            }
            if (prepared.Bytes > _limits.MaxCheckpointBytes) throw EditRefused("The edited checkpoint exceeds the size limit");
            cancellationToken.ThrowIfCancellationRequested();
            var accepted = await _store.RequestDebugCommandAsync(instanceId,
                new(editId, DebugCommandKind.EditData, request.Target.PauseGeneration, actor, Edit: request));
            if (accepted.Status is not (DebugCommandStatus.Accepted or DebugCommandStatus.AlreadyAccepted))
                throw EditRefused($"Edit command refused: {accepted.Status}");
            if (accepted.Command!.EditResult is { } original) return original;
            if (accepted.Command.Order != order) throw new InvalidOperationException("Edit command order changed under its lease");
            var result = new DataEditResult(instanceId, editId, checked(current.Revision + 1), request.Target);
            checkpoint.Trace.AddAudit(new(actor, editId, request.Target, result.Revision, digests), Now);
            try
            {
                await ExecutionCheckpointWriter.CommitPreparedAsync(assignment.Session, current, checkpoint, prepared, Now);
            }
            catch (Exception error) when (error is CheckpointInterruptedException or ExecutionOwnershipLostException)
            {
                throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure,
                    "The edit was not acknowledged; retry the same edit ID to read its durable outcome");
            }
            return result;
        }

        private static DataEditResult? EditReceipt(WorkflowDebugState? state, string editId, DataEditRequest request, string actor)
        {
            if (state?.Commands.SingleOrDefault(c => c.Id == editId) is not { } previous) return null;
            if (previous.Kind != DebugCommandKind.EditData || previous.Actor != actor ||
                JsonSerializer.Serialize(previous.Edit) != JsonSerializer.Serialize(request))
                throw new ArgumentException("An edit ID cannot be reused with different fields");
            return previous.EditResult;
        }

        private static WorkflowEngineException EditRefused(string reason) => new(WorkflowErrorKind.InvalidRequest, reason);

        private static IReadOnlyList<DataEditDigest> EditDigests(string instanceId, string editId, object before,
            object after, IReadOnlyList<DataPatch> patches, byte[] key)
        {
            var oldNode = BranchData.Node(before); var newNode = BranchData.Node(after);
            string Digest(JsonNode root, IReadOnlyList<string> path, string side)
            {
                JsonNode? value = root;
                var exists = true;
                foreach (var segment in path)
                {
                    if (value is not JsonObject obj || !obj.TryGetPropertyValue(segment, out value)) { exists = false; break; }
                }
                var input = JsonSerializer.Serialize(new { Domain = "IxIFlow.DataEdit.v1", instanceId, editId, path, side, exists, value });
                return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(input)));
            }
            return patches.Select(p => new DataEditDigest(p.Path.ToArray(),
                Digest(oldNode, p.Path, "before"), Digest(newNode, p.Path, "after"))).ToArray();
        }
    }
}
