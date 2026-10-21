using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-01")]
public sealed class FlowchartScenarios
{
    public static IEnumerable<object[]> Phases => StoreProfiles.ProfileNames.SelectMany(profile =>
        Enumerable.Range(0, 5).SelectMany(phase => new[] { false, true }
            .Select(prepare => new object[] { profile, phase, prepare })));

    public static IEnumerable<object[]> Steps => StoreProfiles.ProfileNames.SelectMany(profile => new object[][]
    {
        [profile, DebugStepKind.Into, "chart/a/decision", "chart/a/to/0"],
        [profile, DebugStepKind.Into, "chart/a/to/0", "chart/b/enter"],
        [profile, DebugStepKind.Over, "chart/a/decision", "chart/a/to/0"],
        [profile, DebugStepKind.Over, "chart/a/to/0", "chart/b/enter"],
        [profile, DebugStepKind.Out, "chart/a/enter", "chart/b/enter"],
        [profile, DebugStepKind.Over, "chart", "after"]
    });

    [Theory]
    [MemberData(nameof(Steps))]
    public async Task GraphSteppingUsesTheSavedOriginAndActivations(string profile, DebugStepKind kind,
        string start, string expected)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var services = Services(store, store.NewRepository());
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "StoredGraphStepping").WaitFor<Go>("go")
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
    [MemberData(nameof(Phases))]
    public async Task EveryFlowchartPhaseResumesThroughTheStore(string profile, int phase, bool prepare)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile, _ => { });
        var client = cluster.AddHost();
        var definition = new WorkflowBuilder<Data, Seed>([], "StoredGraphPhases")
            .Flowchart(f => f.Start("a").Node("a", e => e.When(c => c.PreviousStep.Text == "saved", "b"))
                .Node("b"))
            .Step<ReadSeed>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Text)
                .Output(a => a.Text).To(c => c.WorkflowData.Text)).Build();
        await cluster.Registry.RegisterWorkflowAsync(definition);
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(),
            new WorkflowOptions(), DateTime.UtcNow);
        checkpoint.RootData = new Data();
        using var services = new ServiceCollection().BuildServiceProvider();
        var branch = checkpoint.Continuations[0];
        branch.SetPrevious(new Seed { Text = "saved" });
        branch.Previous = SerializedValue.From(branch.RuntimePrevious!);
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, null, scopes);
        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        for (var index = 0; index <= phase; index++) transitions.Advance(scopes, checkpoint, branch, 100, 100);
        if (prepare) ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        await cluster.NewRepository().SeedAsync(instance);

        var result = await client.RecoverAsync(instance.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("saved", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CyclesAndNestedBodiesUseOrdinaryBranchAndItemSemantics(string profile)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile, _ => { });
        var client = cluster.AddHost();
        var definition = Workflow.Create<Data>("StoredGraphBodies").Step<Seed>()
            .Parallel(p => p.Do(b => b.ForEach(c => c.WorkflowData.Items, (body, item) => body
                .Flowchart(f => f.Start("work")
                    .Node("work", n => n
                        .Parallel(p => p.Do(b => b.Step<Seed>()).Do(b => b.Step<Seed>()))
                        .Saga(s => s.Step<Seed>())
                        .Step<Increment>(s => s.Input(a => a.Value).From(c => c.WorkflowData.Value)
                            .Output(a => a.Value).To(c => c.WorkflowData.Value)),
                        e => e.When(c => c.PreviousStep.Value < c.Item(item), "work"))))))
            .Build();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(3, Assert.IsType<Data>(result.WorkflowData).Value);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EntryDecisionAndEdgeStopsAndSelectionsPersistOnEveryProfile(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var services = Services(store, store.NewRepository());
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "StoredGraphPicks").WaitFor<Go>("go")
            .Flowchart(f => f.Start("a").Node("a", e => e.When(c => true, "b"))
                .Node("b", e => e.When(c => false, "a"))).WithId("chart").Build();
        var idle = await client.RunAsync(definition, new Data());
        var operations = services.GetRequiredService<IWorkflowOperations>();
        await operations.SetBreakpointsAsync(idle.InstanceId,
            [new("enter", "chart/a/enter"), new("decision", "chart/a/decision"), new("edge", "chart/a/to/0")], "set");
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Go());
        foreach (var expected in new[] { "chart/a/enter", "chart/a/decision", "chart/a/to/0" })
        {
            Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
            var inspection = (await operations.InspectAsync(idle.InstanceId))!;
            Assert.Equal(expected, inspection.Threads[0].AuthorStepId);
            result = await operations.ContinueAsync(idle.InstanceId, "next");
        }
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var trace = await operations.GetTraceAsync(idle.InstanceId);
        var replay = TraceReplay.Load(definition, trace);
        foreach (var entry in trace.Where(e => e.Entry.EntryType == TraceEntryType.TransitionTaken))
        {
            replay.Seek(entry.Sequence);
            Assert.Null(replay.CurrentStepId);
        }
        Assert.Single(trace, e => e.Entry.StepId == "root/1/node/b/1" && e.Entry.EntryType == TraceEntryType.StepEntered);
        Assert.Contains(trace, e => e.Entry.Payload is GraphTrace { Cause: TransitionCause.NoTransition });
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TheGuardSavesItsPendingChoiceAndReleaseContinues(string profile)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile, services =>
            services.AddSingleton(ExecutionLimits.Default with { MaxGraphTransitionsPerPass = 2 }));
        var client = cluster.AddHost();
        var definition = Workflow.Create<Data>("StoredGraphLimit").Flowchart(f => f.Start("a")
            .Node("a", e => e.Always("b")).Node("b", e => e.Always("c")).Node("c")).Build();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        var checkpoint = ExecutionCheckpoint.Read((await cluster.GetInstanceAsync(result.InstanceId))!.ExecutionStateJson);
        Assert.Equal("c", checkpoint.Continuations[0].Stack[0].Graph!.Pending!.Target);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ReleaseAsync(result.InstanceId)).Status);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TimeoutTransitionAndNullLandingConditionPersist(string profile)
    {
        var time = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        await using var services = Services(store, store.NewRepository(), time, compiler: true);
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "StoredGraphTimeout").WaitFor<Go>("go")
            .Flowchart(f => f.Start("a").Node("a", n => n.WaitFor<Go>("answer", configure: w => w
                .TimeoutAfter(TimeSpan.FromMilliseconds(100))
                .OnTimeout(b => b.Transition("b").WithId("timeout-transition"))),
                e => e.When(c => c.PreviousStep.Name == "never", "a")).Node("b"))
            .WithId("chart").Build();
        var idle = await client.RunAsync(definition, new Data());
        await operations.SetBreakpointsAsync(idle.InstanceId,
            [new("landing", "chart/a/decision", Condition: "context.PreviousStep.Name == \"never\"")], "set");
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        await WaitForTimeoutAsync(services.GetRequiredService<IWorkflowStateRepository>(), idle.InstanceId, time);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.RecoverAsync(idle.InstanceId)).Status);
        Assert.Contains((await operations.InspectAsync(idle.InstanceId))!.DebugState.Stop!.Diagnostics,
            diagnostic => diagnostic.Code == "BPC002");
        await operations.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        Assert.Equal(WorkflowExecutionStatus.Success, (await operations.ContinueAsync(idle.InstanceId, "finish")).Status);
        var trace = await operations.GetTraceAsync(idle.InstanceId);
        var transitionId = definition.StepTable.Single(s => s.AuthorId == "timeout-transition").CatalogId;
        var entry = Assert.Single(trace, e => e.Entry.StepId == transitionId && e.Entry.EntryType == TraceEntryType.StepEntered);
        var replay = TraceReplay.Load(definition, trace);
        replay.Seek(entry.Sequence);
        Assert.Equal(transitionId, replay.CurrentStepId);
    }

    internal static async Task WaitForTimeoutAsync(IWorkflowStateRepository repository, string instanceId,
        FakeTimeProvider? time)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // The host can consume the timeout before this read, so include closed waits.
        var wait = Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: instanceId)
            { IncludeClosed = true }).WaitAsync(patience.Token), wait => wait.DeadlineUtc != null);
        var deadline = wait.DeadlineUtc!.Value;
        var page = await repository.GetPendingEventsAsync(instanceId, 0, 100).WaitAsync(patience.Token);
        // Memory and SQLite share the injected clock; server stores report their own time.
        if (time != null)
        {
            if (deadline > page.StoreTimeUtc!.Value)
                time.Advance(deadline - page.StoreTimeUtc!.Value + TimeSpan.FromMilliseconds(1));
            return;
        }
        while (page.StoreTimeUtc!.Value < deadline)
        {
            await Task.Delay(10, patience.Token);
            page = await repository.GetPendingEventsAsync(instanceId, 0, 100).WaitAsync(patience.Token);
        }
    }

    private static ServiceProvider Services(ProfileStore store, IWorkflowStateRepository repository,
        TimeProvider? time = null, bool compiler = false)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => o.Debugging = true);
        services.AddSingleton(repository);
        if (time != null) services.AddSingleton(time);
        if (compiler) services.AddBreakpointConditions();
        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();
        try
        {
            store.StartHostLinksAsync(provider).GetAwaiter().GetResult();
            return provider;
        }
        catch { provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    public sealed class Data
    {
        public int Value { get; set; }
        public string Text { get; set; } = "";
        public List<int> Items { get; set; } = [2, 3];
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
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value++; return Task.CompletedTask; }
    }
}
