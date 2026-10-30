using IxIFlow.Core;

namespace IxIFlow.Authoring;

internal static class PublicationMetadata
{
    internal static TimeSpan Timeout(WorkflowPublisherOptions options) => options.PublicationStaleAfter < TimeSpan.FromSeconds(90)
        ? options.PublicationStaleAfter
        : TimeSpan.FromSeconds(90);

    internal static async Task<T> CallAsync<T>(
        Func<CancellationToken, Task<T>> call,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return await call(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WorkflowEngineException(
                WorkflowErrorKind.Infrastructure,
                "The publication catalog operation timed out", error);
        }
    }
}