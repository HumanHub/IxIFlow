using System.Data.Common;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    // Keep cancellation outside provider admission/serialization so driver errors from opening,
    // commands, transactions and cleanup all have the same store-level cancellation contract.
    internal Task<T> ExecuteOperationAsync<T>(
        Operation operation,
        Func<Task<T>> call,
        CancellationToken cancellationToken = default) =>
        ExecuteStoreAsync(() => ExecuteProviderOperationAsync(operation, call, cancellationToken), cancellationToken);

    internal static Task ExecuteStoreAsync(Func<Task> call, CancellationToken cancellationToken) =>
        ExecuteStoreAsync(
            async () =>
            {
                await call();

                return true;
            }, cancellationToken);

    internal static async Task<T> ExecuteStoreAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (DbException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The store operation was canceled.", error, cancellationToken);
        }
    }
}