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

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
