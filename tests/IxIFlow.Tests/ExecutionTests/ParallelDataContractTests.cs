using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Branch copies and the join see the workflow data as System.Text.Json does (D-14, spec E-01, E-03,
/// E-07): computed properties are not branch data, polymorphic values are merged whole, a property left
/// out when null reads back null, state with no setter must be read back into its object, and a merge the
/// data type refuses fails the join, which a <c>Catch</c> handles, with the data left as it was.
/// </summary>
[Trait("Feature", "control-flow")]
[Trait("Spec", "E-03")]
public sealed class ParallelDataContractTests
{
    [Fact]
    public async Task AComputedPropertyIsNotBranchData()
    {
        using var services = Services();
        var definition = Workflow.Create<Signed>("ComputedCount")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Sign>(step => step
                    .Input(activity => activity.Name).From(_ => "ann")
                    .Input(activity => activity.Signatures).From(ctx => ctx.WorkflowData.Signatures)
                    .Output(activity => activity.Signatures).To(ctx => ctx.WorkflowData.Signatures)))
                .Do(second => second.Step<Sign>(step => step
                    .Input(activity => activity.Name).From(_ => "bob")
                    .Input(activity => activity.Signatures).From(ctx => ctx.WorkflowData.Signatures)
                    .Output(activity => activity.Signatures).To(ctx => ctx.WorkflowData.Signatures))))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Signed());

        // Both branches changed the serialized Count too; it follows the merged list instead of conflicting.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ann", "bob"], Assert.IsType<Signed>(result.WorkflowData).Signatures);
    }

    [Fact]
    [Trait("Spec", "E-01")]
    public void StateWithNoSetterMustBeReadBackIntoItsObjectWhenBranchesCopyIt()
    {
        WorkflowDefinition Define(bool parallel) => parallel
            ? Workflow.Create<Tagged>("GetOnlyListInBranches")
                .Step<Noop>()
                .Parallel(branches => branches.Do(only => only.Step<Noop>()))
                .Build()
            : Workflow.Create<Tagged>("GetOnlyListInSequence").Step<Noop>().Build();

        var refused = Assert.Throws<WorkflowEngineException>(() => Define(parallel: true));
        Assert.Contains("'Tags' has no setter", refused.Message);
        // Without branches nothing is copied, so the same data type is fine.
        Define(parallel: false);
    }

    [Fact]
    [Trait("Spec", "E-01")]
    public async Task AnObjectWithNoSetterThatIsReadBackIntoItselfIsMergedInPlace()
    {
        using var services = Services();
        var definition = Workflow.Create<Populated>("PopulatedTotals")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 3)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Totals.A)))
                .Do(second => second.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 4)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Totals.B))))
            .Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx => $"{ctx.WorkflowData.Totals.A}+{ctx.WorkflowData.Totals.B}"))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Populated());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["3+4"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    public async Task APolymorphicValueIsMergedWholeAndMayChangeItsType()
    {
        using var services = Services();
        var definition = Workflow.Create<Drawing>("ReplacedFigure")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<MakeSquare>(step => step
                    .Output(activity => activity.Figure).To(ctx => ctx.WorkflowData.Figure)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "noted")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Note))))
            .Step<Record>(step => step
                .Input(activity => activity.Value)
                .From(ctx => ctx.WorkflowData.Figure.GetType().Name + ":" + ctx.WorkflowData.Note))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Drawing());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["Square:noted"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-04")]
    public void TwoBranchesWritingInsideOnePolymorphicValueWriteThatValue()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Drawing>("BothResize")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 2)
                    .Output(activity => activity.Value).To(ctx => ((Circle)ctx.WorkflowData.Figure).Radius)))
                .Do(second => second.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 3)
                    .Output(activity => activity.Value).To(ctx => ((Circle)ctx.WorkflowData.Figure).Radius))))
            .Build());

        Assert.Contains("both write 'Figure'", error.Message);
    }

    [Fact]
    [Trait("Spec", "E-01")]
    public async Task APropertyLeftOutWhenNullStaysNullThroughABranchCopy()
    {
        using var services = Services();
        var definition = Workflow.Create<Sparse>("OmittedNulls")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<MakeHome>(step => step
                    .Output(activity => activity.Home).To(ctx => ctx.WorkflowData.Home)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "other")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Other))))
            .Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx =>
                    (ctx.WorkflowData.Note ?? "null") + "|" + ctx.WorkflowData.Home!.City + "|" +
                    (ctx.WorkflowData.Home.Zip ?? "null") + "|" + ctx.WorkflowData.Other))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Sparse { Note = null });

        // Neither the root's null nor the new object's null came back as the initial value.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["null|Lyon|null|other"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-07")]
    public async Task AMergeTheDataTypeRefusesFailsTheJoinAndLeavesTheDataAsItWas()
    {
        using var services = Services();
        var definition = Workflow.Create<Bounds>("RefusedMerge")
            .Step<Noop>()
            .Try(attempt => attempt.Parallel(parallel => parallel
                .Do(first => first.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 50)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Low)))
                .Do(second => second.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 40)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.High)))))
            .Catch<JoinConflictException>(handler => handler.Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx => $"caught {ctx.WorkflowData.Low}-{ctx.WorkflowData.High}")))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Bounds());

        // Each branch was valid on its own; together they are not, and nothing of the merge was applied.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["caught 0-100"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-07")]
    public async Task AMergeTheParentsObjectRefusesIsUndoneAndFailsTheJoin()
    {
        using var services = Services();
        var definition = Workflow.Create<Sealable>("RefusedApply")
            .Step<Seal>(step => step.Output(activity => activity.Sealed).To(ctx => ctx.WorkflowData.Sealed))
            .Try(attempt => attempt.Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "first")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "renamed")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Name)))))
            .Catch<JoinConflictException>(handler => handler.Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx => $"caught '{ctx.WorkflowData.First}' '{ctx.WorkflowData.Name}'")))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Sealable());

        // The branches' copies were not sealed, and the read-back was not either; the parent is, so setting
        // the name failed after the first value was set, and that value was put back.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["caught '' 'draft'"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-07")]
    public async Task ACombinedValueIsReadBackWithTheRestOfTheMerge()
    {
        using var services = Services();
        // The plain value comes first, so applying the merge in its order would take High=40 while Low is
        // still 0, then Low=50; only reading the whole merge back, combined value included, refuses it.
        var definition = Workflow.Create<SummedBounds>("RefusedCombination")
            .Step<Noop>()
            .Try(attempt => attempt.Parallel(parallel => parallel
                .Do(first => first.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 40)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.High)))
                .Do(second => second.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 50)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Low)))))
            .Catch<JoinConflictException>(handler => handler.Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx => $"caught {ctx.WorkflowData.Low}-{ctx.WorkflowData.High}")))
            .Build();

        var result = await Engine(services).RunAsync(definition, new SummedBounds());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["caught 0-100"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-07")]
    public async Task ASetterThatChangesItsObjectAndThenRefusesIsUndoneToo()
    {
        using var services = Services();
        var definition = Workflow.Create<Capped>("AssignThenRefuse")
            .Step<Noop>()
            .Try(attempt => attempt.Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "first")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                .Do(second => second.Step<SetNumber>(step => step
                    .Input(activity => activity.Value).From(_ => 7)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Count)))))
            .Catch<JoinConflictException>(handler => handler.Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx => $"caught '{ctx.WorkflowData.First}' {ctx.WorkflowData.Count}")))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Capped { Max = 5 });

        // Count took 7 before its setter refused it; both values were put back.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["caught '' 0"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-07")]
    public async Task DataThatRefusesToBePutBackIsAnEngineFailure()
    {
        using var services = Services();
        var definition = Workflow.Create<Latching>("RefusedRestore")
            .Step<Noop>()
            .Try(attempt => attempt.Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "first")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "second")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Second)))))
            .Catch<JoinConflictException>(handler => handler.Step<Noop>())
            .Build();

        var result = await Engine(services).RunAsync(definition, new Latching { Watched = true });

        // First latches once it is set, so it cannot be put back after Second is refused: the data is
        // changed and no handler can be told it is not, so the instance fails as the engine's failure.
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("could not put the workflow data back", result.ErrorMessage);
    }

    [Fact]
    [Trait("Spec", "E-07")]
    public async Task ACombinedValueIsNotCheckedAgainstTheDataAtTheFork()
    {
        using var services = Services();
        var definition = Workflow.Create<CombinedBounds>("CombinedBesideItsBound")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first
                    .Step<SetNumber>(step => step
                        .Input(activity => activity.Value).From(_ => 200)
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.High))
                    .Step<SetNumber>(step => step
                        .Input(activity => activity.Value).From(_ => 150)
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Low)))
                .Do(second => second.Step<Noop>()))
            .Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx => $"{ctx.WorkflowData.Low}-{ctx.WorkflowData.High}"))
            .Build();

        var result = await Engine(services).RunAsync(definition, new CombinedBounds());

        // Low=150 is valid beside High=200, which the merge also takes; at the fork High was 100.
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["150-200"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-04")]
    public void WritesTheJoinWouldDropAreRefusedWhereverThePathGoes()
    {
        // An ignored member inside a list's items, and a value merged whole that has no setter.
        Assert.Contains("'Scratch' is not serialized", Assert.Throws<WorkflowEngineException>(() =>
            Workflow.Create<ScratchItems>("IgnoredInsideAList").Step<Noop>()
                .Parallel(parallel => parallel.WaitAny()
                    .Do(only => only.Step<Copy>(step => step
                        .Input(activity => activity.Value).From(_ => "x")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Items[0].Scratch))))
                .Build()).Message);
        Assert.Contains("which a join sets whole but which has no setter", Assert.Throws<WorkflowEngineException>(() =>
            Workflow.Create<Priced>("GetOnlyWholeValue").Step<Noop>()
                .Parallel(parallel => parallel.WaitAny()
                    .Do(only => only.Step<SetNumber>(step => step
                        .Input(activity => activity.Value).From(_ => 3)
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Total.Amount))))
                .Build()).Message);
        // A populated list with a setter still reads back its initializer's items.
        Assert.Contains("'Tags' is read back by adding", Assert.Throws<WorkflowEngineException>(() =>
            Workflow.Create<PopulatedTags>("PopulatedWithSetter").Step<Noop>()
                .Parallel(parallel => parallel.Do(only => only.Step<Noop>()))
                .Build()).Message);
        // Under WaitAny nothing is compared, so a target the rules cannot name is allowed.
        Workflow.Create<Held>("UnnamedTargetUnderWaitAny").Step<Noop>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(only => only.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "x")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Holder().Value))))
            .Build();
    }

    [Fact]
    [Trait("Spec", "E-03")]
    public async Task AnImmutableDictionaryIsMergedWhole()
    {
        using var services = Services();
        var definition = Workflow.Create<Mapped>("ImmutableMap")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "first")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.First)))
                .Do(second => second.Step<AddEntry>(step => step
                    .Input(activity => activity.Map).From(ctx => ctx.WorkflowData.Map)
                    .Output(activity => activity.Map).To(ctx => ctx.WorkflowData.Map))))
            .Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.First + ":" + ctx.WorkflowData.Map.Count))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Mapped());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["first:2"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-03")]
    public async Task AnObjectWhoseConstructorSetsItsStateIsMergedWhole()
    {
        using var services = Services();
        var definition = Workflow.Create<Owned>("ReplacedOwner")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.Step<Rename>(step => step
                    .Output(activity => activity.Owner).To(ctx => ctx.WorkflowData.Owner)))
                .Do(second => second.Step<Noop>()))
            .Step<Record>(step => step.Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Owner.Name))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Owned());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["new"], services.GetRequiredService<Seen>().Values);
    }

    [Fact]
    [Trait("Spec", "E-01")]
    public void EveryPartOfTheDataABranchCopiesMustReadBackWhatItWasGiven()
    {
        static WorkflowDefinition Branched<T>() where T : class, new() =>
            Workflow.Create<T>(typeof(T).Name).Step<Noop>()
                .Parallel(branches => branches.Do(only => only.Step<Noop>()))
                .Build();

        // Inside a list's items, inside a property with a combiner, and a populated dictionary that would
        // keep what its initializer put in it.
        Assert.Contains("'Items[].Tags' has no setter",
            Assert.Throws<WorkflowEngineException>(() => Branched<TaggedItems>()).Message);
        Assert.Contains("'Totals.Tags' has no setter",
            Assert.Throws<WorkflowEngineException>(() => Branched<CombinedTotals>()).Message);
        Assert.Contains("'Seeds' is read back by adding",
            Assert.Throws<WorkflowEngineException>(() => Branched<Seeded>()).Message);
        // A combiner inside a value merged whole never applies.
        Assert.Contains("never applies", Assert.Throws<WorkflowEngineException>(() => Branched<Lines>()).Message);
        // What the serializer ignores is not branch data at all.
        Branched<Cached>();
    }

    [Fact]
    [Trait("Spec", "E-04")]
    public void AWriteTheJoinWouldDropIsRefusedUnderWaitAnyToo()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Cached>("IgnoredWrite")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .WaitAny()
                .Do(first => first.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "x")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Scratch)))
                .Do(second => second.Step<Noop>()))
            .Build());

        Assert.Contains("not serialized with the workflow data", error.Message);
    }

    [Fact]
    [Trait("Spec", "E-01")]
    public async Task APropertyLeftOutWhenNullStaysNullAfterARecovery()
    {
        var store = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<Sparse>("OmittedNullsRecovered")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(first => first.WaitFor<Go>("go"))
                .Do(second => second.Step<Copy>(step => step
                    .Input(activity => activity.Value).From(_ => "other")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Other))))
            .Step<Record>(step => step
                .Input(activity => activity.Value).From(ctx => (ctx.WorkflowData.Note ?? "null") + "|" + ctx.WorkflowData.Other))
            .Build();
        string instanceId;
        using (var first = Services(store))
            instanceId = (await Engine(first).RunAsync(definition, new Sparse { Note = null })).InstanceId;

        using var second = Services(store);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var finished = await Engine(second).ResumeAsync(instanceId, "go", new Go());

        // The saved data says the note is null, so the next host reads null, not the initial value.
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(["null|other"], second.GetRequiredService<Seen>().Values);
    }

    private static ServiceProvider Services(IWorkflowStateRepository? store = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (store != null)
            services.AddSingleton(store);
        services.AddSingleton<Seen>();
        services.AddTransient<Record>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) => services.GetRequiredService<IWorkflowClient>();

    public sealed class Signed
    {
        [Combine<Append<string>>]
        public List<string> Signatures { get; set; } = [];

        public int Count => Signatures.Count;
    }

    public sealed class Tagged
    {
        public List<string> Tags { get; } = [];
    }

    public sealed class Populated
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public Totals Totals { get; } = new();
    }

    public sealed class Totals
    {
        public int A { get; set; }
        public int B { get; set; }
    }

    public sealed class Drawing
    {
        public Figure Figure { get; set; } = new Circle { Radius = 1 };
        public string Note { get; set; } = "";
    }

    [JsonPolymorphic]
    [JsonDerivedType(typeof(Circle), "circle")]
    [JsonDerivedType(typeof(Square), "square")]
    public abstract class Figure;

    public sealed class Circle : Figure
    {
        public int Radius { get; set; }
    }

    public sealed class Square : Figure
    {
        public int Side { get; set; }
    }

    public sealed class Sparse
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; set; } = "default";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Address? Home { get; set; }

        public string Other { get; set; } = "";
    }

    public sealed class Address
    {
        public string City { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Zip { get; set; } = "00000";
    }

    /// <summary>High may never be below Low; the setter refuses it, also when the serializer reads it.</summary>
    public sealed class Bounds
    {
        private int _high = 100;

        public int Low { get; set; }

        public int High
        {
            get => _high;
            set => _high = value >= Low ? value : throw new ArgumentOutOfRangeException(nameof(High), "High is below Low");
        }
    }

    /// <summary>A record whose name may not change once it is sealed, which is not saved.</summary>
    public sealed class Sealable
    {
        private string _name = "draft";

        public string First { get; set; } = "";

        public string Name
        {
            get => _name;
            set => _name = Sealed ? throw new InvalidOperationException("The record is sealed") : value;
        }

        [JsonIgnore]
        public bool Sealed { get; set; }
    }

    public sealed class SummedBounds
    {
        private int _high = 100;

        [Combine<Sum<int>>]
        public int Low { get; set; }

        public int High
        {
            get => _high;
            set => _high = value >= Low ? value : throw new ArgumentOutOfRangeException(nameof(High), "High is below Low");
        }
    }

    /// <summary>Count is assigned, then checked against Max, which is not saved.</summary>
    public sealed class Capped
    {
        private int _count;

        public string First { get; set; } = "";

        public int Count
        {
            get => _count;
            set
            {
                _count = value;
                if (value > Max)
                    throw new ArgumentOutOfRangeException(nameof(Count), "Count is over Max");
            }
        }

        [JsonIgnore]
        public int Max { get; set; } = int.MaxValue;
    }

    /// <summary>First can be set once; Second refuses any change on the parent's object.</summary>
    public sealed class Latching
    {
        private string _first = "";
        private string _second = "";
        private bool _latched;

        public string First
        {
            get => _first;
            set
            {
                if (_latched)
                    throw new InvalidOperationException("First is latched");
                _first = value;
                _latched = value.Length > 0 && Watched;
            }
        }

        public string Second
        {
            get => _second;
            set => _second = Watched && value.Length > 0 ? throw new InvalidOperationException("Second is fixed") : value;
        }

        [JsonIgnore]
        public bool Watched { get; set; }
    }

    public sealed class CombinedBounds
    {
        private int _low;

        public int High { get; set; } = 100;

        [Combine<FirstWins<int>>]
        public int Low
        {
            get => _low;
            set => _low = value <= High ? value : throw new ArgumentOutOfRangeException(nameof(Low), "Low is over High");
        }
    }

    public sealed class ScratchItems
    {
        public List<ScratchItem> Items { get; set; } = [new()];
    }

    public sealed class ScratchItem
    {
        public string Name { get; set; } = "";

        [JsonIgnore]
        public string Scratch { get; set; } = "";
    }

    public sealed class Priced
    {
        public Money Total { get; } = new();
    }

    [JsonConverter(typeof(MoneyConverter))]
    public sealed class Money
    {
        public int Amount { get; set; }
    }

    public sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options) => new() { Amount = reader.GetInt32() };

        public override void Write(System.Text.Json.Utf8JsonWriter writer, Money value,
            System.Text.Json.JsonSerializerOptions options) => writer.WriteNumberValue(value.Amount);
    }

    public sealed class PopulatedTags
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public List<int> Tags { get; set; } = [1];
    }

    public sealed class Held
    {
        private readonly Box _box = new();

        public Box Holder() => _box;
    }

    public sealed class Box
    {
        public string Value { get; set; } = "";
    }

    public sealed class Mapped
    {
        public string First { get; set; } = "";
        public ImmutableDictionary<string, int> Map { get; set; } = ImmutableDictionary<string, int>.Empty.Add("a", 1);
    }

    public sealed class Owned
    {
        public Person Owner { get; set; } = new("old");
    }

    public sealed class Person(string name)
    {
        public string Name { get; } = name;
    }

    public sealed class TaggedItems
    {
        public List<Tagged> Items { get; set; } = [];
    }

    public sealed class CombinedTotals
    {
        [Combine<FirstWins<TagTotals>>]
        public TagTotals Totals { get; set; } = new();
    }

    public sealed class TagTotals
    {
        public List<string> Tags { get; } = [];
    }

    public sealed class Seeded
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public Dictionary<string, int> Seeds { get; } = new() { ["seed"] = 1 };
    }

    public sealed class Lines
    {
        public List<Line> Items { get; set; } = [];
    }

    public sealed class Line
    {
        [Combine<Sum<int>>]
        public int Total { get; set; }
    }

    public sealed class Cached
    {
        public string Other { get; set; } = "";

        [JsonIgnore]
        public string Scratch { get; set; } = "";

        [JsonIgnore]
        public TagTotals Cache { get; set; } = new();
    }

    public sealed class Go;

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

    public sealed class Sign : IAsyncActivity
    {
        public string Name { get; set; } = "";
        public List<string> Signatures { get; set; } = [];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Signatures = [.. Signatures, Name];
            return Task.CompletedTask;
        }
    }

    public sealed class SetNumber : IAsyncActivity
    {
        public int Value { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class Copy : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class MakeSquare : IAsyncActivity
    {
        public Figure Figure { get; set; } = new Circle();

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Figure = new Square { Side = 2 };
            return Task.CompletedTask;
        }
    }

    public sealed class MakeHome : IAsyncActivity
    {
        public Address? Home { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Home = new Address { City = "Lyon", Zip = null };
            return Task.CompletedTask;
        }
    }

    public sealed class Seal : IAsyncActivity
    {
        public bool Sealed { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Sealed = true;
            return Task.CompletedTask;
        }
    }

    public sealed class AddEntry : IAsyncActivity
    {
        public ImmutableDictionary<string, int> Map { get; set; } = ImmutableDictionary<string, int>.Empty;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Map = Map.Add("b", 2);
            return Task.CompletedTask;
        }
    }

    public sealed class Rename : IAsyncActivity
    {
        public Person Owner { get; set; } = new("");

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Owner = new Person("new");
            return Task.CompletedTask;
        }
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
