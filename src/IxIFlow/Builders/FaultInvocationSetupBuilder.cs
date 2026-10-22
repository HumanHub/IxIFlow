using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Maps parent lexical fault data into an independently scoped child workflow.</summary>
public class FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>(
    IWorkflowInvocationSetupBuilder<TData, TChild, TPrevious> inner, WorkflowStep step)
    where TChild : class where TPrevious : class
{
    protected WorkflowStep Step => step;
    public FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> CancelWithParent(bool cancel)
    { inner.CancelWithParent(cancel); return this; }
    public FaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> Input<TValue>(Expression<Func<TChild, TValue>> property) => new(inner, step, property);
    public FaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> Output<TValue>(Expression<Func<TChild, TValue>> property) => new(inner, step, inner.Output(property));
}
public sealed class FaultInvocationInput<TData, TFault, TChild, TPrevious, TValue>(
    IWorkflowInvocationSetupBuilder<TData, TChild, TPrevious> inner, WorkflowStep step, Expression<Func<TChild, TValue>> property)
    : FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>(inner, step) where TChild : class where TPrevious : class
{
    public FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> From(Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> source)
    {
        var evaluate = source.Compile();
        Step.InputMappings.Add(new PropertyMapping
        {
            TargetProperty = ((MemberExpression)property.Body).Member.Name, Expression = source,
            SourceFunction = context => evaluate((FaultContext<TData, TFault, TPrevious>)context),
            ContextFactory = WorkflowStepDelegates.Fault<TData, TFault, TPrevious>,
            SourceType = typeof(TValue), TargetType = typeof(TValue), Direction = PropertyMappingDirection.Input
        });
        return this;
    }
}
public sealed class FaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue>(
    IWorkflowInvocationSetupBuilder<TData, TChild, TPrevious> inner, WorkflowStep step,
    IWorkflowInvocationSetupOutputBuilder<TData, TChild, TValue> mapping)
    : FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>(inner, step) where TChild : class where TPrevious : class
{
    public FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> To(Expression<Func<WorkflowContext<TData>, TValue>> target)
    { mapping.To(target); return this; }
}
