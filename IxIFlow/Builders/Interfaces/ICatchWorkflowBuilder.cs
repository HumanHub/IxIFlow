using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Configures catch blocks in workflows with access to exception context
/// </summary>
public interface ICatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>
    where TException : Exception
    where TPreviousStepData : class
{
    /// <summary>
    /// Adds a step to handle the caught exception
    /// </summary>
    /// <typeparam name="TActivity">The activity type to execute for exception handling</typeparam>
    /// <param name="configure">Configuration for the exception handling activity</param>
    IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity;
}