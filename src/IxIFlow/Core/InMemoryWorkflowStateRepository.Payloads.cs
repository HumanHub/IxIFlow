using System.Data.Common;
using System.Text.Json;
using System.Collections.Concurrent;

namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private IPayloadStore? _payloadStore;
    private PayloadStoreSettings _payloadSettings = new();
    public virtual IPayloadStore PayloadStore => LazyInitializer.EnsureInitialized(ref _payloadStore,
        () => new MemoryPayloadStore(_time, _payloadSettings));

    private sealed class MemoryPayloadStore(TimeProvider time, PayloadStoreSettings settings) : CatalogPayloadStore(settings)
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PayloadRecord> _rows = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _orderedDigests = new(StringComparer.Ordinal);
        private readonly object _index = new();

        internal override async ValueTask<T> Execute<T>(Func<PayloadCatalog, CancellationToken, ValueTask<T>> action, CancellationToken ct)
        {
            while (true)
            {
                var held = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
                var staged = new Dictionary<string, PayloadRecord?>(StringComparer.Ordinal);
                var written = new HashSet<string>(StringComparer.Ordinal);
                PayloadCatalog? catalog = null;
                SemaphoreSlim? blocked = null;
                try
                {
                    ValueTask<PayloadRecord?> Read(string digest, CancellationToken token)
                    {
                        token.ThrowIfCancellationRequested();
                        if (staged.TryGetValue(digest, out var cached)) return ValueTask.FromResult(cached);
                        var gate = _gates.GetOrAdd(digest, _ => new(1, 1));
                        if (!gate.Wait(0)) throw new DigestBusyException(gate);
                        held.Add(digest, gate);
                        PayloadRecord? copy;
                        lock (_index) copy = _rows.TryGetValue(digest, out var row) ? Clone(row) : null;
                        staged.Add(digest, copy);
                        return ValueTask.FromResult(copy);
                    }
                    async ValueTask<IReadOnlyList<PayloadRecord>> Rows(IEnumerable<string> digests, CancellationToken token)
                    {
                        var rows = new List<PayloadRecord>();
                        foreach (var digest in digests.Distinct().Order(StringComparer.Ordinal))
                            if (await Lookup(digest, token) is { } row) rows.Add(row);
                        return rows;
                    }
                    ValueTask<PayloadRecord?> Lookup(string digest, CancellationToken token)
                    {
                        token.ThrowIfCancellationRequested();
                        if (staged.TryGetValue(digest, out var cached)) return ValueTask.FromResult(cached);
                        lock (_index) return ValueTask.FromResult(_rows.TryGetValue(digest, out var row) ? Clone(row) : null);
                    }
                    string[] Digests(PayloadOwner owner)
                    {
                        lock (_index) return _rows.Where(pair => pair.Value.Owners.Contains(owner)).Select(pair => pair.Key)
                            .Concat(staged.Where(pair => pair.Value != null && (pair.Value.Owners.Contains(owner)))
                                .Select(pair => pair.Key)).Distinct(StringComparer.Ordinal).ToArray();
                    }
                    catalog = new PayloadCatalog(Read,
                        (row, _) => { staged[row.Reference.Digest] = row; written.Add(row.Reference.Digest); return ValueTask.CompletedTask; },
                        (digest, _) => { staged[digest] = null; written.Add(digest); return ValueTask.CompletedTask; },
                        () => time.GetUtcNow().UtcDateTime, Settings, null,
                        forOwner: (owner, token) => Rows(Digests(owner), token),
                        readBody: null, discover: (limit, grace, token) =>
                        {
                            var found = new List<PayloadRecord>();
                            lock (_index)
                                foreach (var digest in _orderedDigests)
                                {
                                    token.ThrowIfCancellationRequested();
                                    var row = _rows[digest];
                                    if (row.Owners.Count == 0 && row.TouchedAtUtc + grace <= time.GetUtcNow().UtcDateTime)
                                    { found.Add(row); if (found.Count == limit) break; }
                                }
                            return ValueTask.FromResult<IReadOnlyList<PayloadRecord>>(found);
                        }, lookup: Lookup);
                    var result = await action(catalog, ct);
                    // Publication is the callback's final action. All pins remain locked until staged rows publish.
                    lock (_index) foreach (var digest in written)
                        if (staged[digest] is { } row) { _rows[digest] = row; _orderedDigests.Add(digest); }
                        else { _rows.Remove(digest); _orderedDigests.Remove(digest); }
                    return result;
                }
                catch (DigestBusyException busy) { blocked = busy.Gate; }
                finally
                {
                    catalog?.End();
                    foreach (var gate in held.Values) gate.Release();
                }
                // Never wait for a second digest while holding the first: dependency transactions cannot deadlock.
                await blocked!.WaitAsync(ct);
                blocked.Release();
            }
        }

        private sealed class DigestBusyException(SemaphoreSlim gate) : Exception
        { public SemaphoreSlim Gate { get; } = gate; }

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
