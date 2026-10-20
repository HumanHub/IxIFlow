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
public sealed class PausedTimerTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueCommandsRequireCurrentGenerationAndKeepAcknowledgements(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        await h.Ops.PauseAsync(id, "pause");
        var generation = WorkflowDebugRules.Checkpoint((await h.Store.GetWorkflowInstanceAsync(id))!).PauseGeneration;
        Assert.Equal(DebugCommandStatus.StaleGeneration, (await h.Store.RequestDebugCommandAsync(id,
            new("stale", DebugCommandKind.Continue, generation - 1, "actor"))).Status);
        await h.Ops.ContinueAsync(id, "continue");
        var command = (await h.Store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Kind == DebugCommandKind.Continue);
        Assert.NotNull(command.AcknowledgedRevision);
        Assert.Equal(DebugCommandStatus.AlreadyAccepted, (await h.Store.RequestDebugCommandAsync(id,
            new(command.Id, command.Kind, generation, command.Actor))).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Store.RequestDebugCommandAsync(id,
            new(command.Id, DebugCommandKind.Pause, generation, command.Actor)));
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
        h.Advance(20);
        Assert.Equal(wait.DeadlineUtc, (await h.Store.GetWaitAsync(id, wait.WaitId))!.DeadlineUtc);
        Assert.True((await h.Store.GetPendingEventsAsync(id, 0, 100)).IsDue(wait.WaitId));
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Ops.ContinueAsync(id, "continue")).Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecutionTimeoutStillPrecedesQueuedEvents(bool sqlite, bool expiredBeforePause)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("ExecutionFirst").Step<Noop>().WaitFor<Go>("go").Step<Count>().Build();
        var id = (await h.Client.RunAsync(definition, new Data(), new() { ExecutionTimeout = Ten })).InstanceId;
        if (expiredBeforePause) h.Advance(10);
        await h.Ops.PauseAsync(id, "pause");
        h.Advance(20);
        Assert.Equal(EventAppendStatus.Stored, (await h.Store.AppendEventAsync(Event(id, "event"))).Status);
        if (!expiredBeforePause) h.Advance(10);
        Assert.Equal(WorkflowExecutionStatus.TimedOut, (await h.Ops.ContinueAsync(id, "continue")).Status);
        Assert.Equal(0, h.Probe.Count);
        Assert.NotEqual(EventOutcome.Consumed, (await h.Store.GetEventAsync(id, "event"))!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildTimerRunsAndCompletionStaysQueuedForPausedParent(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var child = Workflow.Create<Data>("TimedChild").Step<Noop>().Delay(Ten).Build();
        await h.Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "PausedParent").Invoke<Data>(child.Name, 1).Step<Count>().Build();
        var id = (await h.Client.RunAsync(parent, new Data())).InstanceId;
        var childId = Assert.Single((await h.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id, Limit: int.MaxValue))).Items).InstanceId;
        try { await Until(async () => (await h.Store.GetWorkflowInstanceAsync(childId))!.Status == WorkflowStatus.Suspended); }
        catch (OperationCanceledException)
        {
            var childState = (await h.Store.GetWorkflowInstanceAsync(childId))!;
            Assert.Fail($"Child status {childState.Status}: {childState.LastError} / {childState.LastErrorStackTrace}");
        }
        await h.Ops.PauseAsync(id, "pause");
        h.Advance(11);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Client.RecoverAsync(childId)).Status);
        Assert.Equal(WorkflowStatus.Paused, (await h.Store.GetWorkflowInstanceAsync(id))!.Status);
        Assert.Single((await h.Store.GetPendingEventsAsync(id, 0, 100)).Events);
        Assert.Equal(0, h.Probe.Count);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Ops.ContinueAsync(id, "continue")).Status);
        Assert.Equal(1, h.Probe.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedDeadlineIsDueOnTimeAndAppliedOnContinue(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var id = (await h.Client.RunAsync(Waiting(), new Data())).InstanceId;
        var wait = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: id)));
        await h.Ops.PauseAsync(id, "pause");
        h.Advance(9);
        Assert.False((await h.Store.GetPendingEventsAsync(id, 0, 100)).IsDue(wait.WaitId));
        h.Advance(1);
        var page = await h.Store.GetPendingEventsAsync(id, 0, 100);
        Assert.Equal(wait.DeadlineUtc, page.StoreTimeUtc);
        Assert.True(page.IsDue(wait.WaitId));
        var arrival = await h.Store.AppendEventAsync(Event(id, "late", wait.WaitId));
        Assert.Equal(EventAppendStatus.Rejected, arrival.Status);
        Assert.Equal(EventRejection.WaitTimedOut, arrival.Rejection);
        var paused = (await h.Store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(WorkflowStatus.Paused, paused.Status);
        Assert.False(JsonSerializer.Deserialize<Data>(paused.WorkflowDataJson.InlineText)!.TimedOut);
        Assert.Equal(wait.DeadlineUtc, (await h.Store.GetWaitAsync(id, wait.WaitId))!.DeadlineUtc);
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Ops.ContinueAsync(id, "continue")).Status);
        Assert.True(JsonSerializer.Deserialize<Data>((await h.Store.GetWorkflowInstanceAsync(id))!.WorkflowDataJson.InlineText)!.TimedOut);
        Assert.Equal(WaitCloseReason.TimedOut, (await h.Store.GetWaitAsync(id, wait.WaitId))!.CloseReason);
    }

    private static WorkflowDefinition Waiting() => Workflow.Create<Data>("PausedWait").Step<Noop>()
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
    public sealed class Probe { public int Count; }
    public sealed class Noop : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class TimeoutMark : IAsyncActivity
    {
        [ActivityOutput] public bool TimedOut { get; set; } = true;
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Count(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) { probe.Count++; return Task.CompletedTask; } }
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
                _folder = Path.Combine(Path.GetTempPath(), "IxIFlow.PausedTimers", Guid.NewGuid().ToString("N"));
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
