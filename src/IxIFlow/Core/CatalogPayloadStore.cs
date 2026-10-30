using System.Data.Common;

namespace IxIFlow.Core;

internal abstract class CatalogPayloadStore(PayloadStoreSettings settings) : IPayloadStore
{
    protected PayloadStoreSettings Settings { get; } = settings;

    internal abstract ValueTask<T> Execute<T>(Func<PayloadCatalog, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken);

    public abstract IPayloadTransaction Enlist(DbTransaction transaction);

    public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> utf8Json,
        IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Settings.ValidateLength(utf8Json.Length);
        var body = utf8Json.ToArray();
        var closure = dependencies?.ToArray();

        return Execute((catalog, token) => catalog.PutAsync(body, closure, token), cancellationToken);
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken cancellationToken = default) =>
        Execute((catalog, token) => catalog.Get(reference, token), cancellationToken);

    public ValueTask<bool> CollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken cancellationToken = default) =>
        Execute((catalog, token) => catalog.Collect(reference, grace, token), cancellationToken);

    public virtual ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace,
        CancellationToken cancellationToken = default) =>
        Execute((catalog, token) => catalog.Candidates(limit, grace, token), cancellationToken);

    public async ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default) =>
        _ = await Execute(async (catalog, token) =>
        {
            await action(catalog, token);

            return true;
        }, cancellationToken);
}