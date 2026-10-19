using System.Text.Json;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    internal sealed partial class InstanceOperations
    {
        public async Task<InstanceInspection?> InspectAsync(string instanceId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance == null || !HasStructuredCheckpoint(instance)) return null;
            var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow definition is unavailable");
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance));
            if (checkpoint.StructureHash != scopes.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow structure differs");
            checkpoint.RootData = JsonSerializer.Deserialize(instance.WorkflowDataJson.InlineText,
                WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)!)!;
            var threads = InspectionProjection.Threads(instance, checkpoint, scopes, Now);
            var debug = checkpoint.Debug;
            var result = new InstanceInspection(new(instance.InstanceId, instance.WorkflowName, instance.WorkflowVersion,
                instance.Status, instance.ParentInstanceId, instance.CreatedAt, instance.CompletedAt, instance.LastError),
                instance.Revision, checkpoint.StructureHash,
                new(debug.PauseGeneration, debug.AppliedOrder, debug.Stop, debug.Stepping, debug.Timers?.StartedAtUtc)
                    { Evaluation = debug.Evaluation, SkipOnce = debug.SkipOnce },
                debug.Breakpoints, threads,
                checkpoint.Continuations.Where(c => c.PendingActivity != null).Select(c =>
                {
                    var p = c.PendingActivity!; var a = p.Attempts.LastOrDefault();
                    return new InspectionAttempt(c.Id, p.Id, p.StepId, a?.Id, a?.Kind, a?.Host,
                        p.StartedAtUtc, p.ResolutionReason);
                }).ToArray(),
                checkpoint.Waits.Select(w => new InspectionWait(WaitReference.Of(instanceId, w.Id), w.ContinuationId,
                    w.StepId, w.TableKind, w.Key, w.DeadlineUtc, w.ChildWorkflowId)).ToArray(),
                checkpoint.Continuations.Select(c => new InspectionData(c.Id,
                    JsonSerializer.SerializeToElement(checkpoint.DataOf(c), checkpoint.RootData.GetType()))).ToArray());
            return InspectionPrivacy.Clean(result, WaitIds(instance, checkpoint));
        }

        public async Task<IReadOnlyList<StoredTraceEntry>> GetTracePageAsync(string instanceId, long afterSequence, int limit,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
            if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            var ids = instance != null && HasStructuredCheckpoint(instance)
                ? WaitIds(instance, ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance))) : [];
            var entries = await _store.GetTraceAsync(instanceId, afterSequence, limit);
            return InspectionPrivacy.Clean(entries, ids);
        }

        private static string[] WaitIds(WorkflowInstance instance, ExecutionCheckpoint checkpoint) =>
            checkpoint.Waits.Select(w => w.Id).Concat(checkpoint.BoundReplies.Select(r => r.WaitId))
                .Concat(checkpoint.Continuations.Where(c => c.AcceptedWait != null).Select(c => c.AcceptedWait!.WaitId))
                .Append(instance.ParentWaitId).Append(checkpoint.ExecutionTimerId)
                .Where(id => !string.IsNullOrEmpty(id)).Cast<string>().Distinct().ToArray();
    }
}
