using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed partial class ActivityTriggerScenarios
{
    public static IEnumerable<object[]> PickKinds => Cases("arm", "activity-fire", "listener-fire");

    [Theory, MemberData(nameof(PickKinds))]
    public async Task ConditionalBreakpointsExposeThePostJoinArmAndBothFireValues(string profile, string pick)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        await using var host = Services(store, repository, compiler: true);
        var client = host.GetRequiredService<IWorkflowClient>(); var ops = host.GetRequiredService<IWorkflowOperations>();
        var definition = Gate("ActivityTriggerPick" + pick).StateMachine(m => m.Start("a").State("a", s => s
            .OnActivity<Work>(t => t.Input(x => x.Number).From(c => 11)
                .Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"))
            .On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"))))
            .WithId("machine").Build();
        var id = await AtArm(host, definition);
        var parent = (await State(repository, id)).Continuations[0].Id;
        if (pick == "listener-fire")
        {
            await Step(ops, id, parent, DebugStepKind.Into, "fork");
            var fork = await State(repository, id);
            var activity = fork.Continuations.Single(c => c.Stack.LastOrDefault()?.Kind == FrameKind.Run).Id;
            var listener = fork.Continuations.Single(c => c.Stack.LastOrDefault()?.Kind == FrameKind.Listen).Id;
            await Step(ops, id, listener, DebugStepKind.Into, "listen");
            await ops.SetBreakpointsAsync(id, [new("hold", "machine/a/on/0/run"),
                new("fire", "machine/a/listen/fire", Condition: "context.PreviousStep != null")], "fire-set");
            // A breakpoint on the held activity moves the skip occurrence away from the listener.
            await Step(ops, id, activity, DebugStepKind.Into, "hold");
            await Append(repository, id, "event", "go", new Signal { Number = 7 });
            await Step(ops, id, listener, DebugStepKind.Out, "fire");
            var stopped = (await ops.InspectAsync(id))!.DebugState.Stop!;
            Assert.Equal("fire", stopped.BreakpointId); Assert.Null(stopped.Reason);
            await ops.SetBreakpointsAsync(id, [], "clear");
            await Step(ops, id, listener, DebugStepKind.Out, "complete-listener");
        }
        else
        {
            var author = pick == "arm" ? "machine/a/arm" : "machine/a/on/0/run/fire";
            await ops.SetBreakpointsAsync(id, [new("pick", author,
                Condition: pick == "arm" ? "context.PreviousStep.Text == \"incoming\"" : "context.PreviousStep.Number == 11")], "pick-set");
            var paused = await ops.ContinueAsync(id, "run");
            Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
            var stopped = (await ops.InspectAsync(id))!.DebugState.Stop!;
            Assert.Equal("pick", stopped.BreakpointId); Assert.Null(stopped.Reason);
            if (pick == "arm")
            {
                Assert.Equal(2, stopped.ArmOccurrence);
                var saved = await State(repository, id);
                Assert.NotNull(saved.Continuations[0].Stack[^2].Graph!.Fired);
                Assert.DoesNotContain(await ops.GetTraceAsync(id), t => t.Entry.EntryType == TraceEntryType.TransitionTaken);
            }
        }
        await ops.SetBreakpointsAsync(id, [], "finish-clear");
        var result = await ops.ContinueAsync(id, "finish");
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(pick == "listener-fire" ? 7 : 11, Assert.IsType<Data>(result.WorkflowData).Number);
    }

    [Theory, MemberData(nameof(Caps))]
    public async Task ExactCapsAlsoReplaceTheLeavingParentInNestedForks(string profile, bool mixed, int cap, bool nested, bool fails)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile, services =>
        { services.AddSingleton(ExecutionLimits.Default with { MaxActiveBranches = cap }); services.AddSingleton(new Probe()); });
        var client = cluster.AddHost();
        var builder = new WorkflowBuilder<Data, object>([], "ActivityTriggerCaps" + mixed + cap + nested);
        void Machine(StateMachineBuilder<Data, object> m) => m.Start("a").State("a", s =>
        {
            s.OnActivity<Work>("end"); if (mixed) s.On<Signal>("go", "end");
        });
        var definition = nested ? builder.Parallel(p => p.Do(b => b.StateMachine(Machine))).Build()
            : builder.StateMachine(Machine).Build();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(fails ? WorkflowExecutionStatus.NeedsResolution : WorkflowExecutionStatus.Success, result.Status);
        if (fails)
        {
            var saved = ExecutionCheckpoint.Read((await cluster.GetInstanceAsync(result.InstanceId))!.ExecutionStateJson);
            Assert.Empty(saved.Joins); Assert.Single(saved.Continuations);
            Assert.Contains("branch", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory, MemberData(nameof(RecoveryPhases))]
    public async Task ForkAndFireAndDeferredBatchAndRecordedWinnerRecoverWithoutReapplyingOutputs(string profile, string phase)
    {
        var probe = new Probe { Block = phase == "listener-fire" };
        await using var cluster = await ConformanceCluster.CreateAsync(profile, services => services.AddSingleton(probe));
        var client = cluster.AddHost();
        var definition = new WorkflowBuilder<Data, Incoming>([], "ActivityTriggerRecovery" + phase)
            .StateMachine(m => m.Start("a").State("a", s =>
            {
                s.OnActivity<Work>(t => t.Input(x => x.Number).From(c => 11)
                    .Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));
                if (phase is "deferred" or "settling")
                    s.OnActivity<Work>(t => t.Input(x => x.Number).From(c => 22)
                        .Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));
                else s.On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));
            })).Build();
        await cluster.Registry.RegisterWorkflowAsync(definition);
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        checkpoint.RootData = new Data();
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, null, scopes);
        using var local = new ServiceCollection().AddSingleton<IActivityRegistry>(new ActivityRegistry([])).BuildServiceProvider();
        var transitions = new ContinuationTransitions(local, TimeProvider.System);
        var parent = checkpoint.Continuations[0]; parent.SetPrevious(new Incoming());
        void Advance(ContinuationState child) => transitions.Advance(scopes, checkpoint, child, 100, 100);
        Advance(parent); Advance(parent); Advance(parent);
        var join = Assert.Single(checkpoint.Joins);
        var children = checkpoint.BranchesOf(join);
        if (phase is "fire" or "deferred" or "settling")
        {
            Settled(children[0], 11);
            if (phase is "deferred" or "settling")
            {
                Hold(children[0]);
                Advance(children[0]); Advance(children[0]);
                if (phase == "deferred")
                {
                    Hold(children[1]);
                    Settled(children[1], 22); Advance(children[1]); Advance(children[1]);
                }
            }
        }
        if (phase == "listener-fire")
        {
            var listener = children[1]; Advance(listener);
            var wait = Assert.Single(checkpoint.Waits);
            checkpoint.RemoveWaits(_ => true, WaitCloseReason.Answered);
            listener.AcceptedWait = new AcceptedWaitState { StepId = wait.StepId, WaitId = wait.Id,
                Event = SerializedValue.From(new Signal { Number = 7 }) };
            listener.Status = ContinuationStatus.Active; GraphTransitions.TriggerFired(checkpoint, listener);
            Advance(listener);
            Assert.Equal(1, listener.Stack[0].NextStepIndex);
        }
        // Hold notifications as independently stepped children do, without changing the runtime paths.
        // The synchronous advance above is held using the ordinary stepping policy below.
        if (phase == "settling")
        {
            checkpoint.Debug = new();
            children[1].PendingActivity = new ActivityInvocationState { StepId = "root/0/state/a/on/1/run/0" };
            children[1].Status = ContinuationStatus.WaitingResolution;
            ContinuationTransitions.NotifyCompletedBranches(scopes, checkpoint);
            Assert.Equal(children[0].Id, join.WinningChildId);
            Assert.True(join.IsCompleting);
            Assert.NotNull(parent.Stack[0].Graph!.Fired);
        }
        foreach (var child in checkpoint.Continuations)
        {
            if (child.RuntimePrevious != null) child.Previous = SerializedValue.From(child.RuntimePrevious);
            foreach (var frame in child.Stack)
                if (frame.RuntimeEntryPrevious != null) frame.EntryPrevious = SerializedValue.From(frame.RuntimeEntryPrevious);
            if (child.ParentJoinId != null && child.Data != null)
            {
                child.DataChanges = BranchData.Changes(BranchData.Node(checkpoint.RootData), child.Data, typeof(Data));
                child.DataChanged = false;
            }
        }
        ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
        if (phase == "deferred") Assert.Equal(2, children.Count(c => c.PendingJoinFrame != null));
        if (phase == "fire") Assert.Equal(1, children[0].Stack[0].NextStepIndex);
        Assert.Equal(ContinuationStatus.Joining, parent.Status);
        checkpoint.Debug = new();
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        var repository = cluster.NewRepository();
        var grant = await StoreTestSupport.LeaseAsync(repository, instance.InstanceId);
        var changes = checkpoint.Inbox.WaitChanges(checkpoint);
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, grant.Epoch, 0, openedWaits: changes.Opened))).Status);
        await StoreTestSupport.ReleaseAsync(repository, instance.InstanceId);
        var result = await client.RecoverAsync(instance.InstanceId);
        if (phase == "settling")
        {
            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
            var saved = ExecutionCheckpoint.Read((await cluster.GetInstanceAsync(instance.InstanceId))!.ExecutionStateJson);
            Assert.Equal(children[0].Id, Assert.Single(saved.Joins).WinningChildId);
            Assert.Equal(ContinuationStatus.Joining, saved.Continuations[0].Status);
            Assert.NotNull(saved.Continuations[0].Stack[0].Graph!.Fired);
            Assert.Equal(0, Assert.IsType<Data>(result.WorkflowData).Number);
            result = await client.ResolveAsync(instance.InstanceId, children[1].PendingActivity!.Id,
                ActivityResolution.Completed(new Dictionary<string, object?> { ["Number"] = 99 }));
        }
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(phase == "listener-fire" ? 7 : 11, Assert.IsType<Data>(result.WorkflowData).Number);
        Assert.Empty(ExecutionCheckpoint.Read((await cluster.GetInstanceAsync(instance.InstanceId))!.ExecutionStateJson).Joins);

        void Settled(ContinuationState child, int number)
        {
            Assert.IsType<Data>(child.Data).Number = number; child.DataChanged = true;
            child.SetPrevious(new Work(probe) { Number = number }); child.Stack[0].NextStepIndex = 1;
        }
        void Hold(ContinuationState child) => checkpoint.Debug = checkpoint.Debug with
        {
            Stepping = new(child.Id, DebugStepKind.Out, SteppingPolicy.Position(child.Stack[0]),
                [child.Stack[0].ActivationId], [child.Id], "hold", "test")
        };
    }
}
