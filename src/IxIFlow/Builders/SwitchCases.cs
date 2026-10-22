using IxIFlow.Core;
using System.Runtime.CompilerServices;

namespace IxIFlow.Builders;

/// <summary>Ordered typed labels and an optional default, using the enclosing body's capabilities.</summary>
public sealed class SwitchCases<TValue, TBody>
{
    private readonly WorkflowStep _step;
    private readonly Func<List<WorkflowStep>, TBody> _body;
    internal SwitchCases(WorkflowStep step, Func<List<WorkflowStep>, TBody> body) => (_step, _body) = (step, body);

    public SwitchCases<TValue, TBody> Case(TValue value, Action<TBody> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var branch = new WorkflowOutcomeBranch { Value = value, SourceLocation = sourcePath + ":" + sourceLine };
        configure(_body(branch.Steps));
        var fallback = _step.OutcomeBranches.FindIndex(arm => arm.IsDefault);
        _step.OutcomeBranches.Insert(fallback < 0 ? _step.OutcomeBranches.Count : fallback, branch);
        return this;
    }

    public SwitchCases<TValue, TBody> Default(Action<TBody> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var branch = new WorkflowOutcomeBranch { IsDefault = true, SourceLocation = sourcePath + ":" + sourceLine };
        configure(_body(branch.Steps));
        _step.OutcomeBranches.Add(branch);
        return this;
    }
}
