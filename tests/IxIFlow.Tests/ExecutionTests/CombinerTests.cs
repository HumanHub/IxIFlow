using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The built-in combiners and a parallel step's own choice of combiner (D-14, spec E-05, E-06).
/// </summary>
[Trait("Feature", "control-flow")]
[Trait("Spec", "E-06")]
public sealed class CombinerTests
{
    [Fact]
    public void ScalarCombinersPickOrAddTheBranchValues()
    {
        Assert.Equal(9, new Max<int>().Combine(5, [3, 9, 7]));
        Assert.Equal(3, new Min<int>().Combine(5, [3, 9, 7]));
        Assert.Equal("c", new LastWins<string>().Combine("a", ["b", "c"]));
        Assert.Equal("b", new FirstWins<string>().Combine("a", ["b", "c"]));
        Assert.Equal(8, new Sum<int>().Combine(5, [6, 7]));
    }

    [Fact]
    public void AppendKeepsTheListAtTheForkAndAddsEachBranchsItemsInOrder()
    {
        var append = new Append<string>();

        Assert.Equal(["a", "b", "c", "d"], append.Combine(["a"], [["a", "b"], ["a", "c", "d"]]));
        Assert.Throws<InvalidOperationException>(() => append.Combine(["a"], [["x", "b"]]));
    }

    [Fact]
    public void UnionMergesDictionariesPerKeyAndRefusesTheSameKeyChangedDifferently()
    {
        var union = new Union<string, int>();
        var original = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

        Assert.Equal(new Dictionary<string, int> { ["a"] = 5, ["c"] = 3 },
            union.Combine(original, [new() { ["a"] = 5, ["b"] = 2 }, new() { ["a"] = 1, ["c"] = 3 }]));
        Assert.Throws<InvalidOperationException>(() => union.Combine(original,
            [new() { ["a"] = 5, ["b"] = 2 }, new() { ["a"] = 6, ["b"] = 2 }]));
    }

    [Fact]
    public void UnionMergesSetsByWhatEachBranchAddedAndRemoved()
    {
        Assert.Equal(["b", "c", "d"], new Union<string>().Combine(["a", "b"], [["b", "c"], ["a", "b", "d"]]).Order());
    }

    [Fact]
    [Trait("Spec", "E-05")]
    public async Task AParallelStepChoosesACombinerForAProperty()
    {
        using var services = Services();
        var definition = Counted(parallel => parallel.Combine<int, Max<int>>(data => data.Score));

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Scores());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(9, Assert.IsType<Scores>(result.WorkflowData).Score);
        // Without the step's choice, the two branches writing Score are a build error.
        Assert.Throws<WorkflowEngineException>(() => Counted(_ => { }));
        // The choice is part of the fingerprint.
        Assert.NotEqual(new WorkflowScopeCatalog(definition).Fingerprint,
            new WorkflowScopeCatalog(Counted(parallel => parallel.Combine<int, Min<int>>(data => data.Score))).Fingerprint);
    }

    [Fact]
    [Trait("Spec", "E-05")]
    public async Task ACombinerThatCannotMergeFailsTheJoinWithAConflict()
    {
        using var services = Services();
        var definition = Workflow.Create<Scores>("BadAppend")
            .Step<Noop>()
            .Try(attempt => attempt.Parallel(parallel => parallel
                .Do(first => first.Step<SetNames>(step => step
                    .Input(activity => activity.Names).From(_ => new List<string> { "x" })
                    .Output(activity => activity.Names).To(ctx => ctx.WorkflowData.Names)))
                .Do(second => second.Step<SetNames>(step => step
                    .Input(activity => activity.Names).From(_ => new List<string> { "a", "b" })
                    .Output(activity => activity.Names).To(ctx => ctx.WorkflowData.Names)))))
            .Catch<JoinConflictException>(handler => handler.Step<SetScore>(step => step
                .Input(activity => activity.Score).From(_ => -1)
                .Output(activity => activity.Score).To(ctx => ctx.WorkflowData.Score)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new Scores { Names = ["a"] });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Scores>(result.WorkflowData);
        Assert.Equal(-1, data.Score);
        Assert.Equal(["a"], data.Names);
    }

    private static WorkflowDefinition Counted(Action<Builders.Interfaces.IParallelBuilder<Scores, Noop>> choose) =>
        Workflow.Create<Scores>("ChosenCombiner")
            .Step<Noop>()
            .Parallel(parallel =>
            {
                choose(parallel);
                parallel
                    .Do(first => first.Step<SetScore>(step => step
                        .Input(activity => activity.Score).From(_ => 9)
                        .Output(activity => activity.Score).To(ctx => ctx.WorkflowData.Score)))
                    .Do(second => second.Step<SetScore>(step => step
                        .Input(activity => activity.Score).From(_ => 4)
                        .Output(activity => activity.Score).To(ctx => ctx.WorkflowData.Score)));
            })
            .Build();

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class Scores
    {
        public int Score { get; set; }

        [Combine<Append<string>>]
        public List<string> Names { get; set; } = [];
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class SetScore : IAsyncActivity
    {
        public int Score { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class SetNames : IAsyncActivity
    {
        public List<string> Names { get; set; } = [];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
