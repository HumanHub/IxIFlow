namespace IxIFlow.Core;

/// <summary>
/// Interface for error handlers in workflow and saga error handling
/// </summary>
public interface IErrorHandler
{
    /// <summary>
    /// Handles an error and returns the result of error handling
    /// </summary>
    /// <param name="exception">The exception that occurred</param>
    /// <param name="context">The execution context</param>
    /// <returns>The result of error handling</returns>
    Task<ErrorHandlingResult> HandleErrorAsync(Exception exception, object context);
}
