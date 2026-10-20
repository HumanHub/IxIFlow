using System.Data.Common;
using System.Text;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F_AReadCannotLeaveADurableOwnerAfterConnectionLoss(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var body = Encoding.UTF8.GetBytes("\"reader snapshot\"");
        var reference = await h.Store.PayloadStore.PutAsync(body);
        var crashing = new CrashAfterReaderPinStore(h.Store.PayloadStore);
        var transfer = new ExecutionPayloadTransfer(crashing, ExecutionLimits.Default);
        try
        {
            var reply = await transfer.ReadAsync(new(Guid.NewGuid(), "read", reference));
            Assert.Equal(body, reply.Body);
        }
        catch (WorkflowEngineException e) when (e.Kind == WorkflowErrorKind.Infrastructure) { }
        // A new reader/host has no knowledge of the failed reader's random identity.
        Assert.Equal(body, (await h.Store.PayloadStore.GetAsync(reference))!.Value.ToArray());
        Assert.True(await h.Store.PayloadStore.CollectAsync(reference, TimeSpan.Zero));
        Assert.Equal(0, crashing.ReaderPins);
    }

    private sealed class CrashAfterReaderPinStore(IPayloadStore inner) : IPayloadStore
    {
        public int ReaderPins;
        public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> body, IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default) => inner.PutAsync(body, dependencies, ct);
        public ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default) => inner.GetAsync(reference, ct);
        public ValueTask<bool> CollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default) => inner.CollectAsync(reference, grace, ct);
        public ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default) => inner.FindCollectionCandidatesAsync(limit, grace, ct);
        public IPayloadTransaction Enlist(DbTransaction transaction) => new Pins(this, inner.Enlist(transaction));
        public async ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default)
        {
            await inner.InTransactionAsync((tx, token) => action(new Pins(this, tx), token), ct);
            if (ReaderPins != 0) throw new IOException("Connection lost after durable reader acquisition");
        }
        private sealed class Pins(CrashAfterReaderPinStore store, IPayloadTransaction inner) : IPayloadTransaction
        {
            public DbTransaction? DatabaseTransaction => inner.DatabaseTransaction;
            public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> body, IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default) => inner.PutAsync(body, dependencies, ct);
            public ValueTask LockAsync(PayloadOwner owner, IReadOnlyList<PayloadReference> references, CancellationToken ct = default) => inner.LockAsync(owner, references, ct);
            public ValueTask PinAsync(PayloadOwner owner, PayloadReference reference, CancellationToken ct = default)
            { if (owner.Kind == "reader") store.ReaderPins++; return inner.PinAsync(owner, reference, ct); }
            public ValueTask ReplaceAsync(PayloadOwner owner, IReadOnlyList<PayloadReference> references, CancellationToken ct = default) => inner.ReplaceAsync(owner, references, ct);
            public ValueTask ReleaseAsync(PayloadOwner owner, CancellationToken ct = default) => inner.ReleaseAsync(owner, ct);
        }
    }
}
