namespace IxIFlow.Core;

/// <summary>
/// Exception thrown to request saga suspension with specific suspend configuration
/// This follows the same pattern as SagaRetryRequestedException for consistency
/// </summary>
public class SagaSuspendRequestedException : Exception
{
    /// <summary>
    /// The original exception that triggered the suspend request
    /// </summary>
    public Exception OriginalException { get; }

    /// <summary>
    /// The saga error configuration containing suspend settings
    /// </summary>
    public SagaErrorConfiguration SagaErrorConfig { get; }

    /// <summary>
    /// Creates a new saga suspend request exception
    /// </summary>
    /// <param name="originalException">The original exception that triggered the suspend</param>
    /// <param name="sagaErrorConfig">The saga error configuration with suspend settings</param>
    public SagaSuspendRequestedException(Exception originalException, SagaErrorConfiguration sagaErrorConfig)
        : base($"Saga suspend requested due to {originalException.GetType().Name}: {originalException.Message}")
    {
        OriginalException = originalException ?? throw new ArgumentNullException(nameof(originalException));
        SagaErrorConfig = sagaErrorConfig ?? throw new ArgumentNullException(nameof(sagaErrorConfig));
    }

    /// <summary>
    /// Creates a new saga suspend request exception with custom message
    /// </summary>
    /// <param name="message">Custom message</param>
    /// <param name="originalException">The original exception that triggered the suspend</param>
    /// <param name="sagaErrorConfig">The saga error configuration with suspend settings</param>
    public SagaSuspendRequestedException(string message, Exception originalException, SagaErrorConfiguration sagaErrorConfig)
        : base(message)
    {
        OriginalException = originalException ?? throw new ArgumentNullException(nameof(originalException));
        SagaErrorConfig = sagaErrorConfig ?? throw new ArgumentNullException(nameof(sagaErrorConfig));
    }
}
