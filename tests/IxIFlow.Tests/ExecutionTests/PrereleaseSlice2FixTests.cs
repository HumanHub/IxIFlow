using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability"), Trait("Spec", "D-18")]
public sealed class PrereleaseSlice2FixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownReceiptsResolveWithoutAdvancingTheWorkflowClock(bool applied)
    {
        var clock = new FakeTimeProvider();
        var host = new ReceiptHost(applied);
        var definition = Workflow.Create<Data>("UnknownReceipt").Build();
        var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data(), new(), clock.GetUtcNow().UtcDateTime);
        var session = new ExecutionSession(host, Guid.NewGuid(), instance.InstanceId, 1, ExecutionLimits.Default, false, clock);

        var send = ExecutionCheckpointWriter.CommitAsync(session, instance, checkpoint, new Data(),
            WorkflowDefinitionValidator.ForExecution(definition), clock.GetUtcNow().UtcDateTime);

        if (applied)
            await send.WaitAsync(TimeSpan.FromSeconds(5));
        else
            await Assert.ThrowsAsync<CheckpointInterruptedException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(applied ? 2 : 3, host.Commits.Count);
        Assert.Single(host.Commits.Select(commit => commit.CommitId).Distinct());
        Assert.Single(host.Commits.Select(commit => Convert.ToHexString(commit.ContentHash)).Distinct());
    }

    [Theory]
    [InlineData("Enumerable.Range(0, 2).All(__started => __started >= 0)")]
    [InlineData("Enumerable.Range(0, 2).All(__remaining => __remaining >= 0)")]
    public void BudgetLocalsDoNotChangeTheBindingOfUserLambdaParameters(string expression)
    {
        using var compiler = new IxIFlow.Debugging.BreakpointConditionCompiler();
        var compiled = compiler.Compile(new("shape", expression, typeof(Data)));
        Assert.Empty(compiled.Diagnostics);
        Assert.True(compiled.Evaluate!(new Data(), null, null, null, DateTimeOffset.UtcNow));
    }

    public sealed class Data;

    private sealed class ReceiptHost(bool applied) : IExecutionHost
    {
        public List<CheckpointCommit> Commits { get; } = [];

        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            Commits.Add(request.Commit);

            return Task.FromResult(new CheckpointReply(request.AssignmentId,
                applied && Commits.Count > 1 ? CheckpointReplyKind.Ack : CheckpointReplyKind.Unknown,
                request.Commit.ExpectedRevision + 1));
        }

        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => throw new NotSupportedException();
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => throw new NotSupportedException();
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) => throw new NotSupportedException();
    }
}