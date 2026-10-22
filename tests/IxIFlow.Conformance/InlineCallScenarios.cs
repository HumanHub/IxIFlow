using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
public sealed class InlineCallScenarios
{
    public static IEnumerable<object[]> Owners => StoreProfiles.ProfileNames.SelectMany(profile => new[] { false, true }
        .Select(branch => new object[] { profile, branch }));
    public static IEnumerable<object[]> Steps => StoreProfiles.ProfileNames.SelectMany(profile => new[] { DebugStepKind.Into, DebugStepKind.Over }
        .Select(kind => new object[] { profile, kind }));
    public static IEnumerable<object[]> CleanupOwners => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.SelectMany(inline => new[] { false, true }
            .Select(recover => new object[] { profile, inline, recover })));

    public static IEnumerable<object[]> PayloadFailures => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "root", "branch", "retained" }.SelectMany(owner => new[] { false, true }
            .Select(corrupt => new object[] { profile, owner, corrupt })));

    [Theory, MemberData(nameof(CleanupOwners))]
    public async Task TerminalOwnerInInheritedFinallyRetiresItsJoinAndRunsEnclosingCleanupOnce(string profile, bool inline, bool recover)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var catalog = new WorkflowCatalog();
        var callee = InheritedCleanupDefinition(recover);
        var pin = new WorkflowInlinePin(callee.Name, callee.Version,
            WorkflowDefinitionValidator.Inspect(callee), WorkflowDefinitionValidator.GetContract(callee).Hash);
        var definition = Workflow.Create<Data>("InlineInheritedCleanup").Step<Copy>()
            .Try(b =>
            {
                if (inline) b.Use<Data>(() => InheritedCleanupDefinition(recover), pin);
                else InheritedCleanup(b, recover);
            }).Finally(b => b.Step<MarkEnclosingCleanup>()).Build();
        WorkflowExecutionResult result;
        await using (var first = Services(store, repository, catalog))
        {
            result = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            if (recover)
            {
                Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
                var state = await ReadState(repository, result.InstanceId);
                Assert.Equal(2, state.Joins.Count);
                var inner = Assert.Single(state.Joins, j => state.ContinuationById(j.ParentContinuationId)!.ParentJoinId != null);
                var owner = state.ContinuationById(inner.ParentContinuationId)!;
                Assert.Equal(ContinuationStatus.Cancelled, owner.Status);
                Assert.Empty(owner.Stack);
                Assert.False(ContinuationTransitions.CanFinish(state, inner));
                Assert.False(ContinuationTransitions.Merges(state, inner));
                var ancestor = Assert.Single(state.Joins, j => j != inner);
                Assert.All(state.BranchesOf(ancestor), c =>
                    Assert.True(c.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled));
                Assert.False(ContinuationTransitions.CanFinish(state, ancestor));
                Assert.Equal("failed", state.UnhandledError!.Message);
                Assert.Equal(0, Assert.IsType<Data>(result.WorkflowData).Value);
            }
        }
        if (recover)
        {
            await using var second = Services(store, repository, catalog);
            var client = second.GetRequiredService<IWorkflowClient>();
            result = await client.ResumeAsync(result.InstanceId, "inner-settle", new Signal());
        }
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, Assert.IsType<Data>(result.WorkflowData).Value);
        var terminal = await ReadState(repository, result.InstanceId);
        Assert.Equal("failed", terminal.UnhandledError!.Message);
        Assert.Contains(nameof(InvalidOperationException), terminal.UnhandledError.Type);
        Assert.Empty(terminal.Joins);
    }

    private static WorkflowDefinition InheritedCleanupDefinition(bool recover)
    {
        var builder = Workflow.Create<Data>("InheritedCleanupBody").Step<Copy>();
        InheritedCleanup(builder, recover);
        return builder.Build();
    }

    private static void InheritedCleanup<T>(IWorkflowBuilder<Data, T> builder, bool recover) where T : class =>
        builder.Try(b => b.Step<Copy>()).Finally(b => b.Parallel(p => p.WaitAny()
            .Do(x => x.Try(y => y.WaitFor<Signal>("hold")).Finally(y => y.Parallel(q => q
                .Do(z => z.Step<Fail>())
                .Do(z =>
                {
                    if (recover) z.Try(w => w.WaitFor<Signal>("inner-hold"))
                        .Finally(w => w.WaitFor<Signal>("inner-settle").Step<Copy>());
                    else z.Step<Copy>();
                }))))
            .Do(x => x.Step<Copy>())));

    [Theory, MemberData(nameof(PayloadFailures))]
    public async Task MissingOrCorruptInlinePayloadPreservesCheckpointAndPendingEvent(string profile, string owner, bool corrupt)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var payloads = new FaultedPayloads(repository.PayloadStore) { Corrupt = corrupt };
        var proxy = System.Reflection.DispatchProxy.Create<IWorkflowStateRepository, PayloadRepository>();
        ((PayloadRepository)(object)proxy).Inner = repository;
        ((PayloadRepository)(object)proxy).Payloads = payloads;
        await using var host = Services(store, proxy);
        var client = host.GetRequiredService<IWorkflowClient>();
        var builder = Workflow.Create<Caller>("BrokenInline" + owner + corrupt).Step<Copy>();
        var definition = owner == "root" ? builder.Use<Waiting, Data>(u => u
                .Input(x => x.Text).From(c => c.WorkflowData.Text).Output(x => x.Value).To(c => c.WorkflowData.Value)).Build()
            : owner == "branch" ? builder.Parallel(p => p.Do(b => b.Use<Waiting, Data>(u => u
                .Input(x => x.Text).From(c => c.WorkflowData.Text).Output(x => x.Value).To(c => c.WorkflowData.Value)))).Build()
            : builder.Parallel(p => p.Do(b => b.Try(x => x.Use<FailingFork, Data>(u => u
                    .Input(x => x.Text).From(c => c.WorkflowData.Text).Input(x => x.Value).From(c => 17)))
                .Catch<InvalidOperationException>(x => x.Step<Copy>(a => a.Input(x => x.Value).From(c => 4)
                    .Output(x => x.Value).To(c => c.WorkflowData.Value))))).Build();
        var idle = await client.RunAsync(definition, new Caller { Text = new string('x', 10000) });
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        if (owner == "retained")
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResumeAsync(idle.InstanceId, "fail", new Signal())).Status);
            var state = await ReadState(repository, idle.InstanceId);
            var frame = Assert.Single(state.InlineFrames);
            Assert.NotEqual(state.Continuations[0].Id, frame.Value.OwnerContinuationId);
            Assert.DoesNotContain(state.Continuations.SelectMany(c => c.Stack), f => f.ActivationId == frame.Key);
            Assert.Contains(state.Joins, j => j.DataFrameId == frame.Key);
        }
        var before = (await repository.GetWorkflowInstanceAsync(idle.InstanceId))!;
        payloads.Broken = Assert.Single(before.SavedParts!, p => p.Key.StartsWith("i/")).Value.Reference;
        Assert.NotNull(payloads.Broken);
        var stopped = await client.ResumeAsync(idle.InstanceId, owner == "retained" ? "cleanup" : "inside", new Signal { Value = 9 }, "answer");
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        Assert.Equal(WorkflowErrorKind.PayloadUnavailable, stopped.ErrorKind);
        var after = (await repository.GetWorkflowInstanceAsync(idle.InstanceId))!;
        Assert.Equal(before.SavedParts, after.SavedParts);
        Assert.Equal(EventOutcome.Pending, (await repository.GetEventAsync(idle.InstanceId, "answer"))!.Outcome);
        Assert.False(await repository.PayloadStore.CollectAsync(payloads.Broken!, TimeSpan.Zero));
        payloads.Broken = null;
        var released = await client.ReleaseAsync(idle.InstanceId);
        Assert.True(released.Status == WorkflowExecutionStatus.Success, released.ErrorMessage);
        Assert.Equal(owner == "retained" ? 4 : 9, Assert.IsType<Caller>(released.WorkflowData).Value);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ActivityTriggerForkInsideInlineRecoversBeforeFireAndMergesOnlyWinner(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Caller>("InlineActivityMachine").Step<Copy>().WaitFor<Signal>("start")
            .Use<ActivityMachine, Data>(u => u.Input(x => x.Value).From(c => 5)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)).WithId("call").Build();
        string id;
        await using (var first = Services(store, repository, registry))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var ops = first.GetRequiredService<IWorkflowOperations>();
            id = (await client.RunAsync(definition, new Caller())).InstanceId;
            await ops.SetBreakpointsAsync(id, [new("fire", "call/machine/a/on/0/run/fire")], "test");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new Signal())).Status);
            var state = await ReadState(repository, id);
            Assert.NotNull(Assert.Single(state.Joins).DataFrameId);
        }
        await using var second = Services(store, repository, registry);
        var operations = second.GetRequiredService<IWorkflowOperations>();
        await operations.SetBreakpointsAsync(id, [], "clear");
        var result = await operations.ContinueAsync(id, "continue");
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(6, Assert.IsType<Caller>(result.WorkflowData).Value);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FailingActivityTriggerInsideInlineRecoversInFinallyAndBypassesOutputs(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Caller>("InlineFailingTrigger").Step<Copy>()
            .Try(b => b.Use<FailingMachine, Data>(u => u.Input(x => x.Value).From(c => 17)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)))
            .Catch<InvalidOperationException>(b => b.Step<Copy>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value + 1)
                .Output(x => x.Value).To(c => c.WorkflowData.Value))).Build();
        string id;
        await using (var first = Services(store, repository, registry))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Caller { Value = 3 });
            Assert.True(idle.Status == WorkflowExecutionStatus.Suspended, idle.ErrorMessage);
            id = idle.InstanceId;
            Assert.Single((await ReadState(repository, id)).InlineFrames);
            Assert.Contains(await repository.GetOpenWaitsAsync(new(InstanceId: id)), w => w.Key == "trigger-cleanup");
        }
        await using var second = Services(store, repository, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "trigger-cleanup", new Signal());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(4, Assert.IsType<Caller>(result.WorkflowData).Value);
        Assert.DoesNotContain(await second.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id), t => t.Entry.EntryType == TraceEntryType.TransitionTaken);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InlineRecordedWinnerWaitsForUnresolvedLoserAcrossRecovery(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Caller>("InlineUnresolvedLoser").Use<DoubleMachine, Data>(u => u
            .Input(x => x.Value).From(c => 5).Output(x => x.Value).To(c => c.WorkflowData.Value)).WithId("call").Build();
        await registry.RegisterWorkflowAsync(definition);
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Caller { Value = 3 }, new(), DateTime.UtcNow);
        checkpoint.RootData = new Caller { Value = 3 };
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, null, scopes);
        using var local = new ServiceCollection().BuildServiceProvider();
        var transitions = new ContinuationTransitions(local, TimeProvider.System);
        var parent = checkpoint.Continuations[0];
        while (checkpoint.Joins.Count == 0) transitions.Advance(scopes, checkpoint, parent, 100, 100);
        var join = Assert.Single(checkpoint.Joins);
        var frameId = join.DataFrameId!;
        var children = checkpoint.BranchesOf(join);
        var winner = children[0];
        ((Data)checkpoint.DataOf(winner)).Value = 6; winner.DataChanged = true;
        winner.SetPrevious(new Increment { Value = 6 }); winner.Stack[0].NextStepIndex = 1;
        checkpoint.Debug = checkpoint.Debug with { Stepping = new(winner.Id, DebugStepKind.Out,
            SteppingPolicy.Position(winner.Stack[0]), [winner.Stack[0].ActivationId], [winner.Id], "hold", "test") };
        transitions.Advance(scopes, checkpoint, winner, 100, 100);
        transitions.Advance(scopes, checkpoint, winner, 100, 100);
        var loser = children[1];
        loser.PendingActivity = new() { StepId = definition.StepTable.Single(s => s.AuthorId == "call/machine/a/on/1/run").CatalogId };
        loser.Status = ContinuationStatus.WaitingResolution;
        checkpoint.Debug = new();
        ContinuationTransitions.NotifyCompletedBranches(scopes, checkpoint);
        Assert.Equal(winner.Id, join.WinningChildId);
        Assert.False(ContinuationTransitions.FinishCompletingJoins(scopes, checkpoint, []));
        Assert.Equal(5, ((Data)checkpoint.InlineFrames[frameId].Value).Value);
        ExecutionCheckpointWriter.Prepare(instance, checkpoint, checkpoint.RootData, scopes, DateTime.UtcNow);
        var grant = await StoreTestSupport.LeaseAsync(repository, instance.InstanceId);
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, grant.Epoch, 0))).Status);
        await StoreTestSupport.ReleaseAsync(repository, instance.InstanceId);
        await using (var first = Services(store, repository, registry))
        {
            var pending = await first.GetRequiredService<IWorkflowClient>().RecoverAsync(instance.InstanceId);
            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, pending.Status);
            var state = await ReadState(repository, instance.InstanceId);
            Assert.Equal(winner.Id, Assert.Single(state.Joins).WinningChildId);
            var reader = new PayloadReader(async r => await repository.PayloadStore.GetAsync(r) ?? throw new InvalidDataException(), new PayloadBudget(1000000));
            await reader.ReadAsync(state.InlineFrames[frameId].Data);
            Assert.Equal(5, ((Data)state.InlineFrames[frameId].Value).Value);
            Assert.Equal(3, Assert.IsType<Caller>(pending.WorkflowData).Value);
        }
        await using var second = Services(store, repository, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().ResolveAsync(instance.InstanceId, loser.PendingActivity.Id,
            ActivityResolution.Completed(new Dictionary<string, object?> { ["Value"] = 99 }));
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(6, Assert.IsType<Caller>(result.WorkflowData).Value);
        Assert.Empty((await ReadState(repository, instance.InstanceId)).InlineFrames);
    }

    [Theory, MemberData(nameof(Owners))]
    public async Task EditingInlineBaselineWithLiveBranchesPreservesTheirDeltasAcrossRestart(string profile, bool branch)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var builder = Workflow.Create<Caller>("InlineLiveEdit" + branch).Step<Copy>();
        var definition = branch ? builder.Parallel(p => p.Do(b => b.Use<EditingFork, Data>(u => u
                .Input(x => x.Value).From(c => 5).Output(x => x.Value).To(c => c.WorkflowData.Value)
                .Output(x => x.Text).To(c => c.WorkflowData.Text)).WithId("call"))).Build()
            : builder.Use<EditingFork, Data>(u => u.Input(x => x.Value).From(c => 5)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)
                .Output(x => x.Text).To(c => c.WorkflowData.Text)).WithId("call").Build();
        string id;
        await using (var first = Services(store, repository, registry))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var ops = first.GetRequiredService<IWorkflowOperations>();
            id = (await client.RunAsync(definition, new Caller { Value = 3 })).InstanceId;
            await ops.PauseAsync(id, "pause");
            var inspection = (await ops.InspectAsync(id))!;
            var owner = inspection.Threads.Single(t => t.Frames.Any(f => f.Kind == "InlineWorkflow"));
            Assert.Equal(branch, owner.ParentJoinId != null);
            var frame = owner.Frames.Single(f => f.Kind == "InlineWorkflow");
            Assert.Equal(branch ? 4 : 3, inspection.Threads.Count);
            await ops.EditDataAsync(id, "edit-baseline", inspection.Revision,
                new(inspection.DebugState.PauseGeneration, owner.ContinuationId, frame.ActivationId),
                [new(["Value"], JsonSerializer.SerializeToElement(11))], "test");
            var edited = (await ops.InspectAsync(id))!;
            Assert.Equal(11, edited.Data.Single(d => d.FrameId == frame.ActivationId).Value.GetProperty("Value").GetInt32());
            Assert.Contains(edited.Data, d => d.FrameId == null && d.Value.TryGetProperty("Text", out var text) && text.GetString() == "branch-delta");
        }
        await using var second = Services(store, repository, registry);
        var operations = second.GetRequiredService<IWorkflowOperations>();
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await operations.ContinueAsync(id, "continue")).Status);
        var engine = second.GetRequiredService<IWorkflowClient>();
        await engine.ResumeAsync(id, "edit-one", new Signal());
        var result = await engine.ResumeAsync(id, "edit-two", new Signal());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(11, Assert.IsType<Caller>(result.WorkflowData).Value);
        Assert.Equal("branch-delta", Assert.IsType<Caller>(result.WorkflowData).Text);
    }

    [Theory, MemberData(nameof(Owners))]
    public async Task LargeInlineFramesRecoverFromPartsAndKeepPayloadsPinned(string profile, bool branch)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var b = Workflow.Create<Caller>("StoredInline" + branch).Step<Copy>();
        var definition = branch ? b.Parallel(p => p.Do(x => x.Use<Waiting, Data>(u => u
                .Input(x => x.Text).From(c => c.WorkflowData.Text).Output(x => x.Value).To(c => c.WorkflowData.Value))))
                .Build()
            : b.Use<Waiting, Data>(u => u.Input(x => x.Text).From(c => c.WorkflowData.Text)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)).Build();
        string id;
        string frameKey;
        StoredJson savedPart;
        await using (var first = Services(store, repository, registry))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Caller { Text = new string('x', 10000) });
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
            var saved = (await repository.GetWorkflowInstanceAsync(id))!;
            var part = Assert.Single(saved.SavedParts!, p => p.Key.StartsWith("i/"));
            (frameKey, savedPart) = (part.Key, part.Value);
            Assert.NotNull(savedPart.Reference);
            Assert.False(await repository.PayloadStore.CollectAsync(savedPart.Reference!, TimeSpan.Zero));
            var again = await first.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "unmatched", new Signal());
            Assert.True(again.Status == WorkflowExecutionStatus.Suspended, again.ErrorMessage);
            Assert.Equal(savedPart.Identity, (await repository.GetWorkflowInstanceAsync(id))!.SavedParts![frameKey].Identity);
        }
        await using var second = Services(store, repository, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "inside", new Signal { Value = 8 });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(8, Assert.IsType<Caller>(result.WorkflowData).Value);
        var completed = (await repository.GetWorkflowInstanceAsync(id))!;
        Assert.DoesNotContain(completed.SavedParts!, p => p.Key.StartsWith("i/"));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FlowchartNodeCallsInlineStateMachineAndTimerUsesStoreClock(string profile)
    {
        var time = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        var repository = store.NewRepository();
        await using var host = Services(store, repository, time: time);
        var definition = Workflow.Create<Caller>("GraphInlineTimer").Flowchart(f => f.Start("call")
            .Node("call", b => b.Use<TimerMachine, Data>(u => u.Input(x => x.Value).From(c => 6)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)))).WithId("chart").Build();
        var client = host.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Caller());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var page = await repository.GetPendingEventsAsync(idle.InstanceId, 0, 100);
        var deadline = Assert.Single(page.Deadlines!).Value;
        if (time != null) time.Advance(deadline - page.StoreTimeUtc!.Value + TimeSpan.FromMilliseconds(1));
        else while (page.StoreTimeUtc!.Value < deadline)
        {
            await Task.Delay(10, patience.Token);
            page = await repository.GetPendingEventsAsync(idle.InstanceId, 0, 100).WaitAsync(patience.Token);
        }
        var result = await client.RecoverAsync(idle.InstanceId);
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(6, Assert.IsType<Caller>(result.WorkflowData).Value);
        var trace = await host.GetRequiredService<IWorkflowOperations>().GetTraceAsync(idle.InstanceId);
        Assert.Contains(trace, t => t.Entry.EntryType == TraceEntryType.TimerFired);
        Assert.Contains(trace, t => t.Entry.EntryType == TraceEntryType.InlineExited);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OuterHandlerRetainsInlineBaselineUntilWaitingFinallyFinishesAfterRestart(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Caller>("StoredInlineUnwind").Step<Copy>()
            .Try(b => b.Use<FailingFork, Data>(u => u.Input(x => x.Value).From(c => 17)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)))
            .Catch<InvalidOperationException>(b => b.Step<Copy>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value + 1)
                .Output(x => x.Value).To(c => c.WorkflowData.Value))).Build();
        string id;
        await using (var first = Services(store, repository, registry))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var idle = await client.RunAsync(definition, new Caller { Value = 3 });
            id = idle.InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            var cleaning = await client.ResumeAsync(id, "fail", new Signal());
            Assert.Equal(WorkflowExecutionStatus.Suspended, cleaning.Status);
            var state = await ReadState(repository, id);
            Assert.NotEmpty(state.InlineFrames);
            Assert.Contains(state.Joins, j => j.DataFrameId != null);
            Assert.DoesNotContain(state.Continuations[0].Stack, f => f.Kind == FrameKind.Inline);
        }
        await using var second = Services(store, repository, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new Signal { Value = 25 });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(4, Assert.IsType<Caller>(result.WorkflowData).Value);
        Assert.Empty((await ReadState(repository, id)).InlineFrames);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OuterHandlerWaitsForInlineChildCancellationAcrossRestart(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(Workflow.Create<Data>("InlineCleanupChild").Step<Copy>()
            .Try(b => b.WaitFor<Signal>("child-hold")).Catch<ArgumentException>(_ => { })
            .Finally(b => b.WaitFor<Signal>("child-cleanup")).Build());
        var definition = Workflow.Create<Caller>("InlineChildUnwind").Step<Copy>()
            .Try(b => b.Use<ChildFork, Data>(u => u.Input(x => x.Value).From(c => 17)))
            .Catch<InvalidOperationException>(b => b.Step<Copy>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value + 1)
                .Output(x => x.Value).To(c => c.WorkflowData.Value))).Build();
        string id;
        string child;
        await using (var first = Services(store, repository, registry))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Caller { Value = 3 })).InstanceId;
            child = await WaitForKey(repository, "InlineCleanupChild", "child-hold");
            await client.ResumeAsync(id, "fail", new Signal());
            await WaitForKey(repository, "InlineCleanupChild", "child-cleanup");
            var checkpoint = await ReadState(repository, id);
            Assert.NotEmpty(checkpoint.InlineFrames);
            Assert.Contains(checkpoint.Joins, j => j.DataFrameId != null);
        }
        await using var second = Services(store, repository, registry);
        var engine = second.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await engine.ResumeAsync(child, "child-cleanup", new Signal())).Status);
        await engine.RecoverAsync(id);
        var result = await engine.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(4, Assert.IsType<Caller>(result.WorkflowData).Value);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InlineCompensationRecoversAfterOwnerUnwindBeforeItsActivityStarts(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Caller>("InlineCompensationRecovery").Step<Copy>()
            .Try(b => b.Use<CompensationFork, Data>(u => u.Input(x => x.Value).From(c => 17)))
            .Catch<InvalidOperationException>(b => b.Step<Copy>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value + 1)
                .Output(x => x.Value).To(c => c.WorkflowData.Value))).Build();
        var fault = new FaultRepository(repository);
        var compensation = definition.StepTable.Single(s => s.Name == nameof(CompensationCopy)).CatalogId;
        string id;
        await using (var first = Services(store, fault, registry))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Caller { Value = 3 })).InstanceId;
            fault.FailCommit = instance => ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations
                .Any(c => c.PendingActivity?.StepId == compensation);
            await Assert.ThrowsAnyAsync<Exception>(() => client.ResumeAsync(id, "fail", new Signal()));
        }
        fault.FailCommit = _ => false;
        var saved = await ReadState(repository, id);
        Assert.NotEmpty(saved.InlineFrames);
        Assert.Contains(saved.Joins, j => j.DataFrameId != null);
        await using var second = Services(store, repository, registry);
        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(4, Assert.IsType<Caller>(result.WorkflowData).Value);
        Assert.Empty((await ReadState(repository, id)).InlineFrames);
    }

    [Theory, MemberData(nameof(Steps))]
    public async Task InlineSteppingAndFrameEditsSurvivePausedRecovery(string profile, DebugStepKind kind)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var definition = Workflow.Create<Caller>("StoredInlineDebug" + kind).Step<Copy>().WaitFor<Signal>("start")
            .Use<Work, Data>(u => u.Input(x => x.Value).From(c => 5).Output(x => x.Value).To(c => c.WorkflowData.Value))
            .WithId("call").Step<Copy>().WithId("after").Build();
        string id;
        await using (var first = Services(store, repository, registry))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var operations = first.GetRequiredService<IWorkflowOperations>();
            id = (await client.RunAsync(definition, new Caller { Value = 3 })).InstanceId;
            await operations.SetBreakpointsAsync(id, [new("call", "call")], "test");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new Signal())).Status);
            await operations.SetBreakpointsAsync(id, [], "clear");
            var before = (await operations.InspectAsync(id))!;
            Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.StepAsync(id, before.Threads[0].ContinuationId, kind, "step")).Status);
            var stepped = (await operations.InspectAsync(id))!;
            Assert.Equal(kind == DebugStepKind.Into ? "call/work" : "after", stepped.Threads[0].AuthorStepId);
        }
        await using var second = Services(store, repository, registry);
        var ops = second.GetRequiredService<IWorkflowOperations>();
        var inspection = (await ops.InspectAsync(id))!;
        if (kind == DebugStepKind.Into)
        {
            var thread = Assert.Single(inspection.Threads);
            var frame = Assert.Single(thread.Frames, f => f.Kind == "InlineWorkflow");
            Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(Data)), frame.DataType);
            Assert.Equal(3, Assert.Single(inspection.Data, d => d.FrameId == null).Value.GetProperty("Value").GetInt32());
            Assert.Equal(5, Assert.Single(inspection.Data, d => d.FrameId == frame.ActivationId).Value.GetProperty("Value").GetInt32());
            await ops.EditDataAsync(id, "frame-edit", inspection.Revision,
                new(inspection.DebugState.PauseGeneration, thread.ContinuationId, frame.ActivationId),
                [new(["Value"], JsonSerializer.SerializeToElement(11))], "test");
            var edited = (await ops.InspectAsync(id))!;
            Assert.Equal(11, Assert.Single(edited.Data, d => d.FrameId == frame.ActivationId).Value.GetProperty("Value").GetInt32());
            Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id, thread.ContinuationId, DebugStepKind.Out, "out")).Status);
            Assert.Equal("after", (await ops.InspectAsync(id))!.Threads[0].AuthorStepId);
        }
        var result = await ops.ContinueAsync(id, "continue");
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(kind == DebugStepKind.Into ? 12 : 6, Assert.IsType<Caller>(result.WorkflowData).Value);
    }

    private static async Task<ExecutionCheckpoint> ReadState(IWorkflowStateRepository repository, string id)
    {
        var instance = (await repository.GetWorkflowInstanceAsync(id))!;
        var reader = new PayloadReader(async reference => await repository.PayloadStore.GetAsync(reference) ?? throw new InvalidDataException("missing"), new PayloadBudget(100_000_000));
        return ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(instance, reader));
    }

    private static async Task<string> WaitForKey(IWorkflowStateRepository repository, string name, string key)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            foreach (var row in (await repository.QueryInstancesAsync(new(WorkflowName: name))).Items)
                if ((await repository.GetOpenWaitsAsync(new(InstanceId: row.InstanceId, Key: key))).Count > 0)
                    return row.InstanceId;
            await Task.Delay(10, patience.Token);
        }
    }

    private static InlineHost Services(ProfileStore store, IWorkflowStateRepository repository,
        IWorkflowCatalog? registry = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => { o.Debugging = true; o.PayloadThresholdBytes = 1024; o.VerifyCheckpointParts = true; });
        services.AddSingleton(repository);
        if (registry != null) services.AddSingleton(registry);
        if (time != null) services.AddSingleton(time);
        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();
        try { store.StartHostLinksAsync(provider).GetAwaiter().GetResult(); return new(provider); }
        catch { provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }
    private sealed class InlineHost(ServiceProvider provider) : IServiceProvider, IAsyncDisposable
    {
        public object? GetService(Type type) => provider.GetService(type);
        public async ValueTask DisposeAsync() { await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None); await provider.DisposeAsync(); }
    }
    public class PayloadRepository : System.Reflection.DispatchProxy
    {
        internal IWorkflowStateRepository Inner { get; set; } = null!;
        internal IPayloadStore Payloads { get; set; } = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_PayloadStore") return Payloads;
            try { return method.Invoke(Inner, args); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
    private sealed class FaultedPayloads(IPayloadStore inner) : IPayloadStore
    {
        public PayloadReference? Broken { get; set; }
        public bool Corrupt { get; init; }
        public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> body, IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default) => inner.PutAsync(body, dependencies, ct);
        public ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default) =>
            reference == Broken ? ValueTask.FromResult<ReadOnlyMemory<byte>?>(Corrupt ? new byte[(int)reference.Bytes] : null) : inner.GetAsync(reference, ct);
        public ValueTask<bool> CollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default) => inner.CollectAsync(reference, grace, ct);
        public ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default) => inner.FindCollectionCandidatesAsync(limit, grace, ct);
        public ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default) => inner.InTransactionAsync(action, ct);
        public IPayloadTransaction Enlist(System.Data.Common.DbTransaction transaction) => inner.Enlist(transaction);
    }
    public sealed class Caller { public int Value { get; set; } public string Text { get; set; } = ""; }
    public sealed class Data { public int Value { get; set; } public string Text { get; set; } = ""; }
    public sealed class Signal { public int Value { get; set; } }
    public sealed class Copy : IAsyncActivity { public int Value { get; set; } public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    public sealed class Increment : IAsyncActivity { public int Value { get; set; } public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) { Value++; return Task.CompletedTask; } }
    public sealed class Fail : IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("failed"); }
    public sealed class MarkEnclosingCleanup : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) { ((Data)context.WorkflowData).Value++; return Task.CompletedTask; } }
    public sealed class CompensationCopy : IAsyncActivity
    { public int Value { get; set; } public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) { Assert.Equal(17, Value); return Task.CompletedTask; } }
    [Workflow("Waiting")] public sealed class Waiting : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Copy>().WaitFor<Signal>("inside", configure: s => s.Output(x => x.Value).To(c => c.WorkflowData.Value)); }
    [Workflow("TimerMachine")] public sealed class TimerMachine : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.StateMachine(m => m.Start("a").State("a", s => s.After(TimeSpan.FromMilliseconds(500), "end"))); }
    [Workflow("Work")] public sealed class Work : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Increment>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value).Output(x => x.Value).To(c => c.WorkflowData.Value)).WithId("work")
        .Parallel(p => p.Do(x => x.Step<Copy>()).Do(x => x.Step<Copy>())); }
    [Workflow("FailingMachine")] public sealed class FailingMachine : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Copy>().Try(x => x.StateMachine(m => m.Start("a").State("a", s => s
        .OnActivity<Fail>("end").On<Signal>("unused", "end")))).Finally(x => x.WaitFor<Signal>("trigger-cleanup")); }
    [Workflow("DoubleMachine")] public sealed class DoubleMachine : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.StateMachine(m => m.Start("a").State("a", s => s
        .OnActivity<Increment>(t => t.Output(x => x.Value).To(c => c.WorkflowData.Value).Otherwise("end"))
        .OnActivity<Increment>(t => t.Output(x => x.Value).To(c => c.WorkflowData.Value).Otherwise("end")))).WithId("machine"); }
    [Workflow("EditingFork")] public sealed class EditingFork : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Copy>().Parallel(p => p
        .Do(x => x.Step<BranchDelta>().WaitFor<Signal>("edit-one"))
        .Do(x => x.WaitFor<Signal>("edit-two"))); }
    public sealed class BranchDelta : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) { ((Data)context.WorkflowData).Text = "branch-delta"; return Task.CompletedTask; } }
    [Workflow("ActivityMachine")] public sealed class ActivityMachine : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.StateMachine(m => m.Start("a").State("a", s => s
        .OnActivity<Increment>(t => t.Input(x => x.Value).From(c => c.WorkflowData.Value)
            .Output(x => x.Value).To(c => c.WorkflowData.Value).Otherwise("end"))
        .On<Signal>("loser", t => t.Output(x => x.Value).To(c => c.WorkflowData.Value).Otherwise("end")))).WithId("machine"); }
    [Workflow("FailingFork")] public sealed class FailingFork : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> b) => b.Step<Copy>().Parallel(p => p
            .Do(x => x.Try(y => y.WaitFor<Signal>("hold")).Catch<ArgumentException>(_ => { })
                .Finally(y => y.WaitFor<Signal>("cleanup", configure: a => a.Output(x => x.Value).To(c => c.WorkflowData.Value))))
            .Do(x => x.WaitFor<Signal>("fail").Step<Fail>()));
    }
    [Workflow("ChildFork")] public sealed class ChildFork : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Copy>().Parallel(p => p
        .Do(x => x.Invoke<Data>("InlineCleanupChild", 1, u => u.Input(x => x.Value).From(c => c.WorkflowData.Value)))
        .Do(x => x.WaitFor<Signal>("fail").Step<Fail>())); }
    [Workflow("CompensationFork")] public sealed class CompensationFork : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Copy>().Parallel(p => p
        .Do(x => x.Saga(s => s.Step<Copy>(a => a.CompensateWith<CompensationCopy>(u => u
            .Input(x => x.Value).From(c => c.WorkflowData.Value))).WaitFor<Signal>("hold")))
        .Do(x => x.WaitFor<Signal>("fail").Step<Fail>())); }
}
