namespace IxIFlow.Core;

/// <summary>
///     Exception thrown when a saga is terminated due to error handling strategy
///     This exception can be caught at the workflow level to handle saga termination
/// </summary>
public class SagaTerminatedException : Exception
{
    public SagaTerminatedException(string reason, Exception originalException, SagaCompensationInfo compensationInfo)
        : base($"Saga terminated: {reason}. Original error: {originalException.Message}", originalException)
    {
        OriginalException = originalException;
        Reason = reason;
        CompensationInfo = compensationInfo;
    }

    public SagaTerminatedException(string reason, Exception originalException)
        : this(reason, originalException, new SagaCompensationInfo())
    {
    }

    public SagaTerminatedException(string reason)
        : this(reason, new Exception("Saga terminated"), new SagaCompensationInfo())
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

    /// <summary>
    ///     Information about compensation that was performed
    /// </summary>
    public SagaCompensationInfo CompensationInfo { get; }
}

/// <summary>
///     Information about compensation that was performed during saga termination
/// </summary>
public class SagaCompensationInfo
{
    /// <summary>
    ///     Number of steps that were compensated
    /// </summary>
    public int CompensatedStepsCount { get; set; }

    /// <summary>
    ///     Names of activities that were compensated
    /// </summary>
    public List<string> CompensatedActivities { get; set; } = new();

    /// <summary>
    ///     Whether all successful steps were compensated
    /// </summary>
    public bool AllStepsCompensated { get; set; }

    /// <summary>
    ///     Compensation strategy that was used
    /// </summary>
    public string CompensationStrategy { get; set; } = string.Empty;

    /// <summary>
    ///     Any errors that occurred during compensation
    /// </summary>
    public List<Exception> CompensationErrors { get; set; } = new();
}