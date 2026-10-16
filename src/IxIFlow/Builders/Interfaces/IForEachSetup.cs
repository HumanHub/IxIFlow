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
    /// target is a settable property reached from the workflow data. The body, and every timeout branch
    /// that leaves it, must end with <c>.Result(...)</c> of exactly this type, and nothing in the body may
    /// write <paramref name="target"/>, a property inside it or one that contains it. Indexes are not
    /// compared, so <c>Groups[0].Results</c> and <c>Groups[1].Value</c> count as overlapping; outputs in
    /// the body must be chains of members and indexes. Nothing is stored when a failure escapes the body.
    /// </summary>
    IForEachSetup<TWorkflowData> Into<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData>, List<ItemResult<TResult>>>> target);
}
