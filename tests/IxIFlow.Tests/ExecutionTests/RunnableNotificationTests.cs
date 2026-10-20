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
            Assert.Equal(applied.Notice, replay.Notice);
            Assert.Equal(applied.MadeRunnable, replay.MadeRunnable);
            await Claim(due.InstanceId, "next-owner");
            Assert.Equal(applied.Notice, (await store.CommitWorkflowInstanceAsync(commit)).Notice);

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
            Assert.Equal(appended.Notice, (await store.AppendEventAsync(arrival)).Notice);
            Assert.Null((await store.AppendEventAsync(arrival with { EventId = "another" })).Notice);
            Assert.Null((await store.AppendEventAsync(arrival with { EventId = "refused", Key = "missing" })).Notice);
            await Claim(idle.InstanceId, "event-owner");
            Assert.Equal(appended.Notice, (await store.AppendEventAsync(arrival)).Notice);

            var delivered = await Idle();
            var delivery = new WorkflowEvent(delivered.InstanceId, "delivery", typeof(Approval).AssemblyQualifiedName!, "{}",
                Key: "approval", DeliveryIdentity: Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
            var accepted = await store.AppendEventAsync(delivery);
            Check(accepted.Notice, delivered.InstanceId);
            await store.DeleteWorkflowInstanceAsync(delivered.InstanceId);
            Assert.Equal(accepted.Notice, (await store.AppendEventAsync(delivery)).Notice);

            var admitted = New();
            var admission = new WorkflowAdmission("sender", "trigger", Guid.NewGuid().ToString("N"), new string('A', 64), "owner");
            var admissionCommit = CheckpointCommit.CreateAdmission(admitted);
            var admissionReceipt = await store.AdmitWorkflowInstanceAsync(admissionCommit, admission);
            Assert.NotNull(admissionReceipt.Notice);
            Assert.Equal(admitted.InstanceId, admissionReceipt.Notice.InstanceId);
            Assert.True(admissionReceipt.Notice.Claimable);
            await store.DeleteWorkflowInstanceAsync(admitted.InstanceId);
            Assert.Equal(admissionReceipt.Notice, (await store.AdmitWorkflowInstanceAsync(admissionCommit, admission)).Notice);
            Assert.Equal(admissionReceipt.Notice,
                (await store.GetWorkflowAdmissionAsync(admission.Sender, admission.Trigger, admission.Key))!.Notice);

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
            var startCommit = CheckpointCommit.Create(parent, owner.Epoch, 0,
                openedWaits: [new OpenedWait(wait, WaitKind.Child, "", "IxIFlow.ChildCompleted", null)],
                childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), wait)]);
            var started = await store.CommitWorkflowInstanceAsync(startCommit);
            var childNotice = Assert.Single(started.MadeRunnable);
            Assert.Equal(child.InstanceId, childNotice.InstanceId);
            Assert.Equal(set.Id, childNotice.DemandSetId);
            Assert.Equal(pin, childNotice.Pin);
            Assert.Null(childNotice.LastHostId);
            Assert.True(childNotice.Claimable);
            child = (await store.GetWorkflowInstanceAsync(child.InstanceId))!;
            owner = await Claim(child.InstanceId, "child");
            child.Status = WorkflowStatus.Completed;
            var completionCommit = CheckpointCommit.Create(child, owner.Epoch, child.Revision,
                parentNotice: new ParentNotice(parent.InstanceId, wait, "completed-" + child.InstanceId,
                    "IxIFlow.ChildCompleted", "{\"Status\":\"Completed\"}"));
            var completed = await store.CommitWorkflowInstanceAsync(completionCommit);
            Check(Assert.Single(completed.MadeRunnable), parent.InstanceId);
            // A replay samples a fresh store clock; its notice must replay unchanged.
            var completedReplay = await store.CommitWorkflowInstanceAsync(completionCommit);
            CommitAssert.Equal(completed with { Status = WorkflowCommitStatus.AlreadyApplied }, completedReplay);
            Assert.Equal(completed.Notice, completedReplay.Notice);
            await CommitAssert.SchedulingAsync(store, child.InstanceId, completedReplay);
            Assert.True(completedReplay.StoreTimeUtc >= completed.StoreTimeUtc);
            await store.DeleteWorkflowInstanceAsync(child.InstanceId);
            var startedReplay = await store.CommitWorkflowInstanceAsync(startCommit);
            CommitAssert.Equal(started with { Status = WorkflowCommitStatus.AlreadyApplied }, startedReplay);
            Assert.Equal(started.Notice, startedReplay.Notice);
            await CommitAssert.SchedulingAsync(store, parent.InstanceId, startedReplay);
            Assert.True(startedReplay.StoreTimeUtc >= started.StoreTimeUtc);
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
            if (block) Assert.Contains("System.TimeoutException", error);
            else Assert.Contains("System.IO.IOException", error);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AThrowingNotifierLogsRawExceptionsOnlyWithSensitiveDiagnosticsEnabled(bool optIn)
    {
        var capture = new LogCapture();
        var error = new IOException("sensitive notifier failure");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(capture));
        services.AddSingleton<IRunnableNotifier>(new CallbackNotifier((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw error;
            drained.TrySetResult();
            return Task.CompletedTask;
        }));
        services.AddIxIFlow(options => options.EnableSensitiveDiagnostics = optIn);
        await using var provider = services.BuildServiceProvider();
        var notifications = provider.GetRequiredService<RunnableNotifications>();
        var notice = new RunnableNotice("instance", "set", null, null, true, false);
        notifications.Publish(notice);
        notifications.Publish(notice);
        // The second call starts only after the first call's ordinary and sensitive logs finish.
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ordinary = Assert.Single(capture.Entries, entry => entry.Category == typeof(RunnableNotifications).FullName);
        Assert.Null(ordinary.Error);
        Assert.Contains("System.IO.IOException", ordinary.Text);
        Assert.DoesNotContain(error.Message, ordinary.Text);
        var sensitive = capture.Entries.Where(entry => entry.Category == InspectionRedactionPolicy.SensitiveDiagnosticsCategory);
        if (optIn) Assert.Same(error, Assert.Single(sensitive).Error);
        else Assert.Empty(sensitive);
    }

    [Fact]
    public async Task AppendReplaysNotifyAndCancellationNotifiesItsNewCommittedChange()
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
        Assert.Equal(eventInstance.InstanceId, (await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).InstanceId);
        await operations.DeliverAsync(eventInstance.InstanceId, "missing", new Approval(), "refused");
        var cancelled = await Idle();
        await operations.RequestCancellationAsync(cancelled.InstanceId, new CancellationReason { ReasonCode = "stop" }, CancellationToken.None);
        Assert.Equal(cancelled.InstanceId, (await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).InstanceId);
        await operations.RequestCancellationAsync(cancelled.InstanceId, new CancellationReason { ReasonCode = "stop" }, CancellationToken.None);
        Assert.Equal(3, recorder.Notices.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALostCommitAcknowledgementPublishesTheReplayedNotice(bool terminal)
    {
        var store = new LostCommitReply();
        var recorder = new Recorder();
        await using var services = Services(store, recorder);
        using var scope = services.CreateScope();
        var definition = terminal ? Workflow.Create<Data>("ReplayTerminal").Step<Noop>().Build() : Definition();
        var result = await scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>()
            .StartAsync(definition, new Data(), new WorkflowOptions(), CancellationToken.None);
        Assert.Equal(terminal ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Scheduled, result.Status);
        Assert.NotNull(store.Replay);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, store.Replay.Status);
        Assert.Equal(store.Applied!.Notice, store.Replay.Notice);
        Assert.Equal(store.Applied.Notice,
            await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(recorder.Notices);
    }

    [Fact]
    public async Task AdmissionLookupReplaysPublishTheOriginalNotice()
    {
        var store = new InMemoryWorkflowStateRepository();
        var recorder = new Recorder();
        await using var services = Services(store, recorder);
        var ingress = services.GetRequiredService<IWorkflowIngressClient>();
        var admission = new WorkflowAdmission("sender", "trigger", "key", new string('A', 64), "owner");
        var first = await ingress.AdmitAsync(Definition(), new Data(), admission);
        Assert.Equal(first.Notice, await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await store.DeleteWorkflowInstanceAsync(first.InstanceId);
        Assert.Equal(first, await ingress.AdmitAsync(Definition(), new Data(), admission));
        Assert.Equal(first.Notice, await recorder.Next.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
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
            Assert.Contains("System.TimeoutException", await logger.Errors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
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

    private sealed class LostCommitReply : FaultInjectingRepository
    {
        public WorkflowCommitResult? Applied;
        public WorkflowCommitResult? Replay;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.AlreadyApplied) Replay = result;
            if (result.Notice != null && Applied == null)
            {
                Applied = result;
                throw new IOException("Commit acknowledgement lost");
            }
            return result;
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

    private sealed class LogCapture : ILoggerProvider
    {
        public readonly ConcurrentQueue<(string Category, string Text, Exception? Error)> Entries = new();
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<(string Category, string Text, Exception? Error)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue((category, formatter(state, exception), exception));
        }
    }

    private sealed class ErrorLogger : ILogger<RunnableNotifications>
    {
        public readonly Channel<string> Errors = Channel.CreateUnbounded<string>();
        public int Dropped;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref Dropped);
            if (logLevel == LogLevel.Error) Errors.Writer.TryWrite(formatter(state, exception));
        }
    }
}
