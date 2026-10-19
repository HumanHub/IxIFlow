using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "G-09")]
public sealed class TimerFreezeStoreTests
{
    public static TheoryData<string, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var profile in new[] { StoreProfiles.LocalMemory, StoreProfiles.LocalSqlite, StoreProfiles.DistributedSqlServer, StoreProfiles.DistributedPostgreSql })
                foreach (var scenario in new[] { "wait", "delay", "execution", "expired", "opened", "armed", "opened-zero", "armed-zero", "retry", "branch-retry" })
                    cases.Add(profile, scenario);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StoreShiftsDeadlinesOnceAcrossRepositoryRecreation(string profile, string scenario)
    {
        var time = new ManualTime();
        await using var environment = await StoreProfiles.CreateAsync(profile, time);
        var store = environment.NewRepository();
        var id = Guid.NewGuid().ToString("N");
        var checkpoint = ExecutionCheckpoint.Read(StoreTestSupport.CheckpointJson);
        if (scenario == "branch-retry") checkpoint.Continuations.Add(new ContinuationState
        { Id = "branch", ParentJoinId = "join", Stack = [new ScopePosition { ScopeId = "root" }] });
        var target = checkpoint.Continuations.Last();
        // A server's time is sampled from a normal store operation; the injected clock is only used locally.
        var instance = new WorkflowInstance { InstanceId = id, ExecutionStateJson = JsonSerializer.Serialize(checkpoint), Status = WorkflowStatus.Paused };
        var kind = scenario == "delay" ? WaitKind.Delay : scenario == "execution" ? WaitKind.Execution : WaitKind.Event;
        var opening = new OpenedWait("wait", kind, "go", typeof(Go).AssemblyQualifiedName!,
            scenario is "armed" or "armed-zero" or "retry" or "branch-retry" ? null : scenario is "expired" or "opened-zero" ? TimeSpan.Zero : TimeSpan.FromMinutes(5));
        await Write(store, instance, opened: scenario is "opened" or "opened-zero" ? [] : [opening]);
        var sample = (await store.GetPendingEventsAsync(id, 0, 100)).StoreTimeUtc!.Value;
        if (scenario is "retry" or "branch-retry")
        {
            instance = (await store.GetWorkflowInstanceAsync(id))!;
            checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            checkpoint.Continuations.Last().RetryAfterUtc = sample.AddMinutes(5);
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            await Write(store, instance);
        }
        await Apply(store, id, "freeze", DebugCommandKind.FreezeTimers);
        var freeze = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(id))!).Timers!;
        var originalRetry = scenario is "retry" or "branch-retry"
            ? ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).Continuations.Last().RetryAfterUtc : null;
        var originalWait = await store.GetWaitAsync(id, "wait");
        await Advance(profile, time);
        if (scenario is "opened" or "armed" or "opened-zero" or "armed-zero")
        {
            instance = (await store.GetWorkflowInstanceAsync(id))!;
            await Write(store, instance, opened: scenario is "opened" or "opened-zero" ? [opening] : [],
                armed: scenario is "armed" or "armed-zero" ? [new("wait", scenario == "armed-zero" ? TimeSpan.Zero : TimeSpan.FromMinutes(5))] : []);
            originalWait = await store.GetWaitAsync(id, "wait");
        }
        var activeFreeze = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(id))!).Timers!;
        await Advance(profile, time);
        // Release SQLite's ownership file and reopen the repository before the shifting transaction.
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        store = environment.NewRepository();
        var (commit, result) = await Apply(store, id, "continue", DebugCommandKind.Continue);
        Assert.NotNull(result.Timers);
        Assert.Null(result.Timers.Debug.Timers);
        var trace = await store.GetTraceAsync(id);
        var audit = Assert.IsType<TimersTrace>(Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.TimersShifted).Entry.Payload);
        Assert.Equal(freeze.StartedAtUtc, audit.FrozenAtUtc);
        Assert.Equal("timer-actor", audit.Actor);
        var end = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.TimersShifted).Entry.Timestamp;
        if (originalRetry != null)
        {
            var retry = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).Continuations.Last().RetryAfterUtc;
            Assert.Equal(originalRetry + (end - activeFreeze.RetryAnchors[target.Id].FrozenFromUtc), retry);
            Assert.Equal(retry, result.Timers.RetryDeadlines[target.Id]);
        }
        else
        {
            var shifted = (await store.GetWaitAsync(id, "wait"))!.DeadlineUtc;
            var expected = scenario == "expired" ? originalWait!.DeadlineUtc
                : originalWait!.DeadlineUtc + (end - activeFreeze.WaitAnchors["wait"]);
            Assert.Equal(expected, shifted);
        }
        // Simulate losing the response: the identical immutable commit is replayed at a later time.
        await Advance(profile, time);
        var replay = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, replay.Status);
        Assert.Equal(result.Timers.Deadlines, replay.Timers!.Deadlines);
        Assert.Equal(result.Timers.RetryDeadlines, replay.Timers.RetryDeadlines);
        Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.TimersShifted);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FreezeAppendAndResumeRaceUnderOneInstanceLock(string profile)
    {
        var time = new ManualTime();
        await using var environment = await StoreProfiles.CreateAsync(profile, time);
        var store = environment.NewRepository();
        var id = Guid.NewGuid().ToString("N");
        await Write(store, new() { InstanceId = id, ExecutionStateJson = StoreTestSupport.CheckpointJson, Status = WorkflowStatus.Paused },
            opened: [new("wait", WaitKind.Event, "go", typeof(Go).AssemblyQualifiedName!, TimeSpan.FromMinutes(5))]);
        var deadline = (await store.GetWaitAsync(id, "wait"))!.DeadlineUtc;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freezeTask = Task.Run(async () => { await gate.Task; return await Apply(store, id, "freeze", DebugCommandKind.FreezeTimers); });
        var appendTask = Task.Run(async () => { await gate.Task; return await store.AppendEventAsync(new(id, "event", typeof(Go).AssemblyQualifiedName!, "{}", "go")); });
        var resumeTask = Task.Run(async () =>
        {
            await gate.Task;
            // Queue behind Freeze as soon as its ordered row exists, possibly before its commit.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!(await store.GetDebugStatesAsync([id]))[id].Commands.Any(c => c.Id == "freeze")) await Task.Delay(1, timeout.Token);
            return await store.RequestDebugCommandAsync(id, new("continue", DebugCommandKind.Continue, 0, "timer-actor"));
        });
        gate.SetResult();
        await Task.WhenAll(freezeTask, appendTask, resumeTask);
        Assert.Equal(EventAppendStatus.Stored, (await appendTask).Status);
        Assert.Equal(DebugCommandStatus.Accepted, (await resumeTask).Status);
        Assert.Contains(await store.GetRunnableInstancesAsync(100), r => r.InstanceId == id);
        await Advance(profile, time);
        var (commit, result) = await Apply(store, id, "continue", DebugCommandKind.Continue);
        Assert.Equal(deadline + Assert.IsType<TimersTrace>(Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.TimersShifted).Entry.Payload).Shift,
            (await store.GetWaitAsync(id, "wait"))!.DeadlineUtc);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        Assert.All((await store.GetDebugStatesAsync([id]))[id].Commands, c => Assert.NotNull(c.AcknowledgedRevision));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LostShiftAcknowledgementRefreshesTheSerializedExecutor(string profile)
    {
        var time = new ManualTime();
        await using var environment = await StoreProfiles.CreateAsync(profile, time);
        var store = environment.NewRepository();
        var lose = false;
        var faulty = new FaultRepository(store) { LoseAcknowledgement = i => lose && i.Status == WorkflowStatus.Running };
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => o.Debugging = true);
        services.AddSingleton<IWorkflowStateRepository>(faulty);
        services.AddSingleton<TimeProvider>(time);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var ops = provider.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Go>("LostTimerAck").Step<Noop>().Delay(TimeSpan.FromMinutes(5)).Build();
        var id = (await client.RunAsync(definition, new Go())).InstanceId;
        var original = Assert.Single(await store.GetOpenWaitsAsync(new(InstanceId: id, Kind: WaitKind.Delay)));
        await ops.PauseAsync(id, "pause");
        await ops.FreezeTimersAsync(id, "freeze");
        await Advance(profile, time);
        lose = true;
        var continued = await ops.ContinueAsync(id, "continue");
        Assert.True(continued.Status == WorkflowExecutionStatus.Suspended, continued.ErrorMessage + "\n" + continued.ErrorStackTrace);
        var shift = Assert.IsType<TimersTrace>(Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.TimersShifted).Entry.Payload).Shift;
        Assert.Equal(original.DeadlineUtc + shift, (await store.GetWaitAsync(id, original.WaitId))!.DeadlineUtc);
        Assert.Equal((await store.GetWaitAsync(id, original.WaitId))!.DeadlineUtc, (await store.GetWorkflowInstanceAsync(id))!.RunnableAtUtc);
    }

    private static async Task<(CheckpointCommit, WorkflowCommitResult)> Apply(IWorkflowStateRepository store, string id, string commandId, DebugCommandKind kind)
    {
        var instance = (await store.GetWorkflowInstanceAsync(id))!;
        var debug = WorkflowDebugRules.Checkpoint(instance);
        var accepted = await store.RequestDebugCommandAsync(id, new(commandId, kind, debug.PauseGeneration, "timer-actor"));
        Assert.True(accepted.Status is DebugCommandStatus.Accepted or DebugCommandStatus.AlreadyAccepted);
        var json = JsonNode.Parse(instance.ExecutionStateJson)!;
        json["Debug"] = JsonSerializer.SerializeToNode(debug with { AppliedOrder = accepted.Command!.Order });
        instance.ExecutionStateJson = json.ToJsonString();
        if (kind == DebugCommandKind.Continue) instance.Status = WorkflowStatus.Running;
        return await Write(store, instance);
    }

    private static async Task<(CheckpointCommit, WorkflowCommitResult)> Write(IWorkflowStateRepository store, WorkflowInstance instance,
        IReadOnlyList<OpenedWait>? opened = null, IReadOnlyList<ArmedWait>? armed = null)
    {
        var lease = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        var commit = CheckpointCommit.Create(instance, lease.Epoch, instance.Revision, openedWaits: opened, armedWaits: armed, stopped: instance.Status == WorkflowStatus.Paused);
        var result = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        return (commit, result);
    }
    private static async Task Advance(string profile, ManualTime time)
    {
        if (profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite) time.Advance(TimeSpan.FromSeconds(3));
        else await Task.Delay(30);
    }
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
}
