using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-03")]
public sealed class SwitchTests
{
    [Theory]
    [InlineData(1, true, false, "case", SwitchPath.Case)]
    [InlineData(2, true, false, "default", SwitchPath.Default)]
    [InlineData(2, false, false, "", SwitchPath.NoMatch)]
    [InlineData(1, true, true, "", SwitchPath.Case)]
    [InlineData(2, true, true, "", SwitchPath.Default)]
    public async Task OneSelectionNoFallthroughAndEntryPreviousRestoration(int route, bool fallback, bool empty, string text, SwitchPath path)
    {
        var definition = Workflow.Create<Data>("SwitchRoutes").Step<Seed>()
            .Switch(ctx => ctx.WorkflowData.Route, cases =>
            {
                cases.Case(1, body => { if (!empty) body.Step<Echo>(s => s.Input(a => a.Text).From(ctx => "case").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)); });
                if (fallback) cases.Default(body => { if (!empty) body.Step<Echo>(s => s.Input(a => a.Text).From(ctx => "default").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)); });
            }).WithId("switch").Named("Route")
            .Step<Echo>(s => s.Input(a => a.Text).From(ctx => ctx.PreviousStep.Text).Output(a => a.Text).To(ctx => ctx.WorkflowData.Previous)).Build();
        var pick = definition.Steps[1];
        var selector = pick.OutcomeSelector!;
        var evaluations = 0;
        pick.OutcomeSelector = context => { evaluations++; return selector(context); };
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data { Route = route });
        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
        Assert.Equal(text, Assert.IsType<Data>(run.WorkflowData).Text);
        Assert.Equal("entry", Assert.IsType<Data>(run.WorkflowData).Previous);
        Assert.Equal(1, evaluations);
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(run.InstanceId);
        var fact = Assert.IsType<SwitchTrace>(Assert.Single(trace, t => t.Entry.EntryType == TraceEntryType.SwitchSelected).Entry.Payload);
        Assert.Equal(path, fact.Path);
        Assert.Equal(path == SwitchPath.NoMatch, fact.ScopeId == null);
        Assert.Single(trace, t => t.Entry.EntryType == TraceEntryType.StepEntered && t.Entry.StepId == "root/1");
        Assert.DoesNotContain(trace, t => t.Entry.EntryType == TraceEntryType.ConditionTaken && t.Entry.StepId == "root/1");
        var replay = TraceReplay.Load(definition, trace);
        replay.Seek(trace.Single(t => t.Entry.EntryType == TraceEntryType.StepEntered && t.Entry.StepId == "root/1").Sequence);
        Assert.Equal("root/1", replay.CurrentStepId);
        Assert.DoesNotContain("entry", JsonSerializer.Serialize(fact));
        Assert.Equal(typeof(int).FullName, definition.SwitchTable.Single().ValueType.Split(',')[0]);
        Assert.Contains(definition.StepTable, row => row.AuthorId == "switch" && row.Kind == WorkflowStepType.Switch);
    }

    [Fact]
    public void EmptyDefaultOnlyAndEmptySwitchesAreDistinctValidStructures()
    {
        var empty = Workflow.Create<Data>("Empty").Switch(ctx => 1, _ => { }).Build();
        var fallback = Workflow.Create<Data>("Empty").Switch(ctx => 1, c => c.Default(_ => { })).Build();
        Assert.NotEqual(Hash(empty), Hash(fallback));
        Assert.Empty(empty.SwitchTable.Single().Cases);
        Assert.EndsWith("/default", fallback.SwitchTable.Single().Cases.Single().ScopeId);
    }

    [Fact]
    public async Task TypedEqualityNullEnumsDecimalsAndBoxedNumericTypesRouteWithoutCoercion()
    {
        Assert.True(await Matches<string?>(null, null));
        Assert.True(await Matches<int?>(null, null));
        Assert.True(await Matches<Mode>(Mode.B, Mode.B));
        Assert.True(await Matches<decimal>(1.25m, 1.250m));
        Assert.False(await Matches<object>(1, 1L));
        Assert.True(await Matches<object>(1L, 1L));
        Assert.True(await Matches(new TypedOnly(3), new TypedOnly(3)));
        Assert.False(await Matches(new TypedOnly(3), new TypedOnly(4)));
    }

    [Fact]
    public void TypedDuplicateDetectionIncludesNullAndDefault()
    {
        Assert.Contains("SWITCH003", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Duplicates")
            .Switch(ctx => new TypedOnly(1), c => c.Case(new TypedOnly(1), _ => { }).Case(new TypedOnly(1), _ => { })).Build()).Message);
        Assert.Contains("SWITCH003", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Duplicates")
            .Switch<string?>(ctx => null, c => c.Case(null, _ => { }).Case(null, _ => { })).Build()).Message);
        Assert.Contains("SWITCH003", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Duplicates")
            .Switch(ctx => 1, c => c.Default(_ => { }).Default(_ => { })).Build()).Message);
    }

    [Fact]
    public async Task GraphSwitchesShareTypedEqualityAndDuplicateRules()
    {
        var definition = Workflow.Create<Data>("TypedGraph").Flowchart(g => g.Start("a")
            .Node("a", e => e.Switch(ctx => new TypedOnly(1)).Case(new TypedOnly(1), "b"))
            .Node("b", b => b.Step<Echo>(s => s.Input(a => a.Text).From(ctx => "typed").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)))).Build();
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal("typed", Assert.IsType<Data>(run.WorkflowData).Text);
        Assert.Contains("SWITCH003", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("DuplicateGraph").Flowchart(g => g.Start("a")
            .Node("a", e => e.Switch(ctx => new TypedOnly(1)).Case(new TypedOnly(1), null).Case(new TypedOnly(1), null))).Build()).Message);
    }

    [Fact]
    public void SelectorExpressionBodiesOrderedLabelsAndCloneFieldsBelongToTheManifest()
    {
        var first = Workflow.Create<Data>("Hash").Switch(ctx => ctx.WorkflowData.Route, c => c.Case(1, _ => { }).Case(2, _ => { })).Build();
        var plusZero = Workflow.Create<Data>("Hash").Switch(ctx => ctx.WorkflowData.Route + 0, c => c.Case(1, _ => { }).Case(2, _ => { })).Build();
        var reordered = Workflow.Create<Data>("Hash").Switch(ctx => ctx.WorkflowData.Route, c => c.Case(2, _ => { }).Case(1, _ => { })).Build();
        Assert.NotEqual(Hash(first), Hash(plusZero));
        Assert.NotEqual(Hash(first), Hash(reordered));
        var clone = first.Steps[0].Clone();
        Assert.Equal(typeof(int), clone.SwitchValueType);
        Assert.Same(first.Steps[0].OutcomeExpression, clone.OutcomeExpression);
        Assert.True(clone.SwitchEquals!(1, 1));
        Assert.False(clone.SwitchEquals(1, 2));
        Assert.Equal(new[] { "case", "case" }, clone.ChildScopes().Where(scope => scope.Kind == "case").Select(scope => scope.Kind));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompleteLabelStateChangesBodyAndGraphHashes(bool graph)
    {
        Assert.Equal(JsonSerializer.Serialize(new ValueTuple<int>(1)), JsonSerializer.Serialize(new ValueTuple<int>(2)));
        Assert.NotEqual(LabelHash(new ValueTuple<int>(1), graph), LabelHash(new ValueTuple<int>(2), graph));
        Assert.NotEqual(LabelHash(new PrivateValue(1), graph), LabelHash(new PrivateValue(2), graph));
        Assert.NotEqual(LabelHash(new MutableValue { Key = 1 }, graph), LabelHash(new MutableValue { Key = 2 }, graph));
        Assert.NotEqual(LabelHash(new ExplicitValue { Key = 1 }, graph), LabelHash(new ExplicitValue { Key = 2 }, graph));
        Assert.NotEqual(LabelHash((1, new PrivateValue(1)), graph), LabelHash((1, new PrivateValue(2)), graph));
        Assert.NotEqual(LabelHash((object)1, graph), LabelHash((object)1L, graph));
        Assert.NotEqual(LabelHash("\uD800", graph), LabelHash("\uD801", graph));
        var signature = SwitchLabelSignature.Of(new PrivateValue(1));
        Assert.DoesNotContain("BackingField", signature);
        Assert.Contains("property/Ignored", signature);
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = SwitchLabelSignature.Of((1.2m, new PrivateValue(4)));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(first, SwitchLabelSignature.Of((1.2m, new PrivateValue(4))));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalBoxMutationCannotChangeBuiltLabelsOrClone(bool graph)
    {
        object label = new MutableValue { Key = 1 };
        var definition = LabelDefinition(label, graph);
        var hash = Hash(definition);
        typeof(MutableValue).GetField(nameof(MutableValue.Key))!.SetValue(label, 2);
        Assert.Equal(hash, Hash(definition));
        var clone = definition.Steps[1].Clone();
        var clonedLabel = graph ? clone.GraphNodes[0].Edges[0][0].EdgeLabel : clone.OutcomeBranches[0].Value;
        Assert.Equal(1, Assert.IsType<MutableValue>(clonedLabel).Key);
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data { Route = 1 });
        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
        Assert.Equal("selected", Assert.IsType<Data>(run.WorkflowData).Text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnsupportedReferenceAndOpaqueLayoutsArePositionedErrors(bool graph)
    {
        foreach (var label in new object[] { new object(), new WithReference { Values = [] }, new NullableReference(), new Opaque { Number = 1 } })
        {
            var error = Assert.Throws<WorkflowEngineException>(() => LabelDefinition(label, graph));
            Assert.Contains("SWITCH004", error.Message);
            Assert.Contains("cases[0].value", error.Message);
            Assert.Contains("SwitchTests.cs:", error.Message);
        }
    }

    [Fact]
    public void DirectMutationTreesAndRecognizedCollectionMutatorsAreRejected()
    {
        var context = Expression.Parameter(typeof(WorkflowContext<Data, object>), "ctx");
        var route = Expression.Property(Expression.Property(context, nameof(WorkflowContext<Data>.WorkflowData)), nameof(Data.Route));
        var selector = Expression.Lambda<Func<WorkflowContext<Data, object>, int>>(Expression.Assign(route, Expression.Constant(1)), context);
        Assert.Contains("state assignment", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Mutation").Switch(selector, _ => { }).Build()).Message);
        Assert.Contains("state mutation", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Mutation")
            .Switch(ctx => ctx.WorkflowData.Items.Remove(1), _ => { }).Build()).Message);
        Assert.Equal(2, SwitchLabel.Value(() => new List<int> { 1, 2 }.Count));
        Assert.Throws<WorkflowEngineException>(() => SwitchLabel.Value(() => DateTime.UtcNow));
    }

    [Theory]
    [InlineData("sequence")] [InlineData("if")] [InlineData("switch")] [InlineData("while")]
    [InlineData("do")] [InlineData("parallel")] [InlineData("each")] [InlineData("parallel-each")]
    [InlineData("graph")] [InlineData("state")] [InlineData("try")] [InlineData("saga")]
    public async Task CatchDescendantsRetainTheirTypedFault(string body)
    {
        var definition = Workflow.Create<Data>("FaultCallbacks").Step<Seed>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(caught =>
            {
                void Read(IxIFlow.Builders.Interfaces.ICatchWorkflowBuilder<Data, Fault, Seed> b) => b.Switch(ctx => ctx.Fault.Message,
                    c => c.Case("fault", arm => arm.Step<Echo>(s => s.Input(a => a.Text).From(ctx => ctx.Fault.Message).Output(a => a.Text).To(ctx => ctx.WorkflowData.Text))));
                switch (body)
                {
                    case "sequence": caught.Sequence(Read); break;
                    case "if": caught.If(ctx => ctx.Fault.Message == "fault", Read); break;
                    case "switch": caught.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", Read)); break;
                    case "while": caught.WhileDo(ctx => ctx.Fault.Message == "fault" && ctx.WorkflowData.Text == "", Read); break;
                    case "do": caught.DoWhile(Read, conditionExpression: ctx => ctx.Fault.Message == "again"); break;
                    case "parallel": caught.Parallel(p => p.Do(Read)); break;
                    case "each": caught.ForEach(ctx => ctx.WorkflowData.Items, (b, item) => Read(b)); break;
                    case "parallel-each": caught.ForEach(ctx => ctx.WorkflowData.Items, (b, item) => Read(b), s => s.Parallel(1).WaitAny()); break;
                    case "graph": caught.Flowchart(g => g.Start("a").Node("a", b => b.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", Read)))); break;
                    case "state": caught.StateMachine(m => m.Start("a").State("a", s => s.Entry(b => b.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", Read))).Otherwise("done", Read)).State("done", s => s.Final())); break;
                    case "try": caught.Try(Read).Catch<ArgumentException>(b => b.Step<Seed>()).Finally(Read); break;
                    case "saga": caught.Saga(s => s.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", b => b.Step<Echo>(a => a.Input(x => x.Text).From(ctx => ctx.Fault.Message).Output(x => x.Text).To(ctx => ctx.WorkflowData.Text).CompensateWith<Seed>())))); break;
                }
            }).Build();
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data { Items = [1] });
        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
        Assert.Equal("fault", Assert.IsType<Data>(run.WorkflowData).Text);
    }

    [Theory]
    [InlineData(1, false, DebugStepKind.Into, "switch", "inside")]
    [InlineData(2, false, DebugStepKind.Into, "switch", "fallback")]
    [InlineData(3, false, DebugStepKind.Into, "switch", "after")]
    [InlineData(1, true, DebugStepKind.Into, "switch", "after")]
    [InlineData(1, false, DebugStepKind.Over, "switch", "after")]
    [InlineData(1, false, DebugStepKind.Out, "inside", "after")]
    public async Task DebuggerStepsUseRealPicksForCasesDefaultNoMatchAndEmptyBodies(int route, bool empty, DebugStepKind kind, string start, string expected)
    {
        var definition = new WorkflowBuilder<Data, object>([], "SwitchStepping").WaitFor<Go>("start")
            .Switch(ctx => ctx.WorkflowData.Route, c =>
            {
                c.Case(1, b => { if (!empty) b.Step<Seed>().WithId("inside"); });
                if (route == 2) c.Default(b => b.Step<Seed>().WithId("fallback"));
            }).WithId("switch").Step<Seed>().WithId("after").Build();
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data { Route = route });
        await operations.SetBreakpointsAsync(idle.InstanceId, [new("start", start)], "test");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Go())).Status);
        var before = (await operations.InspectAsync(idle.InstanceId))!;
        await operations.SetBreakpointsAsync(idle.InstanceId, [], "test");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.StepAsync(idle.InstanceId, before.Threads[0].ContinuationId, kind, "test")).Status);
        var after = (await operations.InspectAsync(idle.InstanceId))!;
        Assert.Equal(expected, after.Threads[0].AuthorStepId);
        if (expected == "inside") Assert.Equal("SwitchCase", after.Threads[0].Frames[^1].Kind);
        var table = await operations.GetSwitchTableAsync(idle.InstanceId);
        Assert.Equal("root/1", table.Single().CatalogId);
        Assert.Equal(WorkflowExecutionStatus.Success, (await operations.ContinueAsync(idle.InstanceId, "test")).Status);
    }

    [Fact]
    public async Task AJoinedCatchHydratesItsLexicalFaultAfterRestartAndNestedCatchShadowing()
    {
        var store = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Data>("SwitchFaultRecovery").Step<Seed>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(outer => outer.Parallel(p => p.Do(b => b
                .Try(t => t.Step<FailInner>())
                .Catch<ArgumentException, Fault>(inner => inner.WaitFor<Go>("inner", matchesExpression: (evt, ctx) => ctx.Fault.Message == "inner").Switch(ctx => ctx.Fault.Message,
                    c => c.Case("inner", arm => arm.Step<Echo>(s => s.Input(a => a.Text).From(ctx => ctx.Fault.Message)
                        .Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)))))
                .Finally(cleanup => cleanup.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", arm => arm.Step<Echo>(s => s
                    .Input(a => a.Text).From(ctx => ctx.WorkflowData.Text + "|" + ctx.Fault.Message)
                    .Output(a => a.Text).To(ctx => ctx.WorkflowData.Text))))))))
            .Build();
        await registry.RegisterWorkflowAsync(definition);
        string id;
        await using (var first = Services(store, registry))
        {
            var result = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            id = result.InstanceId;
        }
        await using var second = Services(store, registry);
        var resumed = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "inner", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal("inner|fault", Assert.IsType<Data>(resumed.WorkflowData).Text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SelectorAndTypedComparerFailuresFollowBusinessCatchRouting(bool comparer)
    {
        var definition = Workflow.Create<Data>("SwitchFailures").Step<Seed>().Try(t =>
        {
            if (comparer) t.Switch(ctx => new ThrowingEquality(1), c => c.Case(new ThrowingEquality(1), _ => { }));
            else t.Switch(ctx => ThrowSelector(), _ => { });
        }).Catch<InvalidOperationException, Fault>(b => b.Step<Echo>(s => s.Input(a => a.Text).From(ctx => ctx.Fault.Message)
            .Output(a => a.Text).To(ctx => ctx.WorkflowData.Text))).Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(comparer ? "comparer" : "selector", Assert.IsType<Data>(result.WorkflowData).Text);
    }
    private static int ThrowSelector() => throw new InvalidOperationException("selector");

    [Fact]
    public async Task FaultAwareStateIntakeMappingsAndPredicatesHydrateBeforeSelectingAnActionSwitch()
    {
        var store = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Data>("FaultStateIntake").Step<Seed>().Try(b => b.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(caught => caught.StateMachine(m =>
            {
                m.Start("a");
                m.State("a", state => state.On<Go>("go", trigger =>
                {
                    trigger.Input(e => e.Text).From(ctx => ctx.Fault.Message);
                    trigger.Matches((evt, ctx) => evt.Text == ctx.Fault.Message);
                    trigger.Otherwise("done", body => body.Switch(ctx => ctx.Fault.Message,
                        cases => cases.Case("fault", arm => arm.Step<Echo>(activity => activity
                            .Input(x => x.Text).From(ctx => ctx.Fault.Message)
                            .Output(x => x.Text).To(ctx => ctx.WorkflowData.Text)))));
                }));
                m.State("done", state => state.Final());
            })).Build();
        await registry.RegisterWorkflowAsync(definition);
        string id;
        await using (var first = Services(store, registry))
        {
            var run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            id = run.InstanceId;
        }
        await using var second = Services(store, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "go", new Go { Text = "fault" });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("fault", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    [Fact]
    public async Task FaultCallbackFamiliesKeepRequestsDelaysInvocationMappingsAndTimeoutTransitions()
    {
        await SwitchFaultSetupCoverage.ExecuteRecoverAndDebug();
    }

    private static async Task<bool> Matches<T>(T value, T label)
    {
        var definition = Workflow.Create<Data>("Typed").Switch(ctx => value, c => c.Case(label, b => b.Step<Echo>(s => s
            .Input(a => a.Text).From(ctx => "selected").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)))).Build();
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
        return Assert.IsType<Data>(run.WorkflowData).Text == "selected";
    }
    private static WorkflowDefinition LabelDefinition(object label, bool graph)
    {
        var builder = Workflow.Create<Data>("Label").Step<Seed>();
        if (graph) return builder.Flowchart(g => g.Start("a").Node("a", e => e.Switch(ctx => (object)new MutableValue { Key = ctx.WorkflowData.Route }).Case(label, "b"))
            .Node("b", b => b.Step<Echo>(s => s.Input(a => a.Text).From(ctx => "selected").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)))).Build();
        return builder.Switch(ctx => (object)new MutableValue { Key = ctx.WorkflowData.Route }, c => c.Case(label, b => b.Step<Echo>(s => s
            .Input(a => a.Text).From(ctx => "selected").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)))).Build();
    }
    private static string LabelHash(object label, bool graph) => Hash(LabelDefinition(label, graph));
    private static string Hash(WorkflowDefinition definition) => WorkflowDefinitionValidator.Inspect(definition);
    private static ServiceProvider Services(IWorkflowStateRepository? store = null, IWorkflowCatalog? registry = null)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow(o => { o.Debugging = true; o.PayloadThresholdBytes = 1; }).Services;
        if (store != null) services.AddSingleton(store);
        if (registry != null) services.AddSingleton(registry);
        return services.BuildServiceProvider();
    }
    public enum Mode { A, B }
    public readonly struct TypedOnly(int key) : IEquatable<TypedOnly>
    {
        private readonly int _key = key;
        public bool Equals(TypedOnly other) => _key == other._key;
        public override bool Equals(object? obj) => false;
        public override int GetHashCode() => _key;
    }
    public readonly struct PrivateValue(int key)
    {
        private readonly int _key = key;
        [JsonIgnore] public int Ignored { get; } = key;
    }
    public struct MutableValue { public int Key; }
    public struct WithReference { public List<int> Values; }
    public struct NullableReference { public WithReference? Hidden; }
    public readonly struct ThrowingEquality(int key) : IEquatable<ThrowingEquality>
    {
        private readonly int _key = key;
        public bool Equals(ThrowingEquality other) => throw new InvalidOperationException("comparer");
    }
    [StructLayout(LayoutKind.Explicit)] public struct ExplicitValue { [FieldOffset(0)] public int Key; }
    [StructLayout(LayoutKind.Explicit)] public struct Opaque { [FieldOffset(0)] public int Number; [FieldOffset(0)] public IntPtr Pointer; }
    public sealed class Data
    {
        public int Route { get; set; }
        public string Text { get; set; } = "";
        public string Previous { get; set; } = "";
        public List<int> Items { get; set; } = [1];
    }
    public sealed class Fault { public string Message { get; set; } = ""; }
    public sealed class Go { public string Text { get; set; } = ""; }
    public sealed class Ask : IRequestActivity<Go>
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Seed : IAsyncActivity
    {
        public string Text { get; set; } = "entry";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Echo : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.FromException(new InvalidOperationException("fault"));
    }
    public sealed class FailInner : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.FromException(new ArgumentException("inner")); }
}
