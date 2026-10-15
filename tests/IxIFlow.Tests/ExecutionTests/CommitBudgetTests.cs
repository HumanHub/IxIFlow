using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Reference scenarios have an exact commit count and a checkpoint size ceiling, so a change that
/// adds or loses commits, or grows checkpoints, fails here first (spec J-04). Commits happen only at effect
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
    public async Task LoopOfActivities()
    {
        var definition = Workflow.Create<BudgetData>("BudgetLoop")
            .Step<WorkActivity>()
            .WhileDo(ctx => ctx.WorkflowData.Count < 3, body => body
                .Step<CountActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Count).To(ctx => ctx.WorkflowData.Count)))
            .Build();

        var (commits, bytes) = await MeasureAsync(definition);

        AssertBudget(commits, bytes, maxCommits: 10, maxBytes: 2100);
    }

    [Fact]
    public async Task ChildInvocation()
    {
        var store = new MeasuringStore();
        using var services = Services(store);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<BudgetData>("BudgetChild").Step<WorkActivity>().Build());
        var definition = Workflow.Create<BudgetData>("BudgetParent")
            .Step<WorkActivity>()
            .Invoke<BudgetData>("BudgetChild", 1)
            .Step<WorkActivity>()
            .Build();

        var result = await Engine(services).RunAsync(definition, new BudgetData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);

        // Parent and child commits together.
        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 12, maxBytes: 1500);
    }

    [Fact]
    [Trait("Spec", "C-03")]
    public async Task RetryParkedOnALongBackoff()
    {
        var store = new MeasuringStore();
        using var services = Services(store);
        var definition = Workflow.Create<BudgetData>("BudgetParkedRetry")
            .Step<WorkActivity>()
            .Saga(saga => saga.Step<FailOnceActivity>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(new RetryPolicy(
                    TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1200), 1, 1)))))
            .Build();

        var result = await Engine(services).RunAsync(definition, new BudgetData());

        // The park is one commit of its own; resuming adds none before the retried activity starts.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 9, maxBytes: 2300);
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

        var started = await Engine(services).RunAsync(definition, new BudgetData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var resumed = await Engine(services).ResumeAsync(started.InstanceId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);

        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 8, maxBytes: 1400);
    }

    // Commit counts are exact, so a lost commit fails as well as an added one. Byte ceilings are the
    // measured size plus about 10%: timestamps vary by a few bytes. Measured on
    // 2026-10-15: sequence 1301, control flow 1712, parallel 4302, caught failure 2811, loop 1902,
    // child invocation 1337, wait and resume 1299, parked retry 2072.
    private static void AssertBudget(int commits, long bytes, int maxCommits, long maxBytes)
    {
        Assert.True(commits == maxCommits, $"{commits} commits; the scenario commits exactly {maxCommits}");
        Assert.True(bytes <= maxBytes, $"{bytes} checkpoint bytes; the ceiling is {maxBytes}");
    }

    private static async Task<(int Commits, long Bytes)> MeasureAsync(WorkflowDefinition definition)
    {
        var store = new MeasuringStore();
        using var services = Services(store);
        var result = await Engine(services).RunAsync(definition, new BudgetData());
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
        services.AddTransient<CountActivity>();
        services.AddTransient<FailActivity>();
        services.AddTransient<FailOnceActivity>();
        services.AddSingleton<FailOnceProbe>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

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

    public sealed class CountActivity : IAsyncActivity
    {
        public int Count { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    public sealed class FailOnceProbe
    {
        private int _runs;

        public bool FirstRun() => Interlocked.Increment(ref _runs) == 1;
    }

    /// <summary>Fails its first run, then succeeds.</summary>
    public sealed class FailOnceActivity(FailOnceProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            probe.FirstRun() ? throw new InvalidOperationException("Planned failure") : Task.CompletedTask;
    }

    public sealed class FailActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Planned failure");
    }
}
