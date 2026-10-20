using IxIFlow.Tests.Infrastructure;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>The same delivery protocol is exercised against memory and every relational provider.</summary>
internal static class OutboxStoreContract
{
    internal static async Task ExpiredUnreclaimedOwnerCannotChangeDelivery(IWorkflowStateRepository store,
        Func<OutboxDelivery, Task> expire)
    {
        var publisher = "expired-" + Guid.NewGuid().ToString("N");
        await store.RegisterPublisherAsync(publisher, [OutboxEvent.WorkflowCompleted]);
        var instance = await CompleteAsync(store);
        try
        {
            var claimed = await Claim(store, publisher, "actual-owner");
            await expire(claimed);
            var before = (await store.GetDeliveryAsync(claimed.Event.Id, publisher))!;
            Assert.True(before.LeaseUntilUtc <= before.StoreNowUtc);
            Assert.Equal("actual-owner", before.LeaseToken);
            Assert.False(await store.RenewDeliveryLeaseAsync(claimed.Event.Id, publisher,
                claimed.LeaseToken!, TimeSpan.FromMinutes(1)));
            Assert.Equal(before, (await store.GetDeliveryAsync(claimed.Event.Id, publisher))! with { StoreNowUtc = before.StoreNowUtc });
            foreach (var result in new[] { OutboxPublishResult.Delivered(), OutboxPublishResult.RetryLater("offline"),
                         OutboxPublishResult.GiveUp("gone", disablePublisher: true) })
            {
                Assert.False(await store.SettleDeliveryAsync(claimed.Event.Id, publisher, claimed.LeaseToken!, result));
                Assert.Equal(before, (await store.GetDeliveryAsync(claimed.Event.Id, publisher))! with { StoreNowUtc = before.StoreNowUtc });
                Assert.Null((await store.GetPublisherAsync(publisher))!.DisabledAtUtc);
            }
        }
        finally
        {
            await store.UnregisterPublisherAsync(publisher);
            await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    internal static async Task VerifyUnclaimedHorizonAsync(IWorkflowStateRepository store, TimeSpan horizon,
        Func<IReadOnlyCollection<string>, TimeSpan, Task> advance, TimeSpan boundaryStep)
    {
        var absent = "unhosted-" + Guid.NewGuid().ToString("N");
        var observer = "observer-" + Guid.NewGuid().ToString("N");
        await store.RegisterPublisherAsync(absent, [OutboxEvent.WorkflowCompleted]);
        await store.RegisterPublisherAsync(observer, [OutboxEvent.WorkflowCompleted]);
        async Task<OutboxDelivery> CompleteAndObserve()
        {
            var instance = await CompleteAsync(store);
            var observed = await Claim(store, observer);
            Assert.Equal(instance.InstanceId, observed.Event.InstanceId);
            Assert.True(await store.SettleDeliveryAsync(observed.Event.Id, observer, "owner", OutboxPublishResult.Delivered()));
            return (await store.GetDeliveryAsync(observed.Event.Id, absent))!;
        }
        var old = new[] { await CompleteAndObserve(), await CompleteAndObserve() };
        var ids = old.Select(d => d.Event.InstanceId).ToArray();
        var tombstones = TimeSpan.FromDays(90);
        await advance(ids, horizon - boundaryStep);
        var skipped = await store.PurgeAsync(TimeSpan.FromMinutes(1), tombstones, 1);
        Assert.Equal(0, skipped.InstancesRemoved);
        Assert.Equal(2, skipped.SkippedUnattemptedByPublisher[absent]);
        foreach (var delivery in old)
        {
            var pending = (await store.GetDeliveryAsync(delivery.Event.Id, absent))!;
            Assert.Equal(OutboxDeliveryStatus.Pending, pending.Status);
            Assert.Equal(0, pending.Attempts);
            Assert.Null(pending.FirstAttemptAtUtc);
        }

        // No host ever claims these deliveries. New events still get their own full horizon.
        var fresh = await CompleteAndObserve();
        await advance(ids, boundaryStep);
        var keepInstances = horizon + TimeSpan.FromDays(1);
        Assert.Equal(0, (await store.PurgeAsync(keepInstances, tombstones, 1)).InstancesRemoved);
        var swept = await Task.WhenAll(old.Select(d => store.GetDeliveryAsync(d.Event.Id, absent)));
        Assert.Single(swept, d => d!.Status == OutboxDeliveryStatus.GivenUp);
        Assert.Single(swept, d => d!.Status == OutboxDeliveryStatus.Pending);
        await store.PurgeAsync(keepInstances, tombstones, 1);
        foreach (var delivery in old)
        {
            var givenUp = (await store.GetDeliveryAsync(delivery.Event.Id, absent))!;
            Assert.Equal(OutboxDeliveryStatus.GivenUp, givenUp.Status);
            Assert.Equal("Never claimed within the horizon", givenUp.LastError);
            Assert.Equal(0, givenUp.Attempts);
            Assert.Null(givenUp.FirstAttemptAtUtc);
            Assert.Null(givenUp.LeaseToken);
            Assert.Null(givenUp.LeaseUntilUtc);
        }
        Assert.Null((await store.GetPublisherAsync(absent))!.DisabledAtUtc);
        Assert.Equal(OutboxDeliveryStatus.Pending, (await store.GetDeliveryAsync(fresh.Event.Id, absent))!.Status);
        Assert.Equal(1, (await store.PurgeAsync(TimeSpan.FromMinutes(1), tombstones, 1)).InstancesRemoved);
        Assert.Equal(1, (await store.PurgeAsync(TimeSpan.FromMinutes(1), tombstones, 1)).InstancesRemoved);
        foreach (var delivery in old)
        {
            Assert.Null(await store.GetWorkflowInstanceAsync(delivery.Event.InstanceId));
            Assert.NotNull(await store.GetTombstoneAsync(delivery.Event.InstanceId));
            Assert.Null(await store.GetDeliveryAsync(delivery.Event.Id, absent));
        }

        // A host returning later may claim the fresh event. Once attempted, event age alone must
        // not end retries or replace the publisher's error with the never-claimed reason.
        var attempted = await Claim(store, absent);
        Assert.Equal(fresh.Event.Id, attempted.Event.Id);
        Assert.True(await store.SettleDeliveryAsync(attempted.Event.Id, absent, "owner", OutboxPublishResult.RetryLater("offline")));
        await advance([fresh.Event.InstanceId], horizon + boundaryStep);
        await store.PurgeAsync(keepInstances + horizon, tombstones, 1);
        var retrying = (await store.GetDeliveryAsync(attempted.Event.Id, absent))!;
        Assert.Equal(OutboxDeliveryStatus.Pending, retrying.Status);
        Assert.Equal(1, retrying.Attempts);
        Assert.Equal(attempted.FirstAttemptAtUtc, retrying.FirstAttemptAtUtc);
        Assert.Equal("offline", retrying.LastError);
        Assert.Equal(OutboxDeliveryStatus.Delivered, (await store.GetDeliveryAsync(fresh.Event.Id, observer))!.Status);
        await store.UnregisterPublisherAsync(absent);
        await store.UnregisterPublisherAsync(observer);
        await store.DeleteWorkflowInstanceAsync(fresh.Event.InstanceId);
    }

    internal static async Task VerifyAsync(IWorkflowStateRepository repository,
        Func<OutboxDelivery, TimeSpan, Task> advance, Func<string, Task> expireTombstone)
    {
        var outbox = repository;
        await RegistrationStartsWithFutureEventsUnlessReplayIsExplicit(repository);
        await UnregisterGivesUpPendingDeliveriesAndStopsPinningEvents(repository);
        await RegistrationAndCompletionCannotMissEachOther(repository);
        await outbox.RegisterPublisherAsync("primary", [OutboxEvent.WorkflowCompleted]);
        await outbox.RegisterPublisherAsync("secondary", [OutboxEvent.WorkflowCompleted]);
        await PerPublisherIsolationAndBackoff(repository, outbox, advance);
        await LeaseReclaimDeliversAtLeastOnceWithTheSameId(repository, outbox, advance);
        await PendingDeliveriesSurviveDeletionAndRecreation(repository, outbox, expireTombstone);
        await DisablePersistsAcrossRegistrationAndFutureEvents(repository, outbox);
    }

    private static async Task RegistrationStartsWithFutureEventsUnlessReplayIsExplicit(IWorkflowStateRepository store)
    {
        var retained = await CompleteAsync(store);
        await store.RegisterPublisherAsync("future-only", [OutboxEvent.WorkflowCompleted]);
        Assert.Empty(await store.ClaimDeliveriesAsync("future-only", "owner", TimeSpan.FromSeconds(30), 100));
        await store.RegisterPublisherAsync("replay", [OutboxEvent.WorkflowCompleted], replayRetained: true);
        var replay = await Claim(store, "replay");
        Assert.Equal(retained.InstanceId, replay.Event.InstanceId);
        var future = await CompleteAsync(store);
        Assert.Equal(future.InstanceId, (await Claim(store, "future-only")).Event.InstanceId);
        await store.UnregisterPublisherAsync("future-only");
        await store.UnregisterPublisherAsync("replay");
        await store.DeleteWorkflowInstanceAsync(retained.InstanceId);
        await store.DeleteWorkflowInstanceAsync(future.InstanceId);
    }

    private static async Task UnregisterGivesUpPendingDeliveriesAndStopsPinningEvents(IWorkflowStateRepository store)
    {
        await store.RegisterPublisherAsync("removed", [OutboxEvent.WorkflowCompleted]);
        var instance = await CompleteAsync(store);
        var pending = await Claim(store, "removed");
        await store.UnregisterPublisherAsync("removed");
        // Unregistering is a durable administrative state, kept with its actor and time.
        var removed = Assert.IsType<OutboxPublisherState>(await store.GetPublisherAsync("removed"));
        Assert.Equal("host", removed.UnregisteredBy);
        Assert.NotNull(removed.UnregisteredAtUtc);
        Assert.Equal(OutboxDeliveryStatus.GivenUp, (await store.GetDeliveryAsync(pending.Event.Id, "removed"))!.Status);
        Assert.False(await store.SettleDeliveryAsync(pending.Event.Id, "removed", "owner", OutboxPublishResult.Delivered()));
        Assert.False(await store.RenewDeliveryLeaseAsync(pending.Event.Id, "removed", "owner", TimeSpan.FromSeconds(30)));
        await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
        Assert.Null(await store.GetDeliveryAsync(pending.Event.Id, "removed"));
        var future = await CompleteAsync(store);
        await store.RegisterPublisherAsync("observer", [OutboxEvent.WorkflowCompleted], replayRetained: true);
        var observed = await Claim(store, "observer");
        Assert.Equal(future.InstanceId, observed.Event.InstanceId);
        Assert.Null(await store.GetDeliveryAsync(observed.Event.Id, "removed"));
        await store.UnregisterPublisherAsync("observer");
        await store.DeleteWorkflowInstanceAsync(future.InstanceId);
    }

    private static async Task RegistrationAndCompletionCannotMissEachOther(IWorkflowStateRepository store)
    {
        for (var i = 0; i < 10; i++)
        {
            var name = "race-" + i;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = Task.Run(async () =>
            {
                await gate.Task;
                await store.RegisterPublisherAsync(name, [OutboxEvent.WorkflowCompleted], replayRetained: true);
            });
            var completion = Task.Run(async () => { await gate.Task; return await CompleteAsync(store); });
            gate.SetResult();
            await Task.WhenAll(registration, completion);
            var delivery = await Claim(store, name);
            Assert.Equal((await completion).InstanceId, delivery.Event.InstanceId);
            await store.UnregisterPublisherAsync(name);
            await store.DeleteWorkflowInstanceAsync(delivery.Event.InstanceId);
        }
    }

    internal static async Task<WorkflowInstance> CompleteAsync(IWorkflowStateRepository store)
    {
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "OutboxContract", WorkflowVersion = 1, Status = WorkflowStatus.Completed };
        var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0, Guid.NewGuid().ToString("N"));
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        return instance;
    }

    private static async Task<OutboxDelivery> Claim(IWorkflowOutbox store, string publisher, string token = "owner") =>
        Assert.Single(await store.ClaimDeliveriesAsync(publisher, token, TimeSpan.FromSeconds(30), 10));

    internal static async Task PerPublisherIsolationAndBackoff(IWorkflowStateRepository repository,
        IWorkflowOutbox store, Func<OutboxDelivery, TimeSpan, Task> advance)
    {
        var instance = await CompleteAsync(repository);
        var first = await Claim(store, "primary");
        var other = await Claim(store, "secondary");
        Assert.Equal(first.Event, other.Event);
        Assert.Equal(1, first.Attempts);
        Assert.True(await store.SettleDeliveryAsync(other.Event.Id, "secondary", "owner", OutboxPublishResult.Delivered()));
        var beforeSettlement = (await store.GetDeliveryAsync(first.Event.Id, "primary"))!.StoreNowUtc;
        Assert.True(await store.SettleDeliveryAsync(first.Event.Id, "primary", "owner", OutboxPublishResult.RetryLater("offline")));
        var retry = (await store.GetDeliveryAsync(first.Event.Id, "primary"))!;
        Assert.Equal("offline", retry.LastError);
        Assert.Equal(OutboxDeliveryStatus.Pending, retry.Status);
        Assert.InRange(retry.NextAttemptAtUtc, beforeSettlement.AddSeconds(5), retry.StoreNowUtc.AddSeconds(5));
        if (repository is InMemoryWorkflowStateRepository or SqliteWorkflowStateRepository)
            Assert.Equal(beforeSettlement.AddSeconds(5), retry.NextAttemptAtUtc);
        Assert.Empty(await store.ClaimDeliveriesAsync("primary", "early", TimeSpan.FromSeconds(30), 10));
        Assert.Empty(await store.ClaimDeliveriesAsync("secondary", "again", TimeSpan.FromSeconds(30), 10));
        await advance(retry, TimeSpan.FromSeconds(5));
        var second = await Claim(store, "primary");
        Assert.Equal(first.Event.Id, second.Event.Id);
        Assert.Equal(2, second.Attempts);
        beforeSettlement = (await store.GetDeliveryAsync(first.Event.Id, "primary"))!.StoreNowUtc;
        Assert.True(await store.SettleDeliveryAsync(first.Event.Id, "primary", "owner", OutboxPublishResult.RetryLater("still offline")));
        retry = (await store.GetDeliveryAsync(first.Event.Id, "primary"))!;
        Assert.InRange(retry.NextAttemptAtUtc, beforeSettlement.AddSeconds(10), retry.StoreNowUtc.AddSeconds(10));
        if (repository is InMemoryWorkflowStateRepository or SqliteWorkflowStateRepository)
            Assert.Equal(beforeSettlement.AddSeconds(10), retry.NextAttemptAtUtc);
        await advance(retry, TimeSpan.FromSeconds(10));
        var third = await Claim(store, "primary");
        Assert.Equal(3, third.Attempts);
        Assert.True(await store.SettleDeliveryAsync(first.Event.Id, "primary", "owner", OutboxPublishResult.GiveUp("invalid")));
        Assert.Equal(OutboxDeliveryStatus.GivenUp, (await store.GetDeliveryAsync(first.Event.Id, "primary"))!.Status);
        Assert.Empty(await store.ClaimDeliveriesAsync("primary", "again", TimeSpan.FromSeconds(30), 10));
        Assert.Equal(1, (await store.GetDeliveryAsync(first.Event.Id, "secondary"))!.Attempts);
        await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        Assert.Null(await store.GetDeliveryAsync(first.Event.Id, "primary"));
    }

    internal static async Task LeaseReclaimDeliversAtLeastOnceWithTheSameId(IWorkflowStateRepository repository,
        IWorkflowOutbox store, Func<OutboxDelivery, TimeSpan, Task> advance)
    {
        var instance = await CompleteAsync(repository);
        var competing = await Task.WhenAll(
            store.ClaimDeliveriesAsync("primary", "dead-a", TimeSpan.FromSeconds(30), 10),
            store.ClaimDeliveriesAsync("primary", "dead-b", TimeSpan.FromSeconds(30), 10));
        var dead = Assert.Single(competing.SelectMany(batch => batch));
        var deadToken = dead.LeaseToken!;
        // The external send succeeded, but the host died before recording its acknowledgment.
        var received = new List<string> { dead.Event.Id };
        Assert.Empty(await store.ClaimDeliveriesAsync("primary", "new", TimeSpan.FromSeconds(30), 10));
        await advance(dead, TimeSpan.FromSeconds(20));
        Assert.True(await store.RenewDeliveryLeaseAsync(dead.Event.Id, "primary", deadToken, TimeSpan.FromSeconds(30)));
        await advance(dead, TimeSpan.FromSeconds(15));
        Assert.Empty(await store.ClaimDeliveriesAsync("primary", "early", TimeSpan.FromSeconds(30), 10));
        await advance(dead, TimeSpan.FromSeconds(16));
        var recovered = await Claim(store, "primary", "new");
        received.Add(recovered.Event.Id);
        Assert.Equal(received[0], received[1]);
        Assert.Equal(2, recovered.Attempts);
        Assert.Equal(dead.FirstAttemptAtUtc, recovered.FirstAttemptAtUtc);
        Assert.Equal(dead.Event.DataJson, recovered.Event.DataJson);
        Assert.False(await store.RenewDeliveryLeaseAsync(dead.Event.Id, "primary", deadToken, TimeSpan.FromSeconds(30)));
        var beforeStaleSettlement = (await store.GetDeliveryAsync(dead.Event.Id, "primary"))!;
        Assert.False(await store.SettleDeliveryAsync(dead.Event.Id, "primary", deadToken, OutboxPublishResult.Delivered()));
        Assert.Equal(beforeStaleSettlement, (await store.GetDeliveryAsync(dead.Event.Id, "primary"))!
            with { StoreNowUtc = beforeStaleSettlement.StoreNowUtc });
        Assert.True(await store.SettleDeliveryAsync(dead.Event.Id, "primary", "new", OutboxPublishResult.Delivered()));
        var other = await Claim(store, "secondary");
        Assert.True(await store.SettleDeliveryAsync(other.Event.Id, "secondary", "owner", OutboxPublishResult.Delivered()));
        await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
    }

    internal static async Task PendingDeliveriesSurviveDeletionAndRecreation(IWorkflowStateRepository repository,
        IWorkflowOutbox store, Func<string, Task> expireTombstone)
    {
        var original = new WorkflowInstance
        {
            ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "Outbox", Status = WorkflowStatus.Completed
        };
        var grant = await StoreTestSupport.LeaseAsync(repository, original.InstanceId);
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(original, grant.Epoch, 0))).Status);
        await repository.DeleteWorkflowInstanceAsync(original.InstanceId);
        var replacement = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = original.InstanceId,
            WorkflowName = "Replacement", Status = WorkflowStatus.Completed };
        Assert.NotNull(await repository.GetTombstoneAsync(original.InstanceId));
        Assert.Equal(WorkflowErrorKind.InstanceRemoved, (await Assert.ThrowsAsync<WorkflowEngineException>(async () =>
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(replacement, grant.Epoch, 0)))).Kind);
        await expireTombstone(original.InstanceId);
        Assert.Null(await repository.GetTombstoneAsync(original.InstanceId));
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(replacement, (await StoreTestSupport.LeaseAsync(repository, replacement.InstanceId)).Epoch, 0, Guid.NewGuid().ToString("N")))).Status);
        foreach (var name in new[] { "primary", "secondary" })
        {
            var pending = await store.ClaimDeliveriesAsync(name, "owner", TimeSpan.FromSeconds(30), 10);
            Assert.Equal(2, pending.Count);
            Assert.Equal(2, pending.Select(d => d.Event.Id).Distinct().Count());
            Assert.All(pending, d => Assert.Equal(original.InstanceId, d.Event.InstanceId));
            foreach (var delivery in pending)
                Assert.True(await store.SettleDeliveryAsync(delivery.Event.Id, name, "owner", OutboxPublishResult.Delivered()));
        }
        await repository.DeleteWorkflowInstanceAsync(original.InstanceId);
    }

    internal static async Task DisablePersistsAcrossRegistrationAndFutureEvents(IWorkflowStateRepository repository,
        IWorkflowOutbox store)
    {
        var instance = await CompleteAsync(repository);
        var gone = await Claim(store, "primary");
        Assert.True(await store.SettleDeliveryAsync(gone.Event.Id, "primary", "owner",
            OutboxPublishResult.GiveUp("HTTP 410 Gone", disablePublisher: true)));
        await store.RegisterPublisherAsync("primary", [OutboxEvent.WorkflowCompleted]);
        var state = (await store.GetPublisherAsync("primary"))!;
        Assert.NotNull(state.DisabledAtUtc);
        Assert.Equal("HTTP 410 Gone", state.LastError);
        Assert.Empty(await store.ClaimDeliveriesAsync("primary", "again", TimeSpan.FromSeconds(30), 10));
        var future = await CompleteAsync(repository);
        var secondary = await store.ClaimDeliveriesAsync("secondary", "owner", TimeSpan.FromSeconds(30), 10);
        Assert.Equal(2, secondary.Count);
        foreach (var item in secondary)
        {
            Assert.True(await store.SettleDeliveryAsync(item.Event.Id, "secondary", "owner", OutboxPublishResult.Delivered()));
            Assert.Equal(OutboxDeliveryStatus.GivenUp, (await store.GetDeliveryAsync(item.Event.Id, "primary"))!.Status);
        }
        await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        await repository.DeleteWorkflowInstanceAsync(future.InstanceId);
    }
}
