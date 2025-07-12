using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for configuring error handling and compensation strategies in sagas
/// </summary>
public interface ISagaErrorBuilder<TWorkflowData, TPreviousStepData> 
    where TPreviousStepData : class
{
    /// <summary>
    /// The exception that triggered the error handling
    /// </summary>
    Exception Exception { get; set; }

    // MUST choose compensation strategy first - new API design
    /// <summary>
    /// Configures standard compensation for the failed step
    /// </summary>
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> Compensate();

    /// <summary>
    /// Configures no compensation for the failed step
    /// </summary>
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateNone();

    // Advanced compensation strategies
    /// <summary>
    /// Configures compensation up to a specific activity in the saga chain
    /// </summary>
    /// <typeparam name="TActivity">Type of activity to compensate up to</typeparam>
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateUpTo<TActivity>()
        where TActivity : IAsyncActivity;

    /// <summary>
    /// Configures nested error handling for a specific exception type
    /// </summary>
    /// <typeparam name="TException">Type of exception to handle</typeparam>
    /// <param name="configure">Configuration for the nested error handler</param>
    ISagaErrorBuilder<TWorkflowData, TPreviousStepData> OnError<TException>(
        Action<ISagaErrorBuilder<TWorkflowData, TPreviousStepData>> configure) where TException : Exception;

    /// <summary>
    /// Configures general nested error handling
    /// </summary>
    /// <param name="configure">Configuration for the nested error handler</param>
    ISagaErrorBuilder<TWorkflowData, TPreviousStepData> OnError(
        Action<ISagaErrorBuilder<TWorkflowData, TPreviousStepData>> configure);

    /// <summary>
    /// Adds a step to the error handling flow without additional configuration
    /// </summary>
    /// <typeparam name="TActivity">Type of activity to execute</typeparam>
    ISagaErrorBuilder<TWorkflowData, TPreviousStepData> Step<TActivity>() where TActivity : class, IAsyncActivity;

    /// <summary>
    /// Adds a configured step to the error handling flow with access to previous step data
    /// </summary>
    /// <typeparam name="TActivity">Type of activity to execute</typeparam>
    /// <param name="configure">Configuration for the error handling activity with previous step access</param>
    ISagaErrorBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>> configure) where TActivity : class, IAsyncActivity;
}
