using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-04")]
public sealed class AssignTests
{
    [Fact]
    public async Task TypedValuesNestedStructsAndTerminalCollectionLocationsRetainPrevious()
    {
        var definition = Workflow.Create<Data>("TypedAssign").Step<Seed>()
            .Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 2)
            .Assign(ctx => ctx.WorkflowData.Nested.Value.Point.X, ctx => ctx.PreviousStep.Number)
            .Assign(ctx => ctx.WorkflowData.Points[0].Point.X, ctx => 11)
            .Assign(ctx => ctx.WorkflowData.Boxes[0].Point.X, ctx => 18)
            .Assign(ctx => ctx.WorkflowData.ReferenceList[0].Count, ctx => 19)
            .Assign(ctx => ctx.WorkflowData.Array[0], ctx => 12)
            .Assign(ctx => ctx.WorkflowData.List[0], ctx => 13)
            .Assign(ctx => ctx.WorkflowData.Dictionary["private-key"], ctx => 14)
            .Assign(ctx => ctx.WorkflowData.Structs["key"].Point.X, ctx => 15)
            .Assign(ctx => ctx.WorkflowData.Text, ctx => (string?)null)
            .Assign(ctx => ctx.WorkflowData.Wide, ctx => ctx.WorkflowData.Count)
            .Assign(ctx => ctx.WorkflowData.List, ctx => new List<int> { 16, 17 })
            .Assign(ctx => ctx.WorkflowData.Previous, ctx => ctx.PreviousStep.Number).WithId("last").Named("Last write").Build();

        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(3, data.Count);
        Assert.Equal(7, data.Nested.Value.Point.X);
        Assert.Equal(11, data.Points[0].Point.X);
        Assert.Equal(18, data.Boxes[0].Point.X);
        Assert.Equal(19, data.ReferenceList[0].Count);
        Assert.Equal(12, data.Array[0]);
        Assert.Equal(new[] { 16, 17 }, data.List);
        Assert.Equal(14, data.Dictionary["private-key"]);
        Assert.Equal(15, data.Structs["key"].Point.X);
        Assert.Null(data.Text);
        Assert.Equal(3L, data.Wide);
        Assert.Equal(7, data.Previous);
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(result.InstanceId);
        Assert.Equal(13, trace.Count(entry => entry.Entry.EntryType == TraceEntryType.DataAssigned));

        Assert.DoesNotContain("private-key",
            JsonSerializer.Serialize(trace.Where(entry => entry.Entry.EntryType == TraceEntryType.DataAssigned)));

        Assert.DoesNotContain(trace, entry => entry.Entry.EntryType == TraceEntryType.ActivityStarted && entry.Entry.StepId != "root/0");
        Assert.Equal("WorkflowData.Previous", definition.StepTable.Single(row => row.AuthorId == "last").Assignment!.Target);
        Assert.Equal("Last write", definition.StepTable.Last().Name);
    }

    [Fact]
    public void TargetReceiverAndIndexPrecedeTheSingleValueEvaluationAndWriteBack()
    {
        var definition = Workflow.Create<Observed>("Order")
            .Assign(ctx => ctx.WorkflowData.Values[ctx.WorkflowData.Position].Point.X, ctx => ctx.WorkflowData.Replacement).Build();

        var data = new Observed();
        var context = new WorkflowContext<Observed, object> { WorkflowData = data };
        definition.Steps.Single().Assignment!.Apply!(context);
        Assert.Equal(new[] { "receiver", "index", "value" }, data.Events);
        Assert.Equal(19, data.Stored[0].Point.X);
    }

    [Fact]
    public async Task PopulateReferenceContainersAreWritableAndJsonNamesReachTheMerge()
    {
        var definition = Workflow.Create<Data>("PopulateAssign").Assign(ctx => ctx.WorkflowData.Populated.Count, ctx => 5)
            .Parallel(p => p.Do(b => b.Assign(ctx => ctx.WorkflowData.Count, ctx => 20))
                .Do(b => b.Assign(ctx => ctx.WorkflowData.Nested.Count, ctx => 21))).Build();

        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(5, data.Populated.Count);
        Assert.Equal(20, data.Count);
        Assert.Equal(21, data.Nested.Count);
    }

    [Theory]
    [InlineData("previous", "ASSIGN001")]
    [InlineData("root", "ASSIGN001")]
    [InlineData("captured", "ASSIGN001")]
    [InlineData("field", "ASSIGN002")]
    [InlineData("ignored", "ASSIGN002")]
    [InlineData("read-only", "ASSIGN002")]
    [InlineData("read-only-struct", "ASSIGN002")]
    [InlineData("unpopulated", "ASSIGN002")]
    public void RejectsNonWorkflowAndNonSerializedLocations(string shape, string diagnostic)
    {
        var captured = new Data();

        var error = Assert.Throws<WorkflowEngineException>(() =>
        {
            if (shape == "unpopulated")
                return Workflow.Create<UnpopulatedData>("BadAssign")
                    .Assign(ctx => ctx.WorkflowData.Unpopulated.Count, ctx => 1).Build();

            var b = Workflow.Create<Data>("BadAssign").Step<Seed>();

            return (shape switch
            {
                "previous" => b.Assign(ctx => ctx.PreviousStep.Number, ctx => 1),
                "root" => b.Assign(ctx => ctx.WorkflowData, ctx => new Data()),
                "captured" => b.Assign(ctx => captured.Count, ctx => 1),
                "field" => b.Assign(ctx => ctx.WorkflowData.Field, ctx => 1),
                "ignored" => b.Assign(ctx => ctx.WorkflowData.Ignored, ctx => 1),
                "read-only" => b.Assign(ctx => ctx.WorkflowData.ReadOnly, ctx => 1),
                "read-only-struct" => b.Assign(ctx => ctx.WorkflowData.ReadOnlyPoint.X, ctx => 1),
                _ => b.Assign(ctx => ctx.WorkflowData.Ignored, ctx => 1)
            }).Build();
        });

        Assert.Contains(diagnostic, error.Message);
        Assert.Contains("AssignTests.cs", error.Message);
    }

    [Fact]
    public void IncompatibleDirectModelHasAValueDiagnostic()
    {
        var b = Workflow.Create<Data>("BadValue").Assign(ctx => ctx.WorkflowData.Count, ctx => 1).Build();
        var clone = b.Steps[0].Clone();
        clone.Assignment = clone.Assignment! with { Value = (Expression<Func<WorkflowContext<Data, object>, string>>)(ctx => "wrong") };

        Assert.Contains("ASSIGN003", Assert.Throws<WorkflowEngineException>(() =>
            new WorkflowBuilder<Data, object>([clone], "BadValue").Build()).Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("index")]
    [InlineData("key")]
    [InlineData("setter")]
    public async Task LocationFailuresUseNormalBusinessCatch(string failure)
    {
        var definition = Workflow.Create<Data>("AssignFailure").Step<Seed>().Try(b =>
        {
            if (failure == "null")
                b.Assign(ctx => ctx.WorkflowData.Missing!.Count, ctx => 1);
            else if (failure == "index")
                b.Assign(ctx => ctx.WorkflowData.Array[5], ctx => 1);
            else if (failure == "key")
                b.Assign(ctx => ctx.WorkflowData.Structs["absent"].Point.X, ctx => 1);
            else
                b.Assign(ctx => ctx.WorkflowData.Reject, ctx => 1);
        }).Catch<Exception, Fault>(c => c.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message)).Build();

        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<Data>(result.WorkflowData).Text));
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(result.InstanceId);
        Assert.Single(trace, entry => entry.Entry.EntryType == TraceEntryType.DataAssigned);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("if")]
    [InlineData("switch")]
    [InlineData("loop")]
    [InlineData("parallel")]
    [InlineData("each")]
    [InlineData("try-finally")]
    [InlineData("flowchart")]
    [InlineData("state")]
    [InlineData("saga")]
    public async Task FaultFacetReachesEveryCallbackAndRecoversAfterAWait(string shape)
    {
        var definition = Workflow.Create<Data>("AssignFault" + shape).Step<Seed>().Try(b => b.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(c =>
            {
                void Body(ICatchWorkflowBuilder<Data, Fault, Seed> b) => b.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message);

                switch (shape)
                {
                    case "sequence":
                        c.Sequence(Body);

                        break;
                    case "if":
                        c.If(ctx => true, Body);

                        break;
                    case "switch":
                        c.Switch(ctx => ctx.Fault.Message, cases => cases.Case("outer", Body));

                        break;
                    case "loop":
                        c.WhileDo(ctx => ctx.WorkflowData.Text == "", Body);

                        break;
                    case "parallel":
                        c.Parallel(p => p.Do(Body));

                        break;
                    case "each":
                        c.ForEach(ctx => ctx.WorkflowData.List,
                            (b, item) => b.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message + ctx.Index(item)));

                        break;
                    case "try-finally":
                        c.Try(Body).Finally(Body);

                        break;
                    case "flowchart":
                        c.Flowchart(g => g.Start("a").Node("a", b => b.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message)));

                        break;
                    case "state":
                        c.StateMachine(m =>
                            m.Start("a").State("a",
                                s => s.Entry(b => b.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message))
                                    .Immediately(t => t.Otherwise("done"))).State("done", s => s.Final()));

                        break;
                    default:
                        c.Saga(s => s.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message));

                        break;
                }

                c.WaitFor<Go>("saved").Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.WorkflowData.Text + "|" + ctx.Fault.Message);
            }).Build();

        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var services = Services(store, catalog))
        {
            var idle = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
        }

        await using var recovered = Services(store, catalog);
        var run = await recovered.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "saved", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
        Assert.Equal(shape == "each" ? "outer0|outer" : "outer|outer", Assert.IsType<Data>(run.WorkflowData).Text);
    }

    [Fact]
    public void SharedWriteDiscoveryRejectsConflictsItemsAndIntoOverlapsInNestedBodies()
    {
        Assert.Contains("combiner", Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "Overlap")
            .Parallel(p => p.Do(b => b.Assign(ctx => ctx.WorkflowData.Count, ctx => 1))
                .Do(b => b.Try(t => t.Step<Seed>()).Finally(f => f.Assign(ctx => ctx.WorkflowData.Count, ctx => 2)))).Build()).Message);

        Assert.Contains("combiner", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("EachOverlap").Step<Seed>()
            .ForEach(ctx => ctx.WorkflowData.List, (b, item) => b.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.Item(item)),
                s => s.Parallel()).Build()).Message);

        Assert.Contains("stores its results", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("IntoOverlap").Step<Seed>()
            .ForEach(ctx => ctx.WorkflowData.List, (b, item) => b
                .Assign(ctx => ctx.WorkflowData.Results, ctx => new List<ItemResult<int>>())
                .Result(ctx => ctx.Item(item)), s => s.Into<int>(ctx => ctx.WorkflowData.Results)).Build()).Message);

        Assert.Contains("ASSIGN001", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("ItemWrite").Step<Seed>()
            .ForEach(ctx => ctx.WorkflowData.Points, (b, item) => b.Assign(ctx => ctx.Item(item).Point.X, ctx => 1)).Build()).Message);
    }

    [Fact]
    public void BothExpressionTreesAreMutationCheckedAndOutOfScopeHandlesAreRejected()
    {
        Assert.Contains("mutation", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("HiddenWrite")
            .Assign(ctx => ctx.WorkflowData.Removed, ctx => ctx.WorkflowData.List.Remove(1)).Build()).Message);

        var context = Expression.Parameter(typeof(WorkflowContext<Data, object>), "ctx");
        var count = Expression.Property(Expression.Property(context, "WorkflowData"), "Count");

        var mutating =
            Expression.Lambda<Func<WorkflowContext<Data, object>, int>>(Expression.Assign(count, Expression.Constant(1)), context);

        Assert.Contains("assignment", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("TreeWrite")
            .Assign(ctx => ctx.WorkflowData.Count, mutating).Build()).Message);

        ItemScope<int>? captured = null;

        var builder = Workflow.Create<Data>("LeakedHandle").Step<Seed>()
            .ForEach(ctx => ctx.WorkflowData.List, (b, item) => captured = item);

        Assert.Contains("outside",
            Assert.Throws<WorkflowEngineException>(() => builder.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.Item(captured!)).Build())
                .Message);
    }

    [Fact]
    public void CloneAndHashRetainOriginalTreesAndIndexValueBodies()
    {
        WorkflowDefinition Definition(int index, bool plusZero) => Workflow.Create<Data>("AssignHash")
            .Assign(ctx => ctx.WorkflowData.Array[index], plusZero ? ctx => ctx.WorkflowData.Count + 0 : ctx => ctx.WorkflowData.Count)
            .Build();

        var first = Definition(0, false);
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(Definition(0, true)));

        // Captured values are deliberately excluded; literal indices are behavior.
        var literal = Workflow.Create<Data>("AssignHash").Assign(ctx => ctx.WorkflowData.Array[1], ctx => ctx.WorkflowData.Count).Build();
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(literal));
        var original = first.Steps[0].Assignment!;
        var clone = first.Steps[0].Clone().Assignment!;
        Assert.Same(original.Target, clone.Target);
        Assert.Same(original.Value, clone.Value);
        Assert.Same(original.Apply, clone.Apply);
    }

    [Theory]
    [InlineData(DebugStepKind.Into)]
    [InlineData(DebugStepKind.Over)]
    public async Task DebuggerSeesOldThenNewDataAndReplayStopsOnlyAtThePick(DebugStepKind kind)
    {
        var definition = Workflow.Create<Data>("AssignDebug").Step<Seed>().WaitFor<Go>("start")
            .Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).WithId("assign")
            .Assign(ctx => ctx.WorkflowData.Previous, ctx => ctx.WorkflowData.Count).WithId("after").Build();

        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("before", "assign")], "test");
        var stopped = await client.ResumeAsync(idle.InstanceId, "start", new Go());
        Assert.Equal(WorkflowExecutionStatus.Paused, stopped.Status);
        Assert.Equal(1, Assert.IsType<Data>(stopped.WorkflowData).Count);
        var inspection = (await ops.InspectAsync(idle.InstanceId))!;
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "test");
        var next = await ops.StepAsync(idle.InstanceId, inspection.Threads[0].ContinuationId, kind, "test");
        Assert.Equal(WorkflowExecutionStatus.Paused, next.Status);
        Assert.Equal(2, Assert.IsType<Data>(next.WorkflowData).Count);
        Assert.Equal("after", (await ops.InspectAsync(idle.InstanceId))!.Threads[0].AuthorStepId);
        var trace = await ops.GetTraceAsync(idle.InstanceId);
        var replay = TraceReplay.Load(definition, trace);
        var fact = Assert.Single(trace, entry => entry.Entry.EntryType == TraceEntryType.DataAssigned);
        replay.Seek(fact.Sequence);
        Assert.Null(replay.CurrentStepId);
        var pick = trace.Last(entry => entry.Entry.EntryType == TraceEntryType.StepEntered && entry.Entry.StepId == "root/2");
        replay.Seek(pick.Sequence);
        Assert.Equal("root/2", replay.CurrentStepId);
    }

    private static ServiceProvider Services(IWorkflowStateRepository? store = null, IWorkflowCatalog? catalog = null)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.PayloadThresholdBytes = 1;
        }).Services;

        if (store != null)
            services.AddSingleton(store);

        if (catalog != null)
            services.AddSingleton(catalog);

        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        [JsonPropertyName("renamed")] public int Count { get; set; } = 1;
        public long Wide { get; set; }
        public int Previous { get; set; }
        public bool Removed { get; set; }
        public string? Text { get; set; } = "";
        public Node Nested { get; set; } = new();
        public Node? Missing { get; set; }

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public Node Populated { get; } = new();

        public Box[] Points { get; set; } = [new()];
        public List<Box> Boxes { get; set; } = [new()];
        public IReadOnlyList<Node> ReferenceList { get; set; } = [new()];
        public int[] Array { get; set; } = [0];
        public List<int> List { get; set; } = [1];
        public Dictionary<string, int> Dictionary { get; set; } = [];
        public Dictionary<string, Box> Structs { get; set; } = new() { ["key"] = new() };
        public List<ItemResult<int>> Results { get; set; } = [];
        public int Field;
        [JsonIgnore] public int Ignored { get; set; }
        public int ReadOnly => 0;
        public Point ReadOnlyPoint => new();

        public int Reject
        {
            get => 0;
            set
            {
                if (value != 0)
                    throw new InvalidOperationException("reject");
            }
        }
    }

    public sealed class UnpopulatedData
    {
        public Node Unpopulated { get; } = new();
    }

    public sealed class Node
    {
        public int Count { get; set; }
        public Box Value { get; set; }
    }

    public struct Box
    {
        public Point Point { get; set; }
    }

    public struct Point
    {
        public int X { get; set; }
    }

    public sealed class Fault
    {
        public string Message { get; set; } = "";
    }

    public sealed class Go
    {
    }

    public sealed class Seed : IAsyncActivity
    {
        public int Number { get; set; } = 7;
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("outer"));
    }

    public sealed class Observed
    {
        [JsonIgnore] public List<string> Events { get; } = [];
        [JsonIgnore] public Box[] Stored { get; } = [new()];

        public Box[] Values
        {
            get
            {
                Events.Add("receiver");

                return Stored;
            }
            set
            {
                Events.Add("set");
            }
        }

        public int Position
        {
            get
            {
                Events.Add("index");

                return 0;
            }
            set
            {
            }
        }

        public int Replacement
        {
            get
            {
                Events.Add("value");

                return 19;
            }
            set
            {
            }
        }
    }
}