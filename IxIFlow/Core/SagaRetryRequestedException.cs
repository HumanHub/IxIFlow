using IxIFlow.Builders;

namespace IxIFlow.Core;

/// <summary>
/// Exception thrown to request saga retry instead of using recursive calls
/// </summary>
public class SagaRetryRequestedException : Exception
{
    public Exception OriginalException { get; }
    public RetryPolicy RetryPolicy { get; }

    public SagaRetryRequestedException(Exception originalException, RetryPolicy? retryPolicy) 
        : base($"Saga retry requested for: {originalException.GetType().Name}: {originalException.Message}")
    {
        OriginalException = originalException ?? throw new ArgumentNullException(nameof(originalException));
        RetryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
    }
}
