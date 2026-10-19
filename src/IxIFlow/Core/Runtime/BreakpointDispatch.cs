namespace IxIFlow.Core.Runtime;

internal static class BreakpointDispatch
{
    public static bool Hit(ExecutionCheckpoint checkpoint, ContinuationState branch, string stepId, DateTime now)
    {
        var position = branch.Stack[^1];
        var debug = checkpoint.Debug;
        if (debug.Breakpoints.Count == 0 && debug.SkipOnce.Count == 0) return false;
        if (debug.SkipOnce.FirstOrDefault(s => s.BranchId == branch.Id) is { } skip)
        {
            checkpoint.Debug = debug = debug with { SkipOnce = debug.SkipOnce.Where(s => s.BranchId != branch.Id).ToArray() };
            if (skip.StepId == stepId && skip.ActivationId == position.ActivationId &&
                skip.Iteration == position.LoopIterationCount) return false;
        }
        BreakpointStop? stop = null;
        var breakpoints = debug.Breakpoints.Select(b =>
        {
            if (!b.Enabled || b.StepId != stepId) return b;
            if (b.StructureHash != null && b.StructureHash != checkpoint.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Breakpoint structure differs");
            var counted = b with { HitCount = checked(b.HitCount + 1) };
            if (BreakpointResolver.Matches(b.HitCondition, counted.HitCount))
                stop ??= new(branch.Id, stepId, b.Id, position.ActivationId, position.LoopIterationCount, counted.HitCount, now);
            return counted;
        }).ToArray();
        checkpoint.Debug = debug with { Breakpoints = breakpoints, Stop = stop };
        return stop != null;
    }
}
