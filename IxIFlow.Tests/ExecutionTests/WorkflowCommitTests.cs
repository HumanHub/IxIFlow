using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

public class WorkflowCommitTests
{
    [Fact]
    public async Task CommitSnapshotsStateWithoutMutatingCaller()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = CreateInstance();
        var result = await repository.CommitWorkflowInstanceAsync(instance, 0, "first");
        Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 1), result);
        Assert.Equal(0, instance.Revision);
        instance.WorkflowDataJson = "changed";
        var saved = (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal("{}", saved.WorkflowDataJson);
        saved.WorkflowDataJson = "also changed";
        Assert.Equal("{}", (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.WorkflowDataJson);
    }

    [Fact]
    public Task FailureBeforeCommitCanRetry() => VerifyFailureBeforeCommit(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task LostAcknowledgmentReturnsReceipt() => VerifyLostAcknowledgment(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task StaleWriterCannotOverwrite() => VerifyConflict(new InMemoryWorkflowStateRepository());

    [Fact]
    public async Task OldReceiptSurvivesLaterCommits()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var first = CreateInstance();
        await repository.CommitWorkflowInstanceAsync(first, 0, "first");
        var second = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;
        second.WorkflowDataJson = "next";
        await repository.CommitWorkflowInstanceAsync(second, 1, "second");
        var repeated = await repository.CommitWorkflowInstanceAsync(first, 0, "first");
        Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, 1), repeated);
        Assert.Equal("next", (await repository.GetWorkflowInstanceAsync(first.InstanceId))!.WorkflowDataJson);
    }

    [Fact]
    public async Task CommitIdCannotNameDifferentPayload()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = CreateInstance();
        await repository.CommitWorkflowInstanceAsync(instance, 0, "first");
        instance.WorkflowDataJson = "different";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CommitWorkflowInstanceAsync(instance, 0, "first"));
    }

    [Fact]
    public async Task LegacySaveCannotOverwriteRevisionedState()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = CreateInstance();
        await repository.CommitWorkflowInstanceAsync(instance, 0, "first");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveWorkflowInstanceAsync(instance));
        Assert.Equal(1, (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
    }

    [Fact]
    public async Task ConcurrentWritersHaveOneWinner()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = CreateInstance();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            repository.CommitWorkflowInstanceAsync(instance, 0, index.ToString()))));
        Assert.Single(results, result => result.Status == WorkflowCommitStatus.Applied);
        Assert.Equal(7, results.Count(result => result.Status == WorkflowCommitStatus.Conflict));
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlCommitHandlesConflictsAndLostAcknowledgments()
    {
        var repository = new SqlWorkflowStateRepository(
            Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!);
        await VerifyFailureBeforeCommit(repository);
        await VerifyLostAcknowledgment(repository);
        await VerifyConflict(repository);
    }

    private static async Task VerifyFailureBeforeCommit(IWorkflowStateRepository repository)
    {
        var instance = CreateInstance();
        try
        {
            Task FailBeforeCommit() => throw new IOException("Connection failed before dispatch");
            await Assert.ThrowsAsync<IOException>(FailBeforeCommit);
            Assert.Null(await repository.GetWorkflowInstanceAsync(instance.InstanceId));
            var result = await repository.CommitWorkflowInstanceAsync(instance, 0, "first");
            Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    private static async Task VerifyLostAcknowledgment(IWorkflowStateRepository repository)
    {
        var instance = CreateInstance();
        try
        {
            async Task LoseAcknowledgment()
            {
                await repository.CommitWorkflowInstanceAsync(instance, 0, "first");
                throw new IOException("The commit succeeded but its acknowledgment was lost");
            }
            await Assert.ThrowsAsync<IOException>(LoseAcknowledgment);
            var result = await repository.CommitWorkflowInstanceAsync(instance, 0, "first");
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, 1), result);
            Assert.Equal(1, (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    private static async Task VerifyConflict(IWorkflowStateRepository repository)
    {
        var instance = CreateInstance();
        try
        {
            await repository.CommitWorkflowInstanceAsync(instance, 0, "winner");
            instance.WorkflowDataJson = "stale";
            var result = await repository.CommitWorkflowInstanceAsync(instance, 0, "loser");
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Conflict, 1), result);
            Assert.Equal("{}", (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.WorkflowDataJson);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    private static WorkflowInstance CreateInstance() => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"),
        WorkflowName = "commit-tests",
        WorkflowDataJson = "{}",
        Status = WorkflowStatus.Running
    };
}
