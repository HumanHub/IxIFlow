namespace IxIFlow.Core.Runtime;

internal static class SwitchRules
{
    internal static void Validate(IReadOnlyList<WorkflowStep> steps)
    {
        foreach (var step in ParallelRules.Flatten(steps))
        {
            if (step.StepType == WorkflowStepType.Switch)
            {
                step.OutcomeBranches = step.OutcomeBranches.OrderBy(arm => arm.IsDefault).ToList();
                Check(step, step.OutcomeBranches);
            }
            if (step.StepType != WorkflowStepType.Flowchart) continue;
            foreach (var node in step.GraphNodes)
            {
                var decision = node.Steps.LastOrDefault();
                if (decision?.OutcomeSelector == null) continue;
                Check(decision, []);
                var labels = new List<object?>();
                var defaults = 0;
                foreach (var edge in node.Edges.SelectMany(body => body))
                {
                    if (edge.IsDefault)
                    {
                        if (++defaults > 1) throw WorkflowBuildError.Invalid(edge, "SWITCH003: duplicate default");
                        continue;
                    }
                    edge.EdgeLabel = Label(edge, decision.SwitchValueType!, edge.EdgeLabel, "cases[" + labels.Count + "].value");
                    if (labels.Any(value => decision.SwitchEquals!(value, edge.EdgeLabel)))
                        throw WorkflowBuildError.Invalid(edge, "SWITCH003: duplicate case label");
                    labels.Add(edge.EdgeLabel);
                }
            }
        }
    }

    private static void Check(WorkflowStep step, IReadOnlyList<WorkflowOutcomeBranch> branches)
    {
        if (step.OutcomeSelector == null || step.OutcomeExpression == null || step.SwitchValueType == null || step.SwitchEquals == null ||
            step.OutcomeExpression.ReturnType != step.SwitchValueType)
            throw WorkflowBuildError.Invalid(step, "SWITCH001: value requires a selector and its declared valueType");
        var labels = new List<object?>();
        var defaults = 0;
        for (var index = 0; index < branches.Count; index++)
        {
            var branch = branches[index];
            var site = branch.SourceLocation == null ? step : new WorkflowStep
                { Id = step.Id, Name = step.Name, SourceLocation = branch.SourceLocation };
            if (branch.IsDefault)
            {
                if (++defaults > 1) throw WorkflowBuildError.Invalid(site, "SWITCH003: duplicate default");
                continue;
            }
            branch.Value = Label(site, step.SwitchValueType, branch.Value, $"cases[{index}].value");
            if (labels.Any(value => step.SwitchEquals(value, branch.Value)))
                throw WorkflowBuildError.Invalid(site, $"SWITCH003: cases[{index}].value duplicates a label");
            labels.Add(branch.Value);
        }
    }

    private static object? Label(WorkflowStep step, Type type, object? value, string member)
    {
        if (value == null ? type.IsValueType && Nullable.GetUnderlyingType(type) == null : !type.IsInstanceOfType(value))
            throw WorkflowBuildError.Invalid(step, $"SWITCH002: {member} is incompatible with valueType '{type}'");
        try
        {
            if (type.IsValueType) SwitchLabelSignature.ValidateType(type);
            return SwitchLabelSignature.Snapshot(value);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Reflection.TargetException)
        {
            throw WorkflowBuildError.Invalid(step, $"SWITCH004: {member}: {error.Message}");
        }
    }
}
