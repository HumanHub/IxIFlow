using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.ExecutionTests.SwitchTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "W-03")]
public sealed class SwitchReviewFixTests
{
    [Fact]
    public async Task ImmutableCollectionCallsSelectWithoutMutatingButBuilderCallsAreRejected()
    {
        var definition = Workflow.Create<ImmutableData>("ImmutableSwitch")
            .Switch(ctx => ctx.WorkflowData.Items.Add(1).Count, c => c.Case(2, b => b.Step<Echo>(a => a.Input(x => x.Text)
                .From(ctx => "selected").Output(x => x.Text).To(ctx => ctx.WorkflowData.Text)))).Build();

        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ImmutableData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { 7 }, Assert.IsType<ImmutableData>(result.WorkflowData).Items);
        Assert.Equal("selected", Assert.IsType<ImmutableData>(result.WorkflowData).Text);

        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<ImmutableData>("MutableBuilder")
            .Switch(ctx => ctx.WorkflowData.Builder.Remove(7), _ =>
            {
            }).Build());

        Assert.Contains("state mutation", error.Message);

        _ = Workflow.Create<ImmutableData>("PersistentSwitch")
            .Switch(ctx => ctx.WorkflowData.Persistent.Add(1).Count, _ =>
            {
            }).Build();

        _ = Workflow.Create<ImmutableData>("ImmutableInterface")
            .Switch(ctx => ((System.Collections.Immutable.IImmutableList<int>)ctx.WorkflowData.Items).Remove(7, null).Clear().Count, _ =>
            {
            }).Build();

        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<ImmutableData>("MutableText")
            .Switch(ctx => ctx.WorkflowData.MutableText.Append(1).Length, _ =>
            {
            }).Build());
    }

    public sealed class ImmutableData
    {
        public System.Collections.Immutable.ImmutableList<int> Items { get; set; } = [7];
        public string Text { get; set; } = "";

        [JsonIgnore]
        public System.Collections.Immutable.ImmutableList<int>.Builder Builder { get; } =
            System.Collections.Immutable.ImmutableList.CreateBuilder<int>();

        [JsonIgnore] public System.Text.StringBuilder MutableText { get; } = new();
        [JsonIgnore] public PersistentCollection Persistent { get; } = new();
    }

    public sealed class PersistentCollection : System.Collections.IEnumerable
    {
        public int Count => 0;
        public PersistentCollection Add(int value) => new();
        public System.Collections.IEnumerator GetEnumerator() => Array.Empty<int>().GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineArrayIdentityIncludesEveryElementAndRoutingUsesThem(bool graph)
    {
        var first = new Pair();
        first[0] = 1;
        first[1] = 2;
        var second = first;
        second[1] = 3;

        WorkflowDefinition Define(Pair label)
        {
            var builder = Workflow.Create<Data>("InlineLabel");

            if (graph)
                return builder.Flowchart(g => g.Start("a").Node("a", e => e.Switch(ctx => first).Case(label, "b"))
                    .Node("b",
                        b => b.Step<Echo>(s =>
                            s.Input(a => a.Text).From(ctx => "selected").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)))).Build();

            return builder.Switch(ctx => first,
                c => c.Case(label,
                    b => b.Step<Echo>(s =>
                        s.Input(a => a.Text).From(ctx => "selected").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text)))).Build();
        }

        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(Define(first)), WorkflowDefinitionValidator.Inspect(Define(second)));

        foreach (var label in new[] { first, second })
        {
            await using var services = Services();
            var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(Define(label), new Data());
            Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
            Assert.Equal(label.Equals(first) ? "selected" : "", Assert.IsType<Data>(run.WorkflowData).Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedAndStringInlineArrayLabelsIncludeTheirWholeStorage(bool graph)
    {
        var nested = new NestedPair();
        nested[0][0] = 1;
        nested[0][1] = 2;
        nested[1][0] = 3;
        nested[1][1] = 4;
        var changedNested = nested;
        changedNested[1][1] = 5;
        var strings = new StringPair();
        strings[0] = "first";
        strings[1] = "last";
        var changedStrings = strings;
        changedStrings[1] = "changed";
        await Verify(nested, changedNested);
        await Verify(strings, changedStrings);

        async Task Verify<T>(T selected, T different)
        {
            WorkflowDefinition Define(T label)
            {
                var builder = Workflow.Create<Data>("InlineStorage");

                if (graph)
                    return builder.Flowchart(g => g.Start("a").Node("a", e => e.Switch(ctx => selected).Case(label, "b"))
                            .Node("b",
                                b => b.Step<Echo>(a =>
                                    a.Input(x => x.Text).From(ctx => "selected").Output(x => x.Text).To(ctx => ctx.WorkflowData.Text))))
                        .Build();

                return builder.Switch(ctx => selected,
                    c => c.Case(label,
                        b => b.Step<Echo>(a =>
                            a.Input(x => x.Text).From(ctx => "selected").Output(x => x.Text).To(ctx => ctx.WorkflowData.Text)))).Build();
            }

            Assert.NotEqual(WorkflowDefinitionValidator.Inspect(Define(selected)), WorkflowDefinitionValidator.Inspect(Define(different)));

            foreach (var label in new[] { selected, different })
            {
                await using var services = Services();
                var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(Define(label), new Data());
                Assert.Equal(WorkflowExecutionStatus.Success, result.Status);

                Assert.Equal(EqualityComparer<T>.Default.Equals(selected, label) ? "selected" : "",
                    Assert.IsType<Data>(result.WorkflowData).Text);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InlineArraysRejectReferenceElementsEvenWhenNull(bool graph, bool populated)
    {
        var label = new ReferencePair();

        if (populated)
            label[1] = new LabelData();

        var error = Assert.Throws<WorkflowEngineException>(() =>
        {
            var builder = Workflow.Create<Data>("ReferenceInline");

            return graph
                ? builder.Flowchart(g => g.Start("a").Node("a", e => e.Switch(ctx => label).Case(label, null))).Build()
                : builder.Switch(ctx => label, c => c.Case(label, _ =>
                {
                })).Build();
        });

        Assert.Contains("Unsupported or opaque switch label state", error.Message);
        Assert.Contains(nameof(LabelData), error.Message);
    }

    [Fact]
    public void GraphCatalogUsesCanonicalStateWithoutJsonConvertersOrGetters()
    {
        var nan = Workflow.Create<LabelData>("NaN")
            .Flowchart(g => g.Start("a").Node("a", e => e.Switch(ctx => ctx.WorkflowData.Number).Case(double.NaN, null))).Build();

        var custom = Workflow.Create<Data>("Converter").Flowchart(g =>
            g.Start("a").Node("a", e => e.Switch(ctx => new CatalogLabel(1)).Case(new CatalogLabel(1), null))).Build();

        var getter = Workflow.Create<Data>("Getter")
            .Flowchart(g => g.Start("a").Node("a", e => e.Switch(ctx => new GetterLabel(1)).Case(new GetterLabel(1), null))).Build();

        Assert.Contains("switch-label/1", getter.GraphTable.Single().Transitions.Single().Cases.Single().LabelSignature);
        Assert.Contains("switch-label/1", nan.GraphTable.Single().Transitions.Single().Cases.Single().LabelSignature);
        Assert.Contains("switch-label/1", custom.GraphTable.Single().Transitions.Single().Cases.Single().LabelSignature);
    }

    [Fact]
    public void NestedMemberInitializationCannotWriteThroughAnExistingReceiver()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("NestedMutation")
            .Switch(ctx => new Wrapper { Existing = { Flag = true } }.Existing.Flag, _ =>
            {
            }).Build());

        Assert.Contains("Existing", error.Message);
        Assert.Contains("existing member state", error.Message);
        Assert.False(Wrapper.Shared.Flag);

        _ = Workflow.Create<Data>("FreshStruct").Switch(ctx => new ValueWrapper { State = { Flag = true } }.State.Flag, _ =>
        {
        }).Build();
    }

    [Fact]
    public async Task CollectionBindingOnAFreshStructFieldRunsButReferencesAndGettersAreRejected()
    {
        var definition = Workflow.Create<Data>("FreshCollection")
            .Switch(ctx => new CollectionHolder { Values = { 1 } }.Values.Count,
                c => c.Case(1, b => b.Step<Echo>(a => a.Input(x => x.Text).From(ctx => "selected")
                    .Output(x => x.Text).To(ctx => ctx.WorkflowData.Text)))).Build();

        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("selected", Assert.IsType<Data>(result.WorkflowData).Text);

        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("ReferenceCollection")
            .Switch(ctx => new CollectionHolder { Items = { 1 } }.Items.Count, _ =>
            {
            }).Build());

        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("GetterCollection")
            .Switch(ctx => new CollectionHolder { Getter = { 1 } }.Getter.Count, _ =>
            {
            }).Build());

        Assert.Empty(CollectionHolder.Shared);
    }

    public struct CollectionCounter : System.Collections.IEnumerable
    {
        public int Count;
        public void Add(int value) => Count += value;
        public System.Collections.IEnumerator GetEnumerator() => Array.Empty<int>().GetEnumerator();
    }

    public sealed class CollectionHolder
    {
        public static List<int> Shared { get; } = [];
        public CollectionCounter Values;
        public List<int> Items = Shared;
        public List<int> Getter => Shared;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandlerWaitValidationVisitsCaseAndDefault(bool fallback)
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("HandlerWait").Step<Seed>().Saga(s => s.Step<Fail>())
            .OnError(e => e.Switch(ctx => true, c =>
            {
                if (fallback)
                    c.Default(b => b.WaitFor<Go>("go"));
                else
                    c.Case(true, b => b.WaitFor<Go>("go"));
            })).Build());

        Assert.Contains("ThenContinue, ThenRetry, or ThenTerminate", error.Message);

        Assert.DoesNotContain(typeof(ISagaErrorBodyBuilder<Data, Fault, Seed>).GetMethods(),
            m => m.Name.StartsWith("Compensate") || m.Name.StartsWith("Then"));
    }

    [Fact]
    public async Task StateIntakeReadsIncomingPreviousAndActionsReadAcceptedEventAfterRestart()
    {
        var registry = new WorkflowCatalog();
        var repository = new InMemoryWorkflowStateRepository();

        var definition = Workflow.Create<Data>("PreviousIntake").Step<Seed>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(c => c.StateMachine(m => m.Start("a").State("a", s => s.On<Go>("go", t => t
                    .Matches((evt, ctx) => ctx.PreviousStep.Text == "entry" && evt.Text == ctx.Fault.Message)
                    .When(ctx => ctx.PreviousStep.Text == "fault", "done",
                        b => b.Step<Echo>(a =>
                            a.Input(x => x.Text).From(ctx => ctx.PreviousStep.Text).Output(x => x.Text).To(ctx => ctx.WorkflowData.Text)))))
                .State("done", s => s.Final()))).Build();

        await registry.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = Services(repository, registry))
        {
            var waiting = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
            id = waiting.InstanceId;
        }

        await using var second = Services(repository, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "go", new Go { Text = "fault" });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("fault", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    [Fact]
    public async Task DirectCatchDelayRestoresFaultAndPreviousAfterItsSavedDeadline()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var registry = new WorkflowCatalog();

        var definition = Workflow.Create<Data>("CatchDelay").Step<Seed>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(c => c
                .Delay(TimeSpan.FromSeconds(1)).Named("Recovery delay").Step<Echo>(a =>
                    a.Input(x => x.Text).From(ctx => ctx.Fault.Message + ctx.PreviousStep.Text).Output(x => x.Text)
                        .To(ctx => ctx.WorkflowData.Text))).Build();

        await registry.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = Services(repository, registry, time))
        {
            var run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            id = run.InstanceId;
        }

        time.Advance(TimeSpan.FromSeconds(2));
        await using var second = Services(repository, registry, time);
        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("faultentry", Assert.IsType<Data>(result.WorkflowData).Text);
        Assert.DoesNotContain(typeof(ICatchWorkflowBuilder<Data, Fault, Seed>).GetMethods(), m => m.Name is "Result" or "Transition");
    }

    [Fact]
    public async Task FaultInvocationCancellationSetupLeavesTheDetachedChildRecoverable()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        var child = Workflow.Create<Data>("DetachedFaultChild").Step<Seed>().WaitFor<Go>("child-go").Build();
        await registry.RegisterWorkflowAsync(child);

        var parent = Workflow.Create<Data>("DetachFromCatch").Step<Seed>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(c => c.Invoke<Data>("DetachedFaultChild", 1, setup => setup.CancelWithParent(false)
                .Input(x => x.Text).From(ctx => ctx.Fault.Message))).Build();

        await registry.RegisterWorkflowAsync(parent);
        string childId;

        await using (var first = Services(repository, registry))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var run = await client.RunAsync(parent, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            var saved = (await repository.GetWorkflowInstanceAsync(run.InstanceId))!;

            var reader = new PayloadReader(async reference => (await repository.PayloadStore.GetAsync(reference))!.Value,
                new PayloadBudget(long.MaxValue));

            var wait = Assert.Single(ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(saved, reader)).Waits,
                w => w.ChildWorkflowId != null);

            Assert.True(wait.Detached);
            childId = wait.ChildWorkflowId!;
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.AwaitAsync(childId)).Status);

            Assert.Equal(WorkflowExecutionStatus.Cancelled,
                (await client.CancelAsync(run.InstanceId, new CancellationReason { ReasonCode = "test" })).Status);

            Assert.Equal(WorkflowStatus.Suspended, (await repository.GetWorkflowInstanceAsync(childId))!.Status);
        }

        await using var second = Services(repository, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(childId, "child-go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("fault", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    internal static ServiceProvider Services(IWorkflowStateRepository? repository = null, IWorkflowCatalog? registry = null,
        TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.PayloadThresholdBytes = 1;
        }).Services;

        if (repository != null)
            services.AddSingleton(repository);

        if (registry != null)
            services.AddSingleton(registry);

        if (time != null)
            services.AddSingleton(time);

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultForEachResultAndTimeoutExitsRetainTheCurrentPreviousType(bool timeout)
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var registry = new WorkflowCatalog();

        var definition = Workflow.Create<ResultData>("FaultResults").Step<Seed>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(c => c.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) =>
            {
                if (timeout)
                    b.WaitFor<Go>("go", configure: w => w.TimeoutAfter(TimeSpan.FromSeconds(1)).OnTimeout(end => end
                            .Step<Echo>(a => a.Input(x => x.Text).From(ctx => ctx.Fault.Message)).Result(ctx => ctx.PreviousStep.Text)))
                        .Result(ctx => ctx.PreviousStep.Text);
                else
                    b.Step<Echo>(a => a.Input(x => x.Text).From(ctx => ctx.Fault.Message)).Result(ctx => ctx.PreviousStep.Text);
            }, setup: s => s.Into<string>(ctx => ctx.WorkflowData.Results))).Build();

        await registry.RegisterWorkflowAsync(definition);
        WorkflowExecutionResult result;

        await using (var first = Services(repository, registry, time))
            result = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ResultData());

        if (timeout)
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            time.Advance(TimeSpan.FromSeconds(2));
            await using var second = Services(repository, registry, time);
            result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(result.InstanceId);
        }

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("fault", Assert.Single(Assert.IsType<ResultData>(result.WorkflowData).Results).Value);
    }

    [InlineArray(2)]
    public struct Pair : IEquatable<Pair>
    {
        private int _first;
        public bool Equals(Pair other) => this[0] == other[0] && this[1] == other[1];
    }

    [InlineArray(2)]
    public struct NestedPair : IEquatable<NestedPair>
    {
        private Pair _first;
        public bool Equals(NestedPair other) => this[0].Equals(other[0]) && this[1].Equals(other[1]);
    }

    [InlineArray(2)]
    public struct StringPair : IEquatable<StringPair>
    {
        private string? _first;
        public bool Equals(StringPair other) => this[0] == other[0] && this[1] == other[1];
    }

    [InlineArray(2)]
    public struct ReferencePair : IEquatable<ReferencePair>
    {
        private LabelData? _first;
        public bool Equals(ReferencePair other) => ReferenceEquals(this[0], other[0]) && ReferenceEquals(this[1], other[1]);
    }

    [JsonConverter(typeof(BrokenConverter))]
    public readonly struct CatalogLabel(int value) : IEquatable<CatalogLabel>
    {
        private readonly int _value = value;
        public bool Equals(CatalogLabel other) => _value == other._value;
        public int Computed => throw new InvalidOperationException("Getter must not run");
    }

    public sealed class BrokenConverter : JsonConverter<CatalogLabel>
    {
        public override CatalogLabel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, CatalogLabel value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    public sealed class LabelData
    {
        public double Number { get; set; }
    }

    public sealed class ResultData
    {
        public List<int> Items { get; set; } = [1];
        public List<ItemResult<string>> Results { get; set; } = [];
    }

    public readonly struct GetterLabel(int value) : IEquatable<GetterLabel>
    {
        private readonly int _value = value;
        public bool Equals(GetterLabel other) => _value == other._value;
        public int Computed => throw new InvalidOperationException("Getter must not run");
    }

    public struct ValueFlag
    {
        public bool Flag;
    }

    public sealed class ValueWrapper
    {
        public ValueFlag State;
    }

    public sealed class FlagState
    {
        public bool Flag { get; set; }
    }

    public sealed class Wrapper
    {
        public static FlagState Shared { get; } = new();
        public FlagState Existing => Shared;
    }
}