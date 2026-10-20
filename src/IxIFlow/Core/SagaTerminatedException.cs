namespace IxIFlow.Core;

/// <summary>
///     Exception thrown when a saga is terminated due to error handling strategy
///     This exception can be caught at the workflow level to handle saga termination
/// </summary>
public class SagaTerminatedException : Exception
{
    public SagaTerminatedException(string reason, Exception originalException)
        : this(reason, originalException, false) { }

    public SagaTerminatedException(string reason, Exception originalException, bool isPolicyTermination)
        : base($"Saga terminated: {reason}. Original error: {originalException.Message}", originalException)
    {
        IsPolicyTermination = isPolicyTermination;
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

    /// <summary>True for an explicit ThenTerminate; failed compensation and exhausted retries are failures.</summary>
    public bool IsPolicyTermination { get; }

    /// <summary>
    ///     The reason for saga termination
    /// </summary>
    public string Reason { get; }
}
