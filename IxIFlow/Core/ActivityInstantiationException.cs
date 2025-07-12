namespace IxIFlow.Core;

/// <summary>
///     Exception thrown when activity instantiation fails
/// </summary>
public class ActivityInstantiationException : Exception
{
    public ActivityInstantiationException(string message) : base(message)
    {
    }

    public ActivityInstantiationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}