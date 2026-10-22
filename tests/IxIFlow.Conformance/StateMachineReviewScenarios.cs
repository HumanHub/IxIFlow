using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed partial class StateMachineScenarios
{
    public static IEnumerable<object[]> Cancellations => Cases("exit", "action");
    public static IEnumerable<object[]> OutStops => Cases("entry", "arm", "exit", "action");

    [Theory, MemberData(nameof(Cancellations))]
    public async Task CancellationDuringExitOrActionCancelsTheRunningBodyAndRunsFinally(string profile, string body)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var probe = new Probe();
        await using var services = Services(store, store.NewRepository(), probe: probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("StateCancelInBody" + body).Step<Incoming>()
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                .Exit(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "exit"))
                    .Step<HoldBody>(a => a.Input(x => x.Enabled).From(c => body == "exit")))
                .On<Signal>("go", t => t.Otherwise("done", a => a
                    .Step<Mark>(x => x.Input(y => y.Name).From(c => "action"))
                    .Step<HoldBody>(x => x.Input(y => y.Enabled).From(c => body == "action")))))
                .State("done", s => s.Final().Entry(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "target"))))))
            .Finally(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "finally")))
            .Step<Mark>(a => a.Input(x => x.Name).From(c => "after")).Build();
        var idle = await client.RunAsync(definition, new Data());
        var run = client.ResumeAsync(idle.InstanceId, "go", new Signal());
        try
        {
            await probe.BodyEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await client.CancelAsync(idle.InstanceId, new CancellationReason { ReasonCode = "in-body" })
                .WaitAsync(TimeSpan.FromSeconds(30));
            await probe.BodyCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await run.WaitAsync(TimeSpan.FromSeconds(30));
            var result = await client.AwaitAsync(idle.InstanceId, WorkflowAwaitCondition.Finished)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
            Assert.Equal(body == "exit" ? new[] { "exit", "finally" } : new[] { "exit", "action", "finally" }, probe.Marks);
        }
        finally { probe.ReleaseBody.TrySetResult(); }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task NullableTimerAcceptancePersistsAndRecoversBeforeGuardDispatch(string profile)
    {
        var time = Clock(profile);
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        var repository = store.NewRepository();
        var probe = new Probe();
        await using var services = Services(store, repository, time, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "StateNullableAcceptance").WaitFor<Signal>("start")
            .Step<Incoming>().StateMachine(m => m.Start("a").State("a", s => s
                .After(TimeSpan.FromSeconds(1), t => t.When(c => c.PreviousStep.Text == "incoming", "done",
                    a => a.Step<Mark>(x => x.Input(y => y.Name).From(c => c.PreviousStep.Text)))))
                .State("done", s => s.Final())).WithId("machine").Build();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("timer", "machine/a/arm", HitCondition: "== 2")], "set");
        await client.ResumeAsync(idle.InstanceId, "start", new Signal());
        await Due(repository, idle.InstanceId, time);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.RecoverAsync(idle.InstanceId)).Status);
        var saved = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson);
        var accepted = Assert.IsType<AcceptedWaitState>(saved.Continuations[0].AcceptedWait);
        Assert.Null(accepted.Event);
        Assert.NotNull(accepted.WaitId);
        Assert.Equal("root/2/state/a/on/0/0", accepted.StepId);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.RecoverAsync(idle.InstanceId)).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continue")).Status);
        Assert.Equal(new[] { "incoming" }, probe.Marks);
        Assert.Empty((await repository.GetPendingEventsAsync(idle.InstanceId, 0, 100)).Deadlines!);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ConditionalCaseBreakpointAndSuccessfulExitTriggerCaseReplayUseRealPicks(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var probe = new Probe();
        await using var services = Services(store, store.NewRepository(), probe: probe, compiler: true);
        var client = services.GetRequiredService<IWorkflowClient>();
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("StateCaseReplay").Step<Incoming>()
            .StateMachine(m => m.Start("a").State("a", s => s
                .Exit(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "exit")
                    .Input(x => x.Value).From(c => c.PreviousStep.Text)))
                .On<Signal>("go", t => t.When(c => c.PreviousStep.Number == 7, "done",
                    a => a.Step<Mark>(x => x.Input(y => y.Name).From(c => "action")
                        .Input(y => y.Value).From(c => c.PreviousStep.Number.ToString())))))
                .State("done", s => s.Final())).WithId("machine").Build();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("case", "machine/a/on/0/case/0",
            Condition: "context.PreviousStep.Number == 7")], "set");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Signal { Number = 7 })).Status);
        Assert.Equal(new[] { "exit" }, probe.Marks);
        var stopped = (await ops.InspectAsync(idle.InstanceId))!;
        Assert.Equal("Case", stopped.Threads[0].Frames[^1].Kind);
        Assert.Equal("case", stopped.DebugState.Stop!.BreakpointId);
        Assert.Null(stopped.DebugState.Stop.Reason);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continue")).Status);
        var finished = await client.AwaitAsync(idle.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(new[] { "exit", "action" }, probe.Marks);
        Assert.Equal("incoming", probe.Values["exit"]);
        Assert.Equal("7", probe.Values["action"]);
        var trace = await ops.GetTraceAsync(idle.InstanceId);
        var selection = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.TransitionTaken);
        var exit = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.ExitEntered);
        var trigger = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.StepEntered && e.Entry.StepId == "root/1/state/a/on/0/0");
        var action = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.ActionEntered);
        Assert.True(selection.Sequence < exit.Sequence && exit.Sequence < trigger.Sequence && trigger.Sequence < action.Sequence);
        var replay = TraceReplay.Load(definition, trace);
        replay.Seek(selection.Sequence);
        Assert.Null(replay.CurrentStepId);
        foreach (var pick in new[] { exit, trigger, action })
        {
            replay.Seek(pick.Sequence);
            Assert.Equal(pick.Entry.StepId, replay.CurrentStepId);
            Assert.Equal(pick.Entry.StepId, replay.Current.Threads.Single().StepId);
        }
    }

    [Theory, MemberData(nameof(OutStops))]
    public async Task OutLeavesTheEnclosingStateFromEntryArmExitAndAction(string profile, string origin)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe();
        await using var services = Services(store, repository, probe: probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "StateOut" + origin).WaitFor<Signal>("start")
            .Step<Incoming>().StateMachine(m => m.Start("a").State("a", s => s
                .Entry(e => e.Sequence(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "entry")).WithId("entry")))
                .Exit(e => e.Sequence(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "exit")).WithId("exit")))
                .On<Signal>("go", t => t.Otherwise("done", a => a.Sequence(b => b
                    .Step<Mark>(x => x.Input(y => y.Name).From(c => "action")).WithId("action")))))
                .State("done", s => s.Final().Entry(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "target")))))
            .WithId("machine").Build();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("origin", origin == "arm" ? "machine/a/arm" : origin)], "set");
        var result = await client.ResumeAsync(idle.InstanceId, "start", new Signal());
        if (origin is "exit" or "action") result = await client.ResumeAsync(idle.InstanceId, "go", new Signal());
        Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
        var thread = (await ops.InspectAsync(idle.InstanceId))!.Threads[0];
        var state = thread.Frames.Single(f => f.Kind == "State");
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        if (origin is "entry" or "arm")
        {
            Assert.Equal(WorkflowExecutionStatus.Paused,
                (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, DebugStepKind.Out, "arm-out")).Status);
            Assert.Equal("Blocked", (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.Reason);
            await Append(repository, idle.InstanceId, "go", "go", new Signal());
        }
        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, DebugStepKind.Out, "out")).Status);
        var after = (await ops.InspectAsync(idle.InstanceId))!;
        Assert.Equal("machine/done/enter", after.Threads[0].AuthorStepId);
        Assert.DoesNotContain(after.Threads[0].Frames, f => f.ActivationId == state.ActivationId);
        Assert.Equal(new[] { "entry", "exit", "action" }, probe.Marks);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continue")).Status);
        Assert.Equal(new[] { "entry", "exit", "action", "target" }, probe.Marks);
    }

    public sealed class HoldBody(Probe probe) : IAsyncActivity
    {
        public bool Enabled { get; set; }
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (!Enabled) return;
            probe.BodyEntered.TrySetResult();
            try { await probe.ReleaseBody.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { probe.BodyCancelled.TrySetResult(); throw; }
        }
    }
}
