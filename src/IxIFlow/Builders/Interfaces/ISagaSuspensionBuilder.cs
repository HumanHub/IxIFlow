namespace IxIFlow.Builders.Interfaces;

///// <summary>
///// Builder for configuring saga suspension and resumption behavior
///// </summary>
//public interface ISagaSuspensionBuilder<TWorkflowData, TPreviousStepData> 
//    where TPreviousStepData : class
//{
//    /// <summary>
//    /// Configures the saga to retry from beginning after suspension
//    /// </summary>
//    /// <param name="maxRetries">Maximum number of retry attempts (default: 1)</param>
//    void ThenRetrySaga(int maxRetries = 1);
//    /// <summary>
//    /// Configures the saga to retry from beginning using a custom retry policy
//    /// </summary>
//    /// <param name="retryPolicy">The retry policy configuration</param>
//    void ThenRetrySaga(RetryPolicy retryPolicy);
//    /// <summary>
//    /// Configures the saga to retry only the failed step after suspension
//    /// </summary>
//    /// <param name="maxRetries">Maximum number of retry attempts (default: 1)</param>
//    void ThenRetryFailedStep(int maxRetries = 1);
//    /// <summary>
//    /// Configures the saga to continue execution after suspension
//    /// </summary>
//    void ThenContinue();
//    /// <summary>
//    /// Configures custom execution logic after suspension
//    /// </summary>
//    /// <param name="configure">Configuration for the custom execution</param>
//    void ThenExecute(Action<ISagaBuilder<TWorkflowData, TPreviousStepData>> configure);
//}
