namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Handles errors at the saga step level
/// </summary>
public interface ISagaStepErrorHandler
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
    /// Terminates the saga execution on error
    /// </summary>
    void ThenTerminate();
    /// <summary>
    /// Ignores the error and continues execution
    /// </summary>
    void ThenIgnore();
}

/// <summary>
/// Handles specific exception types at the saga step level
/// </summary>
/// <typeparam name="TException">Type of exception to handle</typeparam>
public interface ISagaStepErrorHandler<TException> : ISagaStepErrorHandler
    where TException : Exception
{
}
