using System.Text.Json;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    internal sealed partial class InstanceOperations
    {
        private async Task<WorkflowDefinition> DebugDefinitionAsync(string instanceId, CancellationToken cancellationToken)
        {
            var inspection = await InspectAsync(instanceId, cancellationToken)
                ?? throw new InvalidOperationException("Instance is unavailable");
            return await registry.GetWorkflowDefinitionAsync(inspection.Summary.WorkflowName, inspection.Summary.WorkflowVersion)
                ?? throw new InvalidOperationException("Definition is unavailable");
        }

        public async Task<IReadOnlyList<StepTableEntry>> GetStepTableAsync(string instanceId, CancellationToken cancellationToken)
        {
            var definition = await DebugDefinitionAsync(instanceId, cancellationToken);
            var instance = await _store.GetWorkflowInstanceAsync(instanceId) ?? throw new InvalidOperationException("Instance is unavailable");
            return InspectionPrivacy.Clean(definition.StepTable.ToArray(), WaitIds(instance,
                await ReadCheckpointAsync(instance)));
        }

        public async Task<string?> GetSourceAsync(string instanceId, string path, CancellationToken cancellationToken)
        {
            var table = await GetStepTableAsync(instanceId, cancellationToken);
            var paths = table.Select(e => e.SourceLocation is { } source && source.LastIndexOf(':') is var colon && colon > 0
                ? source[..colon] : null);
            if (!paths.Contains(path, StringComparer.Ordinal)) throw new ArgumentException("Unknown source");
            string? content;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidOperationException("Source exceeds the inspection limit");
                content = await File.ReadAllTextAsync(path, cancellationToken);
            }
            else
            {
                var definition = await DebugDefinitionAsync(instanceId, cancellationToken);
                content = services.GetService(typeof(IWorkflowDebugSourceProvider)) is IWorkflowDebugSourceProvider sources
                    ? await sources.ReadAsync(definition.Name, definition.Version, path, cancellationToken) : null;
            }
            if (content == null) return null;
            if (System.Text.Encoding.UTF8.GetByteCount(content) > 1024 * 1024) throw new InvalidOperationException("Source exceeds the inspection limit");
            var instance = await _store.GetWorkflowInstanceAsync(instanceId) ?? throw new InvalidOperationException("Instance is unavailable");
            return InspectionPrivacy.Clean(new[] { content }, WaitIds(instance,
                await ReadCheckpointAsync(instance)))[0];
        }

        public async Task<TraceReplay> OpenReplayAsync(string instanceId, int maxEntries, CancellationToken cancellationToken)
        {
            if (maxEntries is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            var definition = await DebugDefinitionAsync(instanceId, cancellationToken);
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

        public async Task<(WorkflowDefinition, object)> PrepareDebugLaunchAsync(string workflow, int version, JsonElement input)
        {
            if (!(hostSettings ?? HostSettings.Default).Debugging) throw new InvalidOperationException("Debugging is disabled");
            var definition = await registry.GetWorkflowDefinitionAsync(workflow, version)
                ?? throw new InvalidOperationException("Definition is unavailable");
            var data = input.Deserialize(definition.WorkflowDataType!) ?? throw new ArgumentException("Input is required");
            return (definition, data);
        }

        public async Task<InstanceInspection?> InspectAsync(string instanceId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            cancellationToken.ThrowIfCancellationRequested();
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance == null || !HasStructuredCheckpoint(instance)) return null;
            var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Workflow definition is unavailable");
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
                ? WaitIds(instance, await ReadCheckpointAsync(instance)) : [];
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
