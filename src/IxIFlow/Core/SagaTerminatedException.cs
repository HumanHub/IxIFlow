namespace IxIFlow.Core;

/// <summary>
///     Exception thrown when a saga is terminated due to error handling strategy
///     This exception can be caught at the workflow level to handle saga termination
/// </summary>
public class SagaTerminatedException : Exception
{
    public SagaTerminatedException(string reason, Exception originalException)
        : base($"Saga terminated: {reason}. Original error: {originalException.Message}", originalException)
    {
        OriginalException = originalException;
        Reason = reason;
    }

    public SagaTerminatedException(string reason)
        : this(reason, new Exception("Saga terminated"))
    {
    }

    /// <summary>
    ///     The original exception that caused the saga to terminate
    /// </summary>
    public Exception OriginalException { get; }

    /// <summary>
    ///     The reason for saga termination
    /// </summary>
    public string Reason { get; }
}
