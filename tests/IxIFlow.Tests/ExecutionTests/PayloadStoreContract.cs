using System.Text;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

internal sealed class PayloadStoreContract(IPayloadStore store,
    Func<PayloadReference, byte[], Task> corrupt)
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"value\":\"é\"}");
    private static readonly PayloadOwner Owner = new("checkpoint", "instance/revision");

    public async Task VerifyAsync()
    {
        await IdempotentAndVerified();
        await TransactionalPinAndCollection();
        await CollectionAndPut();
        await IndependentOwnersAndDependencies();
        await RollbackPins();
        await TransactionalBodiesAndDependencyRollback();
        await GraceAndCandidateDiscovery();
        await Maximum();
        await Corruption();
    }

    private async Task IdempotentAndVerified()
    {
        var first = await store.PutAsync(Body);
        Assert.Equal(PayloadReference.Of(Body), first);
        Assert.Equal(Body.Length, first.Bytes);
        Assert.Equal(first, await store.PutAsync(Body));
        var repeats = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.PutAsync(Body).AsTask()));
        Assert.All(repeats, item => Assert.Equal(first, item));
        Assert.Equal(Body, (await store.GetAsync(first))!.Value.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(new(first.Digest, first.Bytes + 1)).AsTask());
        var read = (await store.GetAsync(first))!.Value.ToArray();
        read[0] ^= 1;
        Assert.Equal(Body, (await store.GetAsync(first))!.Value.ToArray());
    }

    private async Task TransactionalPinAndCollection()
    {
        var reference = await store.PutAsync("\"pin\""u8.ToArray());
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = store.InTransactionAsync(async (tx, ct) =>
        {
            await tx.PinAsync(Owner, reference, ct);
            pinned.SetResult();
            await finish.Task;
        }).AsTask();
        await pinned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var collection = store.CollectAsync(reference, TimeSpan.Zero).AsTask();
        Assert.False(collection.IsCompleted);
        finish.SetResult();
        await write;
        Assert.False(await collection);
        Assert.NotNull(await store.GetAsync(reference));
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(Owner, ct));
        Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InTransactionAsync(
            (tx, ct) => tx.PinAsync(Owner, reference, ct)).AsTask());
        Assert.Null(await store.GetAsync(reference));
    }

    private async Task CollectionAndPut()
    {
        var body = "\"collection-put\""u8.ToArray();
        var reference = await store.PutAsync(body);
        Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
        Assert.Null(await store.GetAsync(reference));
        // A deleted body can be put and pinned again.
        Assert.Equal(reference, await store.PutAsync(body));
        await store.InTransactionAsync((tx, ct) => tx.PinAsync(Owner, reference, ct));
        Assert.False(await store.CollectAsync(reference, TimeSpan.Zero));
        Assert.Equal(body, (await store.GetAsync(reference))!.Value.ToArray());
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(Owner, ct));
    }

    private async Task IndependentOwnersAndDependencies()
    {
        var grandchild = await store.PutAsync("\"nested-leaf\""u8.ToArray());
        var childBody = "\"nested\""u8.ToArray();
        var child = PayloadReference.Of(childBody);
        Assert.Equal(child, await store.PutAsync(childBody, [grandchild]));
        var body = "{\"part\":true}"u8.ToArray();
        var reference = PayloadReference.Of(body);
        Assert.Equal(reference, await store.PutAsync(body, [child]));
        Assert.Equal(reference, await store.PutAsync(body));
        Assert.Equal(reference, await store.PutAsync(body, [child]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutAsync(body, [grandchild]).AsTask());
        Assert.False(await store.CollectAsync(child, TimeSpan.Zero));
        Assert.False(await store.CollectAsync(grandchild, TimeSpan.Zero));
        var inbox = new PayloadOwner("inbox", "instance/event");
        await store.InTransactionAsync(async (tx, ct) =>
        {
            await tx.PinAsync(Owner, reference, ct);
            await tx.PinAsync(inbox, reference, ct);
        });
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(Owner, ct));
        Assert.False(await store.CollectAsync(reference, TimeSpan.Zero));
        Assert.False(await store.CollectAsync(child, TimeSpan.Zero));
        Assert.False(await store.CollectAsync(grandchild, TimeSpan.Zero));
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(inbox, ct));
        Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
        Assert.True(await store.CollectAsync(child, TimeSpan.Zero));
        Assert.True(await store.CollectAsync(grandchild, TimeSpan.Zero));
        Assert.Null(await store.GetAsync(reference));
    }

    private async Task RollbackPins()
    {
        var reference = await store.PutAsync("\"rollback\""u8.ToArray());
        await Assert.ThrowsAsync<IOException>(() => store.InTransactionAsync(async (tx, ct) =>
        {
            await tx.PinAsync(Owner, reference, ct);
            throw new IOException("referencing transaction failed");
        }).AsTask());
        Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
    }

    private async Task TransactionalBodiesAndDependencyRollback()
    {
        var leaf = await store.PutAsync("leaf"u8.ToArray());
        var body = "transactional-body"u8.ToArray();
        var reference = PayloadReference.Of(body);
        await Assert.ThrowsAsync<IOException>(() => store.InTransactionAsync(async (tx, ct) =>
        {
            Assert.Equal(reference, await tx.PutAsync(body, [leaf], ct));
            await tx.PinAsync(Owner, reference, ct);
            throw new IOException("referencing transaction failed");
        }).AsTask());
        Assert.Null(await store.GetAsync(reference));
        Assert.True(await store.CollectAsync(leaf, TimeSpan.Zero));
        await store.InTransactionAsync(async (tx, ct) =>
        {
            await tx.PutAsync("leaf"u8.ToArray(), ct: ct);
            await tx.PutAsync(body, [leaf], ct);
            await tx.PinAsync(Owner, reference, ct);
        });
        Assert.False(await store.CollectAsync(reference, TimeSpan.Zero));
        Assert.False(await store.CollectAsync(leaf, TimeSpan.Zero));
        Assert.Equal(body, (await store.GetAsync(reference))!.Value.ToArray());
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(Owner, ct));
        Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
        Assert.True(await store.CollectAsync(leaf, TimeSpan.Zero));
    }

    private async Task GraceAndCandidateDiscovery()
    {
        var reference = await store.PutAsync("grace"u8.ToArray());
        Assert.False(await store.CollectAsync(reference, TimeSpan.FromHours(1)));
        Assert.DoesNotContain(reference, await store.FindCollectionCandidatesAsync(1000, TimeSpan.FromHours(1)));
        Assert.Contains(reference, await store.FindCollectionCandidatesAsync(1000, TimeSpan.Zero));
        await store.InTransactionAsync((tx, ct) => tx.PinAsync(Owner, reference, ct));
        Assert.DoesNotContain(reference, await store.FindCollectionCandidatesAsync(1000, TimeSpan.Zero));
        Assert.False(await store.CollectAsync(reference, TimeSpan.Zero));
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(Owner, ct));
        Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
        Assert.DoesNotContain(reference, await store.FindCollectionCandidatesAsync(1000, TimeSpan.Zero));
    }

    private async Task Maximum()
    {
        // Fixtures configure a 1 KiB put limit, so the boundary is cheap on every backend.
        var body = Enumerable.Repeat((byte)' ', 1024).ToArray();
        Assert.Equal(1024, (await store.PutAsync(body)).Bytes);
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, (await Assert.ThrowsAsync<WorkflowEngineException>(
            () => store.PutAsync(new byte[1025]).AsTask())).Kind);
    }

    private async Task Corruption()
    {
        var body = "\"corrupt\""u8.ToArray();
        var reference = await store.PutAsync(body);
        var changed = body.ToArray(); changed[1] ^= 1;
        await corrupt(reference, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(reference).AsTask());
        await corrupt(reference, body[..^1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(reference).AsTask());
    }
}
