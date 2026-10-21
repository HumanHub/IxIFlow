namespace IxIFlow.Core.Runtime;

/// <summary>One eligibility rule for dispatch, inbox, timers, joins and item admission.</summary>
internal static class SteppingPolicy
{
    public static bool Allows(ExecutionCheckpoint checkpoint, string continuationId) =>
        checkpoint.CancellationRequested || checkpoint.Debug.Stepping is not { } step ||
        step.EligibleContinuations.Contains(continuationId);

    public static bool AllowsJoin(ExecutionCheckpoint checkpoint, ParallelJoinState join) =>
        Allows(checkpoint, join.ParentContinuationId) &&
        checkpoint.BranchesOf(join).All(c => Allows(checkpoint, c.Id));

    public static DebugStepPosition Position(ScopePosition frame) => new(frame.ActivationId,
        frame.NextStepIndex, frame.LoopIterationCount, frame.ForEach?.Next ?? 0, frame.ForEach?.Joined ?? false);

    public static DebugStepping Start(ExecutionCheckpoint checkpoint, DebugCommand command)
    {
        var branch = checkpoint.Continuations.SingleOrDefault(c => c.Id == command.ContinuationId);
        if (branch == null || branch.Stack.Count == 0 ||
            branch.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "The selected continuation is not live");
        // Out leaves the enclosing node activation, including any nested body or timeout frame.
        var origin = command.StepKind == DebugStepKind.Out
            ? branch.Stack.LastOrDefault(frame => frame.Kind == FrameKind.Node) ?? branch.Stack[^1]
            : branch.Stack[^1];
        return new(branch.Id, command.StepKind!.Value, Position(origin),
            branch.Stack.Select(f => f.ActivationId).ToArray(), [branch.Id], command.Id, command.Actor)
        {
            OriginGraph = branch.Stack.LastOrDefault(frame => frame.Graph != null) is { } owner
                ? owner.ScopeId + "/" + owner.NextStepIndex : null
        };
    }

    public static void Entered(ExecutionCheckpoint checkpoint, ContinuationState continuation,
        string graphId, ScopePosition frame)
    {
        if (checkpoint.Debug.Stepping is { Kind: DebugStepKind.Over } step &&
            step.ContinuationId == continuation.Id && step.OriginGraph == graphId)
            checkpoint.Debug = checkpoint.Debug with
            {
                Stepping = step with { OriginScopes = step.OriginScopes.Append(frame.ActivationId).ToArray() }
            };
    }

    public static void Forked(ExecutionCheckpoint checkpoint, ContinuationState parent, ContinuationState child)
    {
        child.ForkActivationId = parent.Stack[^1].ActivationId;
        if (checkpoint.Debug.Stepping is { } step && Allows(checkpoint, parent.Id))
            checkpoint.Debug = checkpoint.Debug with { Stepping = step with
                { EligibleContinuations = step.EligibleContinuations.Append(child.Id).ToArray() } };
    }

    public static void Dispatched(ExecutionCheckpoint checkpoint)
    {
        if (checkpoint.Debug.Stepping is { Started: false } step)
            checkpoint.Debug = checkpoint.Debug with { Stepping = step with { Started = true } };
    }

    public static bool StopAtPick(ExecutionCheckpoint checkpoint, ContinuationState branch, string stepId, DateTime now)
    {
        if (checkpoint.CancellationRequested || checkpoint.Debug.Stepping is not { } step ||
            branch.PendingActivity != null) return false;
        var position = branch.Stack[^1];
        var selected = branch.Id == step.ContinuationId;
        var sameOccurrence = selected && Position(position) == step.Origin;
        var stop = step.Kind switch
        {
            DebugStepKind.Into => !sameOccurrence,
            DebugStepKind.Over => selected && (position.ActivationId != step.Origin.ActivationId ||
                position.NextStepIndex != step.Origin.StepIndex) && step.OriginScopes.Contains(position.ActivationId),
            DebugStepKind.Out => selected && branch.Stack.All(f => f.ActivationId != step.Origin.ActivationId),
            _ => false
        };
        if (stop) Pause(checkpoint, branch, stepId, "Step", now);
        return stop;
    }

    public static void Pause(ExecutionCheckpoint checkpoint, ContinuationState branch, string stepId, string reason, DateTime now)
    {
        var position = branch.Stack.LastOrDefault();
        checkpoint.Debug = checkpoint.Debug with { Stop = new(branch.Id, stepId, "",
            position?.ActivationId ?? "", position?.LoopIterationCount ?? 0, 0, now) { Reason = reason } };
    }
}
