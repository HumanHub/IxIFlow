using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for configuring input properties of compensation activities
/// </summary>
public interface ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty> 
    : ICompensationActivityBuilder<TWorkflowData, TCompensationActivity>
    where TCompensationActivity : IAsyncActivity
{
    /// <summary>
    /// Specifies the source of the input value for the compensation activity
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    ICompensationActivityBuilder<TWorkflowData, TCompensationActivity> From(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> source);
}

/// <summary>
/// Builder for configuring input properties of compensation activities with access to current step data
/// </summary>
public interface ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData>
    : ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData>
    where TCompensationActivity : IAsyncActivity where TCurrentStepData : class
{
    /// <summary>
    /// Specifies the source of the input value for the compensation activity with access to current step data
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData> From(
        Expression<Func<CompensationContext<TWorkflowData, TCurrentStepData>, TProperty>> source);
}

/// <summary>
/// Builder for configuring input properties of compensation activities with access to previous step data and activity
/// </summary>
public interface ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TPreviousStepData, TPreviousChainActivity> :
    ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData, TPreviousChainActivity>
    where TCompensationActivity : IAsyncActivity
    where TPreviousStepData : class
    where TPreviousChainActivity : class, IAsyncActivity
{
    /// <summary>
    /// Specifies the source of the input value from workflow data
    /// </summary>
    /// <param name="source">Expression defining where to get the input value from workflow context</param>
    ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
        TPreviousChainActivity> From(Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> source);

    /// <summary>
    /// Specifies the source of the input value from the immediate previous step data
    /// </summary>
    /// <param name="source">Expression defining where to get the input value from previous step data</param>
    ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
        TPreviousChainActivity> From(Expression<Func<TPreviousStepData, TProperty>> source);

    /// <summary>
    /// Specifies the source of the input value from a specific previous activity in the saga chain
    /// </summary>
    /// <param name="source">Expression defining where to get the input value from previous activity</param>
    ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
        TPreviousChainActivity> From(Expression<Func<TPreviousChainActivity, TProperty>> source);
}
