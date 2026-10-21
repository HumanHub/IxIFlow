using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed partial class ActivityTriggerScenarios
{
    public static IEnumerable<object[]> RearmKinds => Cases("event", "timer");

    [Theory, MemberData(nameof(RearmKinds))]
    public async Task FalseListenerGuardsRearmEveryTriggerWithFreshTimersAndKeepEntryRequests(string profile, string kind)
    {
        var clock = Clock(profile);
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var repository = store.NewRepository(); var probe = new Probe();
        await using var host = Services(store, repository, clock, probe);
        var client = host.GetRequiredService<IWorkflowClient>(); var ops = host.GetRequiredService<IWorkflowOperations>();
        var duration = kind == "timer" ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMinutes(1);
        var definition = Gate("ActivityTriggerRearm" + kind).StateMachine(m => m.Start("a").State("a", s => s
            .Entry(e => e.Request<Ask>("answer"))
            .OnActivity<Work>(t => t.When(c => false, "end"))
            .On<Signal>("go", t => t.When(c => false, "end"))
            .OnReply<Signal>("answer", "end")
            .After(duration, t => t.When(c => false, "end")))).WithId("machine").Build();
        var id = await AtArm(host, definition); var parent = (await State(repository, id)).Continuations[0].Id;
        await Step(ops, id, parent, DebugStepKind.Into, "fork");
        var listener = (await State(repository, id)).Continuations.Single(c => c.Stack.LastOrDefault()?.Kind == FrameKind.Listen).Id;
        await Step(ops, id, listener, DebugStepKind.Into, "listen");
        var armed = await State(repository, id);
        var oldTimer = Assert.Single(armed.Waits, w => w.TableKind == WaitKind.Delay);
        var oldReply = Assert.Single(armed.Waits, w => w.IsReply);
        if (kind == "timer") await Due(repository, id, clock);
        else await Append(repository, id, "event", "go", new Signal());
        await Step(ops, id, listener, DebugStepKind.Out, "fire");
        await ops.SetBreakpointsAsync(id, [new("joined", "machine/a/arm")], "join-stop");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.ContinueAsync(id, "join")).Status);
        Assert.Equal(2, (await State(repository, id)).Continuations[0].Stack[^1].ArmOccurrence);
        await ops.SetBreakpointsAsync(id, [], "clear");
        await Step(ops, id, parent, DebugStepKind.Into, "false");
        Assert.Equal(3, (await State(repository, id)).Continuations[0].Stack[^1].ArmOccurrence);
        await Step(ops, id, parent, DebugStepKind.Into, "refork");
        listener = (await State(repository, id)).Continuations.Single(c => c.Stack.LastOrDefault()?.Kind == FrameKind.Listen).Id;
        await Step(ops, id, listener, DebugStepKind.Into, "relisten");
        var rearmed = await State(repository, id);
        var timer = Assert.Single(rearmed.Waits, w => w.TableKind == WaitKind.Delay);
        Assert.NotEqual(oldTimer.Id, timer.Id);
        var row = (await repository.GetWaitAsync(id, timer.Id))!;
        Assert.Equal(duration, row.DeadlineUtc!.Value - row.OpenedAtUtc);
        Assert.Equal(oldReply.Id, Assert.Single(rearmed.Waits, w => w.IsReply).Id);
        Assert.Single(probe.Tokens);
        await client.ReplyAsync(probe.Tokens["answer"], new Signal());
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(id, "finish")).Status);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CancellationDuringAnActivityTriggerSettlesItAndRunsFinallyWithoutStateExit(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository(); var probe = new Probe { Block = true };
        await using var host = Services(store, repository, probe: probe);
        var client = host.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "ActivityTriggerCancellation")
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                .Entry(e => e.Request<Ask>("answer"))
                .Exit(e => e.Step<Work>(w => w.Input(x => x.Number).From(c => 9).Output(x => x.Number).To(c => c.WorkflowData.Number)))
                .OnActivity<Work>("end").OnReply<Signal>("answer", "end").On<Signal>("go", "end"))))
            .Finally(b => b.Step<Work>(w => w.Input(x => x.Number).From(c => 1).Output(x => x.Number).To(c => c.WorkflowData.Action))).Build();
        var run = client.RunAsync(definition, new Data());
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await UntilWait(repository, probe, "go");
        await client.CancelAsync(probe.InstanceId, new CancellationReason { ReasonCode = "test-cancel" });
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData); Assert.Equal(0, data.Number); Assert.Equal(1, data.Action);
        var checkpoint = await State(repository, result.InstanceId);
        Assert.Empty(checkpoint.Waits); Assert.Empty(checkpoint.BoundReplies); Assert.Empty(checkpoint.Joins);
        Assert.All(await repository.GetOpenWaitsAsync(new(InstanceId: result.InstanceId) { IncludeClosed = true }), w => Assert.False(w.IsOpen));
    }
}
