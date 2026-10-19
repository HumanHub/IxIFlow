using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.ExecutionTests.TraceEventsTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "G-11")]
public sealed class TraceReplayTests : IClassFixture<SqliteTestDatabase<TraceReplayTests>>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecutionSuiteControlFlowsReplayTheLiveThreadsAndFramesAtEveryCommit()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        // The workflow and activities from TraceEventsTests, including both ForEach modes and all unwind scopes.
        var definition = Workflow.Create<Data>("ReplayControl")
            .Step<Noop>()
            .Sequence(sequence => sequence.If(ctx => true, then => then.Step<Noop>()))
            .WhileDo(ctx => ctx.WorkflowData.Count < 2, body => body.Step<Increment>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Count)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Count)))
            .DoWhile(body => body.If(ctx => false, then => then.Step<Noop>(), otherwise => otherwise.Step<Noop>()), ctx => false)
            .Parallel(parallel => parallel.Do(branch => branch.Step<Noop>()).Do(branch => branch.Step<Noop>()))
            .Try(body => body.Saga(saga => saga.Step<Noop>(step => step.CompensateWith<Noop>()).Step<Fail>()))
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<Noop>())
            .Finally(body => body.Step<Noop>())
            .ForEach(ctx => ctx.WorkflowData.Items, (body, item) => body.Result(ctx => ctx.Item(item)), setup => setup.Into(ctx => ctx.WorkflowData.Results))
            .ForEach(ctx => ctx.WorkflowData.Items, (body, item) => body.Result(ctx => ctx.Item(item)), setup => setup.Parallel(2).Into(ctx => ctx.WorkflowData.ParallelResults))
            .Build();
        store.Definitions[definition.Name] = definition;
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        await Verify(store, services, result.InstanceId, definition);
        var entries = await Read(services, result.InstanceId);
        var (entered, exited) = FrameLifetimes(entries);
        Assert.Equal(entered.Length, entered.Distinct().Count()); Assert.Equal(entered.Order(), exited.Order());
        foreach (var kind in new[] { TraceEntryType.ReplayOrigin, TraceEntryType.ThreadPosition,
            TraceEntryType.CatchEntered, TraceEntryType.FinallyEntered, TraceEntryType.CompensationCompleted, TraceEntryType.JoinCompleted })
            Assert.Contains(entries, e => e.Entry.EntryType == kind);
        Assert.All(entries.Where(e => e.Entry.EntryType is TraceEntryType.ActivityStarted or TraceEntryType.ActivityCompleted or
            TraceEntryType.ActivityFailed || e.Entry.Payload is ControlFlowTrace or ConditionTrace or JoinTrace), e =>
        {
            Assert.False(string.IsNullOrEmpty(e.Entry.StepId)); Assert.False(string.IsNullOrEmpty(e.Entry.ContinuationId));
            Assert.False(string.IsNullOrEmpty(e.Entry.FrameActivationId));
        });
        var json = JsonSerializer.Serialize(entries.Where(e => e.Entry.Payload is ReplayOriginTrace or ThreadPositionTrace));
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("timeout")]
    [InlineData("delay")]
    [InlineData("execution-timeout")]
    public async Task ExecutionSuiteWaitsReplayAtEveryCommit(string shape)
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        var b = Workflow.Create<Data>("ReplayWait" + shape).Step<Noop>();
        var definition = shape == "delay" ? b.Delay(TimeSpan.FromMinutes(1)).Step<Noop>().Build() : shape == "timeout"
            ? b.WaitFor<Go>("go", configure: wait => wait.TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(body => body.Step<Noop>())).Step<Noop>().Build()
            : b.WaitFor<Go>("go").Step<Noop>().Build();
        store.Definitions[definition.Name] = definition;
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, new Data(), shape == "execution-timeout" ? new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMinutes(1) } : null);
        if (shape == "event") result = await client.ResumeAsync(result.InstanceId, "go", new Go());
        else { time.Advance(TimeSpan.FromMinutes(2)); result = await client.RecoverAsync(result.InstanceId); }
        result = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(shape == "execution-timeout" ? WorkflowExecutionStatus.TimedOut : WorkflowExecutionStatus.Success, result.Status);
        await Verify(store, services, result.InstanceId, definition);
    }

    [Fact]
    public async Task ChildWorkflowsReplayBothInstancesAndNeverExposeTheParentWait()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        var child = new WorkflowBuilder<Data, object>([], "ReplayChild").WaitFor<Go>("child-go").Step<Noop>().Build();
        var parent = new WorkflowBuilder<Data, object>([], "ReplayParent").Invoke<Data>("ReplayChild", 1).Step<Noop>().Build();
        store.Definitions[child.Name] = child; store.Definitions[parent.Name] = parent;
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(child);
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(parent, new Data());
        var parentState = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(started.InstanceId))!.ExecutionStateJson);
        var childId = Assert.Single(parentState.Waits).ChildWorkflowId!;
        var childInstance = (await store.GetWorkflowInstanceAsync(childId))!;
        var ops = services.GetRequiredService<IWorkflowOperations>();
        Assert.DoesNotContain(childInstance.ParentWaitId!, JsonSerializer.Serialize(await ops.InspectAsync(childId)));
        await client.ResumeAsync(childId, "child-go", new Go());
        await client.RecoverAsync(started.InstanceId);
        await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(15));
        await Verify(store, services, started.InstanceId, parent); await Verify(store, services, childId, child);
        var trace = await Read(services, childId);
        var (entered, exited) = FrameLifetimes(trace);
        Assert.NotEmpty(entered); Assert.Equal(entered.Order(), exited.Order());
    }

    [Fact]
    public async Task CursorMovesBothWaysAndStopsAtCatalogBreakpointsWithoutExecutingCode()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        var definition = new WorkflowBuilder<Data, object>([], "Cursor").Sequence(b => b.Step<Noop>().Step<Noop>()).Build();
        store.Definitions[definition.Name] = definition;
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        var entries = await Read(services, result.InstanceId); var replay = TraceReplay.Load(definition, entries);
        var first = replay.Current; Assert.False(replay.StepBackward()); Assert.True(replay.StepForward());
        Assert.True(replay.StepBackward()); Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(replay.Current));
        var stop = entries.First(e => e.Entry.EntryType == TraceEntryType.ActivityStarted);
        replay.SetBreakpoints([stop.Entry.StepId!]); Assert.True(replay.Continue()); Assert.Equal(stop.Sequence, replay.Current.Sequence);
        replay.SetBreakpoints([]); replay.Continue(); Assert.False(replay.CanStepForward);
        replay.SetBreakpoints([stop.Entry.StepId!]); replay.Continue(backwards: true); Assert.Equal(stop.Sequence, replay.Current.Sequence);
        var saved = replay.Current; var original = JsonSerializer.Serialize(saved);
        ((IList<InspectionFrame>)saved.Threads[0].Frames)[0] = saved.Threads[0].Frames[0] with { Name = "changed" };
        Assert.Equal(original, JsonSerializer.Serialize(replay.Current));
    }

    [Fact]
    public async Task ReplayReadsDecisionsWithoutInvokingConditionsOrCombiners()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        var definition = new WorkflowBuilder<Data, object>([], "RecordedDecision")
            .If(c => true, b => b.Step<Noop>()).Parallel(p => p.Do(b => b.Step<Noop>()).Do(b => b.Step<Noop>())).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        var entries = await Read(services, result.InstanceId);
        definition.Steps[0].CompiledCondition = _ => throw new InvalidOperationException("Replay executed a condition");
        var replay = TraceReplay.Load(definition, entries); replay.Continue();
        Assert.True(replay.Current.IsComplete); Assert.False(replay.CanStepForward);
    }

    [Theory]
    [InlineData(2, TraceDetail.Full, null)]
    [InlineData(1000, TraceDetail.Activities, null)]
    [InlineData(1000, TraceDetail.Full, 3L)]
    public async Task RecordingLimitsExplicitlyMarkReconstructionIncomplete(int limit, TraceDetail detail, long? budget)
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time, limit, detail, budget);
        var definition = new WorkflowBuilder<Data, object>([], "Limited").Sequence(b => b.Step<Noop>().Step<Noop>()).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        var entries = await Read(services, result.InstanceId);
        if (budget != null) Assert.Single(entries, e => e.Entry.EntryType == TraceEntryType.TraceBudgetReached);
        var replay = TraceReplay.Load(definition, entries); replay.Continue();
        Assert.False(replay.Current.IsComplete); Assert.NotEmpty(replay.Current.IncompleteReasons);
    }

    [Fact]
    public async Task MissingHistoryAndStructureMismatchAreNeverGuessed()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        var definition = Workflow.Create<Data>("Mismatch").Step<Noop>().Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        var entries = await Read(services, result.InstanceId);
        var replay = TraceReplay.Load(definition, entries.Skip(2)); replay.Continue(); Assert.False(replay.Current.IsComplete);
        var cut = entries.Last(e => e.Sequence < e.CommitEndSequence);
        var tail = TraceReplay.Load(definition, entries.Where(e => e.Sequence <= cut.Sequence));
        tail.Continue(); Assert.False(tail.Current.IsComplete);
        var other = new WorkflowBuilder<Data, object>([], "Mismatch").Sequence(b => b.Step<Noop>()).Build();
        Assert.Throws<WorkflowEngineException>(() => TraceReplay.Load(other, entries));
        Assert.False(TraceReplay.Load(definition, []).Current.IsComplete);
        Assert.Throws<ArgumentException>(() => TraceReplay.Load(definition, entries.Reverse()));
    }

    [Fact]
    public async Task InspectionPagingRejectionsAndErrorsDoNotExposeCapabilities()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        var definition = new WorkflowBuilder<Data, object>([], "Privacy").WaitFor<Go>("go").Step<EchoFailure>(s => s.Input(a => a.Text).From(c => c.WorkflowData.Secret)).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var result = await client.RunAsync(definition, new Data());
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new() { InstanceId = result.InstanceId }));
        // Also exercise values copied into data and error text, as well as structural waits and refused replies.
        var raw = (await store.GetWorkflowInstanceAsync(result.InstanceId))!;
        var data = JsonSerializer.Deserialize<Data>(raw.WorkflowDataJson.InlineText)!; data.Secret = "https://example.test/reply/" + wait.Token + "/done";
        await ops.PauseAsync(result.InstanceId, "inspect");
        var paused = (await ops.InspectAsync(result.InstanceId))!;
        Assert.NotEmpty(paused.Threads); Assert.NotEmpty(paused.Data); Assert.NotEmpty(paused.Waits);
        await ops.FreezeTimersAsync(result.InstanceId, "freeze");
        await client.ReplyAsync(wait.Token, new Wrong());
        var texts = new List<string> { JsonSerializer.Serialize(await ops.InspectAsync(result.InstanceId)), JsonSerializer.Serialize(await Read(services, result.InstanceId)) };
        // Seed a separate workflow with a copied token, then include it in the activity error.
        var failed = await client.RunAsync(Workflow.Create<Data>("PrivacyError").Step<EchoFailure>(s => s.Input(a => a.Text).From(c => c.WorkflowData.Secret)).Build(), data);
        texts.Add(JsonSerializer.Serialize(await ops.InspectAsync(failed.InstanceId)));
        texts.Add(JsonSerializer.Serialize(await Read(services, failed.InstanceId)));
        var replay = TraceReplay.Load(definition, await Read(services, result.InstanceId));
        do { texts.Add(JsonSerializer.Serialize(replay.Current)); } while (replay.StepForward());
        Assert.All(texts, text => { Assert.DoesNotContain(wait.WaitId, text); Assert.DoesNotContain(wait.Token, text); });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ops.GetTraceAsync(result.InstanceId, -1, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ops.GetTraceAsync(result.InstanceId, 0, 1001));
    }

    [Fact]
    public async Task InspectionShowsEachBranchDataAndTheSavedDebugHold()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        var definition = new WorkflowBuilder<Data, object>([], "BranchInspection").Parallel(p => p
            .Do(b => b.Step<Increment>(s => s.Input(a => a.Value).From(c => c.WorkflowData.Count)
                .Output(a => a.Value).To(c => c.WorkflowData.Count)).WaitFor<Go>("left"))
            .Do(b => b.WaitFor<Go>("right"))).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var started = await client.RunAsync(definition, new Data()); await ops.PauseAsync(started.InstanceId, "reader");
        var inspection = (await ops.InspectAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Paused, inspection.Summary.Status); Assert.True(inspection.DebugState.PauseGeneration > 0);
        Assert.Null(inspection.DebugState.Stop); Assert.Equal(3, inspection.Threads.Count);
        Assert.Contains(inspection.Data, d => d.Value.GetProperty("Count").GetInt32() == 1);
        Assert.Contains(inspection.Data, d => d.Value.GetProperty("Count").GetInt32() == 0);
        Assert.All(inspection.Threads.Skip(1), t => { Assert.NotNull(t.ParentContinuationId); Assert.NotNull(t.ForkActivationId); });
        var snapshot = (await client.GetSnapshotAsync(started.InstanceId))!;
        Assert.Equal(snapshot.Frames.Count, inspection.Threads.Sum(t => t.Frames.Count));
        var state = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(started.InstanceId))!.ExecutionStateJson);
        Assert.All(state.Waits, w => Assert.DoesNotContain(w.Id, JsonSerializer.Serialize(inspection)));
    }

    [Fact]
    public async Task InspectionShowsAPendingAttemptWithoutItsInputsOrWaitInternals()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        var gate = new GateState();
        var collection = new ServiceCollection().AddLogging(); collection.AddSingleton<IWorkflowStateRepository>(store);
        collection.AddSingleton<TimeProvider>(time); collection.AddSingleton(gate); collection.AddIxIFlow(o => o.Debugging = true);
        await using var services = collection.BuildServiceProvider();
        var running = services.GetRequiredService<IWorkflowClient>().RunAsync(Workflow.Create<Data>("PendingInspection").Step<GateActivity>().Build(), new Data());
        var id = await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var inspection = (await services.GetRequiredService<IWorkflowOperations>().InspectAsync(id))!;
            var attempt = Assert.Single(inspection.PendingAttempts);
            Assert.Equal("root/0", attempt.StepId); Assert.NotNull(attempt.AttemptId); Assert.NotNull(attempt.Host);
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(inspection.PendingAttempts));
        }
        finally { gate.Finish.TrySetResult(); }
        await running.WaitAsync(TimeSpan.FromSeconds(15));
    }
    public sealed class GateState
    {
        public TaskCompletionSource<string> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class GateActivity(GateState state) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { state.Started.TrySetResult(context.WorkflowInstanceId); await state.Finish.Task.WaitAsync(cancellationToken); }
    }

    [Fact]
    public async Task InspectionIncludesCommittedBreakpointHitsAndReplayRetainsTheirOccurrences()
    {
        var time = new FakeTimeProvider(Start); var store = new RecordingStore(time);
        await using var services = Services(store, time);
        var definition = new WorkflowBuilder<Data, object>([], "HitInspection").WaitFor<Go>("go")
            .WhileDo(c => c.WorkflowData.Count < 3, b => b.Step<Increment>(s => s.Input(a => a.Value).From(c => c.WorkflowData.Count)
                .Output(a => a.Value).To(c => c.WorkflowData.Count)).WithId("increment")).Build();
        store.Definitions[definition.Name] = definition;
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var started = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(started.InstanceId, [new("hit", "increment", HitCondition: ">= 2")], "set");
        await client.ResumeAsync(started.InstanceId, "go", new Go());
        var inspection = (await ops.InspectAsync(started.InstanceId))!;
        Assert.Equal(2, Assert.Single(inspection.Breakpoints).HitCount);
        Assert.Equal("hit", inspection.DebugState.Stop!.BreakpointId);
        Assert.Equal(1, inspection.Data[0].Value.GetProperty("Count").GetInt32());
        await Verify(store, services, started.InstanceId, definition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorTracePagesUseExclusiveSequencesOnMemoryAndSqlite(bool sqlite)
    {
        var time = new FakeTimeProvider(Start);
        await using var relational = sqlite ? new SqliteWorkflowStateRepository(SqliteTestDatabase<TraceReplayTests>.ConnectionString, time) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        var collection = new ServiceCollection().AddLogging(); collection.AddSingleton(store); collection.AddSingleton<TimeProvider>(time);
        collection.AddIxIFlow(); await using var services = collection.BuildServiceProvider();
        var definition = Workflow.Create<Data>("Page" + sqlite).Step<Noop>().WaitFor<Go>("go").Build();
        var started = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        var entries = await Read(services, started.InstanceId); Assert.Equal(Enumerable.Range(1, entries.Length).Select(n => (long)n), entries.Select(e => e.Sequence));
        var ops = services.GetRequiredService<IWorkflowOperations>();
        Assert.Equal(entries.Length, (await ops.GetTraceAsync(started.InstanceId)).Count);
        var replay = TraceReplay.Load(definition, entries); replay.Continue(); Assert.True(replay.Current.IsComplete);
        var inspection = (await ops.InspectAsync(started.InstanceId))!; replay.AttachCheckpoint(inspection);
        Assert.NotEmpty(replay.Current.Threads); Assert.NotNull(replay.Current.Data);
        var client = services.GetRequiredService<IWorkflowClient>();
        EventAssert.Rejected(await client.ResumeAsync(started.InstanceId, "wrong-key", new Go()), EventRejection.NoOpenWait);
        var refused = (await Read(services, started.InstanceId)).Last();
        Assert.Null(refused.CommitEndSequence); Assert.Null(refused.CommitStatus);
        var atRefusal = TraceReplay.Load(definition, await Read(services, started.InstanceId)); atRefusal.Continue();
        Assert.False(atRefusal.Current.IsCheckpoint); Assert.Null(atRefusal.Current.Data);
        await client.ResumeAsync(started.InstanceId, "go", new Go());
        var completed = TraceReplay.Load(definition, await Read(services, started.InstanceId)); completed.Continue();
        Assert.True(completed.Current.IsComplete, string.Join(", ", completed.Current.IncompleteReasons));
        Assert.Equal(WorkflowStatus.Completed, completed.Current.Status);
        Assert.Empty(await ops.GetTraceAsync("absent")); Assert.Null(await ops.InspectAsync("absent"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ops.GetTraceAsync(started.InstanceId, cancellationToken: cancelled.Token));
    }

    private static async Task Verify(RecordingStore store, ServiceProvider services, string id, WorkflowDefinition definition)
    {
        var entries = await Read(services, id); var replay = TraceReplay.Load(definition, entries);
        Assert.Single(entries, e => e.Entry.EntryType == TraceEntryType.ReplayOrigin);
        var expected = store.Snapshots.Where(s => s.Id == id).ToArray(); Assert.NotEmpty(expected);
        foreach (var snapshot in expected)
        {
            var boundary = Assert.Single(entries, e => e.CommitEndSequence == e.Sequence && e.Revision == snapshot.Revision);
            replay.Seek(boundary.Sequence); Assert.True(replay.Current.IsComplete, string.Join(", ", replay.Current.IncompleteReasons));
            Assert.Equal(snapshot.Threads, JsonSerializer.Serialize(replay.Current.Threads));
        }
        var inspection = (await services.GetRequiredService<IWorkflowOperations>().InspectAsync(id))!;
        replay.Seek(entries.Last(e => e.CommitEndSequence == e.Sequence).Sequence);
        Assert.Equal(JsonSerializer.Serialize(inspection.Threads), JsonSerializer.Serialize(replay.Current.Threads));
        Assert.Equal(inspection.Revision, replay.Current.Revision);
        Assert.Null(replay.Current.Data);
        replay.AttachCheckpoint(inspection);
        Assert.Equal(JsonSerializer.Serialize(inspection.Data), JsonSerializer.Serialize(replay.Current.Data));
        replay.Seek(entries[0].Sequence); Assert.Null(replay.Current.Data);
    }
    // Scope lifetimes are derived from successive positions, with no separate lifecycle entries.
    private static (string[] Entered, string[] Exited) FrameLifetimes(IEnumerable<StoredTraceEntry> entries)
    {
        var threads = new Dictionary<string, InspectionThread>();
        var entered = new List<string>(); var exited = new List<string>();
        foreach (var entry in entries)
        {
            var before = threads.Values.SelectMany(t => t.Frames).Select(f => f.ActivationId).ToHashSet();
            switch (entry.Entry.Payload)
            {
                case ReplayOriginTrace origin:
                    threads = origin.Threads.ToDictionary(t => t.ContinuationId);
                    break;
                case ThreadPositionTrace position:
                    if (position.Thread == null) threads.Remove(position.ContinuationId);
                    else threads[position.ContinuationId] = position.Thread;
                    break;
            }
            var after = threads.Values.SelectMany(t => t.Frames).Select(f => f.ActivationId).ToHashSet();
            entered.AddRange(after.Except(before)); exited.AddRange(before.Except(after));
        }
        return (entered.ToArray(), exited.ToArray());
    }
    private static async Task<StoredTraceEntry[]> Read(ServiceProvider services, string id)
    {
        var ops = services.GetRequiredService<IWorkflowOperations>(); var entries = new List<StoredTraceEntry>();
        long after = 0;
        while (true)
        {
            var page = await ops.GetTraceAsync(id, after, 7); if (page.Count == 0) return entries.ToArray();
            Assert.All(page, e => Assert.True(e.Sequence > after)); Assert.InRange(page.Count, 1, 7);
            entries.AddRange(page); after = page[^1].Sequence;
        }
    }
    private static ServiceProvider Services(RecordingStore store, FakeTimeProvider time, int limit = 1000,
        TraceDetail detail = TraceDetail.Full, long? budget = null)
    {
        var services = new ServiceCollection().AddLogging(); services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton<TimeProvider>(time); services.AddIxIFlow(o =>
        { o.Debugging = true; o.MaxTraceEntriesPerCommit = limit; o.TraceDetail = detail; o.MaxTraceEntriesPerInstance = budget; });
        return services.BuildServiceProvider();
    }
    private sealed class RecordingStore : InMemoryWorkflowStateRepository
    {
        private readonly TimeProvider _clock;
        public RecordingStore(TimeProvider time) : base(time) => _clock = time;
        public Dictionary<string, WorkflowDefinition> Definitions { get; } = [];
        public List<(string Id, long Revision, string Threads)> Snapshots { get; } = [];
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            var instance = await GetWorkflowInstanceAsync(commit.InstanceId);
            if (instance != null && Definitions.TryGetValue(instance.WorkflowName, out var definition) &&
                commit.TraceEntries.Count > 0)
            {
                var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
                var scopes = WorkflowDefinitionValidator.ForExecution(definition);
                // Independently read the live snapshot, then adapt its fields for comparison.
                var snapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, _clock.GetUtcNow().UtcDateTime);
                var threads = snapshot.Pointers.Select(p => new InspectionThread(p.PointerId, p.Name, p.ParentPointerId,
                    p.ParentJoinId, p.Sequence, p.ForkActivationId, p.StepId, p.AuthorStepId, p.StepIndex, p.Status, p.FrameId!,
                    snapshot.Frames.Where(f => f.State["ContinuationId"] == p.PointerId).Select(f => new InspectionFrame(f.FrameId,
                        f.Name, f.Kind, f.StepId, f.ParentFrameId, int.Parse(f.State["NextStepIndex"]), f.LoopIteration, f.ForEachItemIndex)).ToArray())).ToArray();
                lock (Snapshots) Snapshots.Add((instance.InstanceId, instance.Revision, JsonSerializer.Serialize(threads)));
            }
            return result;
        }
    }
    public sealed class EchoFailure : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException(Text);
    }
}
