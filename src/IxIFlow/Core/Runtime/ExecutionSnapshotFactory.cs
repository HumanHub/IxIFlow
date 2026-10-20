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
                AuthorStepId = next?.Id ?? "",
                StepIndex = position.NextStepIndex,
                Status = continuation.Status.ToString(),
                FrameId = position.ActivationId
            });
            for (var index = 0; index < continuation.Stack.Count; index++)
            {
                var frame = continuation.Stack[index];
                var parent = index == 0 ? null : continuation.Stack[index - 1];
                var owner = parent == null ? null :
                    scopes.Steps(parent.ScopeId).ElementAtOrDefault(parent.NextStepIndex);
                var iteration = owner?.StepType == WorkflowStepType.Loop ? parent!.LoopIterationCount : frame.LoopIterationCount;
                var name = owner?.Name ?? frame.ScopeId;
                if (owner?.StepType == WorkflowStepType.Loop) name += $" (iteration {iteration})";
                if (frame.ForEachIndex is { } itemIndex) name += $" (item {itemIndex})";
                snapshot.Frames.Add(new ExecutionFrame
                {
                    FrameId = frame.ActivationId,
                    Name = name,
                    LoopIteration = iteration,
                    ForEachItemIndex = frame.ForEachIndex,
                    Kind = owner?.StepType.ToString() ?? frame.ScopeId,
                    StepId = frame.ScopeId,
                    ParentFrameId = parent?.ActivationId,
                    State = new Dictionary<string, string>
                    {
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
}
