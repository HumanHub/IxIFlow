namespace IxIFlow.Core;

/// <summary>
/// Describes the original failure when a saga error handler terminates or exhausts retries.
/// The live exception is not retained; handler activities use their declared fault value.
/// </summary>
public sealed class SagaFailureException(string originalExceptionType, string message) : Exception(message)
{
    public string OriginalExceptionType { get; } = originalExceptionType;
}
