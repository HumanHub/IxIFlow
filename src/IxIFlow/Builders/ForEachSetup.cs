using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>Sets up a <c>ForEach</c> step: its handle's label and where its results go.</summary>
internal sealed class ForEachSetup<TWorkflowData>(WorkflowStep step) : IForEachSetup<TWorkflowData>
    where TWorkflowData : class
{
    public string Label { get; private set; } = "item";

    public IForEachSetup<TWorkflowData> As(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Label = label;
        return this;
    }

    public IForEachSetup<TWorkflowData> Parallel(int? maxConcurrency = null)
    {
        if (maxConcurrency is < 1)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                "A parallel ForEach runs at least one item at a time");
        step.ForEachParallel = true;
        step.ForEachMaxConcurrency = maxConcurrency;
        return this;
    }

    public IForEachSetup<TWorkflowData> WaitAny()
    {
        SetJoin(ParallelJoinMode.WaitAny);
        return this;
    }

    [System.Runtime.CompilerServices.OverloadResolutionPriority(-1)]
    public IForEachSetup<TWorkflowData> WaitConditionally<TResult>(
        Expression<Func<TWorkflowData, IReadOnlyList<ItemResult<TResult>>, bool>> completeWhenExpression)
    {
        ArgumentNullException.ThrowIfNull(completeWhenExpression);
        WaitConditionally(completeWhenExpression.Compile());
        step.ForEachCompletionExpression = completeWhenExpression;
        return this;
    }

    public IForEachSetup<TWorkflowData> WaitConditionally<TResult>(
        Func<TWorkflowData, IReadOnlyList<ItemResult<TResult>>, bool> completeWhen)
    {
        ArgumentNullException.ThrowIfNull(completeWhen);
        SetJoin(ParallelJoinMode.WaitConditionally);
        step.ForEachConditionResultType = typeof(TResult);
        step.ResultType ??= typeof(TResult);
        step.ForEachCompletionCondition = (data, results) => completeWhen((TWorkflowData)data,
            results.Select(result => new ItemResult<TResult>(result.Index, ItemStatus.Completed,
                result.Value is TResult typed ? typed : default)).ToList());
        return this;
    }

    public IForEachSetup<TWorkflowData> Combine<TValue, TCombiner>(Expression<Func<TWorkflowData, TValue>> property)
        where TCombiner : ICombiner<TValue>, new()
    {
        Core.Runtime.BranchData.Choose(step, property, typeof(TWorkflowData), typeof(TValue), typeof(TCombiner));
        return this;
    }

    private void SetJoin(ParallelJoinMode mode)
    {
        if (step.ParallelJoinMode != ParallelJoinMode.WaitAll)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"ForEach step '{step.Name}' already has a join");
        step.ParallelJoinMode = mode;
    }

    public IForEachSetup<TWorkflowData> Into<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData>, List<ItemResult<TResult>>>> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var path = Core.Runtime.MemberPath.Of(target);
        step.ForEachIntoPath = path;
        step.ForEachIntoExpression = target;
        step.ForEachStoredResultType = typeof(TResult);
        step.ResultType ??= typeof(TResult);
        if (target.Body is not MemberExpression { Member: PropertyInfo { SetMethod: not null } property } member ||
            path?.StartsWith(nameof(WorkflowContext<TWorkflowData>.WorkflowData) + ".", StringComparison.Ordinal) != true)
            return this;
        var context = Expression.Parameter(typeof(WorkflowContext<TWorkflowData>), "context");
        var value = Expression.Parameter(typeof(List<ItemResult<TResult>>), "value");
        var owner = ReplaceParameterVisitor.Replace(member.Expression!, target.Parameters[0], context);
        var assign = Expression.Lambda<Action<WorkflowContext<TWorkflowData>, List<ItemResult<TResult>>>>(
            Expression.Assign(Expression.Property(owner, property), value), context, value).Compile();
        step.ForEachInto = (workflowContext, results) => assign(
            (WorkflowContext<TWorkflowData>)workflowContext,
            results.Select((result, index) => new ItemResult<TResult>(index, result.Status,
                result.Value is TResult typed ? typed : default)).ToList());
        return this;
    }
}
