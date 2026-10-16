using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Each parallel branch works on its own copy of the workflow data, taken at the fork, and the join
/// merges the branches' changes (D-14, spec E-01 to E-10).
/// </summary>
[Trait("Feature", "control-flow")]
public sealed class ParallelDataTests
{
    [Fact]
    [Trait("Spec", "E-02")]
    public async Task ABranchDoesNotSeeItsSiblingsChanges()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("IsolatedBranches")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "first")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                .Do(second => second
                    .WaitFor<Go>("go")
                    .Step<Copy>(step => step
                        .Input(activity => activity.Value).From(ctx => "saw:" + ctx.WorkflowData.First)
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Second))))
            .Build();

        var started = await engine.RunAsync(definition, new Data());
        var finished = await engine.ResumeAsync(started.InstanceId, "go", new Go());

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        var data = Assert.IsType<Data>(finished.WorkflowData);
        // The second branch ran after the first had finished, yet read the data as it was at the fork.
        Assert.Equal(("first", "saw:"), (data.First, data.Second));
    }

    [Fact]
    [Trait("Spec", "E-02")]
    public async Task AWaitInABranchMatchesAgainstThatBranchsCopy()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("BranchPredicate")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first
                    .Step<Copy>(step => step
                        .Input(activity => activity.Value).From(_ => "mine")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First))
                    .WaitFor<Go>("go", (go, ctx) => go.Tag == ctx.WorkflowData.First))
                .Do(second => second.WaitFor<Go>("other")))
            .Build();
        var started = await engine.RunAsync(definition, new Data());

        // The root's data still has no tag; only the branch's copy does.
        EventAssert.Consumed(await engine.ResumeAsync(started.InstanceId, "go", new Go { Tag = "mine" }));
        var finished = await engine.ResumeAsync(started.InstanceId, "other", new Go());

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal("mine", Assert.IsType<Data>(finished.WorkflowData).First);
    }

    [Fact]
    [Trait("Spec", "E-09")]
    public async Task AConditionalJoinDecidesOnTheMergeOfTheCompletedBranches()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("ProvisionalMerge")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .WaitConditionally(data => data.First == "done")
                .Do(first => first
                    .WaitFor<Go>("first")
                    .Step<Copy>(step => step
                        .Input(activity => activity.Value).From(_ => "done")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                .Do(second => second.WaitFor<Go>("second")))
            .Build();
        var started = await engine.RunAsync(definition, new Data());

        var finished = await engine.ResumeAsync(started.InstanceId, "first", new Go());

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal("done", Assert.IsType<Data>(finished.WorkflowData).First);
    }

    [Fact]
    [Trait("Spec", "E-04")]
    public void WaitAnyBranchesMayWriteOneProperty()
    {
        var definition = Workflow.Create<Data>("AnyWrites")
            .Step<Noop>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "first")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "second")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First))))
            .Build();

        Assert.NotNull(definition);
    }

    [Fact]
    [Trait("Spec", "E-05")]
    public async Task ACombinerLetsBranchesWriteOnePropertyAndIsPartOfTheFingerprint()
    {
        using var services = Services();
        WorkflowDefinition Define<T>() where T : class, ITagged, new() => Workflow.Create<T>("Tagged")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "first")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Tag)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "second")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Tag))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(Define<FirstTag>(), new FirstTag());

        Assert.Equal("first", Assert.IsType<FirstTag>(result.WorkflowData).Tag);
        // The fingerprint covers each data type's combiners, so changing one changes the definition.
        Assert.Contains("Tag=", ParallelRules.CombinerSignature(typeof(FirstTag)));
        Assert.NotEqual(ParallelRules.CombinerSignature(typeof(FirstTag)),
            ParallelRules.CombinerSignature(typeof(LastTag)).Replace(nameof(LastTag), nameof(FirstTag)));
    }

    [Fact]
    [Trait("Spec", "E-03")]
    public void ChangesArePerPropertyForObjectsAndDictionariesAndWholeForLists()
    {
        var fork = new Shape { Owner = new Person { Name = "ann", Age = 30 }, Scores = { ["a"] = 1 }, Tags = ["x"] };
        var one = new Shape { Owner = new Person { Name = "bob", Age = 30 }, Scores = { ["a"] = 1, ["b"] = 2 }, Tags = ["x"] };
        var two = new Shape { Owner = new Person { Name = "ann", Age = 31 }, Scores = { ["a"] = 5 }, Tags = ["x", "y"] };

        var merged = (Shape)BranchData.Merge(fork, [BranchData.Changes(fork, one), BranchData.Changes(fork, two)]);

        Assert.Equal(("bob", 31), (merged.Owner.Name, merged.Owner.Age));
        Assert.Equal(new Dictionary<string, int> { ["a"] = 5, ["b"] = 2 }, merged.Scores);
        Assert.Equal(["x", "y"], merged.Tags);
    }

    [Fact]
    [Trait("Spec", "E-03")]
    public void APropertyAndOneInsideItChangedByTwoBranchesConflict()
    {
        var fork = new Shape { Owner = new Person { Name = "ann" } };
        var replaced = new Shape { Owner = null! };
        var renamed = new Shape { Owner = new Person { Name = "bob" } };
        var addedTag = new Shape { Owner = new Person { Name = "ann" }, Tags = ["x"] };
        var otherTag = new Shape { Owner = new Person { Name = "ann" }, Tags = ["y"] };

        Assert.Equal("Owner", Assert.Throws<JoinConflictException>(() => BranchData.Merge(fork,
            [BranchData.Changes(fork, replaced), BranchData.Changes(fork, renamed)])).Path);
        // A list is one value, so two branches changing it conflict.
        Assert.Equal("Tags", Assert.Throws<JoinConflictException>(() => BranchData.Merge(fork,
            [BranchData.Changes(fork, addedTag), BranchData.Changes(fork, otherTag)])).Path);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public string First { get; set; } = "";
        public string Second { get; set; } = "";
    }

    public sealed class Shape
    {
        public Person Owner { get; set; } = new();
        public Dictionary<string, int> Scores { get; set; } = [];
        public List<string> Tags { get; set; } = [];
    }

    public sealed class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    public interface ITagged
    {
        string Tag { get; set; }
    }

    public sealed class FirstTag : ITagged
    {
        [Combine<FirstWins<string>>]
        public string Tag { get; set; } = "";
    }

    public sealed class LastTag : ITagged
    {
        [Combine<LastWinsForTest>]
        public string Tag { get; set; } = "";
    }

    public sealed class LastWinsForTest : ICombiner<string>
    {
        public string Combine(string original, IReadOnlyList<string> branchValues) => branchValues[^1];
    }

    public sealed class Go
    {
        public string Tag { get; set; } = "";
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class Copy : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
