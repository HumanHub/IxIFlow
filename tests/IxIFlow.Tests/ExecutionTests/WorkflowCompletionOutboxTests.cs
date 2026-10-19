using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The completion outbox at store level: a finished instance's event waits for each publisher,
/// with a delivery lease fencing acknowledgments from a previous claim (D-11).
/// </summary>
[Trait("Feature", "durability")]
public sealed class WorkflowCompletionOutboxTests : IClassFixture<SqliteTestDatabase<WorkflowCompletionOutboxTests>>
{
    [Fact]
    public async Task ExpiredMemoryOutboxClaimCanBeRecovered()
    {
        var time = new FakeTimeProvider();
        await VerifyExpiredClaimCanBeRecovered(new InMemoryWorkflowStateRepository(time),
            TimeSpan.FromMilliseconds(50), reclaim =>
            {
                time.Advance(TimeSpan.FromMilliseconds(200));
                return reclaim();
            });
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task ExpiredSqlOutboxClaimCanBeRecovered() => VerifyExpiredStoreClaimCanBeRecovered(_ => new SqlWorkflowStateRepository(SqlTestDatabase<WorkflowCompletionOutboxTests>.ConnectionString), TimeSpan.FromSeconds(2));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task ExpiredPostgreSqlOutboxClaimCanBeRecovered() => VerifyExpiredStoreClaimCanBeRecovered(_ => new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<WorkflowCompletionOutboxTests>.ConnectionString), TimeSpan.FromSeconds(2));

    // The dead host's claim lasts deadHostClaim; reclaimOnceExpired lets it expire and claims for the new host.

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task ExpiredSqliteOutboxClaimCanBeRecovered()
    {
        var time = new FakeTimeProvider();
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowCompletionOutboxTests>.ConnectionString, time);
        await VerifyExpiredStoreClaimCanBeRecovered(_ => repository, TimeSpan.FromMilliseconds(50), time);
    }
    private static async Task VerifyExpiredClaimCanBeRecovered(IWorkflowStateRepository repository,
        TimeSpan deadHostClaim,
        Func<Func<Task<IReadOnlyList<OutboxDelivery>>>, Task<IReadOnlyList<OutboxDelivery>>> reclaimOnceExpired)
    {
        var outbox = Assert.IsAssignableFrom<IWorkflowOutbox>(repository);
        var publisherName = "expired-claim-" + Guid.NewGuid().ToString("N");
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "ExpiredOutboxClaim",
            Status = WorkflowStatus.Completed
        };
        try
        {
            await outbox.RegisterPublisherAsync(publisherName, [OutboxEvent.WorkflowCompleted]);
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0,
                    Guid.NewGuid().ToString("N")))).Status);
            var original = Assert.Single(await outbox.ClaimDeliveriesAsync(
                publisherName, "dead-host", deadHostClaim, 1));
            Assert.Equal(instance.InstanceId, original.Event.InstanceId);
            Assert.Empty(await outbox.ClaimDeliveriesAsync(
                publisherName, "new-host", TimeSpan.FromSeconds(5), 1));

            var recovered = Assert.Single(await reclaimOnceExpired(() => outbox.ClaimDeliveriesAsync(
                publisherName, "new-host", TimeSpan.FromSeconds(5), 1)));
            Assert.Equal(original.Event.Id, recovered.Event.Id);
            Assert.False(await outbox.SettleDeliveryAsync(
                original.Event.Id, publisherName, "dead-host", OutboxPublishResult.Delivered()));
            Assert.True(await outbox.SettleDeliveryAsync(
                recovered.Event.Id, publisherName, "new-host", OutboxPublishResult.Delivered()));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    private static async Task VerifyExpiredStoreClaimCanBeRecovered(Func<TimeProvider, IWorkflowStateRepository> newRepository, TimeSpan duration, FakeTimeProvider? time = null)
    {
        var repository = newRepository(time ?? TimeProvider.System);
        // Long enough that the refusal right after the claim cannot land after it on the store's clock; the
        // new host then claims again until the dead host's claim has expired.
        await VerifyExpiredClaimCanBeRecovered(repository, duration, async reclaim =>
        {
            if (time != null)
            {
                time.Advance(TimeSpan.FromMilliseconds(200));
                return await reclaim();
            }
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                var claimed = (await reclaim()).ToList();
                if (claimed.Count > 0)
                    return claimed;
                Assert.True(DateTime.UtcNow < deadline, "The expired claim was never recovered");
                await Task.Delay(100);
            }
        });
    }


}
