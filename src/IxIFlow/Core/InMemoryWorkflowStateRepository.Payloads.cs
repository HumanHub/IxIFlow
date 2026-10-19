using System.Data.Common;
using System.Text.Json;

namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository : IPayloadStoreProvider
{
    private IPayloadStore? _payloadStore;
    private PayloadStoreSettings _payloadSettings = new();
    public IPayloadStore PayloadStore => LazyInitializer.EnsureInitialized(ref _payloadStore,
        () => new MemoryPayloadStore(_time, _payloadSettings));

    private sealed class MemoryPayloadStore(TimeProvider time, PayloadStoreSettings settings) : CatalogPayloadStore(settings)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Dictionary<string, PayloadRecord> _rows = new(StringComparer.Ordinal);

        internal override async ValueTask<T> Execute<T>(Func<PayloadCatalog, CancellationToken, ValueTask<T>> action, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            PayloadCatalog? catalog = null;
            try
            {
                // Copy-on-write transaction: cancellation and callback failures cannot leave partial pins.
                var rows = _rows.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal);
                catalog = new PayloadCatalog(
                    (digest, _) => ValueTask.FromResult(rows.GetValueOrDefault(digest)),
                    (row, _) => { rows[row.Reference.Digest] = row; return ValueTask.CompletedTask; },
                    _ => ValueTask.FromResult<IReadOnlyList<PayloadRecord>>(rows.Values.ToArray()),
                    () => time.GetUtcNow().UtcDateTime, Settings);
                var result = await action(catalog, ct);
                // The callback may have published referencing state as its final action. Cancellation after
                // that point must not roll back its pins. Check cancellation before publication, in the callback.
                _rows = rows;
                return result;
            }
            finally { catalog?.End(); _gate.Release(); }
        }

        private static PayloadRecord Clone(PayloadRecord row)
        {
            var copy = JsonSerializer.Deserialize<PayloadRecord>(JsonSerializer.Serialize(row))!;
            copy.Body = row.Body; // immutable; never returned to callers without copying
            copy.Owners = [.. row.Owners];
            return copy;
        }

        public override IPayloadTransaction Enlist(DbTransaction transaction) =>
            throw new NotSupportedException("Memory payloads require InTransactionAsync");
    }
}
