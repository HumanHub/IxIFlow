using System.Data.Common;

namespace IxIFlow.Core;

/// <summary>Separate ownership domains prevent a checkpoint from releasing concurrent inbox/child/reader pins.</summary>
public sealed record PayloadOwner(string Kind, string Id);
/// <summary>A durable upload ticket. An empty token means the generation is already published; no new upload is authorized.</summary>
public sealed record PayloadReservation(PayloadReference Reference, long Generation, string Token, DateTime ExpiresAtUtc);
public sealed record PayloadDeletion(PayloadReference Reference, long Generation);

/// <summary>
/// Immutable, content-addressed data, separate from code artifacts. Publication is atomic and durably readable
/// after acknowledgement. Get verifies BOTH length and SHA-256, throwing InvalidDataException on corruption.
///
/// D-51 protocol: each digest has durable Uploading/Available/Deleting/Deleted state and a monotonically
/// increasing generation. Reserve durably records an upload BEFORE any external I/O; the external object key
/// includes digest AND generation. Publish must validate token, generation and unexpired store-time deadline
/// under the same lock used by pin acquisition and collection. An expired uploader cannot publish or pin.
/// Collection changes state to Deleting only with no pins and no live reservation; new pins then fail.
/// Delete must conditionally delete only the selected generation, never an object keyed solely by digest.
/// Keep catalog tombstones after deletion (and instance purge), so generations can never be reused.
///
/// A referencing write takes pins in its OWN transaction, using InTransactionAsync or Enlist, before making
/// references visible. Failed transactions roll back pins. Pins include the immutable transitive dependency
/// closure recorded at publication. Owners distinguish checkpoints, each inbox event, child starts/notices,
/// retained results and active readers; release only the retiring owner's pins in its retiring transaction.
/// A lost acknowledgement never releases committed pins. Readers hold their own pins through hydration.
/// External providers must use the state-store catalog and generation-specific objects; collection discovers
/// expired reservations from that catalog, without listing the external store. A grace period is only a policy,
/// never the synchronization mechanism. This step supplies the protocol; workflow writes remain inline.
/// External providers retain a ledger of EVERY reserved generation, including retired generations: a delayed
/// upload can finish after its reservation expired and after an earlier delete succeeded. That generation must
/// remain discoverable for another fenced delete. Never overwrite its sole catalog entry with a replacement
/// generation. The fence must select the physical generation-specific object, not just guard catalog cleanup.
/// Table providers replace/delete the body atomically in the catalog transaction, with no outstanding body I/O.
/// </summary>
public interface IPayloadStore
{
    ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> utf8Json, CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default);
    ValueTask<PayloadReservation> ReserveAsync(PayloadReference reference, TimeSpan lifetime, CancellationToken ct = default);
    ValueTask<bool> PublishAsync(PayloadReservation reservation, ReadOnlyMemory<byte> utf8Json,
        IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default);
    ValueTask<PayloadDeletion?> TryCollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default);
    ValueTask<bool> DeleteAsync(PayloadDeletion deletion, CancellationToken ct = default);
    /// <summary>Catalog-only discovery, including abandoned reservations; candidates must still be claimed with TryCollectAsync.</summary>
    ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default);
    ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default);
    /// <summary>Enlists pins in an existing referencing database transaction; does not commit or dispose it.</summary>
    IPayloadTransaction Enlist(DbTransaction transaction);
}

/// <summary>
/// A pin transaction. DatabaseTransaction is the actual state-store transaction: referencing SQL must use it.
/// Memory callers publish their referencing state inside the callback, without awaiting outside work after
/// publication; all fallible validation precedes it. The callback and pin commit share the repository gate.
/// Do not retain this object after its transaction ends, or call the store recursively inside the callback.
/// </summary>
public interface IPayloadTransaction
{
    DbTransaction? DatabaseTransaction { get; }
    ValueTask PinAsync(PayloadOwner owner, PayloadReference reference, CancellationToken ct = default);
    ValueTask ReleaseAsync(PayloadOwner owner, CancellationToken ct = default);
}

public sealed class PayloadStoreSettings
{
    public const long DefaultMaxPayloadBytes = 64L * 1024 * 1024;
    public long MaxPayloadBytes { get; }
    public long PayloadThresholdBytes { get; }
    public PayloadStoreSettings(long maxPayloadBytes = DefaultMaxPayloadBytes, long payloadThresholdBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadThresholdBytes);
        MaxPayloadBytes = maxPayloadBytes;
        PayloadThresholdBytes = payloadThresholdBytes;
    }
    public void ValidateLength(long length)
    {
        if (length > MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(length), "MaxPayloadBytes exceeded");
    }
}

/// <summary>The state repository's payload provider; external bodies still require this durable catalog.</summary>
public interface IPayloadStoreProvider { IPayloadStore PayloadStore { get; } }
