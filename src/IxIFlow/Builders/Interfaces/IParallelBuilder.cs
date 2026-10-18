using System.Runtime.CompilerServices;
using System.Linq.Expressions;
namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for defining parallel branches in the workflow
/// </summary>
public interface IParallelBuilder<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>
    /// Continue when the first branch completes and cancel the other branches.
    /// </summary>
    IParallelBuilder<TWorkflowData, TPreviousStepData> WaitAny();

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    IParallelBuilder<TWorkflowData, TPreviousStepData> WaitConditionally(
        Expression<Func<TWorkflowData, bool>> completeWhenExpression);

    /// <summary>
    /// Continue when a completed branch makes the condition true, or when all branches complete.
    /// </summary>
    IParallelBuilder<TWorkflowData, TPreviousStepData> WaitConditionally(
        Func<TWorkflowData, bool> completeWhen);

    /// <summary>
    /// Merges the branches' changes to <paramref name="property"/>, and to everything inside it, with
    /// <typeparamref name="TCombiner"/> at this step's join, instead of the combiner the data type declares
    /// for it, if any (spec E-05). The property is reached through members only, has a setter, and is not
    /// inside a value merged whole or one that already has a combiner.
    /// </summary>
    IParallelBuilder<TWorkflowData, TPreviousStepData> Combine<TValue, TCombiner>(
        System.Linq.Expressions.Expression<Func<TWorkflowData, TValue>> property)
        where TCombiner : Core.ICombiner<TValue>, new();

    /// <summary>
    /// Adds a parallel branch to execute
    /// </summary>
    /// <param name="configure">Configuration for the parallel branch</param>
    IParallelBuilder<TWorkflowData, TPreviousStepData> Do(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure);

}
