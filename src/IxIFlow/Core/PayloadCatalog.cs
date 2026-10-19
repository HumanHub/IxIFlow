using System.Data.Common;
using System.Text.Json.Serialization;

namespace IxIFlow.Core;

// Shared state machine, independent of the physical catalog. Each digest is fenced for its transaction.
// Rows are never removed: Deleted rows retain the generation fence after body deletion.
internal sealed class PayloadRecord
{
    public PayloadReference Reference { get; set; } = null!;
    public long Generation { get; set; }
    public string State { get; set; } = "Deleted";
    public string? Token { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime TouchedAtUtc { get; set; }
    public List<PayloadReference> Dependencies { get; set; } = [];
    [JsonIgnore] public byte[]? Body { get; set; }
    [JsonIgnore] public bool BodyChanged { get; set; }
    [JsonIgnore] public HashSet<PayloadOwner> Owners { get; set; } = [];
}

internal sealed class PayloadCatalog(
    Func<string, CancellationToken, ValueTask<PayloadRecord?>> read,
    Func<PayloadRecord, CancellationToken, ValueTask> write,
    Func<CancellationToken, ValueTask<IReadOnlyList<PayloadRecord>>> all,
    Func<DateTime> now, PayloadStoreSettings settings, DbTransaction? transaction = null,
    Func<PayloadOwner, CancellationToken, ValueTask<IReadOnlyList<PayloadRecord>>>? forOwner = null,
    Func<PayloadRecord, CancellationToken, ValueTask<byte[]?>>? readBody = null,
    Func<CancellationToken, ValueTask<IReadOnlyList<PayloadRecord>>>? discover = null) : IPayloadTransaction
{
    public DbTransaction? DatabaseTransaction => transaction;
    private bool _active = true;
    internal void End() => _active = false;
    private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(!_active, this); }

    internal async ValueTask<PayloadReservation> Reserve(PayloadReference reference, TimeSpan lifetime, CancellationToken ct)
    {
        Check(ct);
        settings.ValidateLength(reference.Bytes);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var row = await read(reference.Digest, ct) ?? new PayloadRecord { Reference = reference };
        if (row.Reference != reference) throw new InvalidDataException("Digest length differs from catalog");
        var time = now();
        if (row.State == "Available")
            return new(reference, row.Generation, "", time + lifetime);
        if (row.State == "Uploading" && row.ExpiresAtUtc > time)
            throw new InvalidOperationException("Payload upload already reserved; retry after publication or expiry");
        row.Generation = checked(row.Generation + 1);
        row.State = "Uploading";
        row.Body = null;
        row.BodyChanged = true;
        row.Token = Guid.NewGuid().ToString("N");
        row.ExpiresAtUtc = time + lifetime;
        row.TouchedAtUtc = time;
        row.Dependencies = [];
        await write(row, ct);
        return new(reference, row.Generation, row.Token, row.ExpiresAtUtc);
    }

    internal async ValueTask<bool> Publish(PayloadReservation reservation, ReadOnlyMemory<byte> body,
        IReadOnlyCollection<PayloadReference>? dependencies, CancellationToken ct)
    {
        Check(ct);
        settings.ValidateLength(body.Length);
        reservation.Reference.Verify(body.Span);
        var row = await read(reservation.Reference.Digest, ct);
        var time = now();
        if (row is null || row.Generation != reservation.Generation || reservation.ExpiresAtUtc <= time) return false;
        if (row.State == "Available" && reservation.Token == "")
        {
            try { row.Reference.Verify(await Body(row, ct) ?? throw new InvalidDataException("Available body missing")); }
            catch (InvalidDataException)
            {
                // Re-publication of verified original bytes repairs a damaged table/memory body without
                // changing its identity, generation, dependencies or owners. No external upload is started.
                row.Body = body.ToArray();
                row.BodyChanged = true;
            }
            if (dependencies is not null)
            {
                var proposed = await Closure(dependencies, row.Reference, ct);
                if (!row.Dependencies.SequenceEqual(proposed))
                    throw new InvalidOperationException("Payload dependencies are immutable");
            }
            row.TouchedAtUtc = time;
            await write(row, ct);
            return true;
        }
        if (row.State != "Uploading" || row.Token != reservation.Token || row.ExpiresAtUtc <= time) return false;
        // Cache the closure at publication; a commit pins it without reading/parsing any payload bodies.
        row.Dependencies = await Closure(dependencies ?? [], row.Reference, ct);
        // An available outer body keeps its closure alive even before its first referencing commit.
        // Otherwise a child digest could be collected between publication and acquisition of the outer pin.
        var dependencyOwner = new PayloadOwner("$dependency", row.Reference.Digest);
        foreach (var dependency in row.Dependencies)
        {
            var child = await Available(dependency, ct);
            child.Owners.Add(dependencyOwner);
            await write(child, ct);
        }
        row.Body = body.ToArray();
        row.BodyChanged = true;
        row.State = "Available";
        row.Token = null;
        row.TouchedAtUtc = time;
        await write(row, ct);
        return true;
    }

    private async ValueTask<List<PayloadReference>> Closure(IEnumerable<PayloadReference> dependencies,
        PayloadReference outer, CancellationToken ct)
    {
        var closure = new Dictionary<string, PayloadReference>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            var child = await Available(dependency, ct);
            closure[dependency.Digest] = dependency;
            foreach (var nested in child.Dependencies) closure[nested.Digest] = nested;
        }
        if (closure.ContainsKey(outer.Digest)) throw new InvalidOperationException("Payload dependency cycle");
        return closure.Values.OrderBy(x => x.Digest, StringComparer.Ordinal).ToList();
    }

    private async ValueTask<PayloadRecord> Available(PayloadReference reference, CancellationToken ct)
    {
        var row = await read(reference.Digest, ct);
        return row is { State: "Available" } && row.Reference == reference ? row :
            throw new InvalidOperationException("Payload unavailable or collection already owns its generation");
    }

    public async ValueTask PinAsync(PayloadOwner owner, PayloadReference reference, CancellationToken ct = default)
    {
        Check(ct);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Id);
        if (owner.Kind.Length > 64 || owner.Id.Length > 256) throw new ArgumentOutOfRangeException(nameof(owner));
        if (owner.Kind.StartsWith('$')) throw new ArgumentException("Reserved payload owner kind", nameof(owner));
        var row = await Available(reference, ct);
        var rows = new List<PayloadRecord> { row };
        foreach (var dependency in row.Dependencies) rows.Add(await Available(dependency, ct));
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
            var row = await Available(reference, ct);
            wanted[row.Reference.Digest] = row;
            foreach (var dependency in row.Dependencies)
                wanted[dependency.Digest] = await Available(dependency, ct);
        }
        foreach (var row in await Owned(owner, ct))
            if (!wanted.ContainsKey(row.Reference.Digest) && row.Owners.Remove(owner)) await write(row, ct);
        foreach (var row in wanted.Values)
            if (row.Owners.Add(owner)) await write(row, ct);
    }

    private ValueTask<IReadOnlyList<PayloadRecord>> Owned(PayloadOwner owner, CancellationToken ct) =>
        forOwner is null ? all(ct) : forOwner(owner, ct);

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
        if (row is not { State: "Available" }) return null;
        var body = await Body(row, ct) ?? throw new InvalidDataException("Available payload has no body");
        reference.Verify(body);
        row.Reference.Verify(body);
        return body.ToArray();
    }

    private ValueTask<byte[]?> Body(PayloadRecord row, CancellationToken ct) =>
        readBody is null || row.BodyChanged ? ValueTask.FromResult(row.Body) : readBody(row, ct);

    internal async ValueTask<PayloadDeletion?> Collect(PayloadReference reference, TimeSpan grace, CancellationToken ct)
    {
        Check(ct);
        if (grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grace));
        var row = await read(reference.Digest, ct);
        if (row is null || row.Reference != reference || row.State == "Deleted" || row.Owners.Count != 0) return null;
        var time = now();
        if (row.State == "Uploading" && row.ExpiresAtUtc > time || row.TouchedAtUtc + grace > time) return null;
        row.State = "Deleting";
        row.Token = null;
        await write(row, ct);
        foreach (var dependency in row.Dependencies)
        {
            var child = await read(dependency.Digest, ct);
            if (child is not null && child.Owners.Remove(new PayloadOwner("$dependency", row.Reference.Digest)))
                await write(child, ct);
        }
        return new(reference, row.Generation);
    }

    internal async ValueTask<IReadOnlyList<PayloadReference>> Candidates(int limit, TimeSpan grace, CancellationToken ct)
    {
        Check(ct);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (grace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grace));
        var time = now();
        // Relational All obtains the transaction lock and samples store time before the predicate runs.
        var rows = await (discover ?? all)(ct);
        time = now();
        return rows.Where(row => row.State != "Deleted" && row.Owners.Count == 0 &&
                (row.State != "Uploading" || row.ExpiresAtUtc <= time) && row.TouchedAtUtc + grace <= time)
            .OrderBy(row => row.Reference.Digest, StringComparer.Ordinal).Take(limit).Select(row => row.Reference).ToArray();
    }

    internal async ValueTask<bool> Delete(PayloadDeletion deletion, CancellationToken ct)
    {
        Check(ct);
        var row = await read(deletion.Reference.Digest, ct);
        if (row is null || row.Reference != deletion.Reference || row.Generation != deletion.Generation ||
            row.State != "Deleting" || row.Owners.Count != 0) return false;
        row.Body = null;
        row.State = "Deleted";
        row.BodyChanged = true;
        row.Dependencies = [];
        await write(row, ct);
        return true;
    }
}

