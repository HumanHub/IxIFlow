using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "W-02")]
public sealed partial class StateMachineTests
{
    [Theory]
    [InlineData("event")]
    [InlineData("reply")]
    [InlineData("timer")]
    [InlineData("duration")]
    public void PlainTriggerDestinationsForwardCallerLocationToTheirCaseEdge(string kind)
    {
        var definition = Workflow.Create<Data>("StateCaller" + kind).StateMachine(m => m.Start("a").State("a", s =>
        {
            if (kind == "event")
                s.On<Signal>("go", "end", sourcePath: "author.cs", sourceLine: 40);

            if (kind == "reply")
                s.Entry(e => e.Request<Ask>("answer"))
                    .OnReply<Signal>("answer", "end", sourcePath: "author.cs", sourceLine: 40);

            if (kind == "timer")
                s.After(TimeSpan.FromSeconds(1), "end", sourcePath: "author.cs", sourceLine: 40);

            if (kind == "duration")
                s.After(c => TimeSpan.FromSeconds(1), "end", sourcePath: "author.cs", sourceLine: 40);
        })).Build();

        Assert.All(definition.StepTable.Where(e => e.Kind is WorkflowStepType.Trigger or WorkflowStepType.Edge),
            entry => Assert.Equal("author.cs:40", entry.SourceLocation));
    }

    [Fact]
    public void DuplicateTriggerKeysRequireOrderedCasesUnderOneTrigger()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("DuplicateTriggers")
            .StateMachine(m => m.Start("a").State("a", s => s
                .On<Signal>("go", "end").On<Signal>("go", "end"))).Build());

        Assert.Contains("go", error.Message);
        Assert.Contains("ordered cases under one trigger", error.Message);

        // Keys are ordinal and local to a state.
        Workflow.Create<Data>("OrdinalTriggers").StateMachine(m => m.Start("a")
            .State("a", s => s.On<Signal>("go", "b").On<Signal>("GO", "end"))
            .State("b", s => s.On<Signal>("go", "end"))).Build();
    }

    [Theory]
    [InlineData("event")]
    [InlineData("reply")]
    [InlineData("timer")]
    public void UnconditionalImmediateLastCannotBypassWaitTriggers(string kind)
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("ImmediateWithWait" + kind)
            .StateMachine(m => m.Start("a").State("a", s =>
            {
                if (kind == "event")
                    s.On<Signal>("go", "end");

                if (kind == "reply")
                    s.Entry(e => e.Request<Ask>("answer")).OnReply<Signal>("answer", "end");

                if (kind == "timer")
                    s.After(TimeSpan.FromSeconds(1), "end");

                s.Otherwise("end");
            })).Build());

        Assert.Contains("unconditional immediate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event, reply or timer", error.Message);
    }

    [Fact]
    public async Task EntryExitActionAndFinalUseTheirDeclaredPreviousTypesAndRealReplayPicks()
    {
        await using var services = Services();

        var definition = Workflow.Create<Data>("NativeStateOrder").Step<Incoming>()
            .StateMachine(m => m.Start("pending").State("pending", s => s
                    .Entry(e => e.Step<Other>())
                    .Exit(e => e.Step<Copy>(a => a.Input(x => x.Text).From(c => c.PreviousStep.Text + ":exit")
                        .Output(x => x.Text).To(c => c.WorkflowData.Exit)))
                    .On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                        .When(c => c.PreviousStep.Number == 7, "done", a => a.Step<Copy>(x => x
                            .Input(x => x.Text).From(c => c.PreviousStep.Number.ToString())
                            .Output(x => x.Text).To(c => c.WorkflowData.Action)))))
                .State("done", s => s.Final().Entry(e => e.Step<Copy>(a => a
                    .Input(x => x.Text).From(c => c.PreviousStep.Text)
                    .Output(x => x.Text).To(c => c.WorkflowData.Final)))))
            .WithId("machine").Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Signal { Number = 7 });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal("incoming:exit", data.Exit);
        Assert.Equal("7", data.Action);
        Assert.Equal("incoming", data.Final);
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(idle.InstanceId);
        var selection = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.TransitionTaken);
        var exit = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.ExitEntered);

        var trigger = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.StepEntered &&
            e.Entry.StepId == "root/1/state/pending/on/0/0");

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

        Assert.Contains(trace, e => e.Entry.Payload is GraphTrace { Cause: TransitionCause.Final });
    }

    [Fact]
    public async Task TriggerSuggestionsReadIncomingAndMatchesRejectsWithoutFiringBeforeOutputsAndGuards()
    {
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<Data>("StateIntakeContract").Step<Incoming>()
            .StateMachine(m => m.Start("a").State("a", s => s.Entry(e => e.Step<Other>())
                .On<Signal>("go", t => t.Matches((value, c) => value.Number > 0)
                    .DefaultPayload(new Signal { Number = 99 })
                    .Input(x => x.Number).From(c => c.PreviousStep.Text.Length)
                    .Output(x => x.Number).To(c => c.WorkflowData.Number)
                    .When(c => c.PreviousStep.Number == 2 && c.WorkflowData.Number == 2, "end")))).Build();

        var idle = await client.RunAsync(definition, new Data());
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        Assert.Equal(8, JsonSerializer.Deserialize<Signal>(wait.DefaultPayloadJson!)!.Number);
        var rejected = await client.ResumeAsync(idle.InstanceId, "go", new Signal());
        Assert.Equal(EventRejection.NotMatched, rejected.Event!.Rejection);
        Assert.Equal(wait.WaitId, Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId))).WaitId);
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Signal { Number = 2 });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(2, Assert.IsType<Data>(result.WorkflowData).Number);
    }

    [Fact]
    public async Task ACaseEdgeBreakpointSeesTheTriggerValueOnlyAfterExitHasRun()
    {
        await using var services = Services(compiler: true);
        var client = services.GetRequiredService<IWorkflowClient>();
        var ops = services.GetRequiredService<IWorkflowOperations>();

        var definition = Workflow.Create<Data>("StateEdgeStop").Step<Incoming>()
            .StateMachine(m => m.Start("a").State("a", s => s
                .Exit(e => e.Step<Copy>(a => a.Input(x => x.Text).From(c => c.PreviousStep.Text)
                    .Output(x => x.Text).To(c => c.WorkflowData.Exit)))
                .On<Signal>("go", t => t.When(c => c.PreviousStep.Number == 7, "end"))))
            .WithId("machine").Build();

        var idle = await client.RunAsync(definition, new Data());

        await ops.SetBreakpointsAsync(idle.InstanceId, [
            new("edge", "machine/a/on/0/case/0",
                Condition: "context.PreviousStep.Number == 7")
        ], "set");

        var paused = await client.ResumeAsync(idle.InstanceId, "go", new Signal { Number = 7 });
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        Assert.Equal("incoming", Assert.IsType<Data>(paused.WorkflowData).Exit);
        var inspection = (await ops.InspectAsync(idle.InstanceId))!;
        Assert.Equal("Case", inspection.Threads[0].Frames[^1].Kind);
        Assert.Null(inspection.DebugState.Stop!.Reason);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continue")).Status);
    }

    [Theory]
    [InlineData("immediate")]
    [InlineData("duration")]
    [InlineData("rearm")]
    public async Task ClaimCheckedIncomingIsNormalizedBeforeTypedArmBreakpointsAndGuards(string form)
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(clock);
        await using var services = Services(store, clock, threshold: 64_000, compiler: true);
        var client = services.GetRequiredService<IWorkflowClient>();
        var ops = services.GetRequiredService<IWorkflowOperations>();

        // Park before entry, install a typed condition, and recover the separately saved incoming snapshot.
        var gated = new WorkflowBuilder<Data, object>([], "GatedStatePayload" + form).WaitFor<Signal>("start")
            .Step<Incoming>(a => a.Input(x => x.Text).From(c => c.WorkflowData.Final))
            .StateMachine(m => m.Start("a").State("a", s =>
            {
                s.Entry(e => e.Sequence(b => b.Step<Other>())).On<Signal>("go", t => t.When(c => false, "done"));

                if (form == "immediate")
                    s.When(c => c.PreviousStep.Text.Length == 300_000, "done");
                else
                    s.After(c => TimeSpan.FromMilliseconds(c.PreviousStep.Text.Length), "done");
            }).State("done", s => s.Final())).WithId("machine").Build();

        var idle = await client.RunAsync(gated, new Data { Final = new string('x', 300_000) });

        await ops.SetBreakpointsAsync(idle.InstanceId,
            [new("arm", "machine/a/arm", Condition: "context.PreviousStep.Text.Length == 300000")], "set");

        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Signal())).Status);
        var stop = (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!;
        Assert.Null(stop.Reason);
        Assert.Equal(1, stop.ArmOccurrence);
        var checkpoint = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson);
        Assert.NotNull(checkpoint.Continuations[0].Stack[^1].EntryPrevious!.Json.Reference);
        var result = await ops.ContinueAsync(idle.InstanceId, "continue");

        if (form == "immediate")
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);

            if (form == "rearm")
            {
                Assert.Equal(WorkflowExecutionStatus.Paused,
                    (await client.ResumeAsync(idle.InstanceId, "go", new Signal())).Status);

                Assert.Equal(2, (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.ArmOccurrence);
                Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.ContinueAsync(idle.InstanceId, "guards")).Status);
                var rearm = (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!;
                Assert.Equal(3, rearm.ArmOccurrence);
                Assert.Null(rearm.Reason);
                var rearmed = await ops.ContinueAsync(idle.InstanceId, "rearm");
                Assert.True(rearmed.Status == WorkflowExecutionStatus.Suspended, rearmed.ErrorMessage);
            }

            await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear-timer");
            clock.Advance(TimeSpan.FromSeconds(301));
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.RecoverAsync(idle.InstanceId)).Status);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    public void NativePhasesRoundTripWithLiveOrPreparedPrevious(int phase, bool prepare)
    {
        using var services = Services();

        var definition = new WorkflowBuilder<Data, Incoming>([], "StatePhases")
            .StateMachine(m => m.Start("a").State("a", s => s.Exit(e => e)
                    .When(c => c.PreviousStep.Text == "incoming", "b").Otherwise("b"))
                .State("b", s => s.Final())).Build();

        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var branch = new ContinuationState { Id = "root", Stack = [new() { ScopeId = "root" }] };
        branch.SetPrevious(new Incoming());
        branch.Previous = SerializedValue.From(branch.RuntimePrevious);
        var checkpoint = new ExecutionCheckpoint { StructureHash = scopes.StructureHash, RootData = new Data(), Continuations = [branch] };
        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        checkpoint.Trace.Configure(new WorkflowInstance { InstanceId = "phases" }, checkpoint, ExecutionLimits.Default, null, scopes);

        for (var i = 0; i <= phase; i++)
            transitions.Advance(scopes, checkpoint, branch, 100, 100);

        if (phase == 7)
            Assert.Equal("TransitionDone", GraphFrameInfo.Of(scopes, branch, branch.Stack.Count - 1)!.Phase);

        if (phase == 10)
            Assert.Equal("FinalEntry", GraphFrameInfo.Of(scopes, branch, branch.Stack.Count - 1)!.Phase);

        if (prepare)
            ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);

        checkpoint = ExecutionCheckpoint.Read(JsonSerializer.Serialize(checkpoint));
        checkpoint.RootData = new Data();
        checkpoint.Trace.Configure(new WorkflowInstance { InstanceId = "phases" }, checkpoint, ExecutionLimits.Default, null, scopes);
        branch = checkpoint.Continuations[0];

        for (var i = 0; i < 30 && branch.Status != ContinuationStatus.Completed; i++)
            transitions.Advance(scopes, checkpoint, branch, 100, 100);

        Assert.Equal(ContinuationStatus.Completed, branch.Status);
        Assert.Equal("incoming", Assert.IsType<Incoming>(branch.GetPrevious(services)).Text);
    }

    [Fact]
    public async Task AcknowledgedBreakpointEvaluationAndSkipRecoverOnlyTheirArmGeneration()
    {
        using var services = Services(compiler: true);

        var definition = new WorkflowBuilder<Data, Incoming>([], "StateConditionRecovery")
            .StateMachine(m => m.Start("a").State("a", s => s.On<Signal>("go", "end"))).Build();

        var scopes = WorkflowDefinitionValidator.ForExecution(definition);

        var frame = new ScopePosition
        {
            ScopeId = "root/0/state/a", Kind = FrameKind.State, Node = "a",
            NextStepIndex = 1, ArmOccurrence = 5, EntryPrevious = SerializedValue.From(new Incoming())
        };

        var branch = new ContinuationState
            { Id = "root", Stack = [new() { ScopeId = "root", Graph = new() { Node = "a", Visits = 1 } }, frame] };

        var checkpoint = new ExecutionCheckpoint { StructureHash = scopes.StructureHash, RootData = new Data(), Continuations = [branch] };
        var id = "root/0/state/a/1";

        checkpoint.Debug = checkpoint.Debug with
        {
            Breakpoints = BreakpointResolver.Resolve(definition,
                [new("arm", id, Condition: "context.PreviousStep.Text == \"incoming\"")])
        };

        GraphTransitions.PreparePick(scopes, branch);
        var commits = 0;

        await Assert.ThrowsAsync<Interrupted>(() => BreakpointDispatch.HitAsync(checkpoint, branch, definition, id,
            DateTime.UtcNow, services, () =>
            {
                commits++;

                throw new Interrupted();
            }));

        Assert.Equal(1, commits);
        checkpoint = ExecutionCheckpoint.Read(JsonSerializer.Serialize(checkpoint));
        checkpoint.RootData = new Data();
        branch = checkpoint.Continuations[0];
        GraphTransitions.PreparePick(scopes, branch);

        Assert.True(await BreakpointDispatch.HitAsync(checkpoint, branch, definition, id, DateTime.UtcNow, services,
            () => throw new InvalidOperationException("Evaluation must reuse its acknowledged count")));

        Assert.Equal(1, checkpoint.Debug.Breakpoints[0].HitCount);
        Assert.Equal(5, checkpoint.Debug.Stop!.ArmOccurrence);
        checkpoint.Debug = checkpoint.Debug with { SkipOnce = [checkpoint.Debug.Stop], Stop = null };
        checkpoint = ExecutionCheckpoint.Read(JsonSerializer.Serialize(checkpoint));
        checkpoint.RootData = new Data();
        branch = checkpoint.Continuations[0];
        GraphTransitions.PreparePick(scopes, branch);

        Assert.False(await BreakpointDispatch.HitAsync(checkpoint, branch, definition, id, DateTime.UtcNow, services,
            () => Task.CompletedTask));

        branch.Stack[^1].ArmOccurrence++;

        Assert.True(await BreakpointDispatch.HitAsync(checkpoint, branch, definition, id, DateTime.UtcNow, services,
            () => Task.CompletedTask));

        Assert.Equal(2, checkpoint.Debug.Breakpoints[0].HitCount);
        Assert.Equal(6, checkpoint.Debug.Stop!.ArmOccurrence);
    }

    [Fact]
    public async Task IntakePreparationHydratesStateEntryAndPendingEventBelowAFinishingChild()
    {
        using var services = Services();

        var definition = new WorkflowBuilder<Data, Incoming>([], "StateIntakeHydration")
            .StateMachine(m => m.Start("a").State("a", s => s.Entry(e => e.Sequence(b => b.Step<Other>()))
                .On<Signal>("go", "end"))).Build();

        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var incomingJson = StoredJson.Inline(JsonSerializer.Serialize(new Incoming { Text = new string('x', 300_000) }));
        var eventJson = StoredJson.Inline(JsonSerializer.Serialize(new Signal { Number = 7 }));

        var incoming = new SerializedValue
        {
            Type = WorkflowTypeIdentity.SavedName(typeof(Incoming)),
            Json = StoredJson.FromReference(incomingJson.Identity)
        };

        var fired = new SerializedValue
        {
            Type = WorkflowTypeIdentity.SavedName(typeof(Signal)),
            Json = StoredJson.FromReference(eventJson.Identity)
        };

        var state = new ScopePosition
        {
            ScopeId = "root/0/state/a", Kind = FrameKind.State, Node = "a",
            NextStepIndex = 1, EntryPrevious = incoming, RestorePreviousOnExit = true
        };

        var branch = new ContinuationState
        {
            Stack =
            [
                new()
                {
                    ScopeId = "root", Graph = new()
                    {
                        Node = "a", Visits = 1,
                        Pending = new() { Via = "root/0/state/a/on/0/case/0/0", Trigger = "root/0/state/a/on/0/0", Event = fired }
                    }
                },
                state,
                new()
                {
                    ScopeId = "root/0/state/a/1/sequence", NextStepIndex = 1,
                    EntryPrevious = incoming, RestorePreviousOnExit = true
                }
            ]
        };

        var checkpoint = new ExecutionCheckpoint { StructureHash = scopes.StructureHash, RootData = new Data(), Continuations = [branch] };

        var host = new PayloadHost(new Dictionary<PayloadReference, byte[]>
        {
            [incomingJson.Identity] = incomingJson.Encoded.ToArray(), [eventJson.Identity] = eventJson.Encoded.ToArray()
        });

        var session = new ExecutionSession(host, Guid.NewGuid(), "hydrate", 1, ExecutionLimits.Default, false) { NeedsHydration = true };
        Assert.Throws<InvalidOperationException>(() => incoming.Read(services));
        await session.PrepareReadAsync(checkpoint, branch, scopes, restoreInputs: false);
        Assert.Equal(300_000, Assert.IsType<Incoming>(incoming.Read(services)).Text.Length);
        Assert.Equal(7, Assert.IsType<Signal>(fired.Read(services)).Number);
        new ContinuationTransitions(services, TimeProvider.System).Advance(scopes, checkpoint, branch, 100, 100);
        GraphTransitions.PreparePick(scopes, branch);
        Assert.Equal(300_000, Assert.IsType<Incoming>(branch.GetPrevious(services)).Text.Length);
        Assert.Equal(1, state.ArmOccurrence);
        var stored = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"));
        var references = CheckpointPayloads.References(stored.Trunk, stored.Parts);
        Assert.Contains(incomingJson.Identity, references);
        Assert.Contains(eventJson.Identity, references);
    }

    [Fact]
    public async Task StateEntriesUseTheSharedPerPassGuardAndReleaseResumesThePendingChoice()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store, graphLimit: 2);

        var definition = Workflow.Create<Data>("StateLimit").StateMachine(m => m.Start("a")
            .State("a", s => s.Otherwise("b")).State("b", s => s.Otherwise("c"))
            .State("c", s => s.Final())).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        var saved = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(result.InstanceId))!.ExecutionStateJson);
        Assert.Equal(FrameKind.State, saved.Continuations[0].Stack[^1].Kind);
        Assert.Equal("c", saved.Continuations[0].Stack[0].Graph!.Pending!.Target);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ReleaseAsync(result.InstanceId)).Status);
    }

    [Fact]
    public async Task AnEngineExceptionRaisedByAGuardIsCatchableAndNamesTheTrigger()
    {
        await using var services = Services();

        var definition = new WorkflowBuilder<Data, object>([], "StateGuardLocation")
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                .On<Signal>("go", t => t.When(c => EngineGuard(), "end")))))
            .Catch<WorkflowEngineException, GuardFault>(c => c.Step<Copy>(a => a
                .Input(x => x.Text).From(c => c.Fault.StepId)
                .Output(x => x.Text).To(c => c.WorkflowData.Final))).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Signal());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal("root/0/sequence/0/state/a/on/0/0", Assert.IsType<Data>(result.WorkflowData).Final);
    }

    private static bool EngineGuard() => throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "guard");

    [Theory]
    [InlineData("key")]
    [InlineData("start")]
    [InlineData("target")]
    [InlineData("guard-only")]
    [InlineData("reply-guard")]
    [InlineData("unpaired")]
    [InlineData("nested-request")]
    [InlineData("double-pair")]
    [InlineData("final-exit")]
    [InlineData("final-transition")]
    [InlineData("final-explicit")]
    [InlineData("timeout")]
    [InlineData("case-order")]
    [InlineData("immediate-order")]
    [InlineData("impure-duration")]
    [InlineData("timer-output")]
    [InlineData("timer-matches")]
    public void InvalidStateShapesFailDuringBuild(string shape)
    {
        Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, Incoming>([], "InvalidState")
            .StateMachine(m => m.Start(shape == "start" ? "absent" : "a").State("a", s =>
            {
                if (shape == "guard-only")
                {
                    s.When(c => false, "a");

                    return;
                }

                if (shape == "unpaired")
                    s.OnReply<Signal>("reply", "a");

                if (shape == "nested-request")
                    s.Entry(e => e.Sequence(b => b.Request<Ask>("reply"))).OnReply<Signal>("reply", "a");

                if (shape == "double-pair")
                    s.Entry(e => e.Request<Ask>("reply").WaitFor<Signal>("reply")).OnReply<Signal>("reply", "a");

                if (shape == "reply-guard")
                    s.Entry(e => e.Request<Ask>("reply"))
                        .OnReply<Signal>("reply", t => t.When(c => true, "a"));

                if (shape == "final-exit")
                    s.Final().Exit(e => e);

                if (shape == "final-transition")
                    s.Final();

                if (shape == "final-explicit")
                {
                    s.Final().Entry(e => e.Transition("a"));

                    return;
                }

                if (shape == "timeout")
                    s.Entry(e => e.WaitFor<Signal>("wait", configure: w => w
                        .TimeoutAfter(TimeSpan.FromSeconds(1)).OnTimeout(b => b.Step<Other>())));

                if (shape == "immediate-order")
                    s.Otherwise("a");

                if (shape == "impure-duration")
                    s.After(c => TimeSpan.FromTicks(DateTime.UtcNow.Ticks), "a");

                if (shape == "timer-output")
                    s.After(TimeSpan.FromSeconds(1), t => t
                        .Output(x => x.Text).To(c => c.WorkflowData.Final).Otherwise("a"));

                if (shape == "timer-matches")
                    s.After(TimeSpan.FromSeconds(1), t => t.Matches((value, c) => true).Otherwise("a"));

                s.On<Signal>("go", t =>
                {
                    t.Otherwise(shape == "target" ? "missing" : "a");

                    if (shape == "case-order")
                        t.When(c => true, "a");
                });
            }).State(shape == "key" ? "a" : "b", s => s.Final())).Build());
    }

    [Fact]
    public void KeysAndTriggerSemanticsAreHashedAndAllStateScopesAreCataloguedAndCloned()
    {
        WorkflowDefinition Definition(string key, bool reverse = false, string display = "A", int milliseconds = 1) =>
            new WorkflowBuilder<Data, Incoming>([], "StateIdentity").StateMachine(m =>
            {
                m.Start(key);

                void A() => m.State(key, s => s.Named(display).Exit(e => e)
                    .On<Signal>("go", t => t.When(c => c.PreviousStep.Number > 0, "b", a => a.Step<Other>())
                        .Otherwise("end"))
                    .After(TimeSpan.FromMilliseconds(milliseconds), "b"));

                if (!reverse)
                    A();

                m.State("b", s => s.Final());

                if (reverse)
                    A();
            }).Build();

        var first = Definition("a");

        Assert.Equal(WorkflowDefinitionValidator.Inspect(first),
            WorkflowDefinitionValidator.Inspect(Definition("a", reverse: true, display: "renamed")));

        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(Definition("changed")));
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(Definition("a", milliseconds: 2)));
        var clone = first.Steps[0].Clone();
        var copy = new WorkflowDefinition { Name = "Clone", Version = 1, WorkflowDataType = typeof(Data), Steps = [clone] };
        WorkflowDefinitionValidator.Validate(copy);
        Assert.Equal(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(copy));
        Assert.Contains(first.StepTable, step => step.CatalogId == "root/0/state/a/exit/0");
        Assert.Contains(first.StepTable, step => step.CatalogId == "root/0/state/a/on/0/case/0/1");
    }

    private static ServiceProvider Services(IWorkflowStateRepository? store = null, TimeProvider? clock = null,
        long threshold = 0, bool compiler = false, int graphLimit = 1000)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.PayloadThresholdBytes = threshold;
            o.MaxGraphTransitionsPerPass = graphLimit;
        });

        if (store != null)
            services.AddSingleton(store);

        if (clock != null)
            services.AddSingleton(clock);

        if (compiler)
            services.AddBreakpointConditions();

        return services.BuildServiceProvider();
    }

    private sealed class Interrupted : Exception;

    private sealed class PayloadHost(Dictionary<PayloadReference, byte[]> values) : IExecutionHost
    {
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) =>
            Task.FromResult(new ReadPayloadReply(request.AssignmentId, values[request.Reference]));

        public Task<CheckpointReply> CheckpointAsync(Checkpoint request) => throw new NotSupportedException();
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => throw new NotSupportedException();
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => throw new NotSupportedException();
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
    }

    public sealed class GuardFault
    {
        public string StepId { get; set; } = "";
    }

    public sealed class Data
    {
        public string Exit { get; set; } = "";
        public string Action { get; set; } = "";
        public string Final { get; set; } = "";
        public int Number { get; set; }
    }

    public sealed class Signal
    {
        public int Number { get; set; }
    }

    public sealed class Incoming : IAsyncActivity
    {
        public string Text { get; set; } = "incoming";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Copy : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Other : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Ask : IRequestActivity<Signal>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}