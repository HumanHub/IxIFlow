namespace IxIFlow.Core;

/// <summary>An asynchronous workflow step. Inputs are mapped before execution and outputs are saved after it completes.</summary>
public interface IAsyncActivity
{
    /// <summary>Performs this attempt's work. Recovery can repeat an interrupted invocation; use the context's invocation identity for idempotency.</summary>
    /// <param name="context">Services, identity and recovery information for the current activity attempt.</param>
    /// <param name="cancellationToken">Signals that this attempt must stop.</param>
    /// <returns>A task that completes when outputs are ready to be saved.</returns>
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default);
}