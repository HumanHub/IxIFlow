using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow"), Trait("Spec", "W-01")]
public sealed class FlowchartReviewScenarios
{
    public static IEnumerable<object[]> OutCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "sequence", "timeout" }.SelectMany(body => new[] { "b", "end" }
            .Select(target => new object[] { profile, body, target })));

    public static IEnumerable<object[]> RecoveryCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        Enumerable.Range(0, 5).SelectMany(phase => new[] { false, true }.SelectMany(prepare =>
            new[] { false, true }.Select(claimCheck => new object[] { profile, phase, prepare, claimCheck }))));

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task GraphStepBreakpointHasOneMatchingReplayStopBeforeNodeEntry(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var services = Services(store, store.NewRepository());
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "GraphEntryReview").WaitFor<Go>("go")
            .Flowchart(f => f.Start("a").Node("a")).WithId("chart").Step<BodyResult>().Build();
        var idle = await client.RunAsync(definition, new Data());
        await operations.SetBreakpointsAsync(idle.InstanceId, [new("chart-break", "chart")], "set");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        var live = (await operations.InspectAsync(idle.InstanceId))!.Threads.Single();
        Assert.Equal("chart", live.AuthorStepId);
        var liveFrame = Assert.Single(live.Frames);
        await operations.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        Assert.Equal(WorkflowExecutionStatus.Success, (await operations.ContinueAsync(idle.InstanceId, "finish")).Status);

        var trace = await operations.GetTraceAsync(idle.InstanceId);
        var entry = Assert.Single(trace, e => e.Entry.StepId == live.StepId && e.Entry.EntryType == TraceEntryType.StepEntered);
        Assert.True(entry.Sequence < trace.Single(e => e.Entry.EntryType == TraceEntryType.NodeEntered).Sequence);
        var replay = TraceReplay.Load(definition, trace);
        replay.SetBreakpoints([live.StepId!]);
        Assert.True(replay.Continue());
        Assert.Equal(entry.Sequence, replay.Current.Sequence);
        Assert.Equal(live.StepId, replay.CurrentStepId);
        var replayThread = replay.Current.Threads.Single(t => t.ContinuationId == live.ContinuationId);
        Assert.Equal(live.StepId, replayThread.StepId);
        var replayFrame = Assert.Single(replayThread.Frames);
        Assert.Equal(liveFrame.ActivationId, replayFrame.ActivationId);
        Assert.Equal(liveFrame.NextStepIndex, replayFrame.NextStepIndex);
    }

    [Theory, MemberData(nameof(OutCases))]
    public async Task OutFromNestedNodeBodyRunsUntilTheNodeActivationDisappears(string profile, string body, string target)
    {
        var time = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        await using var services = Services(store, store.NewRepository(), time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "NestedOutReview").WaitFor<Go>("go")
            .Flowchart(f =>
            {
                f.Start("a");
                if (body == "sequence")
                    f.Node("a", n => n.Sequence(s => s.Step<BodyResult>().WithId("origin")
                        .Step<BodyResult>().WithId("nested-second")).Step<BodyResult>().WithId("same-node"),
                        e => e.Always(target));
                else
                    f.Node("a", n => n.WaitFor<Go>("answer", configure: w => w
                        .TimeoutAfter(TimeSpan.FromSeconds(1)).OnTimeout(b => b
                            .Step<BodyResult>().WithId("origin").Transition(target).WithId("explicit"))),
                        e => e.When(c => false, "a"));
                f.Node("b");
            }).WithId("chart").Step<BodyResult>().WithId("after").Build();
        var idle = await client.RunAsync(definition, new Data());
        await operations.SetBreakpointsAsync(idle.InstanceId, [new("origin-break", "origin")], "set");
        var resumed = await client.ResumeAsync(idle.InstanceId, "go", new Go());
        if (body == "timeout")
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, resumed.Status);
            await FlowchartScenarios.WaitForTimeoutAsync(services.GetRequiredService<IWorkflowStateRepository>(),
                idle.InstanceId, time);
            resumed = await client.RecoverAsync(idle.InstanceId);
        }
        Assert.Equal(WorkflowExecutionStatus.Paused, resumed.Status);
        var before = (await operations.InspectAsync(idle.InstanceId))!.Threads.Single();
        Assert.Equal("origin", before.AuthorStepId);
        var source = Assert.Single(before.Frames, frame => frame.Kind == "Node");
        await operations.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await operations.StepAsync(idle.InstanceId, before.ContinuationId, DebugStepKind.Out, "out")).Status);
        var after = (await operations.InspectAsync(idle.InstanceId))!.Threads.Single();
        Assert.Equal(target == "b" ? "chart/b/enter" : "after", after.AuthorStepId);
        Assert.DoesNotContain(after.Frames, frame => frame.ActivationId == source.ActivationId);
        var trace = await operations.GetTraceAsync(idle.InstanceId);
        var exited = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.NodeExited);
        Assert.Equal(source.ActivationId, exited.Entry.FrameActivationId);
        Assert.Equal(WorkflowExecutionStatus.Success, (await operations.ContinueAsync(idle.InstanceId, "finish")).Status);
    }

    [Theory, MemberData(nameof(RecoveryCases))]
    public async Task RecoveryPreservesTheSelectedEdgeTypedValuesVisitsAndFreshActivations(
        string profile, int phase, bool prepare, bool claimCheck)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile);
        var client = cluster.AddHost();
        var definition = new WorkflowBuilder<Data, Incoming>([], "GraphRecoveryReview")
            .Flowchart(f => f.Start("a")
                .Node("a", n => n.Step<BodyResult>(), e => e.When(c => c.WorkflowData.Select(c.PreviousStep), "b"))
                .Node("b", n => n.Step<Observe>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Text))))
            .Step<Read>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Text)
                .Output(a => a.Text).To(c => c.WorkflowData.After)).Build();
        await cluster.Registry.RegisterWorkflowAsync(definition);
        var data = new Data();
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, data, new WorkflowOptions(), DateTime.UtcNow);
        checkpoint.RootData = data;
        using var services = new ServiceCollection().BuildServiceProvider();
        var branch = checkpoint.Continuations[0];
        var incomingText = "saved-" + new string('x', 2048);
        branch.SetPrevious(new Incoming { Text = incomingText });
        branch.Previous = SerializedValue.From(branch.RuntimePrevious!);
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, null, scopes);
        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        transitions.Advance(scopes, checkpoint, branch, 100, 100); // Running: Enter has not dispatched.
        var source = branch.Stack[^1];
        var sourceActivation = source.ActivationId;
        if (phase >= 1)
        {
            transitions.Advance(scopes, checkpoint, branch, 100, 100); // Enter.
            // An acknowledged body activity leaves its differently typed result at Decision.
            branch.SetPrevious(new BodyResult { Code = 17 });
            branch.Previous = SerializedValue.From(branch.RuntimePrevious!);
            source.NextStepIndex++;
        }
        if (phase >= 2) transitions.Advance(scopes, checkpoint, branch, 100, 100); // Edge pushed.
        if (phase >= 3) transitions.Advance(scopes, checkpoint, branch, 100, 100); // Edge chosen.
        if (phase >= 4) transitions.Advance(scopes, checkpoint, branch, 100, 100); // Leaving.

        Assert.Equal(1, branch.Stack[0].Graph!.Visits);
        Assert.Equal("a", branch.Stack[0].Graph!.Node);
        Assert.Equal(phase is 2 or 3 ? 3 : 2, branch.Stack.Count);
        Assert.Equal(phase >= 3 ? "b" : null, branch.Stack[0].Graph!.Pending?.Target);
        Assert.Equal(phase == 0 ? 0 : phase == 4 ? 3 : 2, source.NextStepIndex);
        if (phase >= 2) data.ForbidGuard = true; // The already-selected edge must not arbitrate again.
        if (prepare) ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
        if (prepare && phase >= 3) Assert.Null(branch.Previous);

        var repository = cluster.NewRepository();
        if (claimCheck)
        {
            var values = branch.Stack.Select(f => f.EntryPrevious).Append(branch.Previous)
                .OfType<SerializedValue>().Distinct().ToArray();
            foreach (var value in values)
            {
                var reference = await repository.PayloadStore.PutAsync(Encoding.UTF8.GetBytes(value.Json.InlineText));
                value.Json = StoredJson.FromReference(reference);
            }
            Assert.NotNull(source.EntryPrevious!.Json.Reference);
        }
        instance.WorkflowDataJson = StoredJson.Inline(JsonSerializer.Serialize(data));
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        await repository.SeedAsync(instance);
        var stored = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.ExecutionStateJson);
        Assert.Equal(sourceActivation, stored.Continuations[0].Stack[1].ActivationId);
        if (claimCheck)
        {
            var entry = stored.Continuations[0].Stack[1].EntryPrevious!;
            Assert.NotNull(entry.Json.Reference);
            Assert.Throws<InvalidOperationException>(() => entry.Read(services));
        }

        var result = await client.RecoverAsync(instance.InstanceId);
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var recovered = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(new[] { incomingText }, recovered.Observed);
        Assert.Equal(incomingText, recovered.After);
        Assert.Equal(1, recovered.GuardCalls);
        var trace = await repository.GetTraceAsync(instance.InstanceId);
        var targetEntry = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.NodeEntered &&
            e.Entry.Payload is GraphTrace { Node: "b" });
        var entered = Assert.IsType<GraphTrace>(targetEntry.Entry.Payload);
        Assert.Equal(2, entered.Visit);
        Assert.Equal("root/0/node/a/to/0/0", entered.Via);
        Assert.NotEqual(sourceActivation, targetEntry.Entry.FrameActivationId);
        var sourceExit = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.NodeExited &&
            e.Entry.Payload is GraphTrace { Node: "a" });
        Assert.Equal(sourceActivation, sourceExit.Entry.FrameActivationId);
        Assert.Equal(1, Assert.IsType<GraphTrace>(sourceExit.Entry.Payload).Visit);
        var completed = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.GraphCompleted);
        Assert.Equal("b", Assert.IsType<GraphTrace>(completed.Entry.Payload).Node);
        Assert.Equal(phase >= 3 ? 0 : 1, trace.Count(e => e.Entry.EntryType == TraceEntryType.TransitionTaken &&
            e.Entry.Payload is GraphTrace { Node: "a", Target: "b", Cause: TransitionCause.When }));
    }

    private static ServiceProvider Services(ProfileStore store, IWorkflowStateRepository repository, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => o.Debugging = true);
        services.AddSingleton(repository);
        if (time != null) services.AddSingleton(time);
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
        public List<string> Observed { get; set; } = [];
        public string After { get; set; } = "";
        public int GuardCalls { get; set; }
        public bool ForbidGuard { get; set; }
        public bool Select(BodyResult last)
        {
            if (ForbidGuard) throw new InvalidOperationException("The selected decision was evaluated again");
            GuardCalls++;
            return last.Code == 17;
        }
    }
    public sealed class Go;
    public sealed class Incoming { public string Text { get; set; } = ""; }
    public sealed class BodyResult : IAsyncActivity
    {
        public int Code { get; set; } = 17;
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Observe : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((Data)context.WorkflowData).Observed.Add(Text); return Task.CompletedTask; }
    }
    public sealed class Read : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
