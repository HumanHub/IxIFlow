using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

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
    public async Task ACombinerLetsBranchesWriteOnePropertyAndIsPartOfTheStructureHash()
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

        var merged = MergeInto(fork, one, two);

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

        Assert.Equal("Owner", Assert.Throws<JoinConflictException>(() => MergeInto(fork, replaced, renamed)).Path);
        // A list is one value, so two branches changing it conflict.
        Assert.Equal("Tags", Assert.Throws<JoinConflictException>(() => MergeInto(fork, addedTag, otherTag)).Path);
    }

    // Merges branch copies, in order, into the fork's own object, as a join does.
    internal static T MergeInto<T>(T fork, params T[] branches) where T : class
    {
        var merge = BranchData.Merge(fork, branches
            .Select(branch => new MergingBranch(BranchData.Changes(fork, branch), () => branch)).ToList());
        BranchData.Apply(fork, merge);
        return fork;
    }

    [Fact]
    [Trait("Spec", "E-07")]
    public async Task AJoinChangesTheDataObjectInPlaceAndKeepsWhatNoBranchChanged()
    {
        using var services = Services();
        var definition = Workflow.Create<Data>("InPlaceMerge")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "first")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                .Do(second => second.Step<ClearNote>(step => step
                    .Output(activity => activity.Note).To(ctx => ctx.WorkflowData.Note))))
            .Build();
        var data = new Data { Transient = "kept" };

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, data);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Same(data, result.WorkflowData);
        // A member never saved survives the join, and a value a branch set to null is applied as null.
        Assert.Equal(("first", "kept", null), (data.First, data.Transient, data.Note));
    }

    [Fact]
    [Trait("Spec", "E-08")]
    public async Task AParallelInsideCleanupMergesWhileASiblingsFailureUnwinds()
    {
        using var services = Services();
        var definition = Workflow.Create<Data>("CleanupJoin")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(failing => failing.Step<Fail>())
                .Do(cleaning => cleaning
                    .Try(body => body.WaitFor<Go>("never"))
                    .Finally(cleanup => cleanup
                        .Parallel(inner => inner
                            .Do(first => first.Step<Copy>(step => step
                                .Input(activity => activity.Value).From(_ => "a")
                                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                            .Do(second => second.Step<Copy>(step => step
                                .Input(activity => activity.Value).From(_ => "b")
                                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Second))))
                        .Step<Record>(step => step
                            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.First + ctx.WorkflowData.Second)))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        // The cleanup's own join merged into its branch's copy, so the step after it read both values.
        Assert.Equal(["ab"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-08")]
    public async Task JoinsNestedDeeperInsideCleanupRunAndMergeWhileASiblingsFailureUnwinds()
    {
        using var services = Services();
        var definition = Workflow.Create<Data>("DeepCleanupJoin")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(failing => failing.Step<Fail>())
                .Do(cleaning => cleaning
                    .Try(body => body.WaitFor<Go>("never"))
                    .Finally(cleanup => cleanup
                        .Parallel(outer => outer
                            .Do(deep => deep
                                .Parallel(inner => inner
                                    .Do(first => first.Step<Copy>(step => step
                                        .Input(activity => activity.Value).From(_ => "a")
                                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                                    .Do(second => second.Step<Copy>(step => step
                                        .Input(activity => activity.Value).From(_ => "b")
                                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Second))))
                                .Step<Record>(step => step
                                    .Input(activity => activity.Value)
                                    .From(ctx => ctx.WorkflowData.First + ctx.WorkflowData.Second)))
                            .Do(other => other.Step<Record>(step => step
                                .Input(activity => activity.Value).From(_ => "other")))))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        // A branch forked inside the cleanup, two joins down, is part of it: its join merged, and the
        // step after that join ran.
        Assert.Equal(["ab", "other"], services.GetRequiredService<Seen>().Values.Order());
    }

    [Fact]
    [Trait("Spec", "E-02")]
    public async Task EachBranchReadsItsOwnCopyOfThePreviousStep()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("OwnPrevious")
            .Step<MakeList>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Grow>(step => step
                    .Input(activity => activity.Items).From(ctx => ctx.PreviousStep.Items)))
                .Do(second => second
                    .Delay(TimeSpan.FromMinutes(1))
                    .Step<Record>(step => step
                        .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Items.Count.ToString()))))
            .Build();

        var started = await engine.RunAsync(definition, new Data());
        // The second branch reads the previous step only after its delay, once the first branch has run.
        time.Advance(TimeSpan.FromMinutes(2));
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        // The first branch grew its list; the second still sees the list as it was at the fork.
        Assert.Equal(["1"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-04")]
    public void AWriteIntoAListWritesTheListAndAWriteIntoADictionaryIsMergedPerKey()
    {
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Indexed>("ListElements")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "a")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Rows[0].Value)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "b")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Rows[1].Value))))
            .Build());

        var definition = Workflow.Create<Indexed>("DictionaryKeys")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "a")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.ByName["first"].Value)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "b")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.ByName["second"].Value))))
            .Build();
        Assert.NotNull(definition);
    }

    [Fact]
    [Trait("Spec", "E-05")]
    public void ACombinerMustFitItsProperty()
    {
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<MisfitCombiner>("Misfit")
            .Step<Noop>()
            .Build());
    }

    [Fact]
    [Trait("Spec", "E-05")]
    public async Task ACombinerImplementedExplicitlyMergesTheBranches()
    {
        using var services = Services();
        var definition = Workflow.Create<Explicit>("ExplicitCombiner")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "a")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Tag)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "b")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Tag))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Explicit());

        Assert.Equal("a+b", Assert.IsType<Explicit>(result.WorkflowData).Tag);
    }

    private static ServiceProvider Services(TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (time != null)
            services.AddSingleton(time);
        services.AddSingleton<Seen>();
        services.AddTransient<Record>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public string First { get; set; } = "";
        public string Second { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; } = "default";

        [System.Text.Json.Serialization.JsonIgnore]
        public string Transient { get; set; } = "";
    }

    public sealed class Indexed
    {
        public List<Row> Rows { get; set; } = [new(), new()];
        public Dictionary<string, Row> ByName { get; set; } = [];
    }

    public sealed class Row
    {
        public string Value { get; set; } = "";
    }

    public sealed class MisfitCombiner
    {
        [Combine<FirstWins<string>>]
        public int Count { get; set; }
    }

    public sealed class Explicit
    {
        [Combine<JoinWithPlus>]
        public string Tag { get; set; } = "";
    }

    public sealed class JoinWithPlus : ICombiner<string>
    {
        string ICombiner<string>.Combine(string original, IReadOnlyList<string> branchValues) =>
            string.Join('+', branchValues);
    }

    public sealed class Seen
    {
        private readonly List<string> _values = [];

        public IReadOnlyList<string> Values
        {
            get { lock (_values) return _values.ToArray(); }
        }

        public void Add(string value)
        {
            lock (_values)
                _values.Add(value);
        }
    }

    public sealed class Record(Seen seen) : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(Value);
            return Task.CompletedTask;
        }
    }

    public sealed class ClearNote : IAsyncActivity
    {
        public string? Note { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("branch failed");
    }

    public sealed class MakeList : IAsyncActivity
    {
        public List<string> Items { get; set; } = ["a"];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class Grow : IAsyncActivity
    {
        public List<string> Items { get; set; } = [];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Items.Add("grown");
            return Task.CompletedTask;
        }
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
