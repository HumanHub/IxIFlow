namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Handles errors at the step level
/// </summary>
public interface IStepErrorHandler
{
    /// <summary>
    /// Configures retry behavior for the failed step
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts (default: 3)</param>
    void ThenRetry(int maxRetries = 3);
    /// <summary>
    /// Configures retry behavior using a custom retry policy
    /// </summary>
    /// <param name="retryPolicy">The retry policy configuration</param>
    void ThenRetry(RetryPolicy retryPolicy);
    /// <summary>
    /// Propagates the original failure to enclosing handlers
    /// </summary>
    void ThenTerminate();
    /// <summary>
    /// Ignores the error and continues execution
    /// </summary>
    void ThenIgnore();
}

/// <summary>
/// Handles specific exception types at the step level
/// </summary>
/// <typeparam name="TException">Type of exception to handle</typeparam>
public interface IStepErrorHandler<TException> : IStepErrorHandler
    where TException : Exception
{
}
