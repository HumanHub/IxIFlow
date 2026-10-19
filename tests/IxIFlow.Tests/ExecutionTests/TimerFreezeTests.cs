using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "timers")]
[Trait("Spec", "G-09")]
public sealed class TimerFreezeTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnfreezingAnIdleInstanceRefreshesDeadlineSchedulingWithoutPausingIt(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        var wait = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: id)));
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await h.Ops.FreezeTimersAsync(id, "freeze")).Status);
        h.Advance(20);
        await h.Client.RecoverAsync(id);
        Assert.True((await h.Store.GetWaitAsync(id, wait.WaitId))!.IsOpen);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await h.Ops.UnfreezeTimersAsync(id, "unfreeze")).Status);
        Assert.Equal(wait.DeadlineUtc + TimeSpan.FromMinutes(20), (await h.Store.GetWorkflowInstanceAsync(id))!.RunnableAtUtc);
        h.Advance(10);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Client.RecoverAsync(id)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenInboxIntakeCanConsumeAnArrivalWithoutAPause(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        await h.Ops.FreezeTimersAsync(id, "freeze");
        h.Advance(20);
        var result = await h.Client.ResumeAsync(id, "go", new Go(), "frozen-event");
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(EventOutcome.Consumed, (await h.Store.GetEventAsync(id, "frozen-event"))!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandsRequireCurrentGenerationAndRepeatedFreezeDoesNotRestartTheClock(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        await h.Ops.PauseAsync(id, "pause");
        var generation = WorkflowDebugRules.Checkpoint((await h.Store.GetWorkflowInstanceAsync(id))!).PauseGeneration;
        Assert.Equal(DebugCommandStatus.StaleGeneration, (await h.Store.RequestDebugCommandAsync(id,
            new("stale", DebugCommandKind.FreezeTimers, generation - 1, "actor"))).Status);
        await h.Ops.FreezeTimersAsync(id, "freeze");
        var start = WorkflowDebugRules.Checkpoint((await h.Store.GetWorkflowInstanceAsync(id))!).Timers!.StartedAtUtc;
        h.Advance(3);
        await h.Ops.FreezeTimersAsync(id, "repeat");
        Assert.Equal(start, WorkflowDebugRules.Checkpoint((await h.Store.GetWorkflowInstanceAsync(id))!).Timers!.StartedAtUtc);
        h.Advance(4);
        await h.Ops.UnfreezeTimersAsync(id, "unfreeze");
        await h.Ops.UnfreezeTimersAsync(id, "repeat-unfreeze");
        var audit = Assert.Single(await h.Store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.TimersShifted);
        Assert.Equal(TimeSpan.FromMinutes(7), Assert.IsType<TimersTrace>(audit.Entry.Payload).Shift);
        Assert.Single(await h.Store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.TimersFrozen);
        var command = (await h.Store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Kind == DebugCommandKind.FreezeTimers && c.Actor == "freeze");
        Assert.NotNull(command.AcknowledgedRevision);
        Assert.Equal(DebugCommandStatus.AlreadyAccepted, (await h.Store.RequestDebugCommandAsync(id,
            new(command.Id, command.Kind, generation, command.Actor))).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.RequestDebugCommandAsync(id,
            new(command.Id, DebugCommandKind.UnfreezeTimers, generation, command.Actor)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimersAdvanceByDefault(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        await h.Ops.PauseAsync(id, "pause");
        h.Advance(11);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Ops.ContinueAsync(id, "continue")).Status);
        Assert.True(JsonSerializer.Deserialize<Data>((await h.Store.GetWorkflowInstanceAsync(id))!.WorkflowDataJson.InlineText)!.TimedOut);
        Assert.DoesNotContain(await h.Store.GetTraceAsync(id), e => e.Entry.EntryType is TraceEntryType.TimersFrozen or TraceEntryType.TimersShifted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitIsSuppressedAndContinueShiftsItByExactlyStoreTime(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        var original = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: id)));
        await h.Ops.PauseAsync(id, "pause");
        h.Advance(2);
        h.HostTime.Advance(TimeSpan.FromHours(3));
        await h.Ops.FreezeTimersAsync(id, "freezer");
        var frozenAt = h.StoreTime.GetUtcNow().UtcDateTime;
        Assert.Equal(frozenAt, WorkflowDebugRules.Checkpoint((await h.Store.GetWorkflowInstanceAsync(id))!).Timers!.StartedAtUtc);
        h.Advance(20);
        var page = await h.Store.GetPendingEventsAsync(id, 0, 100);
        Assert.True(page.TimersFrozen);
        Assert.False(page.IsDue(original.WaitId));
        Assert.Equal(WorkflowExecutionStatus.Paused, (await h.Client.RecoverAsync(id)).Status);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await h.Ops.ContinueAsync(id, "resumer")).Status);
        var shifted = (await h.Store.GetWaitAsync(id, original.WaitId))!;
        Assert.Equal(original.DeadlineUtc + TimeSpan.FromMinutes(20), shifted.DeadlineUtc);
        Assert.Equal(shifted.DeadlineUtc, (await h.Store.GetWorkflowInstanceAsync(id))!.RunnableAtUtc);
        var trace = await h.Store.GetTraceAsync(id);
        var frozen = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.TimersFrozen).Entry;
        Assert.Equal(TraceClock.Store, frozen.Clock);
        Assert.Equal("freezer", Assert.IsType<TimersTrace>(frozen.Payload).Actor);
        var audit = Assert.IsType<TimersTrace>(Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.TimersShifted).Entry.Payload);
        Assert.Equal("resumer", audit.Actor);
        Assert.Equal(TimeSpan.FromMinutes(20), audit.Shift);
        h.Advance(7);
        await h.Client.RecoverAsync(id);
        Assert.Equal(WorkflowStatus.Suspended, (await h.Store.GetWorkflowInstanceAsync(id))!.Status);
        h.Advance(1);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Client.RecoverAsync(id)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayAndExecutionDeadlineShiftOnExplicitUnfreezeWhilePauseRemains(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("FrozenDelay").Step<Noop>().Delay(Ten).Build();
        var id = (await h.Client.RunAsync(definition, new Data(), new() { ExecutionTimeout = TimeSpan.FromMinutes(30) })).InstanceId;
        var delay = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: id, Kind: WaitKind.Delay)));
        var execution = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: id, Kind: WaitKind.Execution)));
        await h.Ops.PauseAsync(id, "pause");
        await h.Ops.FreezeTimersAsync(id, "freeze");
        h.Advance(20);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await h.Ops.UnfreezeTimersAsync(id, "unfreeze")).Status);
        Assert.Equal(delay.DeadlineUtc + TimeSpan.FromMinutes(20), (await h.Store.GetWaitAsync(id, delay.WaitId))!.DeadlineUtc);
        Assert.Equal(execution.DeadlineUtc + TimeSpan.FromMinutes(20), (await h.Store.GetWaitAsync(id, execution.WaitId))!.DeadlineUtc);
        Assert.Null((await h.Store.GetWorkflowInstanceAsync(id))!.RunnableAtUtc);
        Assert.Null(WorkflowDebugRules.Checkpoint((await h.Store.GetWorkflowInstanceAsync(id))!).Timers);
        await h.Ops.ContinueAsync(id, "continue");
        h.Advance(10);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Client.RecoverAsync(id)).Status);
        Assert.Single(await h.Store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.TimersShifted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAlreadyExpiredDeadlineIsNeverRevived(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        var wait = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: id)));
        await h.Ops.PauseAsync(id, "pause");
        h.Advance(10); // Equality is already due.
        await h.Ops.FreezeTimersAsync(id, "freeze");
        h.Advance(20);
        Assert.False((await h.Store.GetPendingEventsAsync(id, 0, 100)).IsDue(wait.WaitId));
        await h.Ops.UnfreezeTimersAsync(id, "unfreeze");
        Assert.Equal(wait.DeadlineUtc, (await h.Store.GetWaitAsync(id, wait.WaitId))!.DeadlineUtc);
        Assert.True((await h.Store.GetPendingEventsAsync(id, 0, 100)).IsDue(wait.WaitId));
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Ops.ContinueAsync(id, "continue")).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AppendAndSteppedIntakeAcceptAnEventAfterTheOriginalDeadline(bool sqlite, bool targeted)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        var wait = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: id)));
        await h.Ops.PauseAsync(id, "pause");
        await h.Ops.FreezeTimersAsync(id, "freeze");
        h.Advance(20);
        Assert.Equal(EventAppendStatus.Stored, (await h.Store.AppendEventAsync(Event(id, "arrival", targeted ? wait.WaitId : null))).Status);
        var state = ExecutionCheckpoint.Read((await h.Store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
        await h.Ops.StepAsync(id, state.Continuations[0].Id, DebugStepKind.Over, "step");
        Assert.Equal(EventOutcome.Consumed, (await h.Store.GetEventAsync(id, "arrival"))!.Outcome);
        Assert.DoesNotContain(await h.Store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.WaitTimedOut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWaitOpenedDuringAStepOnlyGetsTheRemainingFreeze(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("LateWait").Step<Noop>().WaitFor<Go>("go")
            .WaitFor<Go>("later", configure: w => w.TimeoutAfter(Ten).OnTimeout(b => b.Step<Noop>())).Step<Noop>().Build();
        var id = (await h.Client.RunAsync(definition, new Data())).InstanceId;
        await h.Ops.PauseAsync(id, "pause");
        await h.Ops.FreezeTimersAsync(id, "freeze");
        h.Advance(5);
        await h.Store.AppendEventAsync(Event(id, "go"));
        var branch = ExecutionCheckpoint.Read((await h.Store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).Continuations[0].Id;
        await h.Ops.StepAsync(id, branch, DebugStepKind.Over, "step");
        await h.Ops.StepAsync(id, branch, DebugStepKind.Over, "open");
        var wait = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: id, Key: "later")));
        h.Advance(15);
        await h.Ops.UnfreezeTimersAsync(id, "unfreeze");
        Assert.Equal(wait.DeadlineUtc + TimeSpan.FromMinutes(15), (await h.Store.GetWaitAsync(id, wait.WaitId))!.DeadlineUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryDeadlineIsShiftedAndItsCachedValueIsRefreshed(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("FrozenRetry").Step<Noop>()
            .Saga(s => s.Step<FailOnce>(a => a.OnError<InvalidOperationException>(e => e.ThenRetry(new RetryPolicy(Ten, Ten, 1, 1)))))
            .Build();
        var id = await h.Client.StartAsync(definition, new Data());
        await Until(async () => (await h.Store.GetWorkflowInstanceAsync(id)) is { RunnableAtUtc: { } due } && due > h.StoreTime.GetUtcNow().UtcDateTime);
        await h.Ops.PauseAsync(id, "pause");
        await Until(async () => (await h.Store.GetWorkflowInstanceAsync(id))!.Status == WorkflowStatus.Paused);
        var retry = Assert.Single(ExecutionCheckpoint.Read((await h.Store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).Continuations).RetryAfterUtc;
        Assert.NotNull(retry);
        await h.Ops.FreezeTimersAsync(id, "freeze");
        h.Advance(20);
        await h.Ops.UnfreezeTimersAsync(id, "unfreeze");
        Assert.Equal(retry + TimeSpan.FromMinutes(20), Assert.Single(ExecutionCheckpoint.Read((await h.Store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).Continuations).RetryAfterUtc);
        await h.Ops.ContinueAsync(id, "continue");
        Assert.Equal(1, h.Probe.Attempts);
        h.Advance(10);
        await h.Client.RecoverAsync(id);
        await Until(async () => (await h.Store.GetWorkflowInstanceAsync(id))!.Status == WorkflowStatus.Completed);
        Assert.Equal(2, h.Probe.Attempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecutionTimeoutStillPrecedesQueuedEvents(bool sqlite, bool expiredBeforeFreeze)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("ExecutionFirst").Step<Noop>().WaitFor<Go>("go").Step<Count>().Build();
        var id = (await h.Client.RunAsync(definition, new Data(), new() { ExecutionTimeout = Ten })).InstanceId;
        await h.Ops.PauseAsync(id, "pause");
        if (expiredBeforeFreeze) h.Advance(10);
        await h.Ops.FreezeTimersAsync(id, "freeze");
        h.Advance(20);
        Assert.Equal(EventAppendStatus.Stored, (await h.Store.AppendEventAsync(Event(id, "event"))).Status);
        await h.Ops.UnfreezeTimersAsync(id, "unfreeze");
        if (!expiredBeforeFreeze) h.Advance(10);
        Assert.Equal(WorkflowExecutionStatus.TimedOut, (await h.Ops.ContinueAsync(id, "continue")).Status);
        Assert.Equal(0, h.Probe.Count);
        Assert.NotEqual(EventOutcome.Consumed, (await h.Store.GetEventAsync(id, "event"))!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildTimerRunsAndCompletionStaysQueuedForFrozenParent(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var child = Workflow.Create<Data>("TimedChild").Step<Noop>().Delay(Ten).Build();
        await h.Services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "FrozenParent").Invoke<Data>(child.Name, 1).Step<Count>().Build();
        var id = (await h.Client.RunAsync(parent, new Data())).InstanceId;
        var childId = Assert.Single(await h.Store.GetChildInstancesAsync(id)).InstanceId;
        try { await Until(async () => (await h.Store.GetWorkflowInstanceAsync(childId))!.Status == WorkflowStatus.Suspended); }
        catch (OperationCanceledException)
        {
            var childState = (await h.Store.GetWorkflowInstanceAsync(childId))!;
            Assert.Fail($"Child status {childState.Status}: {childState.LastError} / {childState.LastErrorStackTrace}");
        }
        await h.Ops.PauseAsync(id, "pause");
        await h.Ops.FreezeTimersAsync(id, "freeze");
        h.Advance(11);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Client.RecoverAsync(childId)).Status);
        Assert.Null(WorkflowDebugRules.Checkpoint((await h.Store.GetWorkflowInstanceAsync(childId))!).Timers);
        Assert.Equal(WorkflowStatus.Paused, (await h.Store.GetWorkflowInstanceAsync(id))!.Status);
        Assert.Single((await h.Store.GetPendingEventsAsync(id, 0, 100)).Events);
        Assert.Equal(0, h.Probe.Count);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Ops.ContinueAsync(id, "continue")).Status);
        Assert.Equal(1, h.Probe.Count);
    }

    private static WorkflowDefinition Waiting() => Workflow.Create<Data>("FrozenWait").Step<Noop>()
        .WaitFor<Go>("go", configure: w => w.TimeoutAfter(Ten).OnTimeout(b => b.Step<TimeoutMark>(a => a.Output(v => v.TimedOut).To(c => c.WorkflowData.TimedOut))))
        .Step<Noop>().Build();
    private static WorkflowEvent Event(string id, string eventId, string? target = null) => new(id, eventId, typeof(Go).AssemblyQualifiedName!, "{}", "go", target);
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }
    public sealed class Data { public bool TimedOut { get; set; } }
    public sealed class Go;
    public sealed class Probe { public int Attempts; public int Count; }
    public sealed class Noop : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class TimeoutMark : IAsyncActivity
    {
        [ActivityOutput] public bool TimedOut { get; set; } = true;
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Count(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) { probe.Count++; return Task.CompletedTask; } }
    public sealed class FailOnce(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            ++probe.Attempts == 1 ? Task.FromException(new InvalidOperationException("retry")) : Task.CompletedTask;
    }
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class Harness : IAsyncDisposable
    {
        public ManualTime StoreTime { get; } = new();
        public ManualTime HostTime { get; } = new();
        public IWorkflowStateRepository Store { get; }
        public ServiceProvider Services { get; }
        public Probe Probe { get; } = new();
        public IWorkflowClient Client => Services.GetRequiredService<IWorkflowClient>();
        public IWorkflowOperations Ops => Services.GetRequiredService<IWorkflowOperations>();
        private readonly string? _folder;
        public Harness(bool sqlite)
        {
            if (sqlite)
            {
                _folder = Path.Combine(Path.GetTempPath(), "IxIFlow.TimerFreeze", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_folder);
                Store = new SqliteWorkflowStateRepository($"Data Source={Path.Combine(_folder, "state.db")}", StoreTime);
            }
            else Store = new InMemoryWorkflowStateRepository(StoreTime);
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow(o => o.Debugging = true);
            services.AddSingleton<TimeProvider>(HostTime);
            services.AddSingleton(Store);
            services.AddSingleton(Probe);
            Services = services.BuildServiceProvider();
        }
        public void Advance(int minutes) { StoreTime.Advance(TimeSpan.FromMinutes(minutes)); HostTime.Advance(TimeSpan.FromMinutes(minutes)); }
        public async ValueTask DisposeAsync()
        {
            await Services.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await Services.DisposeAsync();
            if (Store is IAsyncDisposable disposable) await disposable.DisposeAsync();
            if (_folder != null) await SqliteTestFiles.DeleteAsync(_folder);
        }
    }
}
