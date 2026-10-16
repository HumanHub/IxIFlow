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
    /// compared, so <c>Groups[0].Results</c> and <c>Groups[1].Value</c> count as overlapping. Nothing is
    /// stored when a failure escapes the body.
    /// </summary>
    IForEachSetup<TWorkflowData> Into<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData>, List<ItemResult<TResult>>>> target);

    /// <summary>
    /// Runs the items as parallel branches, each from a copy of the data at the fork, merged at the join
    /// (D-14, D-34). With <paramref name="maxConcurrency"/>, at most that many run at once, started in
    /// item order; the next starts after one completes while the join is still open. A body write that
    /// more than one item could make, to a property with no combiner, is a build error unless the join is
    /// <see cref="WaitAny"/>.
    /// </summary>
    IForEachSetup<TWorkflowData> Parallel(int? maxConcurrency = null);

    /// <summary>Completes the parallel step when the first item completes; the rest are cancelled or never start.</summary>
    IForEachSetup<TWorkflowData> WaitAny();

    /// <summary>
    /// Completes the parallel step when <paramref name="completeWhen"/> holds, evaluated after each item
    /// completes with the provisional merge of the completed items' data and their results so far, in
    /// item order, or when every item has completed. The body must end with a result of
    /// <typeparamref name="TResult"/>.
    /// </summary>
    IForEachSetup<TWorkflowData> WaitConditionally<TResult>(
        Func<TWorkflowData, IReadOnlyList<ItemResult<TResult>>, bool> completeWhen);
}
