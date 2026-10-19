using System.Text;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

internal sealed class PayloadStoreContract(IPayloadStore store,
    Func<PayloadReference, byte[], Task> corrupt, Func<PayloadReference, Task> expire)
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"value\":\"é\"}");
    private static readonly PayloadOwner Owner = new("checkpoint", "instance/revision");

    public async Task VerifyAsync()
    {
        await IdempotentAndVerified();
        await TransactionalPinAndCollection();
        await CollectionAndPut();
        await ExpiredReservation();
        await GenerationFence();
        await IndependentOwnersAndDependencies();
        await RollbackPins();
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
        var collection = store.TryCollectAsync(reference, TimeSpan.Zero).AsTask();
        Assert.False(collection.IsCompleted);
        finish.SetResult();
        await write;
        Assert.Null(await collection);
        Assert.NotNull(await store.GetAsync(reference));
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(Owner, ct));
        var deletion = await store.TryCollectAsync(reference, TimeSpan.Zero);
        Assert.NotNull(deletion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InTransactionAsync(
            (tx, ct) => tx.PinAsync(Owner, reference, ct)).AsTask());
        Assert.True(await store.DeleteAsync(deletion));
    }

    private async Task CollectionAndPut()
    {
        var body = "\"collection-put\""u8.ToArray();
        var reference = await store.PutAsync(body);
        var selected = await store.TryCollectAsync(reference, TimeSpan.Zero);
        Assert.NotNull(selected);
        // A collector has selected the old generation and is delayed before body deletion.
        Assert.Equal(reference, await store.PutAsync(body));
        await store.InTransactionAsync((tx, ct) => tx.PinAsync(Owner, reference, ct));
        Assert.False(await store.DeleteAsync(selected));
        Assert.Null(await store.TryCollectAsync(reference, TimeSpan.Zero));
        Assert.Equal(body, (await store.GetAsync(reference))!.Value.ToArray());
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(Owner, ct));
    }

    private async Task ExpiredReservation()
    {
        var body = "\"expired\""u8.ToArray();
        var reference = PayloadReference.Of(body);
        var reservation = await store.ReserveAsync(reference, TimeSpan.FromMinutes(1));
        Assert.Null(await store.GetAsync(reference));
        Assert.Null(await store.TryCollectAsync(reference, TimeSpan.Zero));
        await expire(reference);
        Assert.False(await store.PublishAsync(reservation, body));
        Assert.Contains(reference, await store.FindCollectionCandidatesAsync(1000, TimeSpan.Zero));
        var selected = await store.TryCollectAsync(reference, TimeSpan.Zero);
        Assert.NotNull(selected);
        Assert.True(await store.DeleteAsync(selected));
        Assert.False(await store.PublishAsync(reservation, body));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InTransactionAsync(
            (tx, ct) => tx.PinAsync(Owner, reference, ct)).AsTask());
    }

    private async Task GenerationFence()
    {
        var body = "\"generation\""u8.ToArray();
        var reference = await store.PutAsync(body);
        var old = (await store.TryCollectAsync(reference, TimeSpan.Zero))!;
        Assert.True(await store.DeleteAsync(old));
        Assert.Equal(reference, await store.PutAsync(body));
        var newer = (await store.TryCollectAsync(reference, TimeSpan.Zero))!;
        Assert.True(newer.Generation > old.Generation);
        Assert.False(await store.DeleteAsync(old));
        Assert.True(await store.DeleteAsync(newer));
        Assert.Null(await store.GetAsync(reference));
    }

    private async Task IndependentOwnersAndDependencies()
    {
        var grandchild = await store.PutAsync("\"nested-leaf\""u8.ToArray());
        var childBody = "\"nested\""u8.ToArray();
        var child = PayloadReference.Of(childBody);
        var childReservation = await store.ReserveAsync(child, TimeSpan.FromMinutes(1));
        Assert.True(await store.PublishAsync(childReservation, childBody, [grandchild]));
        var body = "{\"part\":true}"u8.ToArray();
        var reference = PayloadReference.Of(body);
        var reservation = await store.ReserveAsync(reference, TimeSpan.FromMinutes(1));
        Assert.True(await store.PublishAsync(reservation, body, [child]));
        Assert.Equal(reference, await store.PutAsync(body));
        var repeated = await store.ReserveAsync(reference, TimeSpan.FromMinutes(1));
        Assert.True(await store.PublishAsync(repeated, body, [child]));
        Assert.Null(await store.TryCollectAsync(child, TimeSpan.Zero));
        Assert.Null(await store.TryCollectAsync(grandchild, TimeSpan.Zero));
        var inbox = new PayloadOwner("inbox", "instance/event");
        await store.InTransactionAsync(async (tx, ct) =>
        {
            await tx.PinAsync(Owner, reference, ct);
            await tx.PinAsync(inbox, reference, ct);
        });
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(Owner, ct));
        Assert.Null(await store.TryCollectAsync(reference, TimeSpan.Zero));
        Assert.Null(await store.TryCollectAsync(child, TimeSpan.Zero));
        Assert.Null(await store.TryCollectAsync(grandchild, TimeSpan.Zero));
        await store.InTransactionAsync((tx, ct) => tx.ReleaseAsync(inbox, ct));
        var parentDelete = await store.TryCollectAsync(reference, TimeSpan.Zero);
        Assert.NotNull(parentDelete);
        Assert.NotNull(await store.TryCollectAsync(child, TimeSpan.Zero));
        Assert.NotNull(await store.TryCollectAsync(grandchild, TimeSpan.Zero));
        Assert.True(await store.DeleteAsync(parentDelete));
    }

    private async Task RollbackPins()
    {
        var reference = await store.PutAsync("\"rollback\""u8.ToArray());
        await Assert.ThrowsAsync<IOException>(() => store.InTransactionAsync(async (tx, ct) =>
        {
            await tx.PinAsync(Owner, reference, ct);
            throw new IOException("referencing transaction failed");
        }).AsTask());
        Assert.NotNull(await store.TryCollectAsync(reference, TimeSpan.Zero));
    }

    private async Task Maximum()
    {
        // Fixtures configure a 1 KiB put limit, so the boundary is cheap on every backend.
        var body = Enumerable.Repeat((byte)' ', 1024).ToArray();
        Assert.Equal(1024, (await store.PutAsync(body)).Bytes);
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, (await Assert.ThrowsAsync<WorkflowEngineException>(
            () => store.PutAsync(new byte[1025]).AsTask())).Kind);
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, (await Assert.ThrowsAsync<WorkflowEngineException>(
            () => store.ReserveAsync(PayloadReference.Of(new byte[1025]), TimeSpan.FromMinutes(1)).AsTask())).Kind);
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
