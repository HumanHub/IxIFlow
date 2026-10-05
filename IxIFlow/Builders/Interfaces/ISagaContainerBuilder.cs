namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Container for saga definitions with error handling capabilities
/// </summary>
public interface ISagaContainerBuilder<TWorkflowData, TPreviousStepData> : IWorkflowBuilder<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>
    /// Configures error handling for a specific exception type in the saga container
    /// </summary>
    /// <typeparam name="TException">Type of exception to handle</typeparam>
    /// <param name="configure">Configuration for the error handler</param>
    ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException>(
        Action<ISagaErrorBuilder<TWorkflowData, Core.EmptyFault, TPreviousStepData>> configure) where TException : Exception;

    ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException, TFault>(
        Action<ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>> configure) where TException : Exception;

    /// <summary>
    /// Configures general error handling for the saga container
    /// </summary>
    /// <param name="configure">Configuration for the error handler</param>
    ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError(
        Action<ISagaErrorBuilder<TWorkflowData, Core.EmptyFault, TPreviousStepData>> configure);
}
