using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
///     New interface for continuation decision after compensation choice
/// </summary>
public interface ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> where TPreviousStepData : class
{
    // Choose what to do after compensation decision
    /// <summary>
    /// Continues the saga execution after compensation
    /// </summary>
    /// <param name="ignoreCompensationErrors">Whether to ignore errors during compensation</param>
    void ThenContinue(bool ignoreCompensationErrors = false);
    /// <summary>
    /// Terminates the saga execution after compensation
    /// </summary>
    void ThenTerminate();
    /// <summary>
    /// Retries the saga step after compensation with a maximum attempt count
    /// </summary>
    /// <param name="maxAttempts">Maximum number of retry attempts</param>
    void ThenRetry(int maxAttempts);
    /// <summary>
    /// Retries the saga step after compensation using a custom retry policy
    /// </summary>
    /// <param name="retryPolicy">The retry policy configuration</param>
    void ThenRetry(RetryPolicy retryPolicy);
}
