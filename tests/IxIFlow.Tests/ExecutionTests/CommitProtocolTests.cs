using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Every checkpoint is a fenced, idempotent commit: it carries the owner's execution epoch, a commit
/// ID, the expected revision and a payload hash, and conflicts, stale owners, retries and unknown
/// outcomes are distinct (spec A-03).
/// </summary>
[Trait("Feature", "durability")]
[Trait("Spec", "A-03")]
public sealed class CommitProtocolTests
{
    [Fact]
    public Task MemoryEpochsRiseAndFenceFormerOwners() =>
        VerifyEpochFencing(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlEpochsRiseAndFenceFormerOwners() =>
        VerifyEpochFencing(new SqlWorkflowStateRepository(SqlTestDatabase<CommitProtocolTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlEpochsRiseAndFenceFormerOwners() =>
        VerifyEpochFencing(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CommitProtocolTests>.ConnectionString));

    [Fact]
    public Task MemoryStaleEpochAndRevisionConflictAreDistinct() =>
        VerifyDistinctResults(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlStaleEpochAndRevisionConflictAreDistinct() =>
        VerifyDistinctResults(new SqlWorkflowStateRepository(SqlTestDatabase<CommitProtocolTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlStaleEpochAndRevisionConflictAreDistinct() =>
        VerifyDistinctResults(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CommitProtocolTests>.ConnectionString));

    [Fact]
    public async Task StoreRetryResendsTheSameCommit()
    {
        var store = new ReplyingStore { RetriesBeforeApply = 2 };
        using var services = Services(store);

        var result = await Engine(services).RunAsync(Definition("CommitRetry"), new CommitData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        AssertRetriedCommitWasResentUnchanged(store, retries: 2);
    }

    [Fact]
    public async Task UnknownOutcomeIsSettledBySendingTheSameCommit()
    {
        var store = new ReplyingStore { LostAcknowledgments = 1 };
        using var services = Services(store);

        var result = await Engine(services).RunAsync(Definition("CommitUnknown"), new CommitData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Contains(store.Results, status => status == WorkflowCommitStatus.AlreadyApplied);
        AssertRetriedCommitWasResentUnchanged(store, retries: 1);
    }

    private static async Task VerifyEpochFencing(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            var first = await repository.TryAcquireExecutionLeaseAsync(instanceId, "first", TimeSpan.FromSeconds(10));
            Assert.Equal(1, first?.Epoch);
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId, WorkflowName = "EpochFencing", Status = WorkflowStatus.Running,
                WorkflowDataJson = "{}"
            };
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 1, 0))).Status);
            Assert.True(await repository.ReleaseExecutionLeaseAsync(instanceId, "first"));

            // A released lease keeps its epoch, so the next owner is always newer.
            var second = await repository.TryAcquireExecutionLeaseAsync(instanceId, "second", TimeSpan.FromSeconds(10));
            Assert.Equal(2, second?.Epoch);
            instance.Revision = 1;
            Assert.Equal(WorkflowCommitStatus.StaleEpoch,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 1, 1))).Status);
            Assert.Equal(WorkflowCommitStatus.StaleEpoch,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 1))).Status);

            instance.Status = WorkflowStatus.Completed;
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 2, 1))).Status);
            // A terminal commit releases the lease but not the epoch.
            Assert.Equal(3, (await repository.TryAcquireExecutionLeaseAsync(instanceId, "third", TimeSpan.FromSeconds(10)))?.Epoch);
            Assert.True(await repository.ReleaseExecutionLeaseAsync(instanceId, "third"));

            // A deleted and recreated instance never reuses an epoch, so a former owner stays fenced.
            await repository.DeleteWorkflowInstanceAsync(instanceId);
            Assert.Equal(4, (await repository.TryAcquireExecutionLeaseAsync(instanceId, "fourth", TimeSpan.FromSeconds(10)))?.Epoch);
            instance.Status = WorkflowStatus.Running;
            instance.Revision = 0;
            Assert.Equal(WorkflowCommitStatus.StaleEpoch,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 3, 0))).Status);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static async Task VerifyDistinctResults(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            var epoch = (await repository.TryAcquireExecutionLeaseAsync(instanceId, "owner", TimeSpan.FromSeconds(10)))!.Epoch;
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId, WorkflowName = "DistinctResults", Status = WorkflowStatus.Running,
                WorkflowDataJson = "{}"
            };
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 1),
                await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, epoch, 0, "first")));

            instance.WorkflowDataJson = "\"next\"";
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Conflict, 1),
                await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, epoch, 0, "behind")));
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, 1),
                await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, epoch + 1, 1, "future")));
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 2),
                await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, epoch, 1, "second")));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    private static void AssertRetriedCommitWasResentUnchanged(ReplyingStore store, int retries)
    {
        var resent = store.Commits.GroupBy(commit => commit.CommitId).Single(group => group.Count() > 1).ToList();
        Assert.Equal(retries + 1, resent.Count);
        Assert.All(resent, commit =>
        {
            Assert.Equal(resent[0].ContentHash, commit.ContentHash);
            Assert.Equal(resent[0].ExpectedRevision, commit.ExpectedRevision);
            Assert.Equal(resent[0].Epoch, commit.Epoch);
        });
    }

    private static WorkflowDefinition Definition(string name) =>
        Workflow.Create<CommitData>(name)
            .Step<MarkActivity>(setup => setup
                .Output(activity => activity.Marked).To(ctx => ctx.WorkflowData.Marked))
            .Build();

    private static ServiceProvider Services(IWorkflowStateRepository store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow();
        services.AddTransient<MarkActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    /// <summary>
    /// Asks for a retry, or applies a commit and loses its acknowledgment, for the commit that
    /// completes the instance.
    /// </summary>
    private sealed class ReplyingStore : FaultInjectingRepository
    {
        public int RetriesBeforeApply { get; set; }
        public int LostAcknowledgments { get; set; }
        public ConcurrentQueue<CheckpointCommit> Commits { get; } = new();
        public ConcurrentQueue<WorkflowCommitStatus> Results { get; } = new();

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            Commits.Enqueue(commit);
            var completes = commit.ReadTrunk().Status == WorkflowStatus.Completed;
            if (completes && RetriesBeforeApply > 0)
            {
                RetriesBeforeApply--;
                return new WorkflowCommitResult(WorkflowCommitStatus.Retry, 0);
            }
            var result = await base.CommitWorkflowInstanceAsync(commit);
            Results.Enqueue(result.Status);
            if (completes && LostAcknowledgments > 0)
            {
                LostAcknowledgments--;
                throw new IOException("Commit acknowledgment lost");
            }
            return result;
        }
    }

    public sealed class CommitData
    {
        public bool Marked { get; set; }
    }

    public sealed class MarkActivity : IAsyncActivity
    {
        public bool Marked { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Marked = true;
            return Task.CompletedTask;
        }
    }
}
