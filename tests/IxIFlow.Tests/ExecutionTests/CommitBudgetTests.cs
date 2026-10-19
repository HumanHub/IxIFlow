using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Reference scenarios have an exact commit count and a ceiling on the bytes a commit carries, so a change that
/// adds or loses commits, or grows them, fails here first (spec J-04). Commits happen only at effect
/// boundaries: the start, each activity start and end, a saved wait, a completed join and the end
/// (spec A-04). A commit carries the instance's trunk and only the parts of its state that changed (A-08).
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

        AssertBudget(commits, bytes, maxCommits: 8, maxBytes: 2200);
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

        AssertBudget(commits, bytes, maxCommits: 8, maxBytes: 2700);
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

    // Branches that write save their changes in every commit until the join (D-14, spec E-01).
    [Fact]
    [Trait("Spec", "E-01")]
    public async Task ParallelBranchesThatWrite()
    {
        var definition = Workflow.Create<WritingData>("BudgetParallelWrites")
            .Step<WorkActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<NoteActivity>(step => step
                    .Input(activity => activity.Note).From(_ => "first branch")
                    .Output(activity => activity.Note).To(ctx => ctx.WorkflowData.First)))
                .Do(branch => branch.Step<NoteActivity>(step => step
                    .Input(activity => activity.Note).From(_ => "second branch")
                    .Output(activity => activity.Note).To(ctx => ctx.WorkflowData.Second)))
                .Do(branch => branch.Step<NoteActivity>(step => step
                    .Input(activity => activity.Note).From(_ => "third branch")
                    .Output(activity => activity.Note).To(ctx => ctx.WorkflowData.Third))))
            .Step<WorkActivity>()
            .Build();

        var store = new MeasuringStore();
        using var services = Services(store);
        var result = await Engine(services).RunAsync(definition, new WritingData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 12, maxBytes: 5200);
        Assert.True(store.TotalBytes <= 30000, $"{store.TotalBytes} bytes written in all; the ceiling is 30000");
    }

    // D-34: an uninterrupted parallel ForEach with one activity per item and WaitAll takes 2N + 2 commits
    // for N items, including the workflow's start and end.
    [Fact]
    [Trait("Spec", "E-12")]
    public async Task ParallelForEach()
    {
        var definition = Workflow.Create<ItemsData>("BudgetParallelForEach")
            .Step<WorkActivity>()
            .ForEach(ctx => ctx.WorkflowData.Items, (body, _) => body.Step<WorkActivity>(),
                setup => setup.Parallel())
            .Build();

        var store = new MeasuringStore();
        using var services = Services(store);
        var result = await Engine(services).RunAsync(definition, new ItemsData { Items = ["a", "b", "c"] });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        // 2N + 2 for the ForEach and the workflow, and two for the activity before it.
        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 2 * 3 + 2 + 2, maxBytes: 4900);
        Assert.True(store.TotalBytes <= 28000, $"{store.TotalBytes} bytes written in all; the ceiling is 28000");
    }

    // J-04 at scale: a commit that moves an item carries the trunk and that item's branch, however many items
    // there are. Only three commits carry O(N): the start, whose data holds the list; the first after the fork, which
    // writes every item's branch; and the end, which completes the join, merging the items and deleting their
    // branches. In all, the bytes stay under c·N.
    [Theory]
    [Trait("Spec", "E-12")]
    [InlineData(100)]
    [InlineData(400)]
    public async Task ParallelForEachAtScale(int items)
    {
        var definition = Workflow.Create<ItemsData>($"BudgetParallelForEach{items}")
            .Step<WorkActivity>()
            .ForEach(ctx => ctx.WorkflowData.Items, (body, _) => body.Step<WorkActivity>(),
                setup => setup.Parallel())
            .Build();

        var store = new MeasuringStore();
        using var services = Services(store);
        var result = await Engine(services).RunAsync(definition,
            new ItemsData { Items = Enumerable.Range(0, items).Select(index => $"item-{index}").ToList() });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var commits = store.Applied;
        // 2N + 2 for the ForEach and the workflow, and two for the activity before it.
        Assert.Equal(2 * items + 4, commits.Count);
        var fork = commits.ToList().FindIndex(commit => commit.PartsWritten > 2);
        Assert.True(fork > 0 && fork < commits.Count - 1, "The fork's branches are written by a commit of their own");
        var perItem = commits.Where((_, index) => index != 0 && index != fork && index != commits.Count - 1).ToArray();
        Assert.All(perItem, commit => Assert.Equal(0, commit.PartsDeleted));
        Assert.True(perItem.Max(commit => commit.Bytes) <= ItemCommitCeiling,
            $"An item's commit carried {perItem.Max(commit => commit.Bytes)} bytes; the ceiling is {ItemCommitCeiling}");
        // A commit writes what moved: at most one branch whose activity ended, one whose activity started, and the
        // chunk of finished items the ending one is in.
        Assert.True(perItem.Max(commit => commit.PartsWritten) <= 3,
            $"An item's commit wrote {perItem.Max(commit => commit.PartsWritten)} parts; only what moved");
        Assert.True(store.TotalBytes <= BytesPerItem * items,
            $"{store.TotalBytes} bytes written in all; the ceiling is {BytesPerItem} per item");
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

        AssertBudget(commits, bytes, maxCommits: 10, maxBytes: 3900);
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

        AssertBudget(commits, bytes, maxCommits: 10, maxBytes: 2900);
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

        var result = await Engine(services).RunToEndAsync(definition, new BudgetData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);

        // Parent and child commits together. Starting the child adds no commit of its own: it rides in
        // the parent's idle commit, and the child's completion in its terminal one (D-17). The parent's
        // checkpoint holds its wait for the child beside the invocation, the child's its parent link.
        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 12, maxBytes: 2800);
    }

    [Fact]
    [Trait("Spec", "C-03")]
    public async Task RetryParkedOnALongBackoff()
    {
        var time = new FakeTimeProvider();
        var store = new MeasuringStore(time);
        using var services = Services(store, time);
        var definition = Workflow.Create<BudgetData>("BudgetParkedRetry")
            .Step<WorkActivity>()
            .Saga(saga => saga.Step<FailOnceActivity>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(new RetryPolicy(
                    TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1200), 1, 1)))))
            .Build();
        var instanceId = Guid.NewGuid().ToString("N");

        var running = Engine(services).RunAsync(definition, new BudgetData(),
            new WorkflowOptions { InstanceId = instanceId });
        // The retry is parked once the instance is due later than now; then its backoff passes.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (await store.GetWorkflowInstanceAsync(instanceId) is not
               { Status: WorkflowStatus.Running, RunnableAtUtc: { } due } || due <= time.GetUtcNow().UtcDateTime)
        {
            Assert.True(DateTime.UtcNow < deadline, "The retry was never parked");
            await Task.Delay(10);
        }
        time.Advance(TimeSpan.FromMilliseconds(1300));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        // The park is one commit of its own; resuming adds none before the retried activity starts.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 9, maxBytes: 3350);
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

        AssertBudget(store.Commits, store.MaxBytes, maxCommits: 8, maxBytes: 2300);
    }

    // The largest commit of one item of a parallel ForEach, and the bytes written per item, whatever the count:
    // measured on 2026-10-16 at 3,034 (100 items), 3,109 (400) and 3,213 (1,000) bytes per item's commit, and
    // 6.6 MB in all at 1,000 items.
    private const long ItemCommitCeiling = 3400;
    private const long BytesPerItem = 7300;

    // Commit counts are exact, so a lost commit fails as well as an added one. Byte ceilings are the
    // measured size plus about 10%: timestamps vary by a few bytes. A commit's bytes are its payload, the
    // instance's fields and trunk, and the parts it writes. Measured on 2026-10-16: sequence 2020, control flow
    // 2434, caught failure 3527, loop 2617, child invocation 2503, wait and resume 2069, parked retry 3038.
    private static void AssertBudget(int commits, long bytes, int maxCommits, long maxBytes)
    {
        Assert.True(commits == maxCommits, $"{commits} commits; the scenario commits exactly {maxCommits}");
        Assert.True(bytes <= maxBytes, $"{bytes} bytes in the largest commit; the ceiling is {maxBytes}");
    }

    private static async Task<(int Commits, long Bytes)> MeasureAsync(WorkflowDefinition definition)
    {
        var store = new MeasuringStore();
        using var services = Services(store);
        var result = await Engine(services).RunAsync(definition, new BudgetData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        return (store.Commits, store.MaxBytes);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        if (time != null)
            services.AddSingleton(time);
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

    /// <summary>Records, for each applied commit, the bytes of state it carried and the parts it wrote and deleted.</summary>
    private sealed class MeasuringStore : FaultInjectingRepository
    {
        private readonly ConcurrentQueue<(long Bytes, int PartsWritten, int PartsDeleted)> _applied = new();

        public MeasuringStore()
        {
        }

        public MeasuringStore(TimeProvider time) : base(time)
        {
        }

        public IReadOnlyList<(long Bytes, int PartsWritten, int PartsDeleted)> Applied => _applied.ToArray();
        public int Commits => _applied.Count;
        public long MaxBytes => _applied.Max(commit => commit.Bytes);
        public long TotalBytes => _applied.Sum(commit => commit.Bytes);

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.Applied)
                _applied.Enqueue((commit.StateBytes(), commit.Parts.Count, commit.DeletedParts.Count));
            return result;
        }
    }

    public sealed class BudgetData
    {
        public int Count { get; set; }
    }

    public sealed class ItemsData
    {
        public List<string> Items { get; set; } = [];
    }

    public sealed class WritingData
    {
        public string First { get; set; } = "";
        public string Second { get; set; } = "";
        public string Third { get; set; } = "";
    }

    public sealed class NoteActivity : IAsyncActivity
    {
        public string Note { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
