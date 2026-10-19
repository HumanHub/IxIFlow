using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

internal static class OutboxAdministrationContract
{
    internal static async Task VerifyAsync(IWorkflowStateRepository store, Func<string, TimeSpan, Task> age,
        Func<Task<IWorkflowStateRepository>> restart)
    {
        var name = "admin-" + Guid.NewGuid().ToString("N");
        var other = name + "-other";
        var retainedBeforeRegistration = await OutboxStoreContract.CompleteAsync(store);
        await store.RegisterPublisherAsync(name, [OutboxEvent.WorkflowCompleted]);
        await store.RegisterPublisherAsync(other, [OutboxEvent.WorkflowCompleted]);
        var instances = new List<string> { retainedBeforeRegistration.InstanceId };
        async Task<string> Complete()
        {
            var instance = await OutboxStoreContract.CompleteAsync(store);
            instances.Add(instance.InstanceId);
            return instance.InstanceId;
        }
        async Task<OutboxDelivery> Claim(string publisher) => Assert.Single(await store.ClaimDeliveriesAsync(
            publisher, "secret-lease-token", TimeSpan.FromHours(1), 1));
        async Task<OutboxDelivery> GiveUp(string publisher, OutboxGivenUpReason reason)
        {
            var delivery = await Claim(publisher);
            Assert.True(await store.SettleDeliveryAsync(delivery.Event.Id, publisher, delivery.LeaseToken!,
                OutboxPublishResult.GiveUp("test", reason: reason)));
            Assert.Equal(reason, (await store.GetDeliveryAsync(delivery.Event.Id, publisher))!.GivenUpReason);
            return delivery;
        }

        var first = await Complete();
        await Complete();
        await Complete();
        var exhausted = await GiveUp(name, OutboxGivenUpReason.Exhausted);
        var horizon = await GiveUp(name, OutboxGivenUpReason.Horizon);
        var operatorDelivery = await GiveUp(name, OutboxGivenUpReason.Operator);
        var expected = (await store.ListDeliveriesAsync(new(PublisherName: name))).Items;
        Assert.Equal(3, expected.Count);
        Assert.Equal(3, (await store.ListDeliveriesAsync(new(PublisherName: name, Status: OutboxDeliveryStatus.GivenUp))).Items.Count);
        Assert.Empty((await store.ListDeliveriesAsync(new(PublisherName: name, Status: OutboxDeliveryStatus.Pending))).Items);
        Assert.Empty((await store.ListDeliveriesAsync(new(PublisherName: name, NeverAttempted: true))).Items);
        Assert.Equal(3, (await store.ListDeliveriesAsync(new(PublisherName: other, Status: OutboxDeliveryStatus.Pending, NeverAttempted: true))).Items.Count);
        Assert.Single((await store.ListDeliveriesAsync(new(PublisherName: name, InstanceId: first))).Items);

        // Both publishers share the event ID. Paging must also advance across the publisher tie.
        var page1 = await store.ListDeliveriesAsync(new(InstanceId: first, Limit: 1));
        Assert.Single(page1.Items);
        Assert.NotNull(page1.NextCursor);
        var page2 = await store.ListDeliveriesAsync(new(InstanceId: first, Limit: 1, After: page1.NextCursor));
        Assert.Single(page2.Items);
        Assert.Null(page2.NextCursor);
        Assert.NotEqual(page1.Items[0].PublisherName, page2.Items[0].PublisherName);
        var paged = new List<DeliverySummary>();
        string? cursor = null;
        do
        {
            var page = await store.ListDeliveriesAsync(new(PublisherName: name, Limit: 1, After: cursor));
            paged.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor != null);
        Assert.Equal(expected.Select(d => d.Event.Id), paged.Select(d => d.Event.Id));
        var json = JsonSerializer.Serialize(await store.ListDeliveriesAsync(new(InstanceId: first)));
        Assert.DoesNotContain("Lease", json);
        Assert.DoesNotContain("secret-lease-token", json);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListDeliveriesAsync(new(After: "invalid")));

        // A retry of an old event receives a fresh first-claim horizon and a fresh unattempted horizon.
        await age(exhausted.Event.InstanceId, TimeSpan.FromDays(31));
        Assert.False(await store.RetryDeliveryAsync("missing", name, "alice"));
        Assert.True(await store.RetryDeliveryAsync(exhausted.Event.Id, name, "alice"));
        var reset = (await store.GetDeliveryAsync(exhausted.Event.Id, name))!;
        Assert.Equal(0, reset.Attempts);
        Assert.Null(reset.FirstAttemptAtUtc);
        Assert.Null(reset.LeaseToken);
        Assert.Null(reset.LeaseUntilUtc);
        Assert.Null(reset.GivenUpReason);
        Assert.Null(reset.LastError);
        Assert.Equal("alice", reset.RetriedBy);
        Assert.NotNull(reset.RetriedAtUtc);
        Assert.Equal(DateTimeKind.Utc, reset.RetriedAtUtc!.Value.Kind);
        Assert.True(reset.NextAttemptAtUtc <= reset.StoreNowUtc);
        Assert.True(reset.RetriedAtUtc <= reset.StoreNowUtc);
        Assert.Equal(reset.NextAttemptAtUtc, reset.RetriedAtUtc);
        Assert.False(await store.RetryDeliveryAsync(exhausted.Event.Id, name, "again"));
        await store.PurgeAsync(TimeSpan.FromDays(365), TimeSpan.FromDays(90), 100);
        Assert.Equal(OutboxDeliveryStatus.Pending, (await store.GetDeliveryAsync(exhausted.Event.Id, name))!.Status);
        var fresh = await Claim(name);
        Assert.Equal(exhausted.Event.Id, fresh.Event.Id);
        Assert.Equal(1, fresh.Attempts);
        Assert.True(fresh.FirstAttemptAtUtc >= reset.RetriedAtUtc);
        Assert.True(await store.SettleDeliveryAsync(fresh.Event.Id, name, fresh.LeaseToken!, OutboxPublishResult.Delivered()));

        // Disable covers its triggering delivery, the pending batch, and new/backfilled events.
        await Complete();
        await Complete();
        var trigger = await Claim(name);
        Assert.True(await store.SettleDeliveryAsync(trigger.Event.Id, name, trigger.LeaseToken!,
            OutboxPublishResult.GiveUp("HTTP 410", disablePublisher: true)));
        await Complete();
        await store.RegisterPublisherAsync(name, [OutboxEvent.WorkflowCompleted], replayRetained: true);
        var disabled = (await store.ListDeliveriesAsync(new(PublisherName: name))).Items
            .Where(d => d.GivenUpReason == OutboxGivenUpReason.Disabled).ToArray();
        Assert.True(disabled.Length >= 3);
        Assert.Contains(disabled, d => d.Event.Id == trigger.Event.Id);
        Assert.Contains(disabled, d => d.Event.InstanceId == retainedBeforeRegistration.InstanceId);
        Assert.False(await store.RetryDeliveryAsync(trigger.Event.Id, name, "alice"));
        Assert.True(await store.EnablePublisherAsync(name, "bob"));
        Assert.False(await store.EnablePublisherAsync(name, "again"));
        var enabled = (await store.GetPublisherAsync(name))!;
        Assert.Null(enabled.DisabledAtUtc);
        Assert.Null(enabled.LastError);
        Assert.Equal("bob", enabled.EnabledBy);
        Assert.NotNull(enabled.EnabledAtUtc);
        foreach (var item in disabled)
        {
            var delivery = (await store.GetDeliveryAsync(item.Event.Id, name))!;
            Assert.Equal(OutboxDeliveryStatus.Pending, delivery.Status);
            Assert.Equal(0, delivery.Attempts);
            Assert.Null(delivery.FirstAttemptAtUtc);
            Assert.Null(delivery.GivenUpReason);
            Assert.Equal("bob", delivery.RetriedBy);
            Assert.Equal(enabled.EnabledAtUtc, delivery.RetriedAtUtc);
        }
        Assert.Equal(OutboxGivenUpReason.Horizon, (await store.GetDeliveryAsync(horizon.Event.Id, name))!.GivenUpReason);
        Assert.Equal(OutboxGivenUpReason.Operator, (await store.GetDeliveryAsync(operatorDelivery.Event.Id, name))!.GivenUpReason);
        Assert.Equal(OutboxDeliveryStatus.Delivered, (await store.GetDeliveryAsync(exhausted.Event.Id, name))!.Status);

        await store.UnregisterPublisherAsync(name, "carol");
        var removed = (await store.GetPublisherAsync(name))!;
        Assert.Equal("carol", removed.UnregisteredBy);
        Assert.NotNull(removed.UnregisteredAtUtc);
        Assert.Equal(DateTimeKind.Utc, removed.UnregisteredAtUtc!.Value.Kind);
        foreach (var item in disabled)
            Assert.Equal(OutboxGivenUpReason.Unregistered, (await store.GetDeliveryAsync(item.Event.Id, name))!.GivenUpReason);
        await store.UnregisterPublisherAsync(other, "cleanup");
        Assert.Contains(await store.ListPublishersAsync(), p => p == removed);
        Assert.False(await store.EnablePublisherAsync(name, "denied"));
        Assert.False(await store.RetryDeliveryAsync(trigger.Event.Id, name, "denied"));

        foreach (var id in instances) await age(id, TimeSpan.FromDays(31));

        // Retry and purge can commit in either order, but may never resurrect a purged delivery.
        var racing = name + "-race";
        await store.RegisterPublisherAsync(racing, [OutboxEvent.WorkflowCompleted]);
        var raceId = await Complete();
        var race = await GiveUp(racing, OutboxGivenUpReason.Exhausted);
        await age(raceId, TimeSpan.FromDays(31));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retrying = Task.Run(async () => { await gate.Task; return await store.RetryDeliveryAsync(race.Event.Id, racing, "racer"); });
        var purging = Task.Run(async () => { await gate.Task; return await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(90), 1000); });
        gate.SetResult();
        await Task.WhenAll(retrying, purging).WaitAsync(TimeSpan.FromSeconds(15));
        var surviving = await store.GetDeliveryAsync(race.Event.Id, racing);
        if (await retrying)
        {
            Assert.NotNull(surviving);
            Assert.Equal(OutboxDeliveryStatus.Pending, surviving.Status);
            Assert.NotNull(await store.GetWorkflowInstanceAsync(raceId));
        }
        else Assert.Null(surviving);
        if (surviving != null)
        {
            var claimed = await Claim(racing);
            Assert.True(await store.SettleDeliveryAsync(claimed.Event.Id, racing, claimed.LeaseToken!, OutboxPublishResult.GiveUp()));
        }
        await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(90), 1000);
        Assert.Null(await store.GetDeliveryAsync(race.Event.Id, racing));
        Assert.False(await store.RetryDeliveryAsync(race.Event.Id, racing, "too-late"));
        await store.UnregisterPublisherAsync(racing, "cleanup");

        var unattempted = name + "-unattempted";
        await store.RegisterPublisherAsync(unattempted, [OutboxEvent.WorkflowCompleted]);
        var unattemptedId = await Complete();
        await age(unattemptedId, TimeSpan.FromDays(31));
        await store.PurgeAsync(TimeSpan.FromDays(365), TimeSpan.FromDays(90), 1000);
        var expired = Assert.Single((await store.ListDeliveriesAsync(new(PublisherName: unattempted))).Items);
        Assert.Equal(OutboxGivenUpReason.Horizon, expired.GivenUpReason);
        Assert.Equal(OutboxDeliveryStatus.GivenUp, expired.Status);
        Assert.Equal(0, expired.Attempts);
        await store.UnregisterPublisherAsync(unattempted, "cleanup");
        await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(90), 1000);

        // A new service on another/reopened repository performs normal startup registration.
        store = await restart();
        var publisher = new Publisher(name);
        using (var service = new WorkflowOutboxService(store, [publisher],
            new HostSettings(1, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), "other-host"),
            new OutboxSettings(), TimeProvider.System, NullLogger<WorkflowOutboxService>.Instance))
        {
            await service.StartAsync(default);
            await Complete();
            await service.StopAsync(default);
        }
        Assert.Equal(0, publisher.Calls);
        Assert.Equal(removed, await store.GetPublisherAsync(name));
        Assert.Empty(await store.ClaimDeliveriesAsync(name, "other-host", TimeSpan.FromMinutes(1), 100));
        Assert.Empty((await store.ListDeliveriesAsync(new(PublisherName: name))).Items);
        // Even unregistering an unknown name prevents stale hosts introducing it later.
        var absent = name + "-absent";
        await store.UnregisterPublisherAsync(absent, "dana");
        await store.RegisterPublisherAsync(absent, [OutboxEvent.WorkflowCompleted], replayRetained: true);
        Assert.Equal("dana", (await store.GetPublisherAsync(absent))!.UnregisteredBy);
        Assert.Empty((await store.ListDeliveriesAsync(new(PublisherName: absent))).Items);
        foreach (var id in instances) await store.DeleteWorkflowInstanceAsync(id);
    }

    private sealed class Publisher(string name) : IOutboxPublisher
    {
        public int Calls;
        public string Name => name;
        public IReadOnlyCollection<string> EventTypes => [OutboxEvent.WorkflowCompleted];
        public Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult(OutboxPublishResult.Delivered()); }
    }
}
