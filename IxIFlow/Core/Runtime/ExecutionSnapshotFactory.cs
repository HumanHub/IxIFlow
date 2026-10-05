namespace IxIFlow.Core.Runtime;

internal static class ExecutionSnapshotFactory
{
    public static WorkflowExecutionSnapshot Create(
        WorkflowInstance instance,
        ExecutionCheckpoint checkpoint,
        WorkflowScopeCatalog scopes)
    {
        var snapshot = new WorkflowExecutionSnapshot
        {
            InstanceId = instance.InstanceId,
            WorkflowName = instance.WorkflowName,
            WorkflowVersion = instance.WorkflowVersion,
            Status = instance.Status
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
                StepId = next?.Id ?? "",
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
                snapshot.Frames.Add(new ExecutionFrame
                {
                    FrameId = frame.ActivationId,
                    Kind = owner?.StepType.ToString() ?? frame.ScopeId,
                    StepId = frame.ScopeId,
                    ParentFrameId = parent?.ActivationId,
                    State = new Dictionary<string, string>
                    {
                        ["NextStepIndex"] = frame.NextStepIndex.ToString()
                    }
                });
            }
        }
        return snapshot;
    }
}
