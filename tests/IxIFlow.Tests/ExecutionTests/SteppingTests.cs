using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-06")]
[Trait("Spec", "G-07")]
public sealed class SteppingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public static TheoryData<string, DebugStepKind> Constructs
    {
        get
        {
            var data = new TheoryData<string, DebugStepKind>();
            foreach (var shape in new[] { "sequence", "condition", "loop", "items", "parallel-items", "fork" })
                foreach (var kind in Enum.GetValues<DebugStepKind>()) data.Add(shape, kind);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Constructs))]
    public async Task EveryKindUsesTheSavedOriginAcrossStructuredSteps(string shape, DebugStepKind kind)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var builder = new WorkflowBuilder<Data, object>([], $"Step-{shape}-{kind}").WaitFor<Go>("go");
        builder = shape switch
        {
            "sequence" => builder.Sequence(b => b.Step<Count>().WithId("inside").Step<Count>()),
            "condition" => builder.If(c => true, b => b.Step<Count>().WithId("inside").Step<Count>()),
            "loop" => builder.WhileDo(c => c.WorkflowData.Value < 2, b => b.Step<Increment>(s => s
                .Input(a => a.Value).From(c => c.WorkflowData.Value)
                .Output(a => a.Value).To(c => c.WorkflowData.Value)).WithId("inside")),
            "items" => builder.ForEach(c => c.WorkflowData.Items, (b, item) => b.Step<Count>().WithId("inside")),
            "parallel-items" => builder.ForEach(c => c.WorkflowData.Items,
                (b, item) => b.Step<Count>().WithId("inside"), setup => setup.Parallel(2)),
            "fork" => builder.Parallel(p => p.Do(b => b.Step<Count>().WithId("inside"))
                .Do(b => b.Step<Count>())),
            _ => throw new InvalidOperationException()
        };
        var definition = builder.WithId("target").Step<Count>().WithId("after").Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var paused = await At(services, definition, "target");
        var before = await State(store, paused.InstanceId);
        var selected = before.Debug.Stop!.BranchId;
        await ops.SetBreakpointsAsync(paused.InstanceId, kind == DebugStepKind.Run
            ? [new("break-after", "after")] : [], "configure-step");
        var result = await ops.StepAsync(paused.InstanceId, selected, kind, "stepper").WaitAsync(Patience);
        if (kind == DebugStepKind.Out)
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status); // leaving the root completes it
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
            var state = await State(store, paused.InstanceId);
            Assert.Equal(kind == DebugStepKind.Run ? "break-after" : "", state.Debug.Stop!.BreakpointId);
            Assert.Equal(kind == DebugStepKind.Into ? definition.StepTable.Single(e => e.AuthorId == "inside").CatalogId
                : definition.StepTable.Single(e => e.AuthorId == "after").CatalogId, state.Debug.Stop.StepId);
            Assert.Equal(kind == DebugStepKind.Run ? null : "Step", state.Debug.Stop.Reason);
            Assert.Equal(kind == DebugStepKind.Into ? 0 : shape is "items" or "parallel-items" ? 3 : 2,
                shape == "loop" ? Assert.IsType<Data>(result.WorkflowData).Value : probe.Count);
            await ops.SetBreakpointsAsync(paused.InstanceId, [], "clear");
            Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(paused.InstanceId, "continue")).Status);
            Assert.Null((await State(store, paused.InstanceId)).Debug.Stepping);
        }
        var command = Assert.Single((await store.GetDebugStatesAsync([paused.InstanceId]))[paused.InstanceId].Commands,
            c => c.Kind == DebugCommandKind.Step);
        Assert.NotNull(command.AcknowledgedRevision);
        Assert.Equal(selected, command.ContinuationId);
        Assert.Equal(kind, command.StepKind);
        Assert.Equal(before.Debug.PauseGeneration, command.PauseGeneration);
        Assert.Equal("stepper", command.Actor);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("condition")]
    [InlineData("loop")]
    [InlineData("items")]
    public async Task OutLeavesTheScopeOccurrenceAndStopsInItsParent(string shape)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var b = new WorkflowBuilder<Data, object>([], $"Out-{shape}").WaitFor<Go>("go");
        b = shape switch
        {
            "sequence" => b.Sequence(body => body.Step<Count>().WithId("inside").Step<Count>()),
            "condition" => b.If(c => true, body => body.Step<Count>().WithId("inside")),
            "loop" => b.WhileDo(c => c.WorkflowData.Value < 2, body => body.Step<Increment>(s => s
                .Input(a => a.Value).From(c => c.WorkflowData.Value)
                .Output(a => a.Value).To(c => c.WorkflowData.Value)).WithId("inside")),
            _ => b.ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Count>().WithId("inside"))
        };
        var definition = b.WithId("container").Step<Count>().WithId("after").Build();
        var paused = await At(services, definition, "inside");
        var origin = (await State(store, paused.InstanceId)).Debug.Stop!;
        var result = await services.GetRequiredService<IWorkflowOperations>().StepAsync(paused.InstanceId, origin.BranchId, DebugStepKind.Out, "out");
        Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
        var state = await State(store, paused.InstanceId);
        Assert.Equal("Step", state.Debug.Stop!.Reason);
        Assert.NotEqual(origin.ActivationId, state.Debug.Stop.ActivationId);
        Assert.Equal(definition.StepTable.Single(e => e.AuthorId == (shape is "loop" or "items" ? "container" : "after")).CatalogId,
            state.Debug.Stop.StepId);
    }

    [Fact]
    public async Task OverAForkCompletesEveryBranchAndMergesThroughItsJoin()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "OverFork").WaitFor<Go>("go").Parallel(p => p
            .Do(b => b.Step<Increment>(s => s.Input(a => a.Value).From(c => c.WorkflowData.Value)
                .Output(a => a.Value).To(c => c.WorkflowData.Value)))
            .Do(b => b.Sequence(seq => seq.Step<Count>().Step<Count>())))
            .WithId("fork").Step<Count>().WithId("after").Build();
        var paused = await At(services, definition, "fork");
        var branch = (await State(store, paused.InstanceId)).Debug.Stop!.BranchId;
        var result = await services.GetRequiredService<IWorkflowOperations>().StepAsync(paused.InstanceId, branch, DebugStepKind.Over, "over");
        Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
        Assert.Equal(1, Assert.IsType<Data>(result.WorkflowData).Value);
        Assert.Equal(2, probe.Count);
        Assert.Empty((await State(store, paused.InstanceId)).Joins);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntoAForkStopsAtTheFirstNewBranchWithoutStartingAnActivity(bool items)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var b = new WorkflowBuilder<Data, object>([], "IntoFork" + items).WaitFor<Go>("go");
        b = items ? b.ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Count>(), setup => setup.Parallel(2))
            : b.Parallel(p => p.Do(body => body.Step<Count>()).Do(body => body.Step<Count>()));
        var paused = await At(services, b.WithId("fork").Build(), "fork");
        var origin = (await State(store, paused.InstanceId)).Debug.Stop!;
        await services.GetRequiredService<IWorkflowOperations>().StepAsync(paused.InstanceId, origin.BranchId, DebugStepKind.Into, "into");
        var state = await State(store, paused.InstanceId);
        var first = state.Continuations.Single(c => c.Sequence == 1);
        Assert.Equal(first.Id, state.Debug.Stop!.BranchId);
        Assert.Equal(0, probe.Count);
        Assert.NotEqual(origin.BranchId, first.Id);
        Assert.Contains(first.Id, state.Debug.Stepping!.EligibleContinuations);
    }

    [Theory]
    [InlineData(DebugStepKind.Over)]
    [InlineData(DebugStepKind.Into)]
    [InlineData(DebugStepKind.Out)]
    [InlineData(DebugStepKind.Run)]
    public async Task AWaitThatBlocksCommitsAPauseAndCanBeContinued(DebugStepKind kind)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "Blocked" + kind).WaitFor<Go>("go").WaitFor<Go>("later").WithId("wait")
            .Step<Count>().Build();
        var paused = await At(services, definition, "wait");
        var branch = (await State(store, paused.InstanceId)).Debug.Stop!.BranchId;
        var result = await services.GetRequiredService<IWorkflowOperations>().StepAsync(paused.InstanceId, branch, kind, "step");
        Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
        Assert.Equal("Blocked", (await State(store, paused.InstanceId)).Debug.Stop!.Reason);
        Assert.Null((await store.GetWorkflowInstanceAsync(paused.InstanceId))!.RunnableAtUtc);
        await store.AppendEventAsync(new(paused.InstanceId, "later-event", IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(Go)), "{}", Key: "later"));
        Assert.Equal(WorkflowExecutionStatus.Success, (await services.GetRequiredService<IWorkflowOperations>().ContinueAsync(paused.InstanceId, "continue")).Status);
    }

    [Fact]
    public async Task HeldBranchesKeepTheirEventsDeadlinesAndJoinUntilContinue()
    {
        var time = new MutableTime(); var store = new Store(time); var probe = new Probe(); await using var services = Services(store, probe, time: time);
        var definition = new WorkflowBuilder<Data, object>([], "IsolatedPaths").Parallel(p => p
            .Do(b => b.WaitFor<Go>("event").Step<Count>())
            .Do(b => b.Delay(TimeSpan.FromMinutes(1)).Step<Count>())
            .Do(b => b.WaitFor<Go>("go").Step<Count>().WithId("selected").Step<Count>().WithId("after"))).Build();
        var paused = await At(services, definition, "selected");
        var before = await State(store, paused.InstanceId);
        var selected = before.Debug.Stop!.BranchId;
        var held = before.Continuations.Where(c => c.Id != selected).Select(c => JsonSerializer.Serialize(c)).ToArray();
        var joins = JsonSerializer.Serialize(before.Joins);
        await store.AppendEventAsync(new(paused.InstanceId, "held", IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(Go)), "{}", Key: "event"));
        time.Advance(TimeSpan.FromMinutes(2));
        var result = await services.GetRequiredService<IWorkflowOperations>().StepAsync(paused.InstanceId, selected, DebugStepKind.Over, "step");
        Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
        var after = await State(store, paused.InstanceId);
        Assert.Equal(EventOutcome.Pending, (await store.GetEventAsync(paused.InstanceId, "held"))!.Outcome);
        Assert.Equal(held, after.Continuations.Where(c => c.Id != selected).Select(c => JsonSerializer.Serialize(c)).ToArray());
        Assert.Equal(joins, JsonSerializer.Serialize(after.Joins));
        Assert.Equal(1, probe.Count);
        Assert.Equal(WorkflowExecutionStatus.Success, (await services.GetRequiredService<IWorkflowOperations>().ContinueAsync(paused.InstanceId, "continue")).Status);
        Assert.Equal(EventOutcome.Consumed, (await store.GetEventAsync(paused.InstanceId, "held"))!.Outcome);
        Assert.Equal(4, probe.Count);
    }

    [Theory]
    [InlineData(DebugStepKind.Over)]
    [InlineData(DebugStepKind.Into)]
    public async Task ASelectedDueDelayCountsAsProgressBeforeAnotherStepIsDispatched(DebugStepKind kind)
    {
        var time = new MutableTime(); var store = new Store(time); var probe = new Probe();
        await using var services = Services(store, probe, time: time);
        var definition = new WorkflowBuilder<Data, object>([], "SelectedDeadline" + kind)
            .Delay(TimeSpan.FromMinutes(1)).Step<Count>().Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.PauseAsync(idle.InstanceId, "pause")).Status);
        var selected = Assert.Single((await client.GetSnapshotAsync(idle.InstanceId))!.Pointers).PointerId;
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, selected, kind, "step")).Status);
        var state = await State(store, idle.InstanceId);
        Assert.Equal("Step", state.Debug.Stop!.Reason);
        Assert.Equal("root/1", state.Debug.Stop.StepId);
        Assert.Empty(state.Waits);
        Assert.Equal(0, probe.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletingASelectedBranchDoesNotCompleteItsOutsideJoinOrAdmitItems(bool items)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var b = new WorkflowBuilder<Data, object>([], "OutsideJoin" + items).WaitFor<Go>("go");
        b = items ? b.ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Count>().WithId("selected"), setup => setup.Parallel(1))
            : b.Parallel(p => p.Do(body => body.Step<Count>().WithId("selected")).Do(body => body.Step<Count>()));
        var definition = b.Step<Count>().WithId("after").Build();
        var paused = await At(services, definition, "selected");
        var before = await State(store, paused.InstanceId); var selected = before.Debug.Stop!.BranchId;
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var result = await ops.StepAsync(paused.InstanceId, selected, DebugStepKind.Out, "out");
        Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
        var after = await State(store, paused.InstanceId);
        Assert.Equal("Blocked", after.Debug.Stop!.Reason);
        Assert.Equal(1, probe.Count);
        Assert.Equal(before.BranchesStarted, after.BranchesStarted);
        Assert.Equal(JsonSerializer.Serialize(before.Joins), JsonSerializer.Serialize(after.Joins));
        Assert.Equal(JsonSerializer.Serialize(before.Continuations[0]), JsonSerializer.Serialize(after.Continuations[0]));
        Assert.NotNull(after.Continuations.Single(c => c.Id == selected).PendingJoinFrame);
        // The ordinary breakpoint set is removed while held; Continue must finish the deferred notification.
        await ops.SetBreakpointsAsync(paused.InstanceId, [], "clear");
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(paused.InstanceId, "continue")).Status);
        Assert.Equal(items ? 4 : 3, probe.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashAfterTheDurableStepOrAfterActivityCompletionResumesItsOrigin(bool afterActivity)
    {
        var store = new Store(); var probe = new Probe(); await using var first = Services(store, probe);
        await using var second = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "CrashStep" + afterActivity).WaitFor<Go>("go")
            .Sequence(b => b.Step<Count>().Step<Count>()).WithId("target").Step<Count>().WithId("after").Build();
        var paused = await At(first, definition, "target");
        var selected = (await State(store, paused.InstanceId)).Debug.Stop!.BranchId;
        store.Crash = afterActivity ? "activity" : "resume";
        await Assert.ThrowsAsync<WorkflowEngineException>(() => first.GetRequiredService<IWorkflowOperations>()
            .StepAsync(paused.InstanceId, selected, DebugStepKind.Over, "step"));
        var saved = await State(store, paused.InstanceId);
        Assert.Equal(selected, saved.Debug.Stepping!.ContinuationId);
        Assert.Equal(afterActivity, saved.Debug.Stepping.Started);
        Assert.Equal(afterActivity ? 1 : 0, probe.Count);
        Assert.All((await store.GetDebugStatesAsync([paused.InstanceId]))[paused.InstanceId].Commands,
            c => Assert.NotNull(c.AcknowledgedRevision));
        store.Crash = null;
        var recovered = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(paused.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Paused, recovered.Status);
        var state = await State(store, paused.InstanceId);
        Assert.Equal("Step", state.Debug.Stop!.Reason);
        Assert.Equal(definition.StepTable.Single(e => e.AuthorId == "after").CatalogId, state.Debug.Stop.StepId);
        Assert.Equal(2, probe.Count);
    }

    [Fact]
    public async Task CrashAfterAForkRetainsItsNewBranchesAndKeepsAnExistingSiblingHeld()
    {
        var store = new Store(); var probe = new Probe(); await using var first = Services(store, probe, maxActivities: 1);
        await using var second = Services(store, probe, maxActivities: 1);
        var definition = new WorkflowBuilder<Data, object>([], "CrashFork").WaitFor<Go>("go")
            .Parallel(p => p.Do(b => b.Parallel(inner => inner.Do(c => c.Step<Count>()).Do(c => c.Step<Count>()))
                    .WithId("target").Step<Count>().WithId("after"))
                .Do(b => b.Step<Count>())).Build();
        var paused = await At(first, definition, "target");
        var state = await State(store, paused.InstanceId);
        var sibling = state.Continuations.Single(c => c.Sequence == 2);
        var held = JsonSerializer.Serialize(sibling);
        store.Crash = "activity";
        await Assert.ThrowsAsync<WorkflowEngineException>(() => first.GetRequiredService<IWorkflowOperations>()
            .StepAsync(paused.InstanceId, state.Debug.Stop!.BranchId, DebugStepKind.Over, "step"));
        state = await State(store, paused.InstanceId);
        Assert.Equal(3, state.Debug.Stepping!.EligibleContinuations.Count);
        Assert.DoesNotContain(sibling.Id, state.Debug.Stepping.EligibleContinuations);
        store.Crash = null;
        Assert.Equal(WorkflowExecutionStatus.Paused, (await second.GetRequiredService<IWorkflowClient>().RecoverAsync(paused.InstanceId)).Status);
        state = await State(store, paused.InstanceId);
        Assert.Equal("Step", state.Debug.Stop!.Reason);
        Assert.Equal(2, probe.Count);
        Assert.Equal(held, JsonSerializer.Serialize(state.Continuations.Single(c => c.Id == sibling.Id)));
    }

    [Fact]
    public void AReadyOutsideJoinCannotCompleteWhileAnotherContinuationSteps()
    {
        var definition = new WorkflowBuilder<Data, object>([], "ReadyJoin").Parallel(p => p
            .Do(b => b.Step<Count>()).Do(b => b.Step<Count>())).Build();
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var checkpoint = new ExecutionCheckpoint { RootData = new Data(), StructureHash = scopes.StructureHash,
            Continuations = [new() { Id = "root", Status = ContinuationStatus.Joining, Stack = [new() { ScopeId = "root" }] },
                new() { Id = "one", ParentJoinId = "join", Sequence = 1, Status = ContinuationStatus.Completed },
                new() { Id = "two", ParentJoinId = "join", Sequence = 2, Status = ContinuationStatus.Completed },
                new() { Id = "selected", Stack = [new() { ScopeId = "root" }] }],
            Joins = [new() { Id = "join", ParentContinuationId = "root", StepId = "root/0", IsCompleting = true,
                ChildContinuationIds = { "one", "two" } }]
        };
        checkpoint.Debug = checkpoint.Debug with { Stepping = new("selected", DebugStepKind.Run,
            SteppingPolicy.Position(checkpoint.Continuations[^1].Stack[0]), [], ["selected"], "step", "actor", true) };
        checkpoint.Trace.Configure(new WorkflowInstance { TracingEnabled = false }, checkpoint,
            ExecutionLimits.Default, TimeProvider.System, scopes);
        var before = JsonSerializer.Serialize(checkpoint);
        Assert.False(ContinuationTransitions.FinishCompletingJoins(scopes, checkpoint, []));
        Assert.Equal(before, JsonSerializer.Serialize(checkpoint));
        checkpoint.Debug = checkpoint.Debug with { Stepping = null };
        Assert.True(ContinuationTransitions.FinishCompletingJoins(scopes, checkpoint, []));
        Assert.Empty(checkpoint.Joins);
        Assert.Equal(ContinuationStatus.Active, checkpoint.Continuations[0].Status);
    }

    [Theory]
    [InlineData(DebugStepKind.Over)]
    [InlineData(DebugStepKind.Into)]
    [InlineData(DebugStepKind.Out)]
    [InlineData(DebugStepKind.Run)]
    public async Task CompletingTheRootDoesNotRequireAnotherPickToStop(DebugStepKind kind)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "RootEnd" + kind).WaitFor<Go>("go")
            .Step<Count>().WithId("target").Build();
        var paused = await At(services, definition, "target");
        var origin = (await State(store, paused.InstanceId)).Debug.Stop!;
        Assert.Equal(WorkflowExecutionStatus.Success, (await services.GetRequiredService<IWorkflowOperations>()
            .StepAsync(paused.InstanceId, origin.BranchId, kind, "step")).Status);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task IntoDistinguishesRepeatedVisitsToALoopGuardByProgress()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "RepeatedGuard").WaitFor<Go>("go")
            .WhileDo(c => c.WorkflowData.Value < 2, b => b.Step<Increment>(s => s
                .Input(a => a.Value).From(c => c.WorkflowData.Value)
                .Output(a => a.Value).To(c => c.WorkflowData.Value)))
            .WithId("guard").Build();
        var paused = await At(services, definition, "guard");
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var initial = (await State(store, paused.InstanceId)).Debug.Stop!;
        await ops.SetBreakpointsAsync(paused.InstanceId, [], "clear");
        await ops.StepAsync(paused.InstanceId, initial.BranchId, DebugStepKind.Into, "body");
        var body = (await State(store, paused.InstanceId)).Debug.Stop!;
        await ops.StepAsync(paused.InstanceId, body.BranchId, DebugStepKind.Out, "guard");
        var guard = (await State(store, paused.InstanceId)).Debug.Stop!;
        Assert.Equal(initial.StepId, guard.StepId);
        Assert.Equal(initial.ActivationId, guard.ActivationId);
        Assert.Equal(initial.Iteration + 1, guard.Iteration);
        await ops.StepAsync(paused.InstanceId, guard.BranchId, DebugStepKind.Into, "body-again");
        var next = (await State(store, paused.InstanceId)).Debug.Stop!;
        Assert.Equal(body.StepId, next.StepId);
        Assert.NotEqual(body.ActivationId, next.ActivationId);
        Assert.Equal(1, JsonSerializer.Deserialize<Data>((await store.GetWorkflowInstanceAsync(paused.InstanceId))!
            .WorkflowDataJson.InlineText)!.Value);
    }

    [Fact]
    public async Task StaleStepIsRefusedAndItsAcknowledgementIsOrdered()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "StaleStep").WaitFor<Go>("go").Step<Count>().WithId("target")
            .Step<Count>().WithId("after").Build();
        var paused = await At(services, definition, "target");
        var state = await State(store, paused.InstanceId);
        var request = new DebugCommandRequest("stale", DebugCommandKind.Step, state.Debug.PauseGeneration - 1,
            "stale", ContinuationId: state.Debug.Stop!.BranchId, StepKind: DebugStepKind.Over);
        Assert.Equal(DebugCommandStatus.StaleGeneration, (await store.RequestDebugCommandAsync(paused.InstanceId, request)).Status);
        request = request with { Id = "step", PauseGeneration = state.Debug.PauseGeneration };
        var accepted = await store.RequestDebugCommandAsync(paused.InstanceId, request);
        Assert.Equal(DebugCommandStatus.Accepted, accepted.Status);
        Assert.Equal(DebugCommandStatus.AlreadyAccepted, (await store.RequestDebugCommandAsync(paused.InstanceId, request)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RequestDebugCommandAsync(paused.InstanceId, request with { StepKind = DebugStepKind.Run }));
        Assert.Equal(WorkflowExecutionStatus.Paused, (await services.GetRequiredService<IWorkflowClient>().RecoverAsync(paused.InstanceId)).Status);
        Assert.Equal(DebugCommandStatus.StaleGeneration, (await store.RequestDebugCommandAsync(paused.InstanceId,
            request with { Id = "old-generation" })).Status);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task AFailureCannotUnwindAHeldParentOrCancelHeldSiblings()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "IsolatedFailure").WaitFor<Go>("go")
            .Parallel(p => p.Do(b => b.Step<Throw>().WithId("target")).Do(b => b.Step<Count>())).Build();
        var paused = await At(services, definition, "target");
        var state = await State(store, paused.InstanceId); var selected = state.Debug.Stop!.BranchId;
        var held = state.Continuations.Where(c => c.Id != selected).Select(c => JsonSerializer.Serialize(c)).ToArray();
        var ops = services.GetRequiredService<IWorkflowOperations>();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(paused.InstanceId, selected, DebugStepKind.Run, "step")).Status);
        state = await State(store, paused.InstanceId);
        Assert.Equal("Blocked", state.Debug.Stop!.Reason);
        Assert.NotNull(state.Continuations.Single(c => c.Id == selected).DeferredFailure);
        Assert.Equal(held, state.Continuations.Where(c => c.Id != selected).Select(c => JsonSerializer.Serialize(c)).ToArray());
        Assert.Equal(0, probe.Count);
        Assert.Equal(WorkflowExecutionStatus.Faulted, (await ops.ContinueAsync(paused.InstanceId, "continue")).Status);
    }

    [Theory]
    [InlineData(DebugStepKind.Over)]
    [InlineData(DebugStepKind.Into)]
    [InlineData(DebugStepKind.Out)]
    [InlineData(DebugStepKind.Run)]
    public async Task AChildBlocksTheParentStepAndContinuesIndependently(DebugStepKind kind)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var child = new WorkflowBuilder<Data, object>([], "SteppedChild" + kind).WaitFor<Go>("child").Build();
        await probe.Registry.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "SteppedParent" + kind).WaitFor<Go>("go")
            .Invoke<Data>(child.Name, 1).WithId("invoke").Step<Count>().Build();
        var paused = await At(services, parent, "invoke");
        var state = await State(store, paused.InstanceId);
        var ops = services.GetRequiredService<IWorkflowOperations>();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(paused.InstanceId, state.Debug.Stop!.BranchId, kind, "step")).Status);
        state = await State(store, paused.InstanceId);
        Assert.Equal("Blocked", state.Debug.Stop!.Reason);
        var childId = Assert.Single(state.Waits).ChildWorkflowId!;
        var client = services.GetRequiredService<IWorkflowClient>();
        using var timeout = new CancellationTokenSource(Patience);
        while (await client.GetStatusAsync(childId) != WorkflowExecutionStatus.Suspended)
            await Task.Delay(10, timeout.Token);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(childId, "child", new Go())).Status);
        Assert.Equal(WorkflowExecutionStatus.Paused, await client.GetStatusAsync(paused.InstanceId));
        Assert.Equal(0, probe.Count);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(paused.InstanceId, "continue")).Status);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task OwnershipLossDuringAStepCannotCommitItsOutcomeOrASuccessfulPause()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe, shortLease: true);
        var definition = new WorkflowBuilder<Data, object>([], "LostStep").WaitFor<Go>("go")
            .Step<Block>().WithId("target").Step<Count>().Build();
        var paused = await At(services, definition, "target");
        var state = await State(store, paused.InstanceId);
        var stepping = services.GetRequiredService<IWorkflowOperations>()
            .StepAsync(paused.InstanceId, state.Debug.Stop!.BranchId, DebugStepKind.Over, "step");
        await probe.Started.Task.WaitAsync(Patience);
        var revision = (await store.GetWorkflowInstanceAsync(paused.InstanceId))!.Revision;
        store.LoseLease = true;
        await store.LeaseLost.Task.WaitAsync(Patience);
        probe.Finish.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Running, (await stepping.WaitAsync(Patience)).Status);
        var saved = (await store.GetWorkflowInstanceAsync(paused.InstanceId))!;
        Assert.Equal(WorkflowStatus.Running, saved.Status);
        Assert.Equal(revision, saved.Revision);
        Assert.NotNull(WorkflowDebugRules.Checkpoint(saved).Stepping);
        Assert.DoesNotContain(await store.GetTraceAsync(paused.InstanceId), e =>
            e.Entry.EntryType is TraceEntryType.Paused or TraceEntryType.ActivityCompleted);
        Assert.Equal(0, probe.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task IncapableHostLeavesPendingAndRecoveringStepsHeld(bool debugging, bool conditions)
    {
        var store = new Store(); var probe = new Probe(); await using var capable = Services(store, probe, compiler: true);
        await using var incapable = Services(store, probe, debugging, compiler: false);
        var definition = new WorkflowBuilder<Data, object>([], "MixedStep" + conditions).WaitFor<Go>("go").Step<Count>().WithId("target")
            .Step<Count>().WithId("after").Build();
        var paused = await At(capable, definition, "target", condition: conditions ? "true" : null);
        var state = await State(store, paused.InstanceId);
        var command = await store.RequestDebugCommandAsync(paused.InstanceId, new("step", DebugCommandKind.Step,
            state.Debug.PauseGeneration, "operator", ContinuationId: state.Debug.Stop!.BranchId, StepKind: DebugStepKind.Over));
        Assert.Equal(DebugCommandStatus.Accepted, command.Status);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await incapable.GetRequiredService<IWorkflowClient>().RecoverAsync(paused.InstanceId)).Status);
        Assert.Null((await store.GetDebugStatesAsync([paused.InstanceId]))[paused.InstanceId].Commands.Last().AcknowledgedRevision);
        Assert.Equal(0, probe.Count);
        store.Crash = "resume";
        await Assert.ThrowsAsync<WorkflowEngineException>(() => capable.GetRequiredService<IWorkflowClient>().RecoverAsync(paused.InstanceId));
        store.Crash = null;
        var revision = (await store.GetWorkflowInstanceAsync(paused.InstanceId))!.Revision;
        await incapable.GetRequiredService<IWorkflowClient>().RecoverAsync(paused.InstanceId);
        Assert.Equal(revision, (await store.GetWorkflowInstanceAsync(paused.InstanceId))!.Revision);
        Assert.Equal(0, probe.Count);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await capable.GetRequiredService<IWorkflowClient>().RecoverAsync(paused.InstanceId)).Status);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task LoopAndItemNamesCarryTheirOccurrenceAndForkIdentity()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "LoopForkNames").WaitFor<Go>("go").WhileDo(c => c.WorkflowData.Value < 2,
            loop => loop.Parallel(p => p.Do(b => b.Step<Count>().WithId("inside"))
                    .Do(b => b.Step<Count>())).WithId("fork")
                .Step<Increment>(s => s.Input(a => a.Value).From(c => c.WorkflowData.Value)
                    .Output(a => a.Value).To(c => c.WorkflowData.Value)))
            .Step<Count>().WithId("after").Build();
        var paused = await At(services, definition, "fork");
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var occurrences = new List<ExecutionPointer>();
        for (var iteration = 1; iteration <= 2; iteration++)
        {
            var state = await State(store, paused.InstanceId);
            var snapshot = (await client.GetSnapshotAsync(paused.InstanceId))!;
            var loopFrame = Assert.Single(snapshot.Frames, f => f.Name.Contains($"iteration {iteration}"));
            Assert.Equal(iteration.ToString(), loopFrame.State["LoopIteration"]);
            await ops.StepAsync(paused.InstanceId, state.Debug.Stop!.BranchId, DebugStepKind.Into, "into");
            snapshot = (await client.GetSnapshotAsync(paused.InstanceId))!;
            var child = snapshot.Pointers.Single(p => p.Sequence == (iteration == 1 ? 1 : 3));
            Assert.Equal(loopFrame.FrameId, child.ForkActivationId);
            Assert.NotNull(child.ParentPointerId);
            Assert.NotNull(child.ParentJoinId);
            Assert.Contains(child.ForkActivationId!, child.Name);
            occurrences.Add(child);
            if (iteration == 1) await ops.ContinueAsync(paused.InstanceId, "next-iteration");
        }
        Assert.NotEqual(occurrences[0].ForkActivationId, occurrences[1].ForkActivationId);
        Assert.NotEqual(occurrences[0].ParentJoinId, occurrences[1].ParentJoinId);
        Assert.NotEqual(occurrences[0].PointerId, occurrences[1].PointerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachFramesNameTheItemIndex(bool parallel)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "ItemNames" + parallel).WaitFor<Go>("go")
            .ForEach(c => c.WorkflowData.Items, (b, item) => b.Step<Count>().WithId("inside"),
                setup => { if (parallel) setup.Parallel(1); }).Build();
        var paused = await At(services, definition, "inside");
        for (var item = 0; item < 3; item++)
        {
            var snapshot = (await services.GetRequiredService<IWorkflowClient>().GetSnapshotAsync(paused.InstanceId))!;
            Assert.Contains(snapshot.Frames, f => f.ForEachItemIndex == item && f.Name.Contains($"item {item}") &&
                f.State["ForEachItemIndex"] == item.ToString());
            if (item < 2) await services.GetRequiredService<IWorkflowOperations>().ContinueAsync(paused.InstanceId, "next-item");
        }
    }

    private static async Task<WorkflowExecutionResult> At(ServiceProvider services, WorkflowDefinition definition,
        string target, string? condition = null)
    {
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        await services.GetRequiredService<IWorkflowOperations>().SetBreakpointsAsync(idle.InstanceId,
            definition.StepTable.Any(e => e.AuthorId == "after")
                ? [new("break-target", target, Condition: condition), new("break-after", "after")]
                : [new("break-target", target, Condition: condition)], "setter");
        var paused = await client.ResumeAsync(idle.InstanceId, "go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        return paused;
    }

    private static async Task<ExecutionCheckpoint> State(Store store, string id) =>
        ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);

    private static ServiceProvider Services(Store store, Probe probe, bool debugging = true, bool compiler = false, TimeProvider? time = null, bool shortLease = false, int maxActivities = 100)
    {
        var services = new ServiceCollection().AddLogging(); services.AddIxIFlow(o => { o.Debugging = debugging; o.MaxActivitiesPerRun = maxActivities; });
        if (time != null) services.AddSingleton(time);
        if (compiler) services.AddBreakpointConditions();
        if (shortLease) services.AddSingleton(new ExecutionLeaseSettings(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)));
        services.AddSingleton<IWorkflowStateRepository>(store); services.AddSingleton(probe);
        services.AddSingleton<IWorkflowCatalog>(probe.Registry);
        return services.BuildServiceProvider();
    }

    public sealed class Data { public int Value { get; set; } public List<int> Items { get; set; } = [1, 2, 3]; }
    public sealed class Go;
    public sealed class Probe
    {
        public WorkflowCatalog Registry { get; } = new(); public int Count;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Started.TrySetResult(); await probe.Finish.Task.WaitAsync(cancellationToken); }
    }
    public sealed class Count(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Count); return Task.CompletedTask; }
    }
    public sealed class Increment : IAsyncActivity
    {
        [ActivityInputOutput] public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value++; return Task.CompletedTask; }
    }
    public sealed class Throw : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Selected branch failed"));
    }
    private sealed class MutableTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class Store : InMemoryWorkflowStateRepository
    {
        public Store() { }
        public Store(TimeProvider time) : base(time) { }
        public string? Crash;
        public bool LoseLease;
        public TaskCompletionSource LeaseLost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _crashed;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            if (Crash != null && _crashed) throw new IOException("Injected crash during step");
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (Crash == "resume" && WorkflowDebugRules.Checkpoint(commit.ReadTrunk()).Stepping is { Started: false } ||
                Crash == "activity" && commit.TraceEntries.Any(e => JsonSerializer.Deserialize<ExecutionTraceEntry>(e)!.EntryType == TraceEntryType.ActivityCompleted))
                _crashed = true;
            return result;
        }
        public override Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases)
        {
            if (!LoseLease) return base.RenewExecutionLeasesAsync(leases);
            LeaseLost.TrySetResult();
            return Task.FromResult<IReadOnlyList<ExecutionLeaseRenewalResult>>(
                leases.Select(_ => new ExecutionLeaseRenewalResult(ExecutionLeaseRenewalStatus.Refused)).ToArray());
        }
    }
}
