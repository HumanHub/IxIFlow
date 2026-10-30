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

[Trait("Feature", "control-flow"), Trait("Spec", "W-02")]
public sealed partial class ActivityTriggerScenarios
{
    public static IEnumerable<object[]> ListenerKinds => Cases("event", "timer", "reply");
    public static IEnumerable<object[]> ReceiptPhases => Cases("accepted", "completed");
    public static IEnumerable<object[]> FailureKinds => Cases("activity", "input", "guard", "condition", "output", "listener-output");
    public static IEnumerable<object[]> RecoveryPhases => Cases("forked", "fire", "listener-fire", "deferred", "settling");

    public static IEnumerable<object[]> Caps => StoreProfiles.ProfileNames.SelectMany(p => new object[][]
    {
        [p, false, 1, false, false], [p, true, 2, false, false], [p, true, 1, false, true],
        [p, false, 1, true, false], [p, true, 2, true, false]
    });

    private static IEnumerable<object[]> Cases(params string[] forms) => StoreProfiles.ProfileNames
        .SelectMany(p => forms.Select(f => new object[] { p, f }));

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OverReturnsToTypedPostJoinArmBeforeGuardsAndBothPicksReplay(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        await using var host = Services(store, repository, compiler: true, threshold: 64);
        var client = host.GetRequiredService<IWorkflowClient>();
        var ops = host.GetRequiredService<IWorkflowOperations>();

        var definition = Gate("ActivityTriggerOver").StateMachine(m => m.Start("a").State("a", s => s
            .OnActivity<Work>(t => t.Input(x => x.Number).From(c => c.PreviousStep.Text.Length)
                .Input(x => x.Text).From(c => c.PreviousStep.Text + c.WorkflowData.Payload)
                .Output(x => x.Number).To(c => c.WorkflowData.Number)
                .When(c => c.PreviousStep.Number == 8 && c.WorkflowData.Number == 8, "end"))
            .On<Signal>("go", "end"))).WithId("machine").Build();

        var id = await AtArm(host, definition, new Data { Payload = new string('p', 300_000) });
        var before = await State(repository, id);
        var parentId = before.Continuations[0].Id;
        await Step(ops, id, parentId, DebugStepKind.Over, "over");
        var joined = await State(repository, id);
        Assert.Empty(joined.Joins);
        Assert.NotNull(joined.Continuations[0].Stack[^2].Graph!.Fired);
        Assert.Equal(2, joined.Continuations[0].Stack[^1].ArmOccurrence);

        Assert.Equal(8,
            (await ops.InspectAsync(id))!.Data.Single(d => d.ContinuationId == parentId).Value.GetProperty("Number").GetInt32());

        Assert.DoesNotContain(await ops.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.TransitionTaken);
        await ops.SetBreakpointsAsync(id, [new("post", "machine/a/arm", Condition: "context.PreviousStep.Text == \"incoming\"")], "post");
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(id, "finish")).Status);
        var trace = await ops.GetTraceAsync(id);
        var replay = TraceReplay.Load(definition, trace);
        var arms = trace.Where(e => e.Entry.StepId == "root/2/state/a/1" && e.Entry.EntryType == TraceEntryType.StepEntered).ToArray();
        Assert.Equal(2, arms.Length);

        for (var i = 0; i < arms.Length; i++)
        {
            replay.Seek(arms[i].Sequence);
            Assert.Equal(i + 1, replay.Current.Threads.Single().Frames[^1].ArmOccurrence);
        }

        var fire = Assert.Single(trace,
            e => e.Entry.StepId == "root/2/state/a/on/0/run/1" && e.Entry.EntryType == TraceEntryType.StepEntered);

        replay.Seek(fire.Sequence);
        Assert.Equal(fire.Entry.StepId, replay.CurrentStepId);
    }

    [Theory, MemberData(nameof(ListenerKinds))]
    public async Task ListenerWinsWithTypedAcceptanceFireAndOriginalSurvivingReplyOwner(string profile, string kind)
    {
        var clock = Clock(profile);
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var repository = store.NewRepository();
        var probe = new Probe();
        await using var host = Services(store, repository, clock, probe, compiler: true);
        var client = host.GetRequiredService<IWorkflowClient>();
        var ops = host.GetRequiredService<IWorkflowOperations>();

        var definition = Gate("ActivityTriggerListener" + kind).StateMachine(m => m.Start("a").State("a", s => s
                .Entry(e => e.Request<Ask>("answer").Request<Ask>("other", a => a.Input(x => x.Name).From(c => "other")))
                .OnActivity<Work>("end")
                .On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                    .When(c => c.PreviousStep.Number == 7, "end"))
                .OnReply<Signal>("answer", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"))
                .OnReply<Signal>("other", "end")
                .After(kind == "timer" ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMinutes(2),
                    t => t.When(c => c.PreviousStep.Text == "incoming", "end"))))
            .WithId("machine").Build();

        var id = await AtArm(host, definition);
        var initial = await State(repository, id);
        var parentId = initial.Continuations[0].Id;
        var other = Assert.Single(initial.Waits, w => w.StepId == "root/2/state/a/on/3/0");
        await Step(ops, id, parentId, DebugStepKind.Into, "fork");
        var fork = await State(repository, id);
        var listener = fork.Continuations.Single(c => c.Stack.LastOrDefault()?.Kind == FrameKind.Listen).Id;
        await Step(ops, id, listener, DebugStepKind.Into, "listen");
        Assert.Equal("Blocked", (await ops.InspectAsync(id))!.DebugState.Stop!.Reason);

        if (kind == "timer")
            await Due(repository, id, clock);
        else if (kind == "reply")
            await client.ReplyAsync(probe.Tokens["answer"], new Signal { Number = 7 });
        else
            await Append(repository, id, "event", "go", new Signal { Number = 7 });

        await Step(ops, id, listener, DebugStepKind.Into, "accepted");
        var fired = await State(repository, id);
        Assert.Equal(1, fired.ContinuationById(listener)!.Stack[^1].NextStepIndex);
        Assert.Equal(1, fired.Continuations[0].Stack[^1].ArmOccurrence);
        await ops.SetBreakpointsAsync(id, [new("fire", "machine/a/listen/fire", Condition: "context.PreviousStep != null")], "fire-set");
        await Step(ops, id, listener, DebugStepKind.Out, "complete");

        // The breakpoint is the real Fire pick; resume that same occurrence to hold its completion.
        if ((await State(repository, id)).ContinuationById(listener)!.PendingJoinFrame == null)
        {
            await ops.SetBreakpointsAsync(id, [], "clear-fire");
            await Step(ops, id, listener, DebugStepKind.Out, "complete-again");
        }

        var held = await State(repository, id);
        Assert.NotNull(held.ContinuationById(listener)!.PendingJoinFrame);
        Assert.Equal(parentId, Assert.Single(held.Waits, w => w.Id == other.Id).ContinuationId);
        Assert.True((await repository.GetWaitAsync(id, other.Id))!.IsOpen);
        await client.ReplyAsync(probe.Tokens["other"], new Signal { Number = 9 });
        await ops.SetBreakpointsAsync(id, [], "clear");
        var result = await ops.ContinueAsync(id, "finish");
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(kind == "timer" ? 0 : 7, Assert.IsType<Data>(result.WorkflowData).Number);
        Assert.Equal(WaitCloseReason.Answered, (await repository.GetWaitAsync(id, other.Id))!.CloseReason);
        var trace = await ops.GetTraceAsync(id);

        var fire = Assert.Single(trace,
            e => e.Entry.StepId == "root/2/state/a/listen/1" && e.Entry.EntryType == TraceEntryType.StepEntered);

        var replay = TraceReplay.Load(definition, trace);
        replay.Seek(fire.Sequence);
        Assert.Equal(fire.Entry.StepId, replay.CurrentStepId);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OriginalApprovalTokenAnswersAfterActivityWinsAndAllItsGuardsFail(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe();
        await using var host = Services(store, repository, probe: probe);
        var ops = host.GetRequiredService<IWorkflowOperations>();
        var client = host.GetRequiredService<IWorkflowClient>();

        var definition = Gate("ActivityTriggerOriginalToken").StateMachine(m => m.Start("a").State("a", s => s
            .Entry(e => e.Request<Ask>("answer"))
            .OnActivity<Work>(t => t.When(c => false, "end"))
            .OnReply<Signal>("answer", "end"))).WithId("machine").Build();

        var id = await AtArm(host, definition);
        var initial = await State(repository, id);
        var parent = initial.Continuations[0].Id;
        var original = Assert.Single(initial.Waits);
        var token = probe.Tokens["answer"];
        await Step(ops, id, parent, DebugStepKind.Over, "winner");
        var won = await State(repository, id);
        Assert.Equal(original.Id, Assert.Single(won.Waits).Id);
        Assert.Equal(parent, Assert.Single(won.Waits).ContinuationId);
        await Step(ops, id, parent, DebugStepKind.Into, "false-guard");
        Assert.Equal(3, (await State(repository, id)).Continuations[0].Stack[^1].ArmOccurrence);

        // Answer in the re-arm window, before a new listener exists. The parent binds it.
        await client.ReplyAsync(token, new Signal { Number = 7 });
        var result = await ops.ContinueAsync(id, "finish");
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Single(probe.Tokens);
        Assert.Equal(WaitCloseReason.Answered, (await repository.GetWaitAsync(id, original.Id))!.CloseReason);
        Assert.Empty((await State(repository, id)).BoundReplies);
    }

    [Theory, MemberData(nameof(ReceiptPhases))]
    public async Task ConsumedLosingReplyIsDeliveredFromItsReceiptBeforeDispatchOrAfterHeldCompletion(string profile, string phase)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe();
        var acknowledgementLost = false;

        var fault = new FaultRepository(repository)
        {
            FailCommit = _ => acknowledgementLost,
            LoseAcknowledgement = instance => acknowledgementLost = phase == "accepted" && ExecutionCheckpoint
                .Read(instance.ExecutionStateJson)
                .Continuations.Any(c => c.AcceptedWait != null && c.Stack.LastOrDefault()?.Kind == FrameKind.Listen)
        };

        await using var host = Services(store, fault, probe: probe);
        var ops = host.GetRequiredService<IWorkflowOperations>();
        var client = host.GetRequiredService<IWorkflowClient>();

        var definition = Gate("ActivityTriggerLostReceipt" + phase).StateMachine(m => m.Start("a").State("a", s => s
            .Entry(e => e.Request<Ask>("answer"))
            .OnActivity<Work>(t => t.Input(x => x.Number).From(c => 1)
                .Output(x => x.Number).To(c => c.WorkflowData.Number).When(c => false, "end"))
            .OnReply<Signal>("answer", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                .Otherwise("end")))).WithId("machine").Build();

        var id = await AtArm(host, definition);
        var initial = await State(repository, id);
        var parent = initial.Continuations[0].Id;
        var wait = Assert.Single(initial.Waits);
        await Step(ops, id, parent, DebugStepKind.Into, "fork");
        var fork = await State(repository, id);
        var activity = fork.Continuations.Single(c => c.Stack.LastOrDefault()?.Kind == FrameKind.Run).Id;
        var listener = fork.Continuations.Single(c => c.Stack.LastOrDefault()?.Kind == FrameKind.Listen).Id;
        await Step(ops, id, listener, DebugStepKind.Into, "listen");

        if (phase == "accepted")
            await Step(ops, id, activity, DebugStepKind.Out, "activity-completes");

        await client.ReplyAsync(probe.Tokens["answer"], new Signal { Number = 7 });

        if (phase == "accepted")
            await Assert.ThrowsAnyAsync<Exception>(() => ops.ContinueAsync(id, "accept-and-crash"));
        else
            await Step(ops, id, listener, DebugStepKind.Out, "accept");

        var accepted = await State(repository, id);
        var branch = accepted.ContinuationById(listener)!;

        if (phase == "accepted")
            Assert.NotNull(branch.AcceptedWait);
        else
            Assert.NotNull(branch.PendingJoinFrame);

        Assert.Equal(wait.Id, (branch.Stack.LastOrDefault()?.Fired ?? branch.PendingJoinFrame?.Fired)!.Reply!.WaitId);
        Assert.Equal(WaitCloseReason.Answered, (await repository.GetWaitAsync(id, wait.Id))!.CloseReason);

        if (phase == "completed")
            await Step(ops, id, activity, DebugStepKind.Out, "activity-completes");

        var deferred = await State(repository, id);
        Assert.NotNull(deferred.ContinuationById(activity)!.PendingJoinFrame);
        acknowledgementLost = false;
        var result = phase == "accepted" ? await client.RecoverAsync(id) : await ops.ContinueAsync(id, "arbitrate");
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(7, Assert.IsType<Data>(result.WorkflowData).Number);
        var trace = await ops.GetTraceAsync(id);

        Assert.Single(trace,
            e => e.Entry.EntryType == TraceEntryType.EventReceived && e.Entry.Payload is ReceivedEventTrace r &&
                r.StepId == "root/2/state/a/on/1/0");

        Assert.Contains(trace, e => e.Entry.Payload is GraphTrace { Cause: TransitionCause.Reply });
        Assert.Empty((await State(repository, id)).BoundReplies);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TwoIndependentlySteppedCompletionsWritingOnePropertySelectAndMergeOnlyFirst(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        await using var host = Services(store, repository);
        var ops = host.GetRequiredService<IWorkflowOperations>();

        var definition = Gate("ActivityTriggerDeferred").StateMachine(m => m.Start("a").State("a", s => s
                .OnActivity<Work>(t =>
                    t.Input(x => x.Number).From(c => 11).Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"))
                .OnActivity<Work>(t =>
                    t.Input(x => x.Number).From(c => 22).Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"))))
            .WithId("machine").Build();

        var id = await AtArm(host, definition);
        var parent = (await State(repository, id)).Continuations[0].Id;
        await Step(ops, id, parent, DebugStepKind.Into, "fork");
        var fork = await State(repository, id);
        var children = Assert.Single(fork.Joins).ChildContinuationIds;
        await Step(ops, id, children[1], DebugStepKind.Out, "second-first");
        await Step(ops, id, children[0], DebugStepKind.Out, "first-second");
        var deferred = await State(repository, id);
        Assert.Equal(2, deferred.Continuations.Count(c => c.PendingJoinFrame != null));
        var result = await ops.ContinueAsync(id, "arbitrate");
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(11, Assert.IsType<Data>(result.WorkflowData).Number);
    }

    [Theory, MemberData(nameof(FailureKinds))]
    public async Task AuthoredTriggerFailuresPropagateThroughCatchAndFinallyAndCloseRequests(string profile, string kind)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe();
        await using var host = Services(store, repository, probe: probe);
        var client = host.GetRequiredService<IWorkflowClient>();

        var definition = new WorkflowBuilder<Data, object>([], "ActivityTriggerFailure" + kind)
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s =>
            {
                s.Entry(e => e.Request<Ask>("answer")).OnReply<Signal>("answer", "end");

                if (kind == "activity")
                    s.OnActivity<Fail>("end");
                else
                    s.OnActivity<Work>(t =>
                    {
                        t.Input(x => x.Number).From(c => c.WorkflowData.Input(kind));

                        if (kind == "output")
                            t.Output(x => x.Number).To(c => c.WorkflowData.Throwing);

                        t.When(c => c.WorkflowData.Guard(kind), "end");
                    });

                if (kind == "condition")
                    s.After(c => c.WorkflowData.Duration(), "end");

                if (kind == "listener-output")
                    s.On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Throwing).Otherwise("end"));
            })))
            .Catch<WorkflowEngineException>(c =>
                c.Step<Work>(w => w.Input(x => x.Number).From(c => 1).Output(x => x.Number).To(c => c.WorkflowData.Number)))
            .Finally(b => b.Step<Work>(w => w.Input(x => x.Number).From(c => 2).Output(x => x.Number).To(c => c.WorkflowData.Action)))
            .Build();

        // Hold the activity when the listener itself must fail.
        if (kind == "listener-output")
            probe.Block = true;

        var run = client.RunAsync(definition, new Data());

        if (kind == "listener-output")
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await UntilWait(repository, probe, "go");
            await client.ResumeAsync(probe.InstanceId, "go", new Signal { Number = 7 });
        }

        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(1, data.Number);
        Assert.Equal(2, data.Action);
        var checkpoint = await State(repository, result.InstanceId);
        Assert.Empty(checkpoint.Waits);
        Assert.Empty(checkpoint.BoundReplies);
        Assert.DoesNotContain(checkpoint.Continuations.SelectMany(c => c.Stack), f => f.Graph?.Fired != null);

        Assert.All(await repository.GetOpenWaitsAsync(new(InstanceId: result.InstanceId) { IncludeClosed = true }),
            w => Assert.False(w.IsOpen));
    }

    private static WorkflowBuilder<Data, Incoming> Gate(string name) =>
        (WorkflowBuilder<Data, Incoming>)new WorkflowBuilder<Data, object>([], name).WaitFor<Signal>("start").Step<Incoming>();

    private static async Task<string> AtArm(Host host, WorkflowDefinition definition, Data? data = null)
    {
        var client = host.GetRequiredService<IWorkflowClient>();
        var ops = host.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, data ?? new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("arm", "machine/a/arm")], "arm-set");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Signal())).Status);
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "arm-clear");

        return idle.InstanceId;
    }

    private static async Task Step(IWorkflowOperations ops, string id, string branch, DebugStepKind kind, string command) =>
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id, branch, kind, command)).Status);

    private static async Task<ExecutionCheckpoint> State(IWorkflowStateRepository repository, string id) =>
        ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);

    private static async Task Append(IWorkflowStateRepository repository, string id, string eventId, string key, Signal value) =>
        Assert.Equal(EventAppendStatus.Stored, (await repository.AppendEventAsync(new(id, eventId,
            WorkflowTypeIdentity.SavedName(typeof(Signal)), JsonSerializer.Serialize(value), key))).Status);

    private static FakeTimeProvider? Clock(string profile) =>
        profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new() : null;

    private static async Task Due(IWorkflowStateRepository repository, string id, FakeTimeProvider? clock)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var page = await repository.GetPendingEventsAsync(id, 0, 100).WaitAsync(patience.Token);
        var deadline = Assert.Single(page.Deadlines!).Value;

        if (clock != null)
        {
            if (deadline >= page.StoreTimeUtc!.Value)
                clock.Advance(deadline - page.StoreTimeUtc.Value + TimeSpan.FromMilliseconds(1));
        }
        else
            while (page.StoreTimeUtc!.Value < deadline)
            {
                await Task.Delay(10, patience.Token);
                page = await repository.GetPendingEventsAsync(id, 0, 100).WaitAsync(patience.Token);
            }
    }

    private static async Task UntilWait(IWorkflowStateRepository repository, Probe probe, string key)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (!(await repository.GetOpenWaitsAsync(new(InstanceId: probe.InstanceId))).Any(w => w.Key == key))
            await Task.Delay(10, patience.Token);
    }

    private static Host Services(ProfileStore store, IWorkflowStateRepository repository, TimeProvider? clock = null,
        Probe? probe = null, bool compiler = false, long threshold = 0)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.VerifyCheckpointParts = true;
            o.PayloadThresholdBytes = threshold;
        });

        services.AddSingleton(repository);
        services.AddSingleton(probe ?? new Probe());

        if (clock != null)
            services.AddSingleton(clock);

        if (compiler)
            services.AddBreakpointConditions();

        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();

        try
        {
            store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

            return new(provider);
        }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();

            throw;
        }
    }

    private sealed class Host(ServiceProvider provider) : IServiceProvider, IAsyncDisposable
    {
        public object? GetService(Type type) => provider.GetService(type);

        public async ValueTask DisposeAsync()
        {
            await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    public sealed class Data
    {
        public string Payload { get; set; } = "";
        public int Number { get; set; }
        public int Action { get; set; }

        public int Throwing
        {
            get => 0;
            set
            {
                if (value != 0)
                    throw Failure();
            }
        }

        public bool Guard(string kind) => kind == "guard" ? throw Failure() : true;
        public int Input(string kind) => kind == "input" ? throw Failure() : 7;
        public TimeSpan Duration() => throw Failure();
    }

    private static WorkflowEngineException Failure() => new(WorkflowErrorKind.Infrastructure, "authored trigger failure");

    public sealed class Signal
    {
        public int Number { get; set; }
    }

    public sealed class Incoming : IAsyncActivity
    {
        public string Text { get; set; } = "incoming";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Probe
    {
        public string InstanceId { get; set; } = "";
        public Dictionary<string, string> Tokens { get; } = [];
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Work(Probe probe) : IAsyncActivity
    {
        public int Number { get; set; }
        public string Text { get; set; } = "";

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.InstanceId = context.WorkflowInstanceId;

            if (probe.Block)
            {
                probe.Block = false;
                probe.Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }

    public sealed class Ask(Probe probe) : IRequestActivity<Signal>
    {
        public string Name { get; set; } = "answer";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Tokens[Name] = context.Reply!.Token;

            return Task.CompletedTask;
        }
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.FromException(Failure());
    }
}