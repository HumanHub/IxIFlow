using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>Sets up a <c>ForEach</c> step (D-34).</summary>
public interface IForEachSetup<TWorkflowData>
{
    /// <summary>Names the item handle for diagnostics. The name never finds the item.</summary>
    IForEachSetup<TWorkflowData> As(string label);

    /// <summary>
    /// Stores one <see cref="ItemResult{TResult}"/> per item, in item order, when the step completes. The
    /// body must end with <c>.Result(...)</c> of the same type, and must not write <paramref name="target"/>
    /// itself. Nothing is stored when a failure escapes the body.
    /// </summary>
    IForEachSetup<TWorkflowData> Into<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData>, List<ItemResult<TResult>>>> target);
}
