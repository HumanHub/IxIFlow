using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Configures compensation activities for saga steps
/// </summary>
public interface ICompensationActivityBuilder<TWorkflowData, TCompensationActivity>
    where TCompensationActivity : IAsyncActivity
{
    /// <summary>
    /// Configures an input property for the compensation activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty> Input<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the compensation activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> Output<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property);
}

/// <summary>
/// Configures compensation activities for saga steps with access to current step data
/// </summary>
public interface ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData>
    where TCompensationActivity : IAsyncActivity 
    where TCurrentStepData : class
{
    /// <summary>
    /// Configures an input property for the compensation activity with access to current step data
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData>
        Input<TProperty>(Expression<Func<TCompensationActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the compensation activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> Output<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property);
}

/// <summary>
/// Configures compensation activities with access to previous step data and activity
/// </summary>
public interface ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
    TPreviousChainActivity>
    where TCompensationActivity : IAsyncActivity
    where TPreviousStepData : class
    where TPreviousChainActivity : class, IAsyncActivity
{
    /// <summary>
    /// Configures an input property for the compensation activity with access to previous step data and activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TPreviousStepData,
        TPreviousChainActivity> Input<TProperty>(Expression<Func<TCompensationActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the compensation activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> Output<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property);
}
