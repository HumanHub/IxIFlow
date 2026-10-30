using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Maps parent lexical fault data into an independently scoped child workflow.</summary>
internal class FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>(
    IWorkflowInvocationSetupBuilder<TData, TChild, TPrevious> inner,
    WorkflowStep step)
    : IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> where TChild : class where TPrevious : class
{
    protected WorkflowStep Step => step;

    public IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> CancelWithParent(bool cancel)
    {
        inner.CancelWithParent(cancel);

        return this;
    }

    public IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> OnError<TException>(
        Action<IStepErrorHandler<TException>> configure)
        where TException : Exception
    {
        inner.OnError(configure);

        return this;
    }

    public IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> OnError(Action<IStepErrorHandler> configure)
    {
        inner.OnError(configure);

        return this;
    }

    public IFaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> Input<TValue>(Expression<Func<TChild, TValue>> property) =>
        new FaultInvocationInput<TData, TFault, TChild, TPrevious, TValue>(inner, step, property);

    public IFaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> Output<TValue>(Expression<Func<TChild, TValue>> property) =>
        new FaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue>(inner, step, inner.Output(property));
}

internal sealed class FaultInvocationInput<TData, TFault, TChild, TPrevious, TValue>(
    IWorkflowInvocationSetupBuilder<TData, TChild, TPrevious> inner,
    WorkflowStep step,
    Expression<Func<TChild, TValue>> property)
    : FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>(inner, step),
        IFaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> where TChild : class where TPrevious : class
{
    public new IFaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> OnError<TException>(
        Action<IStepErrorHandler<TException>> configure)
        where TException : Exception
    {
        base.OnError(configure);

        return this;
    }

    public new IFaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> OnError(Action<IStepErrorHandler> configure)
    {
        base.OnError(configure);

        return this;
    }

    public IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> From(
        Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> source)
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

internal sealed class FaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue>(
    IWorkflowInvocationSetupBuilder<TData, TChild, TPrevious> inner,
    WorkflowStep step,
    IWorkflowInvocationSetupOutputBuilder<TData, TChild, TValue, TPrevious> mapping)
    : FaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>(inner, step),
        IFaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> where TChild : class where TPrevious : class
{
    public new IFaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> OnError<TException>(
        Action<IStepErrorHandler<TException>> configure)
        where TException : Exception
    {
        base.OnError(configure);

        return this;
    }

    public new IFaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> OnError(Action<IStepErrorHandler> configure)
    {
        base.OnError(configure);

        return this;
    }

    public IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> To(Expression<Func<WorkflowContext<TData>, TValue>> target)
    {
        mapping.To(target);

        return this;
    }
}