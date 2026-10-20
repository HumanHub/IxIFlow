using System.Data.Common;

namespace IxIFlow.Core;

/// <summary>Separate ownership domains prevent a checkpoint from releasing concurrent inbox/child/reader pins.</summary>
public sealed record PayloadOwner(string Kind, string Id);

/// <summary>
/// Immutable, content-addressed bodies in the state store. Get verifies length and SHA-256 and throws
/// InvalidDataException on corruption. Put records a body and its immutable transitive dependency closure;
/// repeating the same put is harmless and can restore damaged bytes. An outer body retains its dependencies.
/// Referencing writes put new bodies and acquire owner pins in their own transaction, using
/// InTransactionAsync or Enlist. Failed transactions roll back bodies and pins together with the references.
/// Pins include the dependency closure. Owners distinguish checkpoints, inbox events, children, retained
/// results and readers; release only the retiring owner's pins. Lost acknowledgements retain committed pins.
/// Readers keep pins until they hold a verified snapshot. Collection deletes an unpinned body past its grace
/// period atomically under the same digest lock used by puts and pins. Concurrent pins either prevent deletion
/// or fail because the body is missing. Discovery is advisory; collection rechecks eligibility under the lock.
/// </summary>
public interface IPayloadStore
{
    ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> utf8Json,
        IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default);
    ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default);
    ValueTask<bool> CollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default);
    /// <summary>Finds possible unpinned bodies; CollectAsync rechecks eligibility before deleting.</summary>
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
    /// <summary>Records a body and its dependency closure in this referencing transaction.</summary>
    ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> utf8Json,
        IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default);
    /// <summary>Acquires checkpoint payload locks before instance locks, without changing pins.</summary>
    ValueTask LockAsync(PayloadOwner owner, IReadOnlyList<PayloadReference> references, CancellationToken ct = default);
    ValueTask PinAsync(PayloadOwner owner, PayloadReference reference, CancellationToken ct = default);
    /// <summary>Replaces only this owner's pins, writing the differences and including dependency closures.</summary>
    ValueTask ReplaceAsync(PayloadOwner owner, IReadOnlyList<PayloadReference> references, CancellationToken ct = default);
    ValueTask ReleaseAsync(PayloadOwner owner, CancellationToken ct = default);
}

public sealed class PayloadStoreSettings
{
    public const long DefaultPayloadThresholdBytes = 0;
    public const long DefaultMaxPayloadBytes = 64L * 1024 * 1024;
    public long MaxPayloadBytes { get; }
    public long PayloadThresholdBytes { get; }
    public PayloadStoreSettings(long maxPayloadBytes = DefaultMaxPayloadBytes, long payloadThresholdBytes = DefaultPayloadThresholdBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadThresholdBytes);
        MaxPayloadBytes = maxPayloadBytes;
        PayloadThresholdBytes = payloadThresholdBytes;
    }
    public void ValidateLength(long length)
    {
        if (length > MaxPayloadBytes) throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "MaxPayloadBytes exceeded");
    }
}
