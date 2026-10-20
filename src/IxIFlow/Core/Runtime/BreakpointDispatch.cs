namespace IxIFlow.Core.Runtime;

internal static class BreakpointDispatch
{
    public static async Task<bool> HitAsync(ExecutionCheckpoint checkpoint, ContinuationState branch,
        WorkflowDefinition definition, string stepId, DateTime now, IServiceProvider services, Func<Task> persist)
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
        var evaluation = debug.Evaluation;
        if (evaluation != null && (evaluation.BranchId != branch.Id || evaluation.StepId != stepId ||
            evaluation.ActivationId != position.ActivationId || evaluation.Iteration != position.LoopIterationCount))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Pending breakpoint occurrence differs");
        var breakpoints = evaluation != null ? debug.Breakpoints : debug.Breakpoints.Select(b =>
        {
            if (!b.Enabled || b.StepId != stepId) return b;
            if (b.StructureHash != null && b.StructureHash != checkpoint.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Breakpoint structure differs");
            return b with { HitCount = checked(b.HitCount + 1) };
        }).ToArray();
        var candidates = breakpoints.Where(b => b.Enabled && b.StepId == stepId &&
            BreakpointResolver.Matches(b.HitCondition, b.HitCount)).ToArray();
        if (evaluation == null && candidates.Any(b => b.Condition != null))
        {
            evaluation = new(branch.Id, stepId, position.ActivationId, position.LoopIterationCount, now);
            checkpoint.Debug = debug = debug with { Breakpoints = breakpoints, Evaluation = evaluation };
            // No condition is invoked until its count and time have been acknowledged together.
            await persist();
        }
        var hitAt = evaluation?.HitAtUtc ?? now;
        BreakpointStop? stop = null;
        Exception? failure = null;
        foreach (var breakpoint in candidates)
        {
            string? reason = null;
            IReadOnlyList<BreakpointConditionDiagnostic> diagnostics = [];
            Exception? evaluationError = null;
            if (breakpoint.Condition != null)
            {
                try
                {
                    var compiler = services.GetService(typeof(IBreakpointConditionCompiler)) as IBreakpointConditionCompiler;
                    if (compiler == null) reason = "ConditionCompilerUnavailable";
                    else
                    {
                        var compilation = compiler.Compile(BreakpointConditionInputs.Request(definition, stepId, breakpoint.Condition));
                        if (compilation.Evaluate == null)
                        {
                            reason = "ConditionCompilationFailed";
                            diagnostics = compilation.Diagnostics;
                        }
                        else
                        {
                            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
                            var itemScope = BreakpointConditionInputs.ItemScope(scopes, stepId);
                            var item = itemScope == null ? ((int Index, object? Value)?)null :
                                ForEachTransitions.ItemsOf(scopes, checkpoint, branch).Of(itemScope);
                            if (!compilation.Evaluate(checkpoint.DataOf(branch), branch.GetPrevious(services),
                                item?.Value, item?.Index, new DateTimeOffset(hitAt, TimeSpan.Zero))) continue;
                        }
                    }
                }
                catch (Exception error)
                {
                    evaluationError = error;
                    reason = "ConditionEvaluationFailed";
                    // Exception messages can contain application values; persist only the exception type.
                    diagnostics = [new("BPC002", $"Condition failed: {error.GetType().FullName}", 0,
                        breakpoint.Condition.Length, 1, 1)];
                }
            }
            if (stop == null || reason != null && stop.Reason == null)
            {
                stop = new(branch.Id, stepId, breakpoint.Id, position.ActivationId, position.LoopIterationCount,
                    breakpoint.HitCount, hitAt) { Reason = reason, Diagnostics = diagnostics };
                failure = evaluationError;
            }
        }
        if (stop is { Diagnostics.Count: > 0 })
        {
            var diagnostic = failure != null ? checkpoint.RecordDiagnostic(failure) : checkpoint.RecordDiagnostic(
                "BreakpointConditionCompilation", string.Join(Environment.NewLine, stop.Diagnostics.Select(d => d.Message)));
            stop = stop with { Diagnostics = stop.Diagnostics.Select(d => d with { Message = diagnostic.ToString() }).ToArray() };
            if (failure != null)
                ((InspectionRedactionPolicy?)services.GetService(typeof(InspectionRedactionPolicy)) ?? InspectionRedactionPolicy.Default)
                    .LogSensitive(services.GetService(typeof(Microsoft.Extensions.Logging.ILogger<SensitiveDiagnostics>))
                        as Microsoft.Extensions.Logging.ILogger<SensitiveDiagnostics>, failure);
        }
        checkpoint.Debug = debug with { Breakpoints = breakpoints, Stop = stop, Evaluation = null };
        return stop != null;
    }
}
