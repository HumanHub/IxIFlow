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
        var path = Core.Runtime.MemberPath.Of(target);
        if (target.Body is not MemberExpression { Member: PropertyInfo { SetMethod: not null } property } member ||
            path?.StartsWith(nameof(WorkflowContext<TWorkflowData>.WorkflowData) + ".", StringComparison.Ordinal) != true)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"A ForEach stores its results in a settable property of the workflow data, not of an item: {target}");
        var context = Expression.Parameter(typeof(WorkflowContext<TWorkflowData>), "context");
        var value = Expression.Parameter(typeof(List<ItemResult<TResult>>), "value");
        var owner = ReplaceParameterVisitor.Replace(member.Expression!, target.Parameters[0], context);
        var assign = Expression.Lambda<Action<WorkflowContext<TWorkflowData>, List<ItemResult<TResult>>>>(
            Expression.Assign(Expression.Property(owner, property), value), context, value).Compile();
        step.ResultType = typeof(TResult);
        step.ForEachIntoPath = path;
        step.ForEachIntoSignature = Core.Runtime.WorkflowExpressionSignature.Of(target);
        step.ForEachInto = (workflowContext, results) => assign(
            (WorkflowContext<TWorkflowData>)workflowContext,
            results.Select((result, index) => new ItemResult<TResult>(index, result.Status,
                result.Value is TResult typed ? typed : default)).ToList());
        return this;
    }
}
