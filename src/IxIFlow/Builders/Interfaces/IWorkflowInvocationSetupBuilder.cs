using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
///     Configures inputs and outputs for invoked workflows
/// </summary>
public interface IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>
    where TInvokedData : class
{
    /// <summary>
    ///     Configures an input property for the invoked workflow
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty> Input<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property);

    /// <summary>
    ///     Configures an output property for the invoked workflow
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty> Output<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property);

    /// <summary>
    /// Whether cancelling the invoking branch, or failing the parent, cancels the child and waits for it to end
    /// (the default). With <c>false</c> the child is left running on its own: the branch stops waiting for it and
    /// unwinds at once, and the child's later completion is refused.
    /// </summary>
    IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData> CancelWithParent(bool cancel);
}

/// <summary>
///     Configures inputs and outputs for invoked workflows with access to previous step data
/// </summary>
public interface IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>
    : IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>
    where TInvokedData : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Configures an input property for the invoked workflow with access to previous step data
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    new IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property);

    /// <summary>
    ///     Configures an output property for the invoked workflow
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    new IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty> Output<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property);

    /// <inheritdoc cref="IWorkflowInvocationSetupBuilder{TWorkflowData,TInvokedData}.CancelWithParent"/>
    new IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData> CancelWithParent(bool cancel);
}

/// <summary>
///     Builder for configuring input properties of workflow invocations
/// </summary>
public interface IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty>
    : IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>
    where TInvokedData : class
{
    /// <summary>
    ///     Specifies the source of the input value for the invoked workflow
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData> From(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> source);
}

/// <summary>
///     Builder for configuring input properties of workflow invocations with access to previous step data
/// </summary>
public interface IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty, TPreviousStepData>
    : IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>
    where TInvokedData : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Specifies the source of the input value for the invoked workflow with access to previous step data
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData> From(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> source);
}

/// <summary>
///     Builder for configuring output properties of workflow invocations
/// </summary>
public interface IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty>
    : IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>
    where TInvokedData : class
{
    /// <summary>
    ///     Specifies where to store the output value from the invoked workflow
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}