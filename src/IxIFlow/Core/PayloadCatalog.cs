using System.Data.Common;
using System.Text.Json.Serialization;

namespace IxIFlow.Core;

// Shared body, dependency and pin operations. Each digest is locked for its transaction.
internal sealed class PayloadRecord
{
    public PayloadReference Reference { get; set; } = null!;
    public DateTime TouchedAtUtc { get; set; }
    public List<PayloadReference> Dependencies { get; set; } = [];
    [JsonIgnore] public byte[]? Body { get; set; }
    [JsonIgnore] public bool BodyChanged { get; set; }
    [JsonIgnore] public HashSet<PayloadOwner> Owners { get; set; } = [];
}

internal sealed class PayloadCatalog(
    Func<string, CancellationToken, ValueTask<PayloadRecord?>> read,
    Func<PayloadRecord, CancellationToken, ValueTask> write,
    Func<string, CancellationToken, ValueTask> delete,
    Func<DateTime> now, PayloadStoreSettings settings, DbTransaction? transaction,
    Func<PayloadOwner, CancellationToken, ValueTask<IReadOnlyList<PayloadRecord>>> forOwner,
    Func<PayloadRecord, CancellationToken, ValueTask<byte[]?>>? readBody,
    Func<CancellationToken, ValueTask<IReadOnlyList<PayloadRecord>>> discover) : IPayloadTransaction
{
    public DbTransaction? DatabaseTransaction => transaction;
    private bool _active = true;
    internal void End() => _active = false;
    private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(!_active, this); }

    public async ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> utf8Json,
        IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default)
    {
        Check(ct);
        settings.ValidateLength(utf8Json.Length);
        var body = utf8Json.ToArray();
        var requested = dependencies?.ToArray();
        var reference = PayloadReference.Of(body);
        var row = await read(reference.Digest, ct);
        if (row != null)
        {
            if (row.Reference != reference) throw new InvalidDataException("Digest length differs from catalog");
            if (requested != null && !row.Dependencies.SequenceEqual(await Closure(requested, reference, ct)))
                throw new InvalidOperationException("Payload dependencies are immutable");
            try { reference.Verify(await Body(row, ct) ?? throw new InvalidDataException("Payload body missing")); }
            catch (InvalidDataException) { row.Body = body; row.BodyChanged = true; }
        }
        else
        {
            row = new PayloadRecord { Reference = reference, Body = body, BodyChanged = true,
                Dependencies = await Closure(requested ?? [], reference, ct) };
            var dependencyOwner = new PayloadOwner("$dependency", reference.Digest);
            foreach (var dependency in row.Dependencies)
            {
                var child = await RequireBody(dependency, ct);
                child.Owners.Add(dependencyOwner);
                await write(child, ct);
            }
        }
        row.TouchedAtUtc = now();
        await write(row, ct);
        return reference;
    }

    private async ValueTask<List<PayloadReference>> Closure(IEnumerable<PayloadReference> dependencies,
        PayloadReference outer, CancellationToken ct)
    {
        var closure = new Dictionary<string, PayloadReference>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            var child = await RequireBody(dependency, ct);
            closure[dependency.Digest] = dependency;
            foreach (var nested in child.Dependencies) closure[nested.Digest] = nested;
        }
        if (closure.ContainsKey(outer.Digest)) throw new InvalidOperationException("Payload dependency cycle");
        return closure.Values.OrderBy(x => x.Digest, StringComparer.Ordinal).ToList();
    }

    private async ValueTask<PayloadRecord> RequireBody(PayloadReference reference, CancellationToken ct)
    {
        var row = await read(reference.Digest, ct);
        return row != null && row.Reference == reference ? row :
            throw new InvalidOperationException("Payload unavailable");
    }

    public async ValueTask PinAsync(PayloadOwner owner, PayloadReference reference, CancellationToken ct = default)
    {
        Check(ct);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Id);
        if (owner.Kind.Length > 64 || owner.Id.Length > 256) throw new ArgumentOutOfRangeException(nameof(owner));
        if (owner.Kind.StartsWith('$')) throw new ArgumentException("Reserved payload owner kind", nameof(owner));
        var row = await RequireBody(reference, ct);
        var rows = new List<PayloadRecord> { row };
        foreach (var dependency in row.Dependencies) rows.Add(await RequireBody(dependency, ct));
        foreach (var item in rows) { item.Owners.Add(owner); await write(item, ct); }
    }

    public async ValueTask ReleaseAsync(PayloadOwner owner, CancellationToken ct = default)
    {
        Check(ct);
        if (owner.Kind.StartsWith('$')) throw new ArgumentException("Reserved payload owner kind", nameof(owner));
        foreach (var row in await Owned(owner, ct))
            if (row.Owners.Remove(owner)) await write(row, ct);
    }

    public async ValueTask ReplaceAsync(PayloadOwner owner, IReadOnlyList<PayloadReference> references, CancellationToken ct = default)
    {
        Check(ct);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Id);
        if (owner.Kind.Length > 64 || owner.Id.Length > 256) throw new ArgumentOutOfRangeException(nameof(owner));
        if (owner.Kind.StartsWith('$')) throw new ArgumentException("Reserved payload owner kind", nameof(owner));
        var wanted = new Dictionary<string, PayloadRecord>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var row = await RequireBody(reference, ct);
            wanted[row.Reference.Digest] = row;
            foreach (var dependency in row.Dependencies)
                wanted[dependency.Digest] = await RequireBody(dependency, ct);
        }
        foreach (var row in await Owned(owner, ct))
            if (!wanted.ContainsKey(row.Reference.Digest) && row.Owners.Remove(owner)) await write(row, ct);
        foreach (var row in wanted.Values)
            if (row.Owners.Add(owner)) await write(row, ct);
    }

    private ValueTask<IReadOnlyList<PayloadRecord>> Owned(PayloadOwner owner, CancellationToken ct) =>
        forOwner(owner, ct);

    public async ValueTask LockAsync(PayloadOwner owner, IReadOnlyList<PayloadReference> references, CancellationToken ct = default)
    {
        Check(ct);
        var owned = await Owned(owner, ct);
        foreach (var reference in references.Concat(owned.Select(r => r.Reference)).Distinct().OrderBy(r => r.Digest, StringComparer.Ordinal))
        {
            var row = await read(reference.Digest, ct);
            if (row != null)
                foreach (var dependency in row.Dependencies) await read(dependency.Digest, ct);
        }
    }

    internal async ValueTask<ReadOnlyMemory<byte>?> Get(PayloadReference reference, CancellationToken ct)
    {
        Check(ct);
        var row = await read(reference.Digest, ct);
        if (row is null) return null;
        var body = await Body(row, ct) ?? throw new InvalidDataException("Payload has no body");
        reference.Verify(body);
        row.Reference.Verify(body);
        return body.ToArray();
    }

    private ValueTask<byte[]?> Body(PayloadRecord row, CancellationToken ct) =>
        readBody is null || row.BodyChanged ? ValueTask.FromResult(row.Body) : readBody(row, ct);

    internal async ValueTask<bool> Collect(PayloadReference reference, TimeSpan grace, CancellationToken ct)
    {
        Check(ct);
        if (grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grace));
        var row = await read(reference.Digest, ct);
        if (row is null || row.Reference != reference || row.Owners.Count != 0 || row.TouchedAtUtc + grace > now())
            return false;
        foreach (var dependency in row.Dependencies)
        {
            var child = await read(dependency.Digest, ct);
            if (child != null && child.Owners.Remove(new PayloadOwner("$dependency", reference.Digest)))
                await write(child, ct);
        }
        await delete(reference.Digest, ct);
        return true;
    }

    internal async ValueTask<IReadOnlyList<PayloadReference>> Candidates(int limit, TimeSpan grace, CancellationToken ct)
    {
        Check(ct);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grace));
        var rows = await discover(ct);
        var time = now();
        return rows.Where(row => row.Owners.Count == 0 && row.TouchedAtUtc + grace <= time)
            .OrderBy(row => row.Reference.Digest, StringComparer.Ordinal).Take(limit).Select(row => row.Reference).ToArray();
    }
}
