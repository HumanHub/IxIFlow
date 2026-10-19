using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
[Trait("Spec", "C-04")]
[Trait("Spec", "C-13")]
public sealed class RunnableNotificationTests : IClassFixture<SqliteTestDatabase<RunnableNotificationTests>>
{
    [Fact]
    public Task MemoryReportsCommittedChanges() => StoreContract(new InMemoryWorkflowStateRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteReportsCommittedChanges()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<RunnableNotificationTests>.ConnectionString);
        await StoreContract(store);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerReportsCommittedChanges() => StoreContract(new SqlWorkflowStateRepository(
        SqlTestDatabase<RunnableNotificationTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlReportsCommittedChanges() => StoreContract(new PostgreSqlWorkflowStateRepository(
        PostgreSqlTestDatabase<RunnableNotificationTests>.ConnectionString));

    private static async Task StoreContract(IWorkflowStateRepository store)
    {
        var host = Guid.NewGuid();
        var pin = new WorkflowDemand("host", host.ToString("D"), DateTime.UtcNow.Date.AddDays(2), true);
        WorkflowDemand[] demands = [new("zone", "eu"), pin];
        var set = DemandSet.Create(demands);
        var query = new ClaimQuery(host, new Dictionary<string, string> { ["zone"] = "eu", ["host"] = host.ToString("D") });
        var ids = new List<string>();
        WorkflowInstance New()
        {
            var instance = new WorkflowInstance
            {
                InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "notices", Status = WorkflowStatus.Running,
                ExecutionStateJson = JsonSerializer.Serialize(new ExecutionCheckpoint { StructureHash = "notices", Demands = demands.ToList() })
            };
            ids.Add(instance.InstanceId);
            return instance;
        }
        void Check(RunnableNotice? notice, string id)
        {
            Assert.NotNull(notice);
            Assert.True(notice.Claimable);
            Assert.Equal(id, notice.InstanceId);
            Assert.Equal(set.Id, notice.DemandSetId);
            Assert.Equal(pin, notice.Pin);
            Assert.Equal(host, notice.LastHostId);
        }
        async Task<ExecutionLeaseGrant> Claim(string id, string token) => Assert.IsType<ExecutionLeaseGrant>(
            await store.TryAcquireExecutionLeaseAsync(id, token, TimeSpan.FromMinutes(5), query));
        async Task<WorkflowInstance> Idle()
        {
            var instance = New();
            var grant = await Claim(instance.InstanceId, "idle");
            instance.Status = WorkflowStatus.Suspended;
            var saved = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, 0,
                openedWaits: [new OpenedWait("approval-" + instance.InstanceId, WaitKind.Event, "approval", typeof(Approval).AssemblyQualifiedName!, null)]));
            Assert.False(saved.Claimable);
            Assert.True(saved.Notice?.Idle);
            return (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        }
        try
        {
            var due = New();
            var owner = await Claim(due.InstanceId, "due");
            var commit = CheckpointCommit.Create(due, owner.Epoch, 0, parks: true, wakeAfter: TimeSpan.Zero);
            var applied = await store.CommitWorkflowInstanceAsync(commit);
            Assert.True(applied.Claimable);
            Check(applied.Notice, due.InstanceId);
            var replay = await store.CommitWorkflowInstanceAsync(commit);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, replay.Status);
            Assert.Null(replay.Notice);
            Assert.Empty(replay.MadeRunnable);

            var parked = New();
            owner = await Claim(parked.InstanceId, "parked");
            var park = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parked, owner.Epoch, 0,
                parks: true, wakeAfter: TimeSpan.FromHours(1)));
            Assert.False(park.Claimable);
            Assert.Null(park.Notice);

            var idle = await Idle();
            var arrival = new WorkflowEvent(idle.InstanceId, "event", typeof(Approval).AssemblyQualifiedName!, "{}", Key: "approval");
            var appended = await store.AppendEventAsync(arrival);
            Assert.True(appended.Claimable);
            Check(appended.Notice, idle.InstanceId);
            Assert.Null((await store.AppendEventAsync(arrival)).Notice);
            Assert.Null((await store.AppendEventAsync(arrival with { EventId = "another" })).Notice);
            Assert.Null((await store.AppendEventAsync(arrival with { EventId = "refused", Key = "missing" })).Notice);

            var cancelled = await Idle();
            var reason = new CancellationReason { ReasonCode = "stop" };
            var cancellation = await store.RequestCancellationAsync(cancelled.InstanceId, reason);
            Assert.True(cancellation.Accepted);
            Check(cancellation.Notice, cancelled.InstanceId);
            Assert.Null((await store.RequestCancellationAsync(cancelled.InstanceId, reason)).Notice);
            Assert.False((await store.RequestCancellationAsync("missing-" + Guid.NewGuid(), reason)).Accepted);

            var running = New();
            owner = await Claim(running.InstanceId, "running");
            var saved = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(running, owner.Epoch, 0));
            Assert.False(saved.Claimable);
            Assert.Null(saved.Notice);
            Assert.Null((await store.RequestCancellationAsync(running.InstanceId, reason)).Notice);
            Assert.False((await store.ReleaseExecutionLeaseAsync(running.InstanceId, "wrong")).Released);
            var released = await store.ReleaseExecutionLeaseAsync(running.InstanceId, "running");
            Assert.True(released.Released);
            Check(released.Notice, running.InstanceId);
            Assert.Null((await store.ReleaseExecutionLeaseAsync(running.InstanceId, "running")).Notice);

            var terminal = await Idle();
            await store.RequestCancellationAsync(terminal.InstanceId, reason);
            owner = await Claim(terminal.InstanceId, "terminal");
            terminal.Status = WorkflowStatus.Completed;
            var ended = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(terminal, owner.Epoch, terminal.Revision));
            Assert.False(ended.Claimable);
            Assert.True(ended.Notice?.Idle);
            Assert.False((await store.RequestCancellationAsync(terminal.InstanceId, reason)).Accepted);

            var parent = New();
            var child = New();
            var wait = "child-" + child.InstanceId;
            child.ParentInstanceId = parent.InstanceId;
            child.ParentWaitId = wait;
            owner = await Claim(parent.InstanceId, "parent");
            parent.Status = WorkflowStatus.Suspended;
            var started = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, owner.Epoch, 0,
                openedWaits: [new OpenedWait(wait, WaitKind.Child, "", "IxIFlow.ChildCompleted", null)],
                childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), wait)]));
            var childNotice = Assert.Single(started.MadeRunnable);
            Assert.Equal(child.InstanceId, childNotice.InstanceId);
            Assert.Equal(set.Id, childNotice.DemandSetId);
            Assert.Equal(pin, childNotice.Pin);
            Assert.Null(childNotice.LastHostId);
            Assert.True(childNotice.Claimable);
            child = (await store.GetWorkflowInstanceAsync(child.InstanceId))!;
            owner = await Claim(child.InstanceId, "child");
            child.Status = WorkflowStatus.Completed;
            var completed = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(child, owner.Epoch, child.Revision,
                parentNotice: new ParentNotice(parent.InstanceId, wait, "completed-" + child.InstanceId,
                    "IxIFlow.ChildCompleted", "{\"Status\":\"Completed\"}")));
            Check(Assert.Single(completed.MadeRunnable), parent.InstanceId);
        }
        finally
        {
            foreach (var id in ids) await store.DeleteWorkflowInstanceAsync(id);
        }
    }

    [Fact]
    public async Task NoticesFollowTheCommitAndRefusedWritesProduceNone()
    {
        var store = new HeldCommit();
        var recorder = new Recorder();
        await using var services = Services(store, recorder);
        using var scope = services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
        var id = Guid.NewGuid().ToString("N");
        var run = operations.StartAsync(Definition(), new Data(), new WorkflowOptions { InstanceId = id }, CancellationToken.None);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(recorder.Notices);
        Assert.Null(await store.GetWorkflowInstanceAsync(id));
        store.Proceed.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var notice = await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, notice.InstanceId);
        Assert.True(notice.Claimable);
        Assert.NotNull(await store.GetWorkflowInstanceAsync(id));
        Assert.Single(recorder.Notices);

        await operations.DeliverAsync("absent", "approval", new Approval(), "missing");
        Assert.Single(recorder.Notices);

        // The next admission is refused before any transaction writes its instance.
        store.Refuse = true;
        await operations.StartAsync(Definition(), new Data(), new WorkflowOptions { InstanceId = Guid.NewGuid().ToString("N") }, CancellationToken.None);
        Assert.Single(recorder.Notices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AThrowingOrBlockingNotifierCannotHoldTheCaller(bool block)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new ErrorLogger();
        var notifier = new CallbackNotifier((_, _) =>
        {
            entered.TrySetResult();
            if (block) release.Wait();
            throw new IOException("notifier unavailable");
        });
        await using var services = Services(new InMemoryWorkflowStateRepository(), notifier, logger);
        try
        {
            using var scope = services.CreateScope();
            var run = scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>()
                .StartAsync(Definition(), new Data(), new WorkflowOptions(), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowExecutionStatus.Scheduled, result.Status);
            Assert.NotNull(await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(result.InstanceId));
            var error = await logger.Errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (block) Assert.IsType<TimeoutException>(error);
            else Assert.IsType<IOException>(error);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task AppendAndCancellationNotifyOnlyTheirNewCommittedChanges()
    {
        var store = new InMemoryWorkflowStateRepository();
        var recorder = new Recorder();
        await using var services = Services(store, recorder);
        using var scope = services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
        async Task<WorkflowInstance> Idle()
        {
            var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), Status = WorkflowStatus.Suspended,
                ExecutionStateJson = StoreTestSupport.CheckpointJson };
            var owner = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
            var result = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, owner.Epoch, 0,
                openedWaits: [new OpenedWait(instance.InstanceId + "-approval", WaitKind.Event, "approval", typeof(Approval).AssemblyQualifiedName!, null)]));
            instance.Revision = result.Revision;
            return instance;
        }
        var eventInstance = await Idle();
        await operations.DeliverAsync(eventInstance.InstanceId, "approval", new Approval(), "first");
        Assert.Equal(eventInstance.InstanceId, (await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).InstanceId);
        await operations.DeliverAsync(eventInstance.InstanceId, "approval", new Approval(), "first");
        await operations.DeliverAsync(eventInstance.InstanceId, "missing", new Approval(), "refused");
        var cancelled = await Idle();
        await operations.RequestCancellationAsync(cancelled.InstanceId, new CancellationReason { ReasonCode = "stop" }, CancellationToken.None);
        Assert.Equal(cancelled.InstanceId, (await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).InstanceId);
        await operations.RequestCancellationAsync(cancelled.InstanceId, new CancellationReason { ReasonCode = "stop" }, CancellationToken.None);
        Assert.Equal(2, recorder.Notices.Count);
    }

    [Fact]
    public async Task AnInterruptedRunNotifiesAfterItsLeaseIsReleased()
    {
        var store = new FaultInjectingRepository { FailCommit = instance => instance.Status == WorkflowStatus.Suspended };
        var recorder = new Recorder();
        await using var services = Services(store, recorder);
        using var scope = services.CreateScope();
        var definition = Workflow.Create<Data>("ReleaseNotice").Step<Noop>().WaitFor<Approval>("approval").Build();
        var id = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<WorkflowEngineException>(() => scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>()
            .StartAsync(definition, new Data(), new WorkflowOptions { InstanceId = id }, CancellationToken.None));
        var notice = await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, notice.InstanceId);
        Assert.True(notice.Claimable);
        Assert.Equal(WorkflowStatus.Running, (await store.GetWorkflowInstanceSummaryAsync(id))!.Status);
        Assert.Single(recorder.Notices);
    }

    [Fact]
    public async Task IdleAndTerminalCommitsNotifyAwaiters()
    {
        var recorder = new Recorder();
        await using var services = Services(new InMemoryWorkflowStateRepository(), recorder);
        using var scope = services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
        var idle = await operations.StartAsync(Workflow.Create<Data>("IdleNotice").Step<Noop>().WaitFor<Approval>("approval").Build(),
            new Data(), new WorkflowOptions(), CancellationToken.None);
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var changed = await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(idle.InstanceId, changed.InstanceId);
        Assert.True(changed.Idle);
        Assert.False(changed.Claimable);
        var terminal = await operations.StartAsync(Workflow.Create<Data>("TerminalNotice").Step<Noop>().Build(),
            new Data(), new WorkflowOptions(), CancellationToken.None);
        Assert.Equal(WorkflowExecutionStatus.Success, terminal.Status);
        changed = await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(terminal.InstanceId, changed.InstanceId);
        Assert.True(changed.Idle);
        Assert.False(changed.Claimable);
        Assert.Equal(2, recorder.Notices.Count);
    }

    [Fact]
    public async Task ABlockedNotifierHasBoundedBacklogAndOneOutstandingCall()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var logger = new ErrorLogger();
        using var notifications = new RunnableNotifications(new CallbackNotifier((_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            return release.Task;
        }), logger);
        var notice = new RunnableNotice("instance", "set", null, null, true, false);
        try
        {
            notifications.Publish(notice);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                for (var i = 0; i < 1000; i++) notifications.Publish(notice);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(logger.Dropped > 0);
            Assert.IsType<TimeoutException>(await logger.Errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task AwaitHooksTrackAllLocalCallersAcrossChangeSignals()
    {
        await using var services = Services(new InMemoryWorkflowStateRepository(), new NullRunnableNotifier());
        var host = services.GetRequiredService<WorkflowHost>();
        var changes = new List<(string, bool)>();
        host.AwaitingChanged += (id, watching) => changes.Add((id, watching));
        var first = host.WatchSettled("instance", out var signal);
        var second = host.WatchSettled("instance", out var same);
        Assert.Same(signal, same);
        Assert.Equal([("instance", true)], changes);
        host.NotifyCompletion("instance");
        await signal.WaitAsync(TimeSpan.FromSeconds(5));
        var next = host.WatchSettled("instance", out var nextSignal);
        first.Dispose();
        second.Dispose();
        Assert.Single(host.AwaitedInstances);
        next.Dispose();
        next.Dispose();
        Assert.Empty(host.AwaitedInstances);
        Assert.Equal([("instance", true), ("instance", false)], changes);
        Assert.False(nextSignal.IsCompleted);
    }

    [Fact]
    public async Task AChangeCompletesALocalAwaitWithoutAdvancingThePollClock()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), Status = WorkflowStatus.Running,
            ExecutionStateJson = StoreTestSupport.CheckpointJson };
        await store.SeedAsync(instance);
        await using var services = Services(store, new NullRunnableNotifier(), time: time);
        var host = services.GetRequiredService<WorkflowHost>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AwaitingChanged += (_, watching) => { if (watching) started.TrySetResult(); else stopped.TrySetResult(); };
        var waiting = services.GetRequiredService<IWorkflowClient>().AwaitAsync(instance.InstanceId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted);
        instance.Status = WorkflowStatus.Completed;
        await store.SeedAsync(instance);
        host.NotifyCompletion(instance.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(host.AwaitedInstances);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, IRunnableNotifier notifier,
        ErrorLogger? logger = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddSingleton(notifier);
        if (logger != null) services.AddSingleton<ILogger<RunnableNotifications>>(logger);
        if (time != null) services.AddSingleton(time);
        services.AddIxIFlow();
        services.AddTransient<Noop>();
        return services.BuildServiceProvider();
    }

    private static WorkflowDefinition Definition() => Workflow.Create<Data>("Notice").Demand("remote")
        .Step<Noop>().WaitFor<Approval>("approval").Build();
    public sealed class Data;
    public sealed class Approval;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HeldCommit : FaultInjectingRepository
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Refuse;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            Entered.TrySetResult();
            await Proceed.Task;
            return Refuse ? new(WorkflowCommitStatus.Conflict, 0) : await base.CommitWorkflowInstanceAsync(commit);
        }
    }

    private sealed class Recorder : IRunnableNotifier
    {
        public readonly ConcurrentQueue<RunnableNotice> Notices = new();
        public readonly Channel<RunnableNotice> Next = Channel.CreateUnbounded<RunnableNotice>();
        public Task NotifyAsync(RunnableNotice notice, CancellationToken cancellationToken)
        {
            Notices.Enqueue(notice);
            Next.Writer.TryWrite(notice);
            return Task.CompletedTask;
        }
    }

    private sealed class CallbackNotifier(Func<RunnableNotice, CancellationToken, Task> notify) : IRunnableNotifier
    {
        public Task NotifyAsync(RunnableNotice notice, CancellationToken cancellationToken) => notify(notice, cancellationToken);
    }

    private sealed class ErrorLogger : ILogger<RunnableNotifications>
    {
        public readonly Channel<Exception> Errors = Channel.CreateUnbounded<Exception>();
        public int Dropped;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref Dropped);
            if (exception != null) Errors.Writer.TryWrite(exception);
        }
    }
}
