using System.Data.Common;

namespace IxIFlow.Core;

internal abstract class CatalogPayloadStore(PayloadStoreSettings settings) : IPayloadStore
{
    protected PayloadStoreSettings Settings { get; } = settings;
    internal abstract ValueTask<T> Execute<T>(Func<PayloadCatalog, CancellationToken, ValueTask<T>> action, CancellationToken ct);
    public abstract IPayloadTransaction Enlist(DbTransaction transaction);

    public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Settings.ValidateLength(utf8Json.Length);
        // Own the bytes before the first await; a caller retaining its backing array cannot mutate publication.
        var body = utf8Json.ToArray();
        var reference = PayloadReference.Of(body);
        return Execute(async (catalog, token) =>
        {
            var reservation = await catalog.Reserve(reference, TimeSpan.FromMinutes(5), token);
            if (!await catalog.Publish(reservation, body, null, token))
                throw new InvalidOperationException("Upload reservation expired before publication");
            return reference;
        }, ct);
    }
    public ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default) =>
        Execute((catalog, token) => catalog.Get(reference, token), ct);
    public ValueTask<PayloadReservation> ReserveAsync(PayloadReference reference, TimeSpan lifetime, CancellationToken ct = default) =>
        Execute((catalog, token) => catalog.Reserve(reference, lifetime, token), ct);
    public ValueTask<bool> PublishAsync(PayloadReservation reservation, ReadOnlyMemory<byte> utf8Json,
        IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Settings.ValidateLength(utf8Json.Length);
        var body = utf8Json.ToArray();
        var closure = dependencies?.ToArray();
        return Execute((catalog, token) => catalog.Publish(reservation, body, closure, token), ct);
    }
    public ValueTask<PayloadDeletion?> TryCollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default) =>
        Execute((catalog, token) => catalog.Collect(reference, grace, token), ct);
    public ValueTask<bool> DeleteAsync(PayloadDeletion deletion, CancellationToken ct = default) =>
        Execute((catalog, token) => catalog.Delete(deletion, token), ct);
    public ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default) =>
        Execute((catalog, token) => catalog.Candidates(limit, grace, token), ct);
    public async ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default) =>
        _ = await Execute(async (catalog, token) => { await action(catalog, token); return true; }, ct);
}
