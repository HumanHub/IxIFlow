using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Configures activities within catch blocks with access to exception context
/// </summary>
public interface ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData> 
    where TActivity : class, IAsyncActivity 
    where TException : Exception 
    where TPreviousStepData : class
{
    /// <summary>
    /// Configures an input property for the catch activity with access to exception context
    /// </summary>
    /// <typeparam name="TProperty">Type of the input property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData> Input<TProperty>(
        Expression<Func<TActivity, TProperty>> property);

    /// <summary>
    /// Configures an output property for the catch activity with access to exception context
    /// </summary>
    /// <typeparam name="TProperty">Type of the output property</typeparam>
    /// <param name="property">Expression identifying the property</param>
    ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData> Output<TProperty>(
        Expression<Func<TActivity, TProperty>> property);
}

/// <summary>
/// Builder for configuring inputs and outputs of catch activities
/// </summary>
public interface ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>
    : ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TException : Exception
    where TPreviousStepData : class
{

    /// <summary>
    /// Specifies the source of the input value with access to exception context
    /// </summary>
    /// <param name="source">Expression defining where to get the input value</param>
    ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData> From(
        Expression<Func<CatchContext<TWorkflowData, TException, TPreviousStepData>, TProperty>> source);
}

/// <summary>
/// Builder for configuring output properties of catch activities with exception context
/// </summary>
public interface ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>
    : ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TException : Exception
    where TPreviousStepData : class
{
    /// <summary>
    /// Specifies where to store the output value from the catch activity
    /// </summary>
    /// <param name="destination">Expression defining where to store the output value</param>
    ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination);
}
