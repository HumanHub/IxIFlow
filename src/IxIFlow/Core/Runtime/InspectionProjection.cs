namespace IxIFlow.Core.Runtime;

internal static class InspectionProjection
{
    // Positions are derived from these fields and the immutable scope catalog. Most trace calls
    // do not move a position, so compare against the live fields before allocating a projection.
    public static bool Unchanged(IReadOnlyDictionary<string, InspectionThread> positions,
        ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes)
    {
        var count = 0;
        foreach (var continuation in checkpoint.Continuations)
        {
            if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled) continue;
            count++;
            if (!positions.TryGetValue(continuation.Id, out var before) ||
                before.ParentJoinId != continuation.ParentJoinId || before.Sequence != continuation.Sequence ||
                before.ForkActivationId != continuation.ForkActivationId || before.Status != continuation.Status.ToString() ||
                before.Frames.Count != continuation.Stack.Count) return false;
            var parentId = continuation.ParentJoinId == null ? null :
                checkpoint.Joins.SingleOrDefault(j => j.Id == continuation.ParentJoinId)?.ParentContinuationId;
            if (before.ParentContinuationId != parentId) return false;
            for (var index = 0; index < continuation.Stack.Count; index++)
            {
                var frame = continuation.Stack[index];
                var parent = index == 0 ? null : continuation.Stack[index - 1];
                var owner = parent == null ? null : scopes.Steps(parent.ScopeId).ElementAtOrDefault(parent.NextStepIndex);
                var iteration = owner?.StepType == WorkflowStepType.Loop ? parent!.LoopIterationCount : frame.LoopIterationCount;
                var saved = before.Frames[index];
                var graph = GraphFrameInfo.Of(scopes, continuation, index);
                var kind = frame.Kind is FrameKind.Node or FrameKind.Edge
                    ? frame.Kind.ToString() : owner?.StepType.ToString() ?? frame.ScopeId;
                if (saved.ActivationId != frame.ActivationId || saved.ScopeId != frame.ScopeId ||
                    saved.ParentActivationId != parent?.ActivationId || saved.NextStepIndex != frame.NextStepIndex ||
                    saved.LoopIteration != iteration || saved.ForEachItemIndex != frame.ForEachIndex ||
                    saved.Kind != kind || saved.Phase != graph?.Phase ||
                    saved.Visit != graph?.Visit || saved.Via != graph?.Via) return false;
            }
        }
        return count == positions.Count;
    }

    // Use the same naming, ordering and frame semantics as the live snapshot.
    public static IReadOnlyList<InspectionThread> Threads(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes, DateTime now)
    {
        // Replay records these typed fields. Building the public snapshot first allocated a
        // string dictionary for every frame, then parsed those same integers back on every pick.
        var threads = new List<InspectionThread>();
        foreach (var continuation in checkpoint.Continuations)
        {
            if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled) continue;
            var position = continuation.Stack[^1];
            var next = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);
            var frames = new List<InspectionFrame>();
            for (var index = 0; index < continuation.Stack.Count; index++)
            {
                var frame = continuation.Stack[index];
                var parent = index == 0 ? null : continuation.Stack[index - 1];
                var owner = parent == null ? null : scopes.Steps(parent.ScopeId).ElementAtOrDefault(parent.NextStepIndex);
                var iteration = owner?.StepType == WorkflowStepType.Loop ? parent!.LoopIterationCount : frame.LoopIterationCount;
                var name = owner?.Name ?? frame.ScopeId;
                if (owner?.StepType == WorkflowStepType.Loop) name += $" (iteration {iteration})";
                if (frame.ForEachIndex is { } itemIndex) name += $" (item {itemIndex})";
                var graph = GraphFrameInfo.Of(scopes, continuation, index);
                var kind = frame.Kind is FrameKind.Node or FrameKind.Edge
                    ? frame.Kind.ToString() : owner?.StepType.ToString() ?? frame.ScopeId;
                frames.Add(new(frame.ActivationId, graph?.Name ?? name, kind,
                    frame.ScopeId, parent?.ActivationId, frame.NextStepIndex, iteration, frame.ForEachIndex)
                { Phase = graph?.Phase, Visit = graph?.Visit, Via = graph?.Via });
            }
            threads.Add(new(continuation.Id,
                continuation.ParentJoinId == null ? "Root" : $"Branch {continuation.Sequence} ({continuation.ForkActivationId})",
                checkpoint.Joins.SingleOrDefault(j => j.Id == continuation.ParentJoinId)?.ParentContinuationId,
                continuation.ParentJoinId, continuation.Sequence, continuation.ForkActivationId,
                next == null ? "" : scopes.Id(next), next?.Id ?? "", position.NextStepIndex,
                continuation.Status.ToString(), position.ActivationId, frames.ToArray()));
        }
        return threads.ToArray();
    }
}
