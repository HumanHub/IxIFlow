using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
///     Configures inputs and outputs for inlined workflows
/// </summary>
public interface IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData>
    where TInvokedData : class
{
    /// <summary>
    ///     Configures an input property for the inlined workflow
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IWorkflowInlineSetupInputBuilder<TWorkflowData, TInvokedData, TProperty> Input<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property);

    /// <summary>
    ///     Configures an output property for the inlined workflow
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IWorkflowInlineSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty> Output<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property);
}

/// <summary>
///     Configures inputs and outputs for inlined workflows with access to previous step data
/// </summary>
public interface IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>
    : IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData>
    where TInvokedData : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Configures an input property for the inlined workflow with access to previous step data
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    new IWorkflowInlineSetupInputBuilder<TWorkflowData, TInvokedData, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property);

    /// <summary>
    ///     Configures an output property for the inlined workflow
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    new IWorkflowInlineSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty> Output<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property);
}

/// <summary>
///     Builder for configuring input properties of workflow inline calls
/// </summary>
public interface IWorkflowInlineSetupInputBuilder<TWorkflowData, TInvokedData, TProperty>
    : IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData>
    where TInvokedData : class
{
    /// <summary>
    ///     Specifies the source of the input value for the inlined workflow
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData> From(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> source);
}

/// <summary>
///     Builder for configuring input properties of workflow inline calls with access to previous step data
/// </summary>
public interface IWorkflowInlineSetupInputBuilder<TWorkflowData, TInvokedData, TProperty, TPreviousStepData>
    : IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>
    where TInvokedData : class
    where TPreviousStepData : class
{
    /// <summary>
    ///     Specifies the source of the input value for the inlined workflow with access to previous step data
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData> From(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> source);
}

/// <summary>
///     Builder for configuring output properties of workflow inline calls
/// </summary>
public interface IWorkflowInlineSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty>
    : IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData>
    where TInvokedData : class
{
    /// <summary>
    ///     Specifies where to store the output value from the inlined workflow
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}