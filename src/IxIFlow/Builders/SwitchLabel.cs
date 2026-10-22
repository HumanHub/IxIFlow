using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Evaluates a context-free typed label expression once while authoring a definition.</summary>
public static class SwitchLabel
{
    public static TValue Value<TValue>(Expression<Func<TValue>> expression,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var site = new WorkflowStep { Name = "Switch label", SourceLocation = sourcePath + ":" + sourceLine };
        ExpressionRules.CheckLabel(site, expression);
        return expression.Compile()();
    }
}
