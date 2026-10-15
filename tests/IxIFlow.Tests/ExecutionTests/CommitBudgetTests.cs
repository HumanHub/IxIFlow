using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Reference scenarios have a commit budget and a checkpoint size budget, so a change that adds
/// commits or grows checkpoints fails here first (spec J-04). Commits happen only at effect
/// boundaries: the start, each activity start and end, a saved wait, a completed join and the end
/// (spec A-04).
/// </summary>
[Trait("Feature", "durability")]
[Trait("Spec", "J-04")]
public sealed class CommitBudgetTests
{
    [Fact]
    public async Task SequenceOfActivities()
    {
        var definition = Workflow.Create<BudgetData>("BudgetSequence")
            .Step<WorkActivity>()
            .Step<WorkActivity>()
            .Step<WorkActivity>()
            .Build();

        var (commits, bytes) = await MeasureAsync(definition);

        AssertBudget(commits, bytes, maxCommits: 8, maxBytes: 1400);
    }

    [Fact]
    public async Task ControlFlowBetweenActivities()
    {
        var definition = Workflow.Create<BudgetData>("BudgetControlFlow")
            .Step<WorkActivity>()
            .If(ctx => ctx.WorkflowData.Count >= 0,
                then => then.Step<WorkActivity>(),
                @else => @else.Step<WorkActivity>())
            .Step<WorkActivity>()
            .Build();

        var (commits, bytes) = await MeasureAsync(definition);

        AssertBudget(commits, bytes, maxCommits: 8, maxBytes: 1900);
    }

    [Fact]
    public async Task ParallelBranches()
    {
        var definition = Workflow.Create<BudgetData>("BudgetParallel")
            .Step<WorkActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<WorkActivity>())
                .Do(branch => branch.Step<WorkActivity>())
                .Do(branch => branch.Step<WorkActivity>()))
            .Step<WorkActivity>()
            .Build();

        var (commits, bytes) = await MeasureAsync(definition);

        AssertBudget(commits, bytes, maxCommits: 12, maxBytes: 4700);
    }

    [Fact]
    public async Task CaughtFailure()
    {
        var definition = Workflow.Create<BudgetData>("BudgetCatch")
            .Step<WorkActivity>()
            .Try(body => body.Step<FailActivity>())
            .Catch<InvalidOperationException>(handler => handler.Step<WorkActivity>())
            .Step<WorkActivity>()
            .Build();

        var (commits, bytes) = await MeasureAsync(definition);

        AssertBudget(commits, bytes, maxCommits: 10, maxBytes: 3100);
    }

    [Fact]
    public async Task WaitAndResume()
    {
        var store = new MeasuringStore();
        using var services = Services(store);
        var definition = Workflow.Create<BudgetData>("BudgetWait")
            .Step<WorkActivity>()
            .WaitFor<Approval>("approval")
            .Step<WorkActivity>()
            .Build();

        var started = await Engine(services).ExecuteWorkflowAsync(definition, new BudgetData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var resumed = await Engine(services).ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);

        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 8, maxBytes: 1400);
    }

    // Byte budgets are the measured size plus about 10%: timestamps vary by a few bytes.
    private static void AssertBudget(int commits, long bytes, int maxCommits, long maxBytes)
    {
        Assert.True(commits <= maxCommits, $"{commits} commits; the budget is {maxCommits}");
        Assert.True(bytes <= maxBytes, $"{bytes} checkpoint bytes; the budget is {maxBytes}");
    }

    private static async Task<(int Commits, long Bytes)> MeasureAsync(WorkflowDefinition definition)
    {
        var store = new MeasuringStore();
        using var services = Services(store);
        var result = await Engine(services).ExecuteWorkflowAsync(definition, new BudgetData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        return (store.Commits, store.MaxBytes);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow();
        services.AddTransient<WorkActivity>();
        services.AddTransient<FailActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowEngine Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowEngine>();

    /// <summary>Counts applied commits and the largest checkpoint.</summary>
    private sealed class MeasuringStore : FaultInjectingRepository
    {
        private int _commits;
        private long _maxBytes;

        public int Commits => Volatile.Read(ref _commits);
        public long MaxBytes => Interlocked.Read(ref _maxBytes);

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.Applied)
            {
                Interlocked.Increment(ref _commits);
                var bytes = ExecutionCheckpointWriter.CheckpointBytes(commit.ReadInstance());
                long seen;
                while (bytes > (seen = Interlocked.Read(ref _maxBytes)) &&
                       Interlocked.CompareExchange(ref _maxBytes, bytes, seen) != seen)
                {
                }
            }
            return result;
        }
    }

    public sealed class BudgetData
    {
        public int Count { get; set; }
    }

    public sealed class Approval;

    public sealed class WorkActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class FailActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Planned failure");
    }
}
