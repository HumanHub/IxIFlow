using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Configures activities within saga error handling blocks
/// </summary>
public interface ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity>
    where TActivity : class, IAsyncActivity
{
    /// <summary>
    /// Configures an input property for the error handling activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ISagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty> Input<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the error handling activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ISagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty> Output<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures error handling for a specific exception type in the error handling activity
    /// </summary>
    /// <typeparam name="TException">Type of exception to handle</typeparam>
    /// <param name="handler">Configuration for the error handler</param>
    ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler) where TException : Exception;

    /// <summary>
    /// Configures general error handling for the error handling activity
    /// </summary>
    /// <param name="handler">Configuration for the error handler</param>
    ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity> OnError(Action<ISagaStepErrorHandler> handler);
}

/// <summary>
/// Configures activities within saga error handling blocks with access to previous step data
/// </summary>
public interface ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    /// <summary>
    /// Configures an input property for the error handling activity with access to previous step data
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ISagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the error handling activity
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ISagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Output<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures error handling for a specific exception type in the error handling activity
    /// </summary>
    /// <typeparam name="TException">Type of exception to handle</typeparam>
    /// <param name="handler">Configuration for the error handler</param>
    ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler) where TException : Exception;

    /// <summary>
    /// Configures general error handling for the error handling activity
    /// </summary>
    /// <param name="handler">Configuration for the error handler</param>
    ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError(Action<ISagaStepErrorHandler> handler);
}

/// <summary>
/// Builder for configuring input properties of error handling activities
/// </summary>
public interface ISagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty> : ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity>
    where TActivity : class, IAsyncActivity
{
    /// <summary>
    /// Specifies the source of the input value from exception context
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity> From(
        Expression<Func<CatchContext<TWorkflowData, Exception>, TProperty>> source);
}

/// <summary>
/// Builder for configuring input properties of error handling activities with access to previous step data
/// </summary>
public interface ISagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> : ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    /// <summary>
    /// Specifies the source of the input value from exception context with access to previous step data
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> From(
        Expression<Func<CatchContext<TWorkflowData, Exception, TPreviousStepData>, TProperty>> source);
}

/// <summary>
/// Builder for configuring output properties of error handling activities
/// </summary>
public interface ISagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty> : ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity>
    where TActivity : class, IAsyncActivity
{
    /// <summary>
    /// Specifies where to store the output value from the error handling activity
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}

/// <summary>
/// Builder for configuring output properties of error handling activities with access to previous step data
/// </summary>
public interface ISagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> : ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    /// <summary>
    /// Specifies where to store the output value from the error handling activity
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}
