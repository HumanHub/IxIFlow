using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
public sealed class InlineRetryTests
{
    [Fact]
    public async Task RecoveryKeepsInlineDataAndRetryInputsBeforeTheNextAttempt()
    {
        var repository = new RetryRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Caller>("InlineRetryRecovery").Use<Body, Data>(u => u
            .Input(x => x.Value).From(c => 13).Output(x => x.Value).To(c => c.WorkflowData.Result)).Build();
        var id = Guid.NewGuid().ToString("N");
        await using (var first = Services(repository, registry))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Caller(), new() { InstanceId = id }));
        }
        var saved = (await repository.GetWorkflowInstanceAsync(id))!;
        var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(saved));
        Assert.Single(checkpoint.InlineFrames);
        Assert.NotNull(checkpoint.Continuations[0].RetryInputs);
        repository.FailSecondAttempt = false;
        await using var second = Services(repository, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(14, Assert.IsType<Caller>(result.WorkflowData).Result);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository, IWorkflowCatalog registry)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => o.VerifyCheckpointParts = true);
        services.AddSingleton(repository);
        services.AddSingleton(registry);
        return services.BuildServiceProvider();
    }
    private sealed class RetryRepository : InMemoryWorkflowStateRepository
    {
        public bool FailSecondAttempt { get; set; } = true;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var instance = commit.ReadInstance(commit.ReplacesParts ? null : await GetWorkflowInstanceAsync(commit.InstanceId));
            var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            if (FailSecondAttempt && checkpoint.Continuations.Any(c => c.PendingActivity?.Attempts.Count >= 2))
                throw new IOException("Interrupted before the retry intent committed");
            return await base.CommitWorkflowInstanceAsync(commit);
        }
    }
    public sealed class Caller { public int Result { get; set; } }
    public sealed class Data { public int Value { get; set; } }
    public sealed class RetryOnce : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            if (context.Attempt.Number == 1) throw new InvalidOperationException("retry");
            Value++;
            return Task.CompletedTask;
        }
    }
    public sealed class Noop : IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    [Workflow("RetryBody")] public sealed class Body : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>().Saga(s => s.Step<RetryOnce>(a => a
        .Input(x => x.Value).From(c => c.WorkflowData.Value).Output(x => x.Value).To(c => c.WorkflowData.Value)
        .OnError<InvalidOperationException>(h => h.ThenRetry(new RetryPolicy(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1, 1))))); }
}
