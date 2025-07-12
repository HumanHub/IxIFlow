using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Configures activity inputs and outputs for workflow steps without previous step data
/// </summary>
public interface IActivitySetupBuilder<TWorkflowData, TActivity>
    where TActivity : IAsyncActivity
{
    /// <summary>
    /// Configures an input property for the activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty> Input<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty> Output<TProperty>(
        Expression<Func<TActivity, TProperty>> property);
}

/// <summary>
/// Configures activity inputs and outputs for workflow steps with previous step data
/// </summary>
public interface IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : IAsyncActivity
    where TPreviousStepData : class
{
    /// <summary>
    /// Configures an input property for the activity with access to previous step data
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    IActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty> Output<TProperty>(
        Expression<Func<TActivity, TProperty>> property);
}

/// <summary>
/// Builder for configuring activity input properties
/// </summary>
public interface IActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty>
    : IActivitySetupBuilder<TWorkflowData, TActivity>
    where TActivity : IAsyncActivity
{
    /// <summary>
    /// Specifies the source of the input value
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    IActivitySetupBuilder<TWorkflowData, TActivity> From(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> source);
}

/// <summary>
/// Builder for configuring activity input properties with access to previous step data
/// </summary>
public interface IActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>
    : IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : IAsyncActivity
    where TPreviousStepData : class
{
    /// <summary>
    /// Specifies the source of the input value with access to previous step data
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> From(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> source);
}

/// <summary>
/// Builder for configuring activity output properties
/// </summary>
public interface IActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty> : IActivitySetupBuilder<TWorkflowData, TActivity>
    where TActivity : IAsyncActivity
{
    /// <summary>
    /// Specifies where to store the output value
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    IActivitySetupBuilder<TWorkflowData, TActivity> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}
