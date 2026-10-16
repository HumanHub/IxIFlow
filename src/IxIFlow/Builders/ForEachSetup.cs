using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>Sets up a <c>ForEach</c> step: its handle's label and where its results go (D-34).</summary>
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

    public IForEachSetup<TWorkflowData> Into<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData>, List<ItemResult<TResult>>>> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Body is not MemberExpression { Member: PropertyInfo { SetMethod: not null } property } member)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"A ForEach stores its results in a settable property: {target}");
        var context = Expression.Parameter(typeof(WorkflowContext<TWorkflowData>), "context");
        var value = Expression.Parameter(typeof(List<ItemResult<TResult>>), "value");
        var owner = new ParameterSwap(target.Parameters[0], context).Visit(member.Expression)!;
        var assign = Expression.Lambda<Action<WorkflowContext<TWorkflowData>, List<ItemResult<TResult>>>>(
            Expression.Assign(Expression.Property(owner, property), value), context, value).Compile();
        step.ResultType = typeof(TResult);
        step.ForEachIntoPath = Core.Runtime.MemberPath.Of(target);
        step.ForEachInto = (workflowContext, results) => assign(
            (WorkflowContext<TWorkflowData>)workflowContext,
            results.Select((result, index) => new ItemResult<TResult>(index, result.Status,
                result.Value is TResult typed ? typed : default)).ToList());
        return this;
    }

    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
