namespace IxIFlow.Core.Runtime;

/// <summary>
/// A store or child-engine failure that interrupts an instance without failing it.
/// The instance keeps its last committed checkpoint and is retried by recovery; the failure
/// never reaches Catch, saga handlers or a failed state (spec A-07).
/// </summary>
internal sealed class WorkflowInfrastructureException(string message, Exception inner)
    : Exception(message, inner)
{
    public static async Task<T> GuardAsync<T>(Func<Task<T>> call, string operation)
    {
        try
        {
            return await call();
        }
        catch (Exception error) when (error is not (WorkflowInfrastructureException or OperationCanceledException))
        {
            throw new WorkflowInfrastructureException($"{operation} failed: {error.Message}", error);
        }
    }
}
