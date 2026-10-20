using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Every call runs through the in-process host: at most the configured number of slots run at
/// once, work for one instance queues without holding a slot, a start returns once it is saved,
/// cancellation needs no slot, and a stopping host drains (spec B-02, B-03, B-11, B-12).
/// </summary>
[Trait("Feature", "host")]
public sealed class LocalHostTests : IClassFixture<SqliteTestDatabase<LocalHostTests>>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait("Spec", "B-02")]
    public async Task SlotsLimitHowManyInstancesRunAtOnce()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 2);
        var gate = Gate(services);

        var runs = Enumerable.Range(0, 4)
            .Select(index => Client(services).RunAsync(BlockingWorkflow($"Slots{index}"), new HostData()))
            .ToArray();
        await gate.WaitForRunningAsync(2);
        await Task.Delay(200);
        Assert.Equal(2, gate.Running);

        gate.Release();
        var results = await Task.WhenAll(runs).WaitAsync(Patience);

        Assert.All(results, result => Assert.Equal(WorkflowExecutionStatus.Success, result.Status));
        Assert.Equal(2, gate.MaxRunning);
    }

    [Fact]
    [Trait("Spec", "B-02")]
    public async Task WorkForABusyInstanceWaitsWithoutHoldingASlot()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 2);
        var gate = Gate(services);
        var busy = Client(services).RunAsync(BlockingWorkflow("BusyInstance"), new HostData(),
            new WorkflowOptions { InstanceId = "busy-instance" });
        await gate.WaitForRunningAsync(1);

        // Queued behind the running instance; the second slot stays free for other work.
        var queued = Client(services).RecoverAsync("busy-instance");
        var other = await Client(services).RunAsync(QuickWorkflow("OtherInstance"), new HostData())
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Success, other.Status);
        Assert.False(queued.IsCompleted);
        gate.Release();
        Assert.Equal(WorkflowExecutionStatus.Success, (await busy.WaitAsync(Patience)).Status);
        await queued.WaitAsync(Patience);
    }

    [Fact]
    [Trait("Spec", "B-02")]
    public async Task WorkForAStartedInstanceQueuesBehindItWithoutASlot()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 2);
        var gate = Gate(services);
        var instanceId = await Client(services).StartAsync(BlockingWorkflow("GeneratedId"), new HostData())
            .WaitAsync(Patience);
        await gate.WaitForRunningAsync(1);

        var queued = Client(services).RecoverAsync(instanceId);
        var other = await Client(services).RunAsync(QuickWorkflow("BesideGeneratedId"), new HostData())
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Success, other.Status);
        Assert.False(queued.IsCompleted);
        gate.Release();
        // Behind the finished run there is nothing left to recover.
        Assert.Equal(WorkflowExecutionStatus.Success, (await queued.WaitAsync(Patience)).Status);
    }

    [Fact]
    [Trait("Spec", "B-02")]
    public async Task QueuedCallerCanStopWaiting()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 2);
        var gate = Gate(services);
        var busy = Client(services).RunAsync(BlockingWorkflow("CallerGivesUp"), new HostData(),
            new WorkflowOptions { InstanceId = "caller-gives-up" });
        await gate.WaitForRunningAsync(1);

        using var giveUp = new CancellationTokenSource();
        var queued = Client(services).RecoverAsync("caller-gives-up", giveUp.Token);
        giveUp.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Patience));
        gate.Release();
        Assert.Equal(WorkflowExecutionStatus.Success, (await busy.WaitAsync(Patience)).Status);
    }

    [Fact]
    [Trait("Spec", "B-02")]
    public async Task AnActivityCallingTheClientRunsInlineInItsSlot()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 1);
        var definition = Workflow.Create<HostData>("CallsTheClient")
            .Step<ClientCallingActivity>()
            .Build();

        var result = await Client(services).RunAsync(definition, new HostData()).WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(2, Probe(services).Count("quick"));
    }

    [Fact]
    [Trait("Spec", "B-02")]
    public async Task WorkLeftBehindByAFinishedRunQueuesLikeAnyOtherCall()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 1);
        var probe = services.GetRequiredService<BackgroundProbe>();
        var definition = Workflow.Create<HostData>("LeavesWorkBehind")
            .Step<BackgroundCallingActivity>()
            .Build();

        var result = await Client(services).RunAsync(definition, new HostData()).WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(probe.ParentScope!.Disposed);
        var busy = Client(services).RunAsync(BlockingWorkflow("HoldsTheEscapedCallsSlot"), new HostData());
        await Gate(services).WaitForRunningAsync(1);
        probe.Later.SetResult();
        await probe.Calling.Task.WaitAsync(Patience);
        await Task.Delay(100);
        Assert.False(probe.Result.Task.IsCompleted);
        Gate(services).Release();
        Assert.Equal(WorkflowExecutionStatus.Success, (await busy.WaitAsync(Patience)).Status);
        var background = await probe.Result.Task.WaitAsync(Patience);

        // The run's context went with the background task, but the run is over: the call takes its
        // own slot and scope instead of the disposed ones.
        var scopes = Probe(services).QuickScopes.ToArray();
        Assert.Equal(2, scopes.Length);
        Assert.All(scopes, scope => Assert.NotSame(probe.ParentScope, scope));
        Assert.Same(scopes[0], scopes[1]);
        Assert.All(scopes, scope => Assert.True(scope.Disposed));
        Assert.Equal(WorkflowExecutionStatus.Success, background.Status);
        Assert.Equal(2, Probe(services).Count("quick"));
    }

    [Fact]
    [Trait("Spec", "B-02")]
    public async Task AnInlineCallStillRunningWhenItsRunEndsKeepsItsScope()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 1);
        var probe = services.GetRequiredService<BackgroundProbe>();
        var definition = Workflow.Create<HostData>("HandsOffInlineWork")
            .Step<HandOffActivity>()
            .Build();

        var result = await Client(services).RunAsync(definition, new HostData()).WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        probe.Later.SetResult();

        // The inline run started before its caller's run ended; it finishes on a live scope.
        var inner = await probe.Result.Task.WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, inner.Status);
        Assert.Equal(1, Probe(services).Count("quick"));
    }

    [Fact]
    [Trait("Spec", "B-02")]
    public async Task AnInlineCallStillRunningWhenItsRunEndsKeepsItsSlot()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 1);
        var probe = services.GetRequiredService<BackgroundProbe>();
        var definition = Workflow.Create<HostData>("HandsOffAndHoldsTheSlot")
            .Step<HandOffActivity>()
            .Build();
        var result = await Client(services).RunAsync(definition, new HostData()).WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);

        // The only slot is still in use by the inline run, so new work waits for it.
        var next = Client(services).RunAsync(QuickWorkflow("WaitsForTheSlot"), new HostData());
        await Task.Delay(300);
        Assert.False(next.IsCompleted);

        probe.Later.SetResult();
        Assert.Equal(WorkflowExecutionStatus.Success, (await probe.Result.Task.WaitAsync(Patience)).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await next.WaitAsync(Patience)).Status);
    }

    [Fact]
    [Trait("Spec", "B-12")]
    public async Task DisposingTheHostCancelsQueuedCallers()
    {
        var services = Services(new InMemoryWorkflowStateRepository(), slots: 1);
        var gate = Gate(services);
        _ = Client(services).RunAsync(BlockingWorkflow("DisposedWhileBusy"), new HostData(),
            new WorkflowOptions { InstanceId = "disposed-while-busy" });
        await gate.WaitForRunningAsync(1);
        var queued = Client(services).RecoverAsync("disposed-while-busy");

        await services.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Patience));
    }

    [Fact]
    [Trait("Spec", "B-03")]
    [Trait("Spec", "D-01")]
    public async Task StartReturnsOnceSavedAndAwaitReturnsWhenIdle()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, slots: 4);
        var gate = Gate(services);

        var instanceId = await Client(services).StartAsync(BlockingWorkflow("StartAndAwait"), new HostData())
            .WaitAsync(Patience);

        Assert.Equal(WorkflowStatus.Running, (await repository.GetWorkflowInstanceAsync(instanceId))!.Status);
        var awaiting = Client(services).AwaitAsync(instanceId, WorkflowAwaitCondition.Finished);
        Assert.False(awaiting.IsCompleted);
        gate.Release();
        var finished = await awaiting.WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
    }

    [Fact]
    [Trait("Spec", "B-03")]
    [Trait("Spec", "D-01")]
    public async Task AwaitFinishedWaitsThroughASuspension()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), slots: 4);
        var definition = Workflow.Create<HostData>("AwaitThroughWait")
            .Step<QuickActivity>()
            .WaitFor<Approval>("approval")
            .Step<QuickActivity>()
            .Build();
        var started = await Client(services).RunAsync(definition, new HostData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        Assert.Equal(WorkflowExecutionStatus.Suspended,
            (await Client(services).AwaitAsync(started.InstanceId)).Status);
        var finished = Client(services).AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished);
        await Task.Delay(100);
        Assert.False(finished.IsCompleted);
        await Client(services).ResumeAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, (await finished.WaitAsync(Patience)).Status);
    }

    [Fact]
    [Trait("Spec", "B-03")]
    [Trait("Spec", "D-01")]
    public async Task CancellationIsSavedWhileEverySlotIsBusy()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, slots: 1);
        var gate = Gate(services);
        var run = Client(services).RunAsync(BlockingWorkflow("CancelWhileBusy"), new HostData(),
            new WorkflowOptions { InstanceId = "cancel-while-busy" });
        await gate.WaitForRunningAsync(1);

        var requested = await Client(services).CancelAsync("cancel-while-busy",
            new CancellationReason { ReasonCode = "operator" }).WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Running, requested.Status);
        Assert.NotNull(await repository.GetCancellationRequestAsync("cancel-while-busy"));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await run.WaitAsync(Patience)).Status);
    }

    [Fact]
    [Trait("Spec", "B-12")]
    public async Task StoppingHostLetsRunningActivitiesFinishThenStops()
    {
        // The clock stands still, so the watch's 250 ms timer cannot fire while the host drains.
        var time = new FakeTimeProvider();
        var repository = new CountingStore(time);
        var definition = Workflow.Create<HostData>("DrainedInstance")
            .Step<BlockingActivity>()
            .Step<QuickActivity>()
            .Build();
        string instanceId;
        using (var services = Services(repository, slots: 1, time))
        {
            var gate = Gate(services);
            var run = Client(services).RunAsync(definition, new HostData());
            await gate.WaitForRunningAsync(1);
            var queued = Client(services).RunAsync(QuickWorkflow("NeverStarted"), new HostData());

            var stopping = services.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Patience));
            // While draining, the run waits for its activity instead of spinning on the store.
            var readsBefore = repository.CancellationReads;
            await Task.Delay(300);
            Assert.True(repository.CancellationReads - readsBefore <= 1,
                $"{repository.CancellationReads - readsBefore} cancellation reads while draining");
            gate.Release();
            await stopping.WaitAsync(Patience);

            var drained = await run.WaitAsync(Patience);
            Assert.Equal(WorkflowExecutionStatus.Running, drained.Status);
            instanceId = drained.InstanceId;
            // The drain started no new step.
            Assert.Equal(0, Probe(services).Count("quick"));
        }

        // The finished activity was committed and the instance continues on another host.
        var saved = (await repository.GetWorkflowInstanceAsync(instanceId))!;
        Assert.Equal(WorkflowStatus.Running, saved.Status);
        using var next = Services(repository, slots: 1, time);
        await next.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        Gate(next).Release();
        var recovered = await Client(next).RecoverAsync(instanceId).WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal(0, Probe(next).Count("blocking-started"));
        Assert.Equal(1, Probe(next).Count("quick"));
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public async Task OneWatchReadsCancellationForAllRunningInstances()
    {
        var time = new TimerRecordingClock();
        var store = new CountingStore(time);
        using var services = Services(store, slots: 8, time);
        var gate = Gate(services);
        var runs = Enumerable.Range(0, 5)
            .Select(index => Client(services).RunAsync(BlockingWorkflow($"Watched{index}"), new HostData()))
            .ToArray();
        await gate.WaitForRunningAsync(5);

        // One second in steps of the watch's 250 ms interval. The watch sets its timer after each read, so
        // the count starts once it waits for its next read, and each step waits until the read it brought
        // is done and the timer set again.
        var interval = TimeSpan.FromMilliseconds(250);
        await time.WaitForTimerDueAtAsync(time.GetUtcNow() + interval);
        Assert.True(store.BatchCancellationReads >= 1);
        var singleBefore = store.CancellationReads;
        var batchBefore = store.BatchCancellationReads;
        for (var step = 1; step <= 4; step++)
        {
            var timersBefore = time.Armed;
            time.Advance(interval);
            await time.WaitForTimerDueAtAsync(time.GetUtcNow() + interval, timersBefore, Patience);
        }
        var single = store.CancellationReads - singleBefore;
        var batches = store.BatchCancellationReads - batchBefore;

        // Five leases polling on their own would read 20 times in that second.
        Assert.Equal(0, single);
        Assert.Equal(4, batches);
        Assert.Equal(5, store.LargestBatch);
        gate.Release();
        await Task.WhenAll(runs).WaitAsync(Patience);
    }

    [Fact]
    [Trait("Spec", "B-10")]
    public Task MemoryStoreReadsCancellationRequestsInOneCall() =>
        VerifyBatchCancellationReads(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "B-10")]
    public Task SqlStoreReadsCancellationRequestsInOneCall() =>
        VerifyBatchCancellationReads(new SqlWorkflowStateRepository(SqlTestDatabase<LocalHostTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "B-10")]
    public Task PostgreSqlStoreReadsCancellationRequestsInOneCall() =>
        VerifyBatchCancellationReads(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<LocalHostTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "B-10")]
    public async Task SqliteStoreReadsCancellationRequestsInOneCall()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<LocalHostTests>.ConnectionString);
        await VerifyBatchCancellationReads(repository);
    }

    private static async Task VerifyBatchCancellationReads(IWorkflowStateRepository repository)
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        try
        {
            foreach (var id in ids)
                await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(new WorkflowInstance
                { ExecutionStateJson = StoreTestSupport.CheckpointJson,
                    InstanceId = id, WorkflowName = "BatchCancellation", Status = WorkflowStatus.Running,
                    WorkflowDataJson = "{}"
                }, (await StoreTestSupport.LeaseAsync(repository, id)).Epoch, 0));
            Assert.True((await repository.RequestCancellationAsync(ids[0], new CancellationReason { ReasonCode = "a" })).Accepted);
            Assert.True((await repository.RequestCancellationAsync(ids[2], new CancellationReason { ReasonCode = "c" })).Accepted);

            var requests = await repository.GetCancellationRequestsAsync([.. ids, "missing-instance"]);

            Assert.Equal(2, requests.Count);
            Assert.Equal("a", requests[ids[0]].ReasonCode);
            Assert.Equal("c", requests[ids[2]].ReasonCode);
        }
        finally
        {
            foreach (var id in ids)
                await repository.DeleteWorkflowInstanceAsync(id);
        }
    }

    [Fact]
    [Trait("Spec", "B-11")]
    public async Task OneInstanceWaitingOnItsCommitDoesNotHoldBackAnother()
    {
        var store = new HoldingStore { HeldInstanceName = "HeldCommits" };
        using var services = Services(store, slots: 4);

        var held = Client(services).RunAsync(QuickWorkflow("HeldCommits"), new HostData());
        await store.Holding.Task.WaitAsync(Patience);
        var other = await Client(services).RunAsync(QuickWorkflow("FreeCommits"), new HostData())
            .WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, other.Status);
        Assert.False(held.IsCompleted);

        store.Release();
        Assert.Equal(WorkflowExecutionStatus.Success, (await held.WaitAsync(Patience)).Status);
        // One instance's commits are applied in revision order.
        Assert.Equal(store.Revisions("HeldCommits").Order(), store.Revisions("HeldCommits"));
    }

    private static WorkflowDefinition BlockingWorkflow(string name) =>
        Workflow.Create<HostData>(name).Step<BlockingActivity>().Build();

    private static WorkflowDefinition QuickWorkflow(string name) =>
        Workflow.Create<HostData>(name).Step<QuickActivity>().Step<QuickActivity>().Build();

    private static ServiceProvider Services(IWorkflowStateRepository repository, int slots,
        TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repository);
        if (time != null)
            services.AddSingleton(time);
        services.AddIxIFlow(options => options.Slots = slots);
        services.AddSingleton<ActivityGate>();
        services.AddSingleton<HostProbe>();
        services.AddScoped<RunScope>();
        services.AddTransient<BlockingActivity>();
        services.AddTransient<QuickActivity>();
        services.AddTransient<ClientCallingActivity>();
        services.AddTransient<BackgroundCallingActivity>();
        services.AddTransient<HandOffActivity>();
        services.AddTransient<HeldInlineActivity>();
        services.AddSingleton<BackgroundProbe>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Client(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    private static ActivityGate Gate(IServiceProvider services) => services.GetRequiredService<ActivityGate>();

    private static HostProbe Probe(IServiceProvider services) => services.GetRequiredService<HostProbe>();

    public sealed class HostData;

    public sealed class Approval;

    /// <summary>Blocks activities until released, and counts how many run at once.</summary>
    public sealed class ActivityGate
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _maxRunning;

        public int Running => Volatile.Read(ref _running);
        public int MaxRunning => Volatile.Read(ref _maxRunning);

        public void Release() => _released.TrySetResult();

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref _running);
            int seen;
            while (running > (seen = Volatile.Read(ref _maxRunning)) &&
                   Interlocked.CompareExchange(ref _maxRunning, running, seen) != seen)
            {
            }
            try
            {
                await _released.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public async Task WaitForRunningAsync(int count)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (Running < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Only {Running} of {count} activities started");
                await Task.Delay(10);
            }
        }
    }

    public sealed class HostProbe
    {
        private readonly ConcurrentDictionary<string, int> _counts = new();
        public ConcurrentQueue<RunScope> QuickScopes { get; } = new();
        public void Record(string name) => _counts.AddOrUpdate(name, 1, (_, count) => count + 1);
        public int Count(string name) => _counts.GetValueOrDefault(name);
    }

    public sealed class BlockingActivity(ActivityGate gate, HostProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Record("blocking-started");
            await gate.EnterAsync(cancellationToken);
            probe.Record("blocking-finished");
        }
    }

    /// <summary>Runs another workflow through the public client from inside its own slot.</summary>
    public sealed class ClientCallingActivity(IWorkflowClient client) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            var result = await client.RunAsync(QuickWorkflow("CalledFromAnActivity"), new HostData(),
                cancellationToken: cancellationToken);
            if (result.Status != WorkflowExecutionStatus.Success)
                throw new InvalidOperationException(result.ErrorMessage);
        }
    }

    public sealed class BackgroundProbe
    {
        public TaskCompletionSource InnerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Later { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorkflowExecutionResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RunScope? ParentScope { get; set; }
        public TaskCompletionSource Calling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Leaves a task behind that calls the client after the run has finished.</summary>
    internal sealed class BackgroundCallingActivity(RunScope scope, IWorkflowClient client,
        BackgroundProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Assert.False(scope.Disposed);
            probe.ParentScope = scope;
            _ = Task.Run(async () =>
            {
                try
                {
                    await probe.Later.Task;
                    probe.Calling.SetResult();
                    probe.Result.SetResult(await client.RunAsync(QuickWorkflow("CalledAfterTheRun"),
                        new HostData()));
                }
                catch (Exception error)
                {
                    probe.Result.SetException(error);
                }
            });
            return Task.CompletedTask;
        }
    }

    /// <summary>Starts an inline run that is still going when this activity's run ends.</summary>
    public sealed class HandOffActivity(IWorkflowClient client, BackgroundProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    probe.Result.SetResult(await client.RunAsync(Workflow.Create<HostData>("HeldInline")
                        .Step<HeldInlineActivity>()
                        .Step<QuickActivity>()
                        .Build(), new HostData()));
                }
                catch (Exception error)
                {
                    probe.Result.SetException(error);
                }
            });
            await probe.InnerStarted.Task;
        }
    }

    public sealed class HeldInlineActivity(BackgroundProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.InnerStarted.SetResult();
            await probe.Later.Task;
        }
    }

    public sealed class RunScope : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    public sealed class QuickActivity(HostProbe probe, RunScope scope) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Assert.False(scope.Disposed);
            probe.QuickScopes.Enqueue(scope);
            probe.Record("quick");
            return Task.CompletedTask;
        }
    }

    /// <summary>Counts reads of cancellation requests.</summary>
    private sealed class CountingStore : FaultInjectingRepository
    {
        private int _cancellationReads;

        public CountingStore()
        {
        }

        public CountingStore(TimeProvider time) : base(time)
        {
        }

        public int CancellationReads => Volatile.Read(ref _cancellationReads);

        public override Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
        {
            Interlocked.Increment(ref _cancellationReads);
            return base.GetCancellationRequestAsync(instanceId);
        }

        private int _batchReads;
        private int _largestBatch;

        public int BatchCancellationReads => Volatile.Read(ref _batchReads);
        public int LargestBatch => Volatile.Read(ref _largestBatch);

        public override Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
            IReadOnlyCollection<string> instanceIds)
        {
            Interlocked.Increment(ref _batchReads);
            int seen;
            while (instanceIds.Count > (seen = Volatile.Read(ref _largestBatch)) &&
                   Interlocked.CompareExchange(ref _largestBatch, instanceIds.Count, seen) != seen)
            {
            }
            return base.GetCancellationRequestsAsync(instanceIds);
        }
    }

    /// <summary>Holds the commits of one workflow until released, and records applied revisions.</summary>
    private sealed class HoldingStore : FaultInjectingRepository
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<(string Name, long Revision)> _applied = new();

        public string HeldInstanceName { get; init; } = "";
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public long[] Revisions(string name) =>
            _applied.Where(item => item.Name == name).Select(item => item.Revision).ToArray();

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var instance = commit.ReadTrunk();
            if (instance.WorkflowName == HeldInstanceName && instance.Revision >= 1)
            {
                Holding.TrySetResult();
                await _released.Task;
            }
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.Applied)
                _applied.Enqueue((instance.WorkflowName, result.Revision));
            return result;
        }
    }
}
