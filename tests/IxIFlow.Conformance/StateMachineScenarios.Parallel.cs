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

public sealed partial class StateMachineScenarios
{
    [Trait("Feature", "control-flow"), Trait("Spec", "W-02")]
    public sealed class RecoveryPart1
    {
        [Theory, MemberData(nameof(Recoveries))]
        public Task EveryNativePhaseResumesFromTheStoreWithItsSavedPreviousAndChoice(string profile, string phase, bool prepare) =>
            StateMachineScenarios.EveryNativePhaseResumesFromTheStoreWithItsSavedPreviousAndChoice(profile, phase, prepare);

        public static IEnumerable<object[]> Recoveries =>
            ScenarioCaseShards.Select(StateMachineScenarios.Recoveries, 0, 2);
    }

    [Trait("Feature", "control-flow"), Trait("Spec", "W-02")]
    public sealed class RecoveryPart2
    {
        [Theory, MemberData(nameof(Recoveries))]
        public Task EveryNativePhaseResumesFromTheStoreWithItsSavedPreviousAndChoice(string profile, string phase, bool prepare) =>
            StateMachineScenarios.EveryNativePhaseResumesFromTheStoreWithItsSavedPreviousAndChoice(profile, phase, prepare);

        public static IEnumerable<object[]> Recoveries =>
            ScenarioCaseShards.Select(StateMachineScenarios.Recoveries, 1, 2);
    }

    [Trait("Feature", "control-flow"), Trait("Spec", "W-02")]
    public sealed class BehaviorPart1
    {
        [Theory, MemberData(nameof(OutputFailures), MemberType = typeof(StateMachineScenarios))]
        public async Task ThrowingTriggerOutputsReachCatchAndFinallyIncludingCommittedAcceptance(
            string profile, string kind, bool recover)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var probe = new Probe();
            var fault = new FaultRepository(repository);
            await using var services = Services(store, fault, probe: probe);
            var client = services.GetRequiredService<IWorkflowClient>();

            var definition = Workflow.Create<Data>("StateOutputFailure" + kind + recover).Step<Incoming>()
                .Try(b => b.StateMachine(m => m.Start("a").State("a", s =>
                {
                    s.Exit(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "exit")));

                    if (kind == "reply")
                        s.Entry(e => e.Request<Ask>("answer", a => a.Input(x => x.Name).From(c => "answer")))
                            .OnReply<Signal>("answer",
                                t => t.Output(x => x.Number).To(c => c.WorkflowData.Missing!.Number).Otherwise("end"));
                    else
                        s.On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Missing!.Number).Otherwise("end"));
                })))
                .Catch<NullReferenceException>(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "catch")).WithId("catch-effect"))
                .Finally(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "finally"))).Build();

            var idle = await client.RunAsync(definition, new Data());
            var catchId = definition.StepTable.Single(e => e.AuthorId == "catch-effect").CatalogId;

            fault.FailCommit = instance => recover && ExecutionCheckpoint.Read(instance.ExecutionStateJson)
                .Continuations[0].PendingActivity?.StepId == catchId;

            Task<WorkflowExecutionResult> Fire() => kind == "reply"
                ? client.ReplyAsync(probe.Tokens["answer"], new Signal { Number = 7 })
                : client.ResumeAsync(idle.InstanceId, "go", new Signal { Number = 7 });

            WorkflowExecutionResult result;

            if (recover)
            {
                // Intake committed the acceptance; interrupt the next durable effect after the mapping throws.
                await Assert.ThrowsAnyAsync<Exception>(Fire);
                var saved = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson);
                Assert.NotNull(saved.Continuations[0].AcceptedWait);
                Assert.Empty(probe.Marks);
                fault.FailCommit = _ => false;
                result = await client.RecoverAsync(idle.InstanceId);
            }
            else
                result = await Fire();

            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            Assert.Equal(new[] { "catch", "finally" }, probe.Marks);
            Assert.Empty(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));

            Assert.Null(ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson)
                .Continuations[0].AcceptedWait);
        }

        [Theory, MemberData(nameof(Replies), MemberType = typeof(StateMachineScenarios))]
        public async Task RepliesBindBeforeArmAndInTheFiredWindowAndArbitrateInDeclarationOrder(string profile, string phase)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var probe = new Probe();
            await using var services = Services(store, repository, probe: probe);
            var client = services.GetRequiredService<IWorkflowClient>();
            var ops = services.GetRequiredService<IWorkflowOperations>();

            var definition = Workflow.Create<Data>("StateReplies" + phase).StateMachine(m => m.Start("a").State("a", s =>
            {
                s.Entry(e => e.Request<Ask>("first", a => a.Input(x => x.Name).From(c => "first"))
                    .Request<Ask>("second", a => a.Input(x => x.Name).From(c => "second"))
                    .WaitFor<Signal>("entry-gate"));

                // The second request is deliberately declared first among the transitions.
                s.OnReply<Signal>("second", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("second"))
                    .On<Signal>("pulse", t => t.When(c => false, "second"))
                    .OnReply<Signal>("first", "first");
            }).State("first", s => s.Final()).State("second", s => s.Final())).WithId("machine").Build();

            var idle = await client.RunAsync(definition, new Data());
            Assert.Equal(2, probe.Tokens.Count);

            if (phase is "early" or "two-early")
            {
                if (phase == "two-early")
                    EventAssert.Consumed(await client.ReplyAsync(probe.Tokens["first"], new Signal { Number = 1 }));

                EventAssert.Consumed(await client.ReplyAsync(probe.Tokens["second"], new Signal { Number = 2 }));
            }

            if (phase is not ("early" or "two-early"))
                Assert.Equal(WorkflowExecutionStatus.Suspended,
                    (await client.ResumeAsync(idle.InstanceId, "entry-gate", new Signal())).Status);

            WorkflowExecutionResult result;

            if (phase == "fired")
            {
                await ops.SetBreakpointsAsync(idle.InstanceId, [new("arm", "machine/a/arm", HitCondition: "== 1")], "set");

                // The first Arm was dispatched before the breakpoint was installed; this is its guard-ready occurrence.
                Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "pulse", new Signal())).Status);
                await client.ReplyAsync(probe.Tokens["second"], new Signal { Number = 2 });
                result = await ops.ContinueAsync(idle.InstanceId, "continue");
            }
            else
                result = phase is "early" or "two-early"
                    ? await client.ResumeAsync(idle.InstanceId, "entry-gate", new Signal())
                    : await client.ReplyAsync(probe.Tokens["second"], new Signal { Number = 2 });

            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            Assert.Equal(2, Assert.IsType<Data>(result.WorkflowData).Number);
            Assert.Equal(2, probe.Tokens.Count);
            var trace = await ops.GetTraceAsync(idle.InstanceId);

            Assert.Equal("second", Assert.IsType<GraphTrace>(Assert.Single(trace,
                entry => entry.Entry.EntryType == TraceEntryType.TransitionTaken).Entry.Payload).Target);

            Assert.Empty(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        }

        [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
        public async Task GraphsInParallelShareTheInstancesEventKeySpace(string profile)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            await using var services = Services(store, repository);
            var client = services.GetRequiredService<IWorkflowClient>();

            var definition = Workflow.Create<Data>("StateSharedKeys").Step<Incoming>().Parallel(p => p
                    .Do(b => b.StateMachine(m => m.Start("a").State("a", s => s.On<Signal>("shared", "end"))))
                    .Do(b => b.StateMachine(m => m.Start("b").State("b", s => s.On<Signal>("shared", "end")))))
                .Build();

            var idle = await client.RunAsync(definition, new Data());
            EventAssert.Rejected(await client.ResumeAsync(idle.InstanceId, "shared", new Signal()), EventRejection.Ambiguous);
            var waits = await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId));
            Assert.Equal(2, waits.Count);
            Assert.All(waits, wait => Assert.Equal("shared", wait.Key));
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ReplyAsync(waits[0].Token, new Signal())).Status);
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.ReplyAsync(waits[1].Token, new Signal())).Status);
        }

        [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
        public async Task ExplicitTimeoutLeavesEntryThroughArmAndExitWithoutATriggerAction(string profile)
        {
            var time = Clock(profile);
            await using var store = await StoreProfiles.CreateAsync(profile, time);
            var repository = store.NewRepository();
            var probe = new Probe();
            await using var services = Services(store, repository, time, probe, compiler: true);
            var client = services.GetRequiredService<IWorkflowClient>();
            var ops = services.GetRequiredService<IWorkflowOperations>();

            var definition = new WorkflowBuilder<Data, object>([], "StateEntryTimeout").WaitFor<Signal>("start").Step<Incoming>()
                .StateMachine(m => m.Start("a").State("a", s => s
                        .Entry(e => e.WaitFor<Signal>("entry", configure: w => w.TimeoutAfter(TimeSpan.FromMilliseconds(100))
                            .OnTimeout(b => b.Transition("done"))))
                        .Exit(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => c.PreviousStep.Text)))
                        .On<Signal>("pulse", "a"))
                    .State("done", s => s.Final())).WithId("machine").Build();

            var idle = await client.RunAsync(definition, new Data());

            await ops.SetBreakpointsAsync(idle.InstanceId, [
                new("arm", "machine/a/arm",
                    Condition: "context.PreviousStep.Text == \"incoming\"")
            ], "set");

            await client.ResumeAsync(idle.InstanceId, "start", new Signal());
            await Due(repository, idle.InstanceId, time);
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.RecoverAsync(idle.InstanceId)).Status);
            Assert.Null((await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.Reason);
            Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continue")).Status);
            Assert.Equal(new[] { "incoming" }, probe.Marks);
            var trace = await ops.GetTraceAsync(idle.InstanceId);
            Assert.Contains(trace, t => t.Entry.Payload is GraphTrace { Cause: TransitionCause.Explicit });
            Assert.DoesNotContain(trace, t => t.Entry.EntryType == TraceEntryType.ActionEntered);
        }

        [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
        public async Task NestedGraphsInBranchesAndItemsAndCatchBodiesShareOrdinaryControlFlow(string profile)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            await using var services = Services(store, repository);

            var definition = Workflow.Create<Data>("StateNested").Step<Incoming>()
                .Parallel(p => p.Do(b => b.ForEach(c => c.WorkflowData.Items, (body, item) => body.StateMachine(m => m
                    .Start("a").State("a", s => s.Entry(e => e.Flowchart(f => f.Start("n").Node("n"))
                        .Saga(saga => saga.Step<Incoming>())).When(c => c.Item(item) > 0, "done").Otherwise("done"))
                    .State("done", s => s.Final())))))
                .Try(b => b.Step<Fail>()).Catch<InvalidOperationException>(c => c.StateMachine(m => m.Start("done")
                    .State("done", s => s.Final()))).Build();

            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        }

        [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
        public async Task AcknowledgedArmEvaluationAndCommandSkipRecoverWithoutRecountingAndNewFireCounts(string profile)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var fault = new FaultRepository(repository);
            await using var services = Services(store, fault, compiler: true);
            var client = services.GetRequiredService<IWorkflowClient>();
            var ops = services.GetRequiredService<IWorkflowOperations>();

            var definition = new WorkflowBuilder<Data, object>([], "StateDebugRecovery").WaitFor<Signal>("start")
                .Step<Incoming>().StateMachine(m => m.Start("a").State("a", s => s
                    .Entry(e => e.Step<Copy>()).On<Signal>("pulse", t => t.When(c => false, "end"))))
                .WithId("machine").Build();

            var idle = await client.RunAsync(definition, new Data());

            await ops.SetBreakpointsAsync(idle.InstanceId, [
                new("arm", "machine/a/arm",
                    Condition: "context.PreviousStep.Text == \"incoming\"")
            ], "set");

            fault.FailCommit = instance => WorkflowDebugRules.Checkpoint(instance).Stop?.BreakpointId == "arm";
            await Assert.ThrowsAnyAsync<Exception>(() => client.ResumeAsync(idle.InstanceId, "start", new Signal()));
            var saved = WorkflowDebugRules.Checkpoint((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!);
            Assert.Equal(1, saved.Evaluation!.ArmOccurrence);
            Assert.Equal(1, saved.Breakpoints[0].HitCount);
            fault.FailCommit = _ => false;
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.RecoverAsync(idle.InstanceId)).Status);
            Assert.Equal(1, (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.HitCount);
            fault.FailCommit = instance => instance.Status == WorkflowStatus.Suspended;
            await Assert.ThrowsAnyAsync<Exception>(() => ops.ContinueAsync(idle.InstanceId, "continue"));
            saved = WorkflowDebugRules.Checkpoint((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!);
            Assert.Equal(1, Assert.Single(saved.SkipOnce).ArmOccurrence);
            fault.FailCommit = _ => false;
            var resumed = await client.RecoverAsync(idle.InstanceId);

            if (resumed.Status == WorkflowExecutionStatus.Faulted)
            {
                // A wake may already have applied the command and returned to idle after the fault is removed.
                Assert.Contains("no interrupted execution to recover", resumed.ErrorMessage);
                resumed = await client.AwaitAsync(idle.InstanceId).WaitAsync(TimeSpan.FromSeconds(30));
            }

            Assert.True(resumed.Status == WorkflowExecutionStatus.Suspended, resumed.ErrorMessage);
            Assert.Empty(WorkflowDebugRules.Checkpoint((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!).SkipOnce);
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "pulse", new Signal())).Status);
            var stop = (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!;
            Assert.Equal(2, stop.ArmOccurrence);
            Assert.Equal(2, stop.HitCount);
            Assert.Null(stop.Reason);
        }
    }

    [Trait("Feature", "control-flow"), Trait("Spec", "W-02")]
    public sealed class BehaviorPart2
    {
        [Theory, MemberData(nameof(Races), MemberType = typeof(StateMachineScenarios))]
        public async Task OneInboxPageSelectsOneFireAndClosesEventAndTimerSiblings(string profile, string race)
        {
            var time = Clock(profile);
            await using var store = await StoreProfiles.CreateAsync(profile, time);
            var repository = store.NewRepository();
            await using var services = Services(store, repository, time);
            var client = services.GetRequiredService<IWorkflowClient>();

            var definition = Workflow.Create<Data>("StateRace" + race).StateMachine(m => m.Start("a").State("a", s => s
                    .On<Signal>("one", "event-one").On<Signal>("two", "event-two")
                    .After(TimeSpan.FromSeconds(1), "timer-one").After(TimeSpan.FromSeconds(1), "timer-two"))
                .State("event-one", s => s.Final()).State("event-two", s => s.Final())
                .State("timer-one", s => s.Final()).State("timer-two", s => s.Final())).Build();

            var idle = await client.RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            var timerIds = (await repository.GetPendingEventsAsync(idle.InstanceId, 0, 100)).Deadlines!.Keys.ToArray();

            if (race != "timers")
                await Append(repository, idle.InstanceId, "first", "one", new Signal());

            if (race == "events")
                await Append(repository, idle.InstanceId, "second", "two", new Signal());
            else
                await Due(repository, idle.InstanceId, time);

            var result = await client.RecoverAsync(idle.InstanceId);
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(idle.InstanceId);

            var choice = Assert.IsType<GraphTrace>(Assert.Single(trace,
                entry => entry.Entry.EntryType == TraceEntryType.TransitionTaken).Entry.Payload);

            Assert.Equal(race == "timers" ? "timer-one" : "event-one", choice.Target);
            Assert.Equal(race == "timers" ? 1 : 0, trace.Count(e => e.Entry.EntryType == TraceEntryType.TimerFired));

            if (race == "events")
            {
                Assert.Equal(EventOutcome.Consumed, (await repository.GetEventAsync(idle.InstanceId, "first"))!.Outcome);
                Assert.Equal(EventRejection.NoOpenWait, (await repository.GetEventAsync(idle.InstanceId, "second"))!.Rejection);
            }

            var waits = (await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId) { IncludeClosed = true }))
                .Concat(await Task.WhenAll(timerIds.Select(async id => (await repository.GetWaitAsync(idle.InstanceId, id))!))).ToArray();

            Assert.Equal(4, waits.Length);
            Assert.All(waits, wait => Assert.False(wait.IsOpen));
            Assert.Equal(3, waits.Count(wait => wait.CloseReason == WaitCloseReason.Cancelled));
        }

        [Theory, MemberData(nameof(Arbitration), MemberType = typeof(StateMachineScenarios))]
        public async Task BoundRepliesAndSatisfiedImmediatesUseOneDeclarationOrder(string profile, string order)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var probe = new Probe();
            await using var services = Services(store, repository, probe: probe);
            var client = services.GetRequiredService<IWorkflowClient>();

            var definition = Workflow.Create<Data>("StateArbitration" + order).StateMachine(m => m.Start("a").State("a", s =>
            {
                s.Entry(e => e.Request<Ask>("answer", a => a.Input(x => x.Name).From(c => "answer")).WaitFor<Signal>("entry"));

                if (order == "immediate-first")
                    s.When(c => true, "immediate");

                s.OnReply<Signal>("answer", "reply");

                if (order == "reply-first")
                    s.When(c => true, "immediate");
            }).State("reply", s => s.Final()).State("immediate", s => s.Final())).Build();

            var idle = await client.RunAsync(definition, new Data());
            EventAssert.Consumed(await client.ReplyAsync(probe.Tokens["answer"], new Signal()));
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(idle.InstanceId, "entry", new Signal())).Status);
            var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(idle.InstanceId);

            Assert.Equal(order == "reply-first" ? "reply" : "immediate", Assert.IsType<GraphTrace>(Assert.Single(trace,
                e => e.Entry.EntryType == TraceEntryType.TransitionTaken).Entry.Payload).Target);

            Assert.Empty(ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson)
                .BoundReplies);
        }

        [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
        public async Task ImmediateWinnerClosesTheEntryRequestAndDiscoveryShowsOnlyEventTriggers(string profile)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var probe = new Probe();
            await using var services = Services(store, repository, probe: probe);
            var client = services.GetRequiredService<IWorkflowClient>();

            var definition = Workflow.Create<Data>("StateDiscovery").StateMachine(m => m.Start("a").State("a", s => s
                    .Entry(e => e.Request<Ask>("answer", a => a.Input(x => x.Name).From(c => "answer")))
                    .OnReply<Signal>("answer", "end").On<Signal>("pulse", "end")
                    .After(TimeSpan.FromHours(1), "end").When(c => c.WorkflowData.Number == 1, "next"))
                .State("next", s => s.On<Signal>("next", "end"))).Build();

            var idle = await client.RunAsync(definition, new Data());
            var discovery = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
            Assert.Equal((WaitKind.Event, "pulse"), (discovery.Kind, discovery.Key));
            var immediate = await client.RunAsync(definition, new Data { Number = 1 });
            Assert.Equal(WorkflowExecutionStatus.Suspended, immediate.Status);
            var waits = await repository.GetOpenWaitsAsync(new(InstanceId: immediate.InstanceId) { IncludeClosed = true });
            Assert.Contains(waits, w => w.Kind == WaitKind.Reply && w.CloseReason == WaitCloseReason.Cancelled);
            EventAssert.Rejected(await client.ReplyAsync(probe.Tokens["answer"], new Signal()), EventRejection.WaitCancelled);

            var checkpoint =
                ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(immediate.InstanceId))!.ExecutionStateJson);

            Assert.Empty(checkpoint.BoundReplies);
        }

        [Theory, MemberData(nameof(Steps), MemberType = typeof(StateMachineScenarios))]
        public async Task SteppingAnArmedStateStopsBeforeGuardsOnTheNextOccurrenceAndReplayMatches(string profile, DebugStepKind kind)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            await using var services = Services(store, repository);
            var client = services.GetRequiredService<IWorkflowClient>();
            var ops = services.GetRequiredService<IWorkflowOperations>();

            var definition = new WorkflowBuilder<Data, object>([], "StateStep" + kind).WaitFor<Signal>("start")
                .StateMachine(m => m.Start("a").State("a", s => s.On<Signal>("pulse", t => t.When(c => false, "end"))))
                .WithId("machine").Build();

            var idle = await client.RunAsync(definition, new Data());
            await ops.SetBreakpointsAsync(idle.InstanceId, [new("arm", "machine/a/arm")], "set");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Signal())).Status);
            await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
            var thread = (await ops.InspectAsync(idle.InstanceId))!.Threads[0];
            Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, kind, "arm")).Status);
            Assert.Equal("Blocked", (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.Reason);
            await Append(repository, idle.InstanceId, "pulse", "pulse", new Signal());

            Assert.Equal(WorkflowExecutionStatus.Paused,
                (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, kind, "fire")).Status);

            var fired = (await ops.InspectAsync(idle.InstanceId))!;
            Assert.Equal("machine/a/arm", fired.Threads[0].AuthorStepId);
            Assert.Equal(2, fired.DebugState.Stop!.ArmOccurrence);
            var checkpoint = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson);
            Assert.NotNull(checkpoint.Continuations[0].AcceptedWait);

            Assert.Equal(WorkflowExecutionStatus.Paused,
                (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, kind, "guard")).Status);

            Assert.Equal(3, (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.ArmOccurrence);
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await ops.ContinueAsync(idle.InstanceId, "rearm")).Status);
            var trace = await ops.GetTraceAsync(idle.InstanceId);
            var replay = TraceReplay.Load(definition, trace);
            var arms = trace.Where(t => t.Entry.EntryType == TraceEntryType.StepEntered && t.Entry.StepId == "root/1/state/a/1").ToArray();
            Assert.Equal(3, arms.Length);

            for (var index = 0; index < arms.Length; index++)
            {
                replay.Seek(arms[index].Sequence);
                Assert.Equal("root/1/state/a/1", replay.CurrentStepId);
                Assert.Equal(index + 1, replay.Current.Threads.Single().Frames[^1].ArmOccurrence);
            }
        }

        [Theory, MemberData(nameof(Effects), MemberType = typeof(StateMachineScenarios))]
        public async Task CommittedExitActionAndTargetEffectsRecoverWithTheChoiceAndTriggerValue(string profile, string stage)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var probe = new Probe();
            var fault = new FaultRepository(repository);
            await using var services = Services(store, fault, probe: probe, threshold: 64_000);
            var client = services.GetRequiredService<IWorkflowClient>();

            var definition = Workflow.Create<Data>("StateEffectRecovery" + stage).Step<Incoming>()
                .StateMachine(m => m.Start("a").State("a", s => s
                        .Exit(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "exit")
                            .Input(x => x.Value).From(c => c.PreviousStep.Text)).WithId("exit-effect"))
                        .On<Signal>("pulse", t => t.Otherwise("done", a => a.Step<Mark>(x => x
                                .Input(x => x.Name).From(c => "action")
                                .Input(x => x.Value).From(c =>
                                    c.PreviousStep.Number.ToString() + ":" + c.PreviousStep.Text.Length.ToString()))
                            .WithId("action-effect"))))
                    .State("done", s => s.Final().Entry(e => e.Step<Mark>(a => a
                            .Input(x => x.Name).From(c => "target").Input(x => x.Value).From(c => c.PreviousStep.Text))
                        .WithId("target-effect"))))
                .Step<Mark>(a => a.Input(x => x.Name).From(c => "after")
                    .Input(x => x.Value).From(c => c.PreviousStep.Text)).WithId("after-effect").Build();

            var idle = await client.RunAsync(definition, new Data());
            var next = stage switch { "exit" => "action", "action" => "target", _ => "after" };
            var nextId = definition.StepTable.Single(s => s.AuthorId == next + "-effect").CatalogId;

            fault.FailCommit = instance => probe.Marks.Contains(stage) &&
                ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations[0].PendingActivity?.StepId == nextId;

            await Assert.ThrowsAnyAsync<Exception>(() =>
                client.ResumeAsync(idle.InstanceId, "pulse", new Signal { Number = 7, Text = new string('x', 300_000) }));

            if (stage is "exit" or "action")
            {
                var saved = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson);
                Assert.NotNull(saved.Continuations[0].Stack[0].Graph!.Pending!.Event!.Json.Reference);
            }

            fault.FailCommit = _ => false;
            var recovered = await client.RecoverAsync(idle.InstanceId);
            Assert.True(recovered.Status == WorkflowExecutionStatus.Success, recovered.ErrorMessage);
            Assert.Equal(new[] { "exit", "action", "target", "after" }, probe.Marks);
            Assert.Equal("incoming", probe.Values["exit"]);
            Assert.Equal("7:300000", probe.Values["action"]);
            Assert.Equal("incoming", probe.Values["target"]);
            Assert.Equal("incoming", probe.Values["after"]);
            var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(idle.InstanceId);
            Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.TransitionTaken);
            Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.EventReceived);
        }
    }

    [Trait("Feature", "control-flow"), Trait("Spec", "W-02")]
    public sealed class BehaviorPart3
    {
        [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
        public async Task FalseEventAndTimerGuardsRescheduleAllWaitsWithFullDurationsAndKeepEntryOnce(string profile)
        {
            var time = Clock(profile);
            await using var store = await StoreProfiles.CreateAsync(profile, time);
            var repository = store.NewRepository();
            var probe = new Probe();
            await using var services = Services(store, repository, time, probe);
            var client = services.GetRequiredService<IWorkflowClient>();
            var ops = services.GetRequiredService<IWorkflowOperations>();

            var definition = new WorkflowBuilder<Data, object>([], "StateReschedule").WaitFor<Signal>("start")
                .StateMachine(m => m.Start("a").State("a", s => s
                        .Entry(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "entry")))
                        .On<Signal>("set", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).When(c => false, "done"))
                        .After(TimeSpan.FromSeconds(1), t => t.When(c => c.WorkflowData.Number == 2, "done")))
                    .State("done", s => s.Final())).WithId("machine").Build();

            var idle = await client.RunAsync(definition, new Data());
            await ops.SetBreakpointsAsync(idle.InstanceId, [new("arm", "machine/a/arm")], "set");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Signal())).Status);
            await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
            var thread = (await ops.InspectAsync(idle.InstanceId))!.Threads[0].ContinuationId;

            async Task Step(string command, bool blocked = false)
            {
                var result = await ops.StepAsync(idle.InstanceId, thread, DebugStepKind.Into, command);
                Assert.True(result.Status == WorkflowExecutionStatus.Paused, result.ErrorMessage);

                if (blocked)
                    Assert.Equal("Blocked", (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.Reason);
            }

            // Blocked debugger stops hold the waits still on every clock and coordinator profile.
            await Step("open", blocked: true);
            var first = await Timer(repository, idle.InstanceId);
            await Due(repository, idle.InstanceId, time);
            await Step("timer-fire");
            await Step("timer-guard");
            await Step("timer-rearm", blocked: true);
            var restarted = await Timer(repository, idle.InstanceId);
            Assert.NotEqual(first.WaitId, restarted.WaitId);
            Assert.True(restarted.DeadlineUtc > first.DeadlineUtc);
            Assert.Equal(TimeSpan.FromSeconds(1), restarted.DeadlineUtc - restarted.OpenedAtUtc);
            await Append(repository, idle.InstanceId, "set", "set", new Signal { Number = 2 });
            await Step("event-fire");
            await Step("event-guard");
            await Step("event-rearm", blocked: true);
            var afterEvent = await Timer(repository, idle.InstanceId);
            Assert.NotEqual(restarted.WaitId, afterEvent.WaitId);
            await Due(repository, idle.InstanceId, time);
            await Step("last-timer-fire");
            Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "finish")).Status);
            Assert.Equal(new[] { "entry" }, probe.Marks);
        }

        [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
        public async Task ReenteringAStateCreatesAFreshActivationAndRequestToken(string profile)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var probe = new Probe();
            await using var services = Services(store, repository, probe: probe);
            var client = services.GetRequiredService<IWorkflowClient>();

            var definition = Workflow.Create<Data>("StateReentry").StateMachine(m => m.Start("a").State("a", s => s
                    .Entry(e => e.Request<Ask>("answer", a => a.Input(x => x.Name).From(c => "answer")))
                    .OnReply<Signal>("answer", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                        .When(c => c.PreviousStep.Number < 2, "a").Otherwise("done")))
                .State("done", s => s.Final())).Build();

            var idle = await client.RunAsync(definition, new Data());
            var first = probe.Tokens["answer"];
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ReplyAsync(first, new Signal { Number = 1 })).Status);
            var second = probe.Tokens["answer"];
            Assert.NotEqual(first, second);
            EventAssert.Rejected(await client.ReplyAsync(first, new Signal { Number = 2 }), EventRejection.WaitAnswered);
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.ReplyAsync(second, new Signal { Number = 2 })).Status);

            var entries = (await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(idle.InstanceId))
                .Where(e => e.Entry.EntryType == TraceEntryType.StateEntered).ToArray();

            Assert.Equal(new long[] { 1, 2, 3 }, entries.Select(e => Assert.IsType<GraphTrace>(e.Entry.Payload).Visit));
            Assert.Equal(3, entries.Select(e => e.Entry.FrameActivationId).Distinct().Count());
        }

        [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
        public async Task ThrowingGuardsReachCatchAndCancellationUnwindsFinallyWithoutExit(string profile)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var probe = new Probe();
            await using var services = Services(store, repository, probe: probe);
            var client = services.GetRequiredService<IWorkflowClient>();

            var guarded = Workflow.Create<Data>("StateGuardFailure").Step<Incoming>()
                .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                    .Entry(e => e.Request<Ask>("answer", a => a.Input(x => x.Name).From(c => "answer")))
                    .Exit(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "exit")))
                    .OnReply<Signal>("answer", "end").On<Signal>("pulse", t => t.When(c => ThrowGuard(), "end")))))
                .Catch<InvalidOperationException>(c => c.Step<Mark>(a => a.Input(x => x.Name).From(c => "catch"))).Build();

            var idle = await client.RunAsync(guarded, new Data());
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(idle.InstanceId, "pulse", new Signal())).Status);
            Assert.Equal(new[] { "catch" }, probe.Marks);
            EventAssert.Rejected(await client.ReplyAsync(probe.Tokens["answer"], new Signal()), EventRejection.WaitCancelled);
            Assert.Empty(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));

            var cancel = Workflow.Create<Data>("StateCancellation").Step<Incoming>()
                .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                    .Exit(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "exit"))).On<Signal>("pulse", "a"))))
                .Finally(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "finally"))).Build();

            idle = await client.RunAsync(cancel, new Data());

            Assert.Equal(WorkflowExecutionStatus.Cancelled,
                (await client.CancelAsync(idle.InstanceId, new CancellationReason { ReasonCode = "stop" })).Status);

            Assert.Equal(new[] { "catch", "finally" }, probe.Marks);
            Assert.Empty(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        }

        [Theory, MemberData(nameof(Payloads), MemberType = typeof(StateMachineScenarios))]
        public async Task RecoveryAfterTheLastEntryActivityHydratesIncomingBeforeImmediateDurationAndRearm(string profile, string form)
        {
            var time = Clock(profile);
            await using var store = await StoreProfiles.CreateAsync(profile, time);
            var repository = store.NewRepository();
            var registry = new WorkflowCatalog();
            var probe = new Probe();

            var definition = Workflow.Create<Data>("StateEntryRecovery" + form)
                .Step<Incoming>(a => a.Input(x => x.Text).From(c => c.WorkflowData.Text))
                .StateMachine(m => m.Start("a").State("a", s =>
                {
                    s.Entry(e => e.Sequence(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "entry"))))
                        .On<Signal>("pulse", t => t.When(c => false, "end"));

                    if (form == "immediate")
                        s.When(c => c.PreviousStep.Text.Length == 300_000, "end");
                    else
                        s.After(c => TimeSpan.FromMilliseconds(c.PreviousStep.Text.Length / 300), "end");
                })).Build();

            await registry.RegisterWorkflowAsync(definition);

            var fail = new FaultRepository(repository)
            {
                FailCommit = instance =>
                    probe.Marks.Count == 1 && instance.Status == WorkflowStatus.Suspended
            };

            string id;

            await using (var first = Services(store, fail, time, probe, registry, threshold: 64_000))
            {
                id = Guid.NewGuid().ToString("N");

                if (form == "immediate")
                    fail.FailCommit = instance => probe.Marks.Count == 1 && instance.Status == WorkflowStatus.Completed;

                await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>()
                    .RunAsync(definition, new Data { Text = new string('x', 300_000) }, new() { InstanceId = id }));
            }

            await using var second = Services(store, repository, time, probe, registry, threshold: 64_000);
            var client = second.GetRequiredService<IWorkflowClient>();
            var recovered = await client.RecoverAsync(id);

            if (form != "immediate")
            {
                Assert.Equal(WorkflowExecutionStatus.Suspended, recovered.Status);

                if (form == "rearm")
                    Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResumeAsync(id, "pulse", new Signal())).Status);

                await Due(repository, id, time);
                recovered = await client.RecoverAsync(id);
            }

            Assert.True(recovered.Status == WorkflowExecutionStatus.Success, recovered.ErrorMessage);
            Assert.Equal(new[] { "entry" }, probe.Marks);
        }

        [Theory, MemberData(nameof(Acceptance), MemberType = typeof(StateMachineScenarios))]
        public async Task TriggerConsumptionSurvivesFailureBeforeCommitAndLossAfterCommit(string profile, string point)
        {
            await using var store = await StoreProfiles.CreateAsync(profile);
            var repository = store.NewRepository();
            var probe = new Probe();

            bool Accepted(WorkflowInstance instance) =>
                ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations[0].AcceptedWait != null;

            var fault = new FaultRepository(repository)
            {
                FailCommit = instance => point == "before" && Accepted(instance),
                LoseAcknowledgement = instance => point == "after" && Accepted(instance)
            };

            await using var services = Services(store, fault, probe: probe);
            var client = services.GetRequiredService<IWorkflowClient>();

            var definition = Workflow.Create<Data>("StateConsumption" + point).StateMachine(m => m.Start("a")
                .State("a", s => s.On<Signal>("pulse", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                    .Otherwise("done")))
                .State("done", s => s.Final().Entry(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "done"))))).Build();

            var idle = await client.RunAsync(definition, new Data());
            await Append(repository, idle.InstanceId, "event", "pulse", new Signal { Number = 7 });

            if (point == "before")
            {
                await Assert.ThrowsAnyAsync<Exception>(() => client.RecoverAsync(idle.InstanceId));
                Assert.Equal(EventOutcome.Pending, (await repository.GetEventAsync(idle.InstanceId, "event"))!.Outcome);
            }

            fault.FailCommit = _ => false;
            var result = await client.RecoverAsync(idle.InstanceId);
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            Assert.Equal(7, Assert.IsType<Data>(result.WorkflowData).Number);
            Assert.Equal(EventOutcome.Consumed, (await repository.GetEventAsync(idle.InstanceId, "event"))!.Outcome);
            Assert.Equal(new[] { "done" }, probe.Marks);

            Assert.Single(await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(idle.InstanceId),
                e => e.Entry.EntryType == TraceEntryType.EventReceived);
        }
    }
}