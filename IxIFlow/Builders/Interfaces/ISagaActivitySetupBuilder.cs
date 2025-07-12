using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Configures saga activities with compensation and error handling capabilities
/// </summary>
public interface ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : class, IAsyncActivity 
    where TPreviousStepData : class
{
    /// <summary>
    /// Configures an input property for the saga activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ISagaActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the saga activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ISagaActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Output<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures error handling for a specific exception type
    /// </summary>
    /// <typeparam name="TException">Type of exception to handle</typeparam>
    /// <param name="handler">Configuration for the error handler</param>
    ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler) where TException : Exception;

    /// <summary>
    /// Configures general error handling for the activity
    /// </summary>
    /// <param name="handler">Configuration for the error handler</param>
    ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError(Action<ISagaStepErrorHandler> handler);

    /// <summary>
    /// Configures a compensation activity for this step
    /// </summary>
    /// <typeparam name="TCompensationActivity">Type of the compensation activity</typeparam>
    ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity>()
        where TCompensationActivity : IAsyncActivity;

    /// <summary>
    /// Configures a compensation activity for this step with custom setup
    /// </summary>
    /// <typeparam name="TCompensationActivity">Type of the compensation activity</typeparam>
    /// <param name="configure">Configuration for the compensation activity</param>
    ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity>(
        Action<ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity>> configure)
        where TCompensationActivity : IAsyncActivity;

    // For accessing a specific previous step in the saga chain
    /// <summary>
    /// Configures a compensation activity with access to a specific previous activity in the saga chain
    /// </summary>
    /// <typeparam name="TCompensationActivity">Type of the compensation activity</typeparam>
    /// <typeparam name="TPreviousChainActivity">Type of the previous activity to access</typeparam>
    /// <param name="configure">Configuration for the compensation activity</param>
    ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity,
        TPreviousChainActivity>(
        Action<ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
            TPreviousChainActivity>> configure)
        where TCompensationActivity : IAsyncActivity
        where TPreviousChainActivity : class, IAsyncActivity;
}

/// <summary>
/// Builder for configuring input properties of saga activities with access to previous step data
/// </summary>
public interface ISagaActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> : ISagaActivitySetupBuilder<
    TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    /// <summary>
    /// Specifies the source of the input value for the saga activity with access to previous step data
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> From(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> source);
}

/// <summary>
/// Builder for configuring output properties of saga activities with access to previous step data
/// </summary>
public interface ISagaActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>
    : ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    /// <summary>
    /// Specifies where to store the output value from the saga activity
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}
