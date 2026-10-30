namespace IxIFlow.Core.Runtime;

internal static class ExecutionSnapshotFactory
{
    public static WorkflowExecutionSnapshot Create(
        WorkflowInstance instance,
        ExecutionCheckpoint checkpoint,
        WorkflowScopeCatalog scopes, DateTime capturedAtUtc)
    {
        var snapshot = new WorkflowExecutionSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            InstanceId = instance.InstanceId,
            WorkflowName = instance.WorkflowName,
            WorkflowVersion = instance.WorkflowVersion,
            BundleDigest = instance.BundleDigest,
            Status = instance.Status,
            Errors = checkpoint.Diagnostics?.ToArray() ?? []
        };

        foreach (var continuation in checkpoint.Continuations)
        {
            if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
                continue;

            var position = continuation.Stack[^1];
            var next = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);

            snapshot.Pointers.Add(new ExecutionPointer
            {
                PointerId = continuation.Id,
                Name = continuation.ParentJoinId == null ? "Root" : $"Branch {continuation.Sequence} ({continuation.ForkActivationId})",
                ParentPointerId = checkpoint.Joins.SingleOrDefault(j => j.Id == continuation.ParentJoinId)?.ParentContinuationId,
                ParentJoinId = continuation.ParentJoinId,
                Sequence = continuation.Sequence,
                ForkActivationId = continuation.ForkActivationId,
                StepId = next == null ? "" : scopes.Id(next),
                AuthorStepId = next == null ? "" : scopes.AuthorId(next),
                StepIndex = position.NextStepIndex,
                Status = continuation.Status.ToString(),
                CancellationUnwind = continuation.CancellationUnwind,
                UnwindCause = continuation.UnwindCause,
                FrameId = position.ActivationId
            });

            for (var index = 0; index < continuation.Stack.Count; index++)
            {
                var frame = continuation.Stack[index];
                var parent = index == 0 ? null : continuation.Stack[index - 1];
                var owner = parent == null ? null : scopes.Steps(parent.ScopeId).ElementAtOrDefault(parent.NextStepIndex);
                var iteration = owner?.StepType == WorkflowStepType.Loop ? parent!.LoopIterationCount : frame.LoopIterationCount;
                var name = owner?.Name ?? frame.ScopeId;

                if (owner?.StepType == WorkflowStepType.Loop)
                    name += $" (iteration {iteration})";

                if (frame.Kind == FrameKind.SwitchCase)
                    name += frame.ScopeId.EndsWith("/default", StringComparison.Ordinal)
                        ? " / default"
                        : " / case " + frame.ScopeId[(frame.ScopeId.LastIndexOf('/') + 1)..];

                if (frame.ForEachIndex is { } itemIndex)
                    name += $" (item {itemIndex})";

                var graph = GraphFrameInfo.Of(scopes, continuation, index, checkpoint);

                snapshot.Frames.Add(new ExecutionFrame
                {
                    FrameId = frame.ActivationId,
                    DataType = FrameDataType(checkpoint, continuation, index, scopes),
                    Name = graph?.Name ?? name,
                    LoopIteration = iteration,
                    ForEachItemIndex = frame.ForEachIndex,
                    Kind = frame.Kind is FrameKind.Scope or FrameKind.Node or FrameKind.Edge or FrameKind.State or FrameKind.StateExit
                        or FrameKind.Transition or FrameKind.Case or FrameKind.Run or FrameKind.Listen or FrameKind.SwitchCase
                        or FrameKind.CancellationHandler
                        ? frame.Kind.ToString()
                        : owner?.StepType.ToString() ?? frame.ScopeId,
                    StepId = frame.ScopeId,
                    ParentFrameId = parent?.ActivationId,
                    State = new Dictionary<string, string>
                    {
                        ["UnwindCause"] = parent?.CancellationState?.Cause?.ToString() ?? continuation.UnwindCause?.ToString() ?? "",
                        ["Phase"] = graph?.Phase ?? parent?.CancellationState?.Phase.ToString() ?? "",
                        ["Visit"] = graph?.Visit.ToString() ?? "",
                        ["ArmOccurrence"] = frame.ArmOccurrence.ToString(),
                        ["Via"] = graph?.Via ?? "",
                        ["GraphId"] = graph?.GraphId ?? "",
                        ["Node"] = graph?.Node ?? "",
                        ["PendingVia"] = graph?.Pending?.Via ?? "",
                        ["PendingTrigger"] = graph?.Pending?.Trigger ?? "",
                        ["PendingTarget"] = graph?.Pending?.Target ?? "",
                        ["PendingCause"] = graph?.Pending?.Cause ?? "",
                        ["RethrowOwnerId"] = scopes.Steps(frame.ScopeId).ElementAtOrDefault(frame.NextStepIndex)?.RethrowOwnerId ?? "",
                        ["NextStepIndex"] = frame.NextStepIndex.ToString(),
                        ["LoopIteration"] = iteration.ToString(),
                        ["ForEachItemIndex"] = frame.ForEachIndex?.ToString() ?? "",
                        ["ContinuationId"] = continuation.Id,
                        ["ParentJoinId"] = continuation.ParentJoinId ?? "",
                        ["Sequence"] = continuation.Sequence.ToString(),
                        ["ForkActivationId"] = continuation.ForkActivationId ?? ""
                    }
                });
            }
        }

        return snapshot;
    }

    internal static string FrameDataType(ExecutionCheckpoint checkpoint, ContinuationState continuation, int index,
        WorkflowScopeCatalog scopes)
    {
        var frame = continuation.Stack.Take(index + 1).LastOrDefault(f => f.Kind == FrameKind.Inline);

        if (frame != null)
            return checkpoint.InlineFrames[frame.ActivationId].DataType;

        while (continuation.ParentJoinId is { } joinId)
        {
            var join = checkpoint.Joins.Single(j => j.Id == joinId);

            if (join.DataFrameId is { } id)
                return checkpoint.InlineFrames[id].DataType;

            continuation = checkpoint.ContinuationById(join.ParentContinuationId)!;
        }

        return scopes.RootDataType == null ? "" : WorkflowTypeIdentity.SavedName(scopes.RootDataType);
    }
}