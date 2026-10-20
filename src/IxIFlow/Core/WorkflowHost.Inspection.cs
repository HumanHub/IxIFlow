using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
using System.Text.Json;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    internal sealed partial class InstanceOperations
    {
        private async Task<WorkflowDefinitionLease> DebugDefinitionAsync(string instanceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId)
                ?? throw new InvalidOperationException("Instance is unavailable");
            return await registry.AcquireWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion,
                instance.BundleDigest, cancellationToken);
        }

        public async Task<IReadOnlyList<StepTableEntry>> GetStepTableAsync(string instanceId, CancellationToken cancellationToken)
        {
            using var definitionLease = await DebugDefinitionAsync(instanceId, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            return InspectionPrivacy.Clean(definition.StepTable.ToArray(), await DurableWaitIdsAsync(instanceId, cancellationToken));
        }

        public async Task<string?> GetSourceAsync(string instanceId, string path, CancellationToken cancellationToken)
        {
            using var definitionLease = await DebugDefinitionAsync(instanceId, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            var table = definition.StepTable;
            var paths = table.Select(e => e.SourceLocation is { } source && source.LastIndexOf(':') is var colon && colon > 0
                ? source[..colon] : null);
            if (!paths.Contains(path, StringComparer.Ordinal)) throw new ArgumentException("Unknown source");
            string? content;
            if (definition.BundleSources is { } bundled)
                content = bundled.GetValueOrDefault(path);
            else if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("Source exceeds the inspection limit");
                content = await File.ReadAllTextAsync(path, cancellationToken);
            }
            else
            {
                content = services.GetService(typeof(IWorkflowDebugSourceProvider)) is IWorkflowDebugSourceProvider sources
                    ? await sources.ReadAsync(definition.Name, definition.Version, path, cancellationToken) : null;
            }
            if (content == null) return null;
            if (System.Text.Encoding.UTF8.GetByteCount(content) > 1024 * 1024) throw new InvalidOperationException("Source exceeds the inspection limit");
            return InspectionPrivacy.Clean(new[] { content }, await DurableWaitIdsAsync(instanceId, cancellationToken))[0];
        }

        public async Task<TraceReplay> OpenReplayAsync(string instanceId, int maxEntries, CancellationToken cancellationToken)
        {
            if (maxEntries is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            using var definitionLease = await DebugDefinitionAsync(instanceId, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            var entries = new List<StoredTraceEntry>();
            while (entries.Count < maxEntries)
            {
                var page = await GetTracePageAsync(instanceId, entries.LastOrDefault()?.Sequence ?? 0,
                    Math.Min(1000, maxEntries - entries.Count), cancellationToken);
                if (page.Count == 0) break;
                entries.AddRange(page);
            }
            if (entries.Count == maxEntries && (await GetTracePageAsync(instanceId, entries[^1].Sequence, 1, cancellationToken)).Count != 0)
                throw new InvalidOperationException("Replay history exceeds the requested limit");
            var replay = TraceReplay.Load(definition, entries);
            var inspection = await InspectAsync(instanceId, cancellationToken);
            if (inspection != null && entries.LastOrDefault()?.CommitEndSequence == entries.LastOrDefault()?.Sequence &&
                entries.LastOrDefault()?.Revision == inspection.Revision)
                replay.AttachCheckpoint(inspection);
            return replay;
        }

        public async Task<InstanceInspection?> InspectAsync(string instanceId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance == null || !HasStructuredCheckpoint(instance)) return null;
            using var definitionLease = await registry.AcquireWorkflowDefinitionAsync(instance.WorkflowName,
                instance.WorkflowVersion, instance.BundleDigest, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            var reader = NewPayloadReader();
            var checkpoint = await ReadCheckpointAsync(instance, reader);
            if (checkpoint.StructureHash != scopes.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow structure differs");
            checkpoint.RootData = JsonSerializer.Deserialize(await reader.ReadAsync(instance.WorkflowDataJson),
                WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)!)!;
            foreach (var branch in checkpoint.Continuations) await reader.HydrateAsync(branch.DataChanges);
            var threads = InspectionProjection.Threads(instance, checkpoint, scopes, Now);
            var debug = checkpoint.Debug;
            var result = new InstanceInspection(new(instance.InstanceId, instance.WorkflowName, instance.WorkflowVersion,
                instance.Status, instance.ParentInstanceId, instance.CreatedAt, instance.CompletedAt,
                InspectionRedactionPolicy.Default.Reason(checkpoint.ResultDiagnostic))
                    { Incarnation = instance.Incarnation },
                instance.Revision, checkpoint.StructureHash,
                new(debug.PauseGeneration, debug.AppliedOrder, debug.Stop, debug.Stepping)
                    { Evaluation = debug.Evaluation, SkipOnce = debug.SkipOnce },
                debug.Breakpoints, threads,
                checkpoint.Continuations.Where(c => c.PendingActivity != null).Select(c =>
                {
                    var p = c.PendingActivity!; var a = p.Attempts.LastOrDefault();
                    return new InspectionAttempt(c.Id, p.Id, p.StepId, a?.Id, a?.Kind, a?.Host,
                        p.StartedAtUtc, InspectionRedactionPolicy.Default.Reason(p.ResolutionDiagnostic));
                }).ToArray(),
                checkpoint.Waits.Select(w => new InspectionWait(WaitReference.Of(instanceId, w.Id), w.ContinuationId,
                    w.StepId, w.TableKind, w.Key, w.DeadlineUtc, w.ChildWorkflowId)).ToArray(),
                checkpoint.Continuations.Select(c => new InspectionData(c.Id,
                    InspectionRedactionPolicy.Default.Value(checkpoint.DataOf(c), services))).ToArray());
            return InspectionPrivacy.Clean(result, WaitIds(instance, checkpoint));
        }

        public async Task<IReadOnlyList<StoredTraceEntry>> GetTracePageAsync(string instanceId, long afterSequence, int limit,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
            if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await _store.GetTraceAsync(instanceId, afterSequence, limit);
            return InspectionPrivacy.Clean(entries, await DurableWaitIdsAsync(instanceId, cancellationToken));
        }

        private async Task<string[]> DurableWaitIdsAsync(string instanceId, CancellationToken cancellationToken)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in Enum.GetValues<WaitKind>())
            {
                string? after = null;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await _store.GetOpenWaitsAsync(new(InstanceId: instanceId, Kind: kind,
                        AfterWaitRef: after, Limit: 1000) { IncludeClosed = true });
                    foreach (var wait in page) ids.Add(wait.WaitId);
                    if (page.Count < 1000) break;
                    after = WaitReference.Of(instanceId, page[^1].WaitId);
                }
            }
            if ((await _store.GetWorkflowInstanceSummaryAsync(instanceId))?.ParentWaitId is { } parent) ids.Add(parent);
            return ids.ToArray();
        }

        private static string[] WaitIds(WorkflowInstance instance, ExecutionCheckpoint checkpoint) =>
            checkpoint.Waits.Select(w => w.Id).Concat(checkpoint.BoundReplies.Select(r => r.WaitId))
                .Concat(checkpoint.Continuations.Where(c => c.AcceptedWait != null).Select(c => c.AcceptedWait!.WaitId))
                .Append(instance.ParentWaitId).Append(checkpoint.ExecutionTimerId)
                .Where(id => !string.IsNullOrEmpty(id)).Cast<string>().Distinct().ToArray();
    }
}
