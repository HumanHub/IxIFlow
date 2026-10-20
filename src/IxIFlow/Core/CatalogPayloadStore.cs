using System.Data.Common;

namespace IxIFlow.Core;

internal abstract class CatalogPayloadStore(PayloadStoreSettings settings) : IPayloadStore
{
    protected PayloadStoreSettings Settings { get; } = settings;
    internal abstract ValueTask<T> Execute<T>(Func<PayloadCatalog, CancellationToken, ValueTask<T>> action, CancellationToken ct);
    public abstract IPayloadTransaction Enlist(DbTransaction transaction);

    public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> utf8Json,
        IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Settings.ValidateLength(utf8Json.Length);
        var body = utf8Json.ToArray();
        var closure = dependencies?.ToArray();
        return Execute((catalog, token) => catalog.PutAsync(body, closure, token), ct);
    }
    public ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default) =>
        Execute((catalog, token) => catalog.Get(reference, token), ct);
    public ValueTask<bool> CollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default) =>
        Execute((catalog, token) => catalog.Collect(reference, grace, token), ct);
    public virtual ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default) =>
        Execute((catalog, token) => catalog.Candidates(limit, grace, token), ct);
    public async ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default) =>
        _ = await Execute(async (catalog, token) => { await action(catalog, token); return true; }, ct);
}
