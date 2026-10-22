using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-01")]
public sealed class FlowchartTests
{
    [Fact]
    public async Task CyclesUseTheLastBodyValueAndRestoreTheIncomingValueOnEveryVisit()
    {
        await using var services = Services();
        var definition = Workflow.Create<Data>("Cycle")
            .Step<Seed>()
            .Flowchart(f => f.Start("count")
                .Node("count", n => n.Step<Increment>(s => s
                    .Input(a => a.Value).From(c => c.WorkflowData.Value)
                    .Input(a => a.Incoming).From(c => c.PreviousStep.Text)
                    .Output(a => a.Value).To(c => c.WorkflowData.Value)), e => e
                    .When(c => c.PreviousStep.Value < 3 && c.PreviousStep.Incoming == "incoming", "count")
                    .Otherwise("done"))
                .Node("done", e => e.Switch(c => c.PreviousStep.Text).Case("incoming", null)))
            .Step<ReadSeed>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Text)
                .Output(a => a.Text).To(c => c.WorkflowData.Text)).Build();

        var result = await Client(services).RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(3, Assert.IsType<Data>(result.WorkflowData).Value);
        Assert.Equal("incoming", Assert.IsType<Data>(result.WorkflowData).Text);
        var trace = await Trace(services, result.InstanceId);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, trace.Where(e => e.Entry.EntryType == TraceEntryType.NodeEntered)
            .Select(e => Assert.IsType<GraphTrace>(e.Entry.Payload).Visit));
        Assert.Equal(4, trace.Where(e => e.Entry.EntryType == TraceEntryType.NodeEntered)
            .Select(e => e.Entry.FrameActivationId).Distinct().Count());
        Assert.Equal(new[] { TransitionCause.When, TransitionCause.When, TransitionCause.Default, TransitionCause.Case },
            trace.Where(e => e.Entry.EntryType == TraceEntryType.TransitionTaken)
                .Select(e => Assert.IsType<GraphTrace>(e.Entry.Payload).Cause!.Value));
        var replay = TraceReplay.Load(definition, trace);
        foreach (var entry in trace.Where(e => e.Entry.EntryType == TraceEntryType.NodeEntered ||
                     e.Entry.EntryType == TraceEntryType.StepEntered && e.Entry.StepId!.Contains("/node/")))
        {
            replay.Seek(entry.Sequence);
            Assert.Equal(entry.Entry.StepId, replay.CurrentStepId);
            var thread = replay.Current.Threads.Single(t => t.ContinuationId == entry.Entry.ContinuationId);
            Assert.Equal(entry.Entry.StepId, thread.StepId);
            if (entry.Entry.Payload is GraphTrace graph)
            {
                Assert.Equal(entry.Entry.FrameActivationId, thread.Frames[^1].ActivationId);
                Assert.Equal(graph.Visit, thread.Frames[^1].Visit);
                Assert.Equal(0, thread.Frames[^1].NextStepIndex);
            }
        }
    }

    [Fact]
    public async Task ParallelAndSagaBodiesFinishBeforeTheNodeChooses()
    {
        await using var services = Services();
        var definition = Workflow.Create<Data>("NestedBodies").Step<Seed>()
            .Flowchart(f => f.Start("work").Node("work", n => n
                .Parallel(p => p.Do(b => b.Step<Seed>()).Do(b => b.Step<Seed>()))
                .Saga(s => s.Step<Seed>()).Step<Seed>(), e => e.Always("end")))
            .Build();
        var result = await Client(services).RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var trace = await Trace(services, result.InstanceId);
        Assert.True(trace.Single(e => e.Entry.EntryType == TraceEntryType.JoinCompleted).Sequence <
            trace.Single(e => e.Entry.EntryType == TraceEntryType.TransitionTaken).Sequence);
    }

    [Fact]
    public async Task GraphsInBranchesAndItemsKeepTheirDataAndItemContext()
    {
        await using var services = Services();
        var definition = Workflow.Create<Data>("NestedGraphs").Step<Seed>()
            .Parallel(p => p.Do(b => b.Flowchart(f => f.Start("item").Node("item", n => n
                .ForEach(c => c.WorkflowData.Items, (body, item) => body
                    .Flowchart(g => g.Start("inner").Node("inner", e => e
                        .When(c => c.Item(item) > 0, null))))))))
            .Build();
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await Client(services).RunAsync(definition, new Data())).Status);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(3, false)] [InlineData(4, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    [InlineData(3, true)] [InlineData(4, true)]
    public void EveryFlowchartPhaseRecoversWithThePreviousValueLiveOrPrepared(int phase, bool prepare)
    {
        using var services = Services();
        var definition = new WorkflowBuilder<Data, Seed>([], "Phases")
            .Flowchart(f => f.Start("a").Node("a", e => e.Always("b")).Node("b"))
            .Build();
        WorkflowDefinitionValidator.Validate(definition);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var branch = new ContinuationState { Id = "root", Stack = [new() { ScopeId = "root" }] };
        branch.SetPrevious(new Seed { Text = "saved" });
        branch.Previous = SerializedValue.From(branch.RuntimePrevious!);
        var checkpoint = new ExecutionCheckpoint { StructureHash = scopes.StructureHash, RootData = new Data(), Continuations = [branch] };
        checkpoint.Trace.Configure(new WorkflowInstance { InstanceId = "phases" }, checkpoint, ExecutionLimits.Default, null, scopes);
        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        // Running, Deciding, EdgePushed, EdgeChosen, Leaving: the five flowchart rows of table 1.5.
        for (var index = 0; index <= phase; index++) transitions.Advance(scopes, checkpoint, branch, 100, 100);
        if (prepare) ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
        if (prepare && phase >= 3) Assert.Null(branch.Previous);
        var read = ExecutionCheckpoint.Read(JsonSerializer.Serialize(checkpoint));
        read.RootData = new Data();
        read.Trace.Configure(new WorkflowInstance { InstanceId = "phases" }, read, ExecutionLimits.Default, null, scopes);
        branch = read.Continuations[0];
        for (var index = 0; index < 30 && branch.Status != ContinuationStatus.Completed; index++)
            transitions.Advance(scopes, read, branch, 100, 100);
        Assert.Equal(ContinuationStatus.Completed, branch.Status);
        Assert.Equal("saved", Assert.IsType<Seed>(branch.GetPrevious(services)).Text);
        Assert.Contains(read.Trace.Pending, entry => entry.EntryType == TraceEntryType.GraphCompleted);
    }

    [Fact]
    public async Task ThePerPassGuardStopsBeforePopAndReleaseTakesThePendingTransition()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store, limit: 2);
        var definition = Workflow.Create<Data>("Guard").Flowchart(f => f.Start("a")
            .Node("a", e => e.Always("b")).Node("b", e => e.Always("c")).Node("c")).Build();
        var stopped = await Client(services).RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        var checkpoint = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(stopped.InstanceId))!.ExecutionStateJson);
        Assert.Equal(FrameKind.Node, checkpoint.Continuations[0].Stack[^1].Kind);
        Assert.Equal("c", checkpoint.Continuations[0].Stack[0].Graph!.Pending!.Target);
        Assert.Equal(2, checkpoint.Continuations[0].Stack[0].Graph!.Visits);
        Assert.Equal(WorkflowExecutionStatus.Success, (await Client(services).ReleaseAsync(stopped.InstanceId)).Status);
    }

    [Fact]
    public async Task AGuardedCycleWhoseEffectsAreSkippedStopsForAnOperator()
    {
        await using var services = Services(limit: 3);
        var definition = Workflow.Create<Data>("SkippedEffects").Flowchart(f => f.Start("a")
            .Node("a", n => n.If(c => false, b => b.Step<Seed>()), e => e.When(c => true, "a"))).Build();
        var result = await Client(services).RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        Assert.Contains("3 entries per pass", result.ErrorMessage);
        Assert.DoesNotContain(await Trace(services, result.InstanceId), e => e.Entry.EntryType == TraceEntryType.ActivityStarted);
    }

    [Fact]
    public async Task ClaimCheckedIncomingValuesAreHydratedBeforeAnEntrySnapshotIsRestored()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store, payloadThreshold: 64_000);
        var definition = Workflow.Create<Data>("GraphPayload")
            .Step<Seed>(s => s.Input(a => a.Text).From(c => c.WorkflowData.Text))
            .Flowchart(f => f.Start("wait")
                .Node("wait", n => n.WaitFor<Go>("resume"), e => e.Always("read"))
                .Node("read", e => e.When(c => c.PreviousStep.Text.Length == 300_000, null)))
            .Step<ReadSeed>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Text)
                .Output(a => a.Text).To(c => c.WorkflowData.Text)).Build();
        var idle = await Client(services).RunAsync(definition, new Data { Text = new string('x', 300_000) });
        var checkpoint = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson);
        Assert.NotNull(checkpoint.Continuations[0].Stack[^1].EntryPrevious!.Json.Reference);
        var result = await Client(services).ResumeAsync(idle.InstanceId, "resume", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(300_000, Assert.IsType<Data>(result.WorkflowData).Text.Length);
    }

    [Theory]
    [InlineData(DebugStepKind.Into, "chart/a/decision", "chart/a/to/0")]
    [InlineData(DebugStepKind.Into, "chart/a/to/0", "chart/b/enter")]
    [InlineData(DebugStepKind.Out, "chart/a/enter", "chart/b/enter")]
    [InlineData(DebugStepKind.Over, "chart", "after")]
    public async Task SteppingUsesTheOriginGraphAndNodeActivation(DebugStepKind kind, string start, string expected)
    {
        await using var services = Services();
        var client = Client(services); var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "GraphStepping").WaitFor<Go>("go")
            .Flowchart(f => f.Start("a").Node("a", e => e.Always("b")).Node("b"))
            .WithId("chart").Step<Seed>().WithId("after").Build();
        var idle = await client.RunAsync(definition, new Data());
        await operations.SetBreakpointsAsync(idle.InstanceId, [new("start", start)], "set");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        var before = (await operations.InspectAsync(idle.InstanceId))!;
        await operations.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await operations.StepAsync(idle.InstanceId, before.Threads[0].ContinuationId, kind, "step")).Status);
        Assert.Equal(expected, (await operations.InspectAsync(idle.InstanceId))!.Threads[0].AuthorStepId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EntryDecisionAndEdgeAreRealBreakpointsAndReplayStops(bool noMatch)
    {
        await using var services = Services();
        var definition = new WorkflowBuilder<Data, object>([], "Picks" + noMatch).WaitFor<Go>("go")
            .Flowchart(f => f.Start("a").Node("a", e => e.When(c => !noMatch, "b")).Node("b"))
            .WithId("chart").Step<Seed>().WithId("after").Build();
        var client = Client(services); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("entry", "chart/a/enter")], "set");
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
        Assert.Equal("Node", (await ops.InspectAsync(idle.InstanceId))!.Threads[0].Frames[^1].Kind);
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        foreach (var expected in noMatch ? new[] { "chart/a/decision", "after" } :
                     new[] { "chart/a/decision", "chart/a/to/0", "chart/b/enter", "chart/b/decision", "after" })
        {
            result = await ops.StepAsync(idle.InstanceId, (await ops.InspectAsync(idle.InstanceId))!.Threads[0].ContinuationId, DebugStepKind.Over, "step");
            Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
            Assert.Equal(expected, (await ops.InspectAsync(idle.InstanceId))!.Threads[0].AuthorStepId);
        }
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "finish")).Status);
        var entries = await Trace(services, idle.InstanceId);
        var replay = TraceReplay.Load(definition, entries);
        var stops = new List<string>();
        do { if (replay.CurrentStepId is { } id) stops.Add(id); } while (replay.StepForward());
        var decisionId = definition.StepTable.Single(s => s.AuthorId == "chart/a/decision").CatalogId;
        Assert.Single(stops, id => id == decisionId);
        foreach (var selection in entries.Where(e => e.Entry.EntryType == TraceEntryType.TransitionTaken))
        {
            replay.Seek(selection.Sequence);
            Assert.Null(replay.CurrentStepId);
        }
        Assert.All(entries.Where(e => e.Entry.Payload is GraphTrace), e =>
        {
            Assert.NotNull(e.Entry.StepId);
            Assert.NotNull(e.Entry.ContinuationId);
            Assert.IsType<GraphTrace>(JsonSerializer.Deserialize<ExecutionTraceEntry>(JsonSerializer.Serialize(e.Entry))!.Payload);
        });
    }

    [Fact]
    public async Task TimeoutLandsOnDecisionWithNullPreviousAndSkipsGuardEvaluation()
    {
        var time = new FakeTimeProvider(); var store = new InMemoryWorkflowStateRepository(time);
        await using var services = Services(store, time: time, compiler: true);
        var definition = new WorkflowBuilder<Data, object>([], "Timeout").WaitFor<Go>("go")
            .Flowchart(f => f.Start("a").Node("a", n => n.WaitFor<Go>("answer", configure: w => w
                .TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(b => b.Transition("b"))),
                e => e.When(c => c.PreviousStep.Name == "never", "a"))
                .Node("b"))
            .WithId("chart").Build();
        var client = Client(services); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId,
            [new("landing", "chart/a/decision", Condition: "context.PreviousStep.Name == \"never\"")], "set");
        await client.ResumeAsync(idle.InstanceId, "go", new Go());
        time.Advance(TimeSpan.FromMinutes(2));
        var paused = await client.RecoverAsync(idle.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        var state = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson);
        Assert.Null(state.Continuations[0].Previous);
        Assert.Contains(state.Debug.Stop!.Diagnostics, d => d.Code == "BPC002");
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "finish")).Status);
        Assert.DoesNotContain(await Trace(services, idle.InstanceId), e => e.Entry.StepId == "root/1/node/a/to/0/0");
    }

    [Fact]
    public async Task ATimeoutInANestedSequenceContinuesTheNodeWithoutAnExplicitTransition()
    {
        var time = new FakeTimeProvider();
        await using var services = Services(new InMemoryWorkflowStateRepository(time), time: time);
        var definition = Workflow.Create<Data>("NestedTimeout").Step<Seed>()
            .Flowchart(f => f.Start("a").Node("a", n => n.Sequence(s => s.WaitFor<Go>("wait", configure: w => w
                .TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(b => b.Step<Seed>())))
                .Step<Increment>(s => s.Input(a => a.Incoming).From(c => c.PreviousStep.Text)
                    .Output(a => a.Value).To(c => c.WorkflowData.Value)),
                e => e.When(c => c.PreviousStep.Incoming == "incoming", null))).Build();
        var idle = await Client(services).RunAsync(definition, new Data());
        time.Advance(TimeSpan.FromMinutes(2));
        var result = await Client(services).RecoverAsync(idle.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Assert.IsType<Data>(result.WorkflowData).Value);
    }

    [Fact]
    public async Task FailureUnwindsANodeAndTracesItsExitBeforeTheOuterCatch()
    {
        await using var services = Services();
        var definition = Workflow.Create<Data>("GraphFailure").Step<Seed>()
            .Try(b => b.Flowchart(f => f.Start("a").Node("a", n => n.Step<Fail>())))
            .Catch<InvalidOperationException>(b => b.Step<Seed>()).Build();
        var result = await Client(services).RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var trace = await Trace(services, result.InstanceId);
        var exit = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.NodeExited);
        Assert.NotNull(Assert.IsType<GraphTrace>(exit.Entry.Payload).ExitCause);
        Assert.True(exit.Sequence < trace.Single(e => e.Entry.EntryType == TraceEntryType.CatchEntered).Sequence);
    }

    [Fact]
    public void KeysTargetsAndEdgeOrderAreStructureButNodeOrderAndNamesAreNot()
    {
        WorkflowDefinition Define(bool reverse = false, string name = "A", string target = "b", string key = "a")
        {
            var builder = Workflow.Create<Data>("Hash").Flowchart(f =>
            {
                f.Start(key);
                if (reverse) f.Node("b");
                f.Node(key, e => e.Always(target)).Named(name);
                if (!reverse) f.Node("b");
            });
            return builder.Build();
        }
        string Hash(WorkflowDefinition definition) => WorkflowDefinitionValidator.Inspect(definition);
        Assert.Equal(Hash(Define()), Hash(Define(true, "renamed")));
        Assert.NotEqual(Hash(Define()), Hash(Define(target: "end")));
        Assert.NotEqual(Hash(Define()), Hash(Define(key: "renamed")));
        var clone = Define().Steps[0].Clone();
        Assert.Equal(Define().Steps[0].ChildScopes().Select(s => s.Steps.Count), clone.ChildScopes().Select(s => s.Steps.Count));
        Assert.Equal(WorkflowDefinitionValidator.CheckpointContractVersion,
            ExecutionCheckpoint.Read(JsonSerializer.Serialize(new ExecutionCheckpoint())).SchemaVersion);
    }

    [Fact]
    public void TheCheckpointReaderRefusesThePreviousContractVersion()
    {
        var previous = JsonSerializer.Serialize(new ExecutionCheckpoint
        { SchemaVersion = WorkflowDefinitionValidator.CheckpointContractVersion - 1, StructureHash = "previous" });
        var error = Assert.Throws<WorkflowEngineException>(() => ExecutionCheckpoint.Read(previous));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        Assert.Contains($"expected {WorkflowDefinitionValidator.CheckpointContractVersion}", error.Message);
    }

    [Theory]
    [InlineData("missing-start")] [InlineData("duplicate")] [InlineData("slash")]
    [InlineData("reserved")] [InlineData("target")] [InlineData("timeout")]
    [InlineData("explicit")] [InlineData("default-order")] [InlineData("mixed")]
    public void InvalidGraphsAreRefusedAtBuild(string shape)
    {
        Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "Invalid").Flowchart(f =>
        {
            if (shape != "missing-start") f.Start("a");
            if (shape == "duplicate") f.Node("a");
            if (shape == "slash") f.Node("a/b");
            if (shape == "reserved") f.Node("end");
            if (shape == "timeout") f.Node("a", n => n.WaitFor<Go>("wait", configure: w => w
                .TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(b => b.Step<Seed>())));
            else if (shape == "explicit") f.Node("a", n => n.Transition("a"));
            else f.Node("a", e =>
            {
                if (shape == "target") e.Always("absent");
                if (shape == "default-order") e.Otherwise(null).When(c => true, "a");
                if (shape == "mixed") { e.When(c => true, "a"); e.Switch(c => 1).Case(1, null); }
            });
        }).Build());
    }

    private static ServiceProvider Services(IWorkflowStateRepository? store = null, int limit = 1000,
        TimeProvider? time = null, bool compiler = false, long payloadThreshold = 0)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o =>
        { o.Debugging = true; o.MaxGraphTransitionsPerPass = limit; o.PayloadThresholdBytes = payloadThreshold; });
        if (store != null) services.AddSingleton(store);
        if (time != null) services.AddSingleton(time);
        if (compiler) services.AddBreakpointConditions();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Client(IServiceProvider services) => services.GetRequiredService<IWorkflowClient>();
    private static async Task<StoredTraceEntry[]> Trace(IServiceProvider services, string id) =>
        (await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id, limit: 1000)).ToArray();

    public sealed class Data
    {
        public int Value { get; set; }
        public string Text { get; set; } = "";
        public List<int> Items { get; set; } = [1, 2];
    }
    public sealed class Go { public string Name { get; set; } = ""; }
    public sealed class Seed : IAsyncActivity
    {
        public string Text { get; set; } = "incoming";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class ReadSeed : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Increment : IAsyncActivity
    {
        public int Value { get; set; }
        public string Incoming { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value++; return Task.CompletedTask; }
    }
    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Node failed"));
    }
}
