using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The store's clock decides deadlines (D-35): the store stamps a deadline at the commit that records
/// its wait, refuses an event that arrives after it, and says which deadlines are due when the inbox is
/// read, so an event received in time beats its deadline. A host's clock only decides when to look.
/// </summary>
[Trait("Feature", "timers")]
[Trait("Spec", "C-03")]
[Trait("Spec", "J-05")]
public sealed class StoreClockTests : IClassFixture<SqliteTestDatabase<StoreClockTests>>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // One SQL Server store for these tests, so its schema is checked once.
    private static readonly Lazy<SqlWorkflowStateRepository> SqlStore =
        new(() => new SqlWorkflowStateRepository(SqlTestDatabase<StoreClockTests>.ConnectionString));

    // One PostgreSQL store for these tests, so its schema is checked once.
    private static readonly Lazy<PostgreSqlWorkflowStateRepository> PostgreSqlStore =
        new(() => new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<StoreClockTests>.ConnectionString));

    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    public async Task AWaitTimesOutByTheStoreClockWhateverTheHostClockSays(int hostAheadMinutes)
    {
        var clocks = new Clocks(hostAheadMinutes);
        using var services = Services(clocks);
        var started = await Engine(services).RunAsync(ApprovalWorkflow("StoreClockTimeout"), new ClockData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        // Nine minutes on the store's clock: a host five minutes ahead reads fourteen, and still nothing is
        // due, whether the poll looks or the host runs the instance itself.
        clocks.Advance(TimeSpan.FromMinutes(9));
        await RecoverOnceAsync(services, clocks);
        Assert.Equal(WorkflowExecutionStatus.Faulted, (await Engine(services).RecoverAsync(started.InstanceId)).Status);
        Assert.Equal(WorkflowStatus.Suspended, (await InstanceAsync(services, started.InstanceId)).Status);

        // Eleven on the store's clock: a host five minutes behind reads six, and the wait has timed out.
        clocks.Advance(TimeSpan.FromMinutes(2));
        await RecoverOnceAsync(services, clocks);
        await FinishedAsync(services, started.InstanceId);
        var saved = await InstanceAsync(services, started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        Assert.Contains("\"Outcome\":\"timeout\"", saved.WorkflowDataJson);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    public async Task AnEventReceivedInTimeBeatsTheDeadlineWhateverTheHostClockSays(int hostAheadMinutes)
    {
        var clocks = new Clocks(hostAheadMinutes);
        using var services = Services(clocks);
        var started = await Engine(services).RunAsync(ApprovalWorkflow("StoreClockOnTime"), new ClockData());
        var store = services.GetRequiredService<IWorkflowStateRepository>();

        // The approval arrives at nine minutes on the store's clock, and nothing runs the instance until
        // after the deadline.
        clocks.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(ApprovalEvent(started.InstanceId, "a1"))).Status);
        clocks.Advance(TimeSpan.FromMinutes(2));
        await RecoverOnceAsync(services, clocks);
        await FinishedAsync(services, started.InstanceId);

        var saved = await InstanceAsync(services, started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        Assert.Contains("\"Outcome\":\"approved\"", saved.WorkflowDataJson);
        Assert.Equal(EventOutcome.Consumed, (await store.GetEventAsync(started.InstanceId, "a1"))!.Outcome);
    }

    [Fact]
    public async Task ADelayEndsByTheStoreClockAndAHostThatLooksEarlyFindsNothingDue()
    {
        var clocks = new Clocks(5);
        using var services = Services(clocks);
        var definition = Workflow.Create<ClockData>("StoreClockDelay")
            .Step<NoopActivity>()
            .Delay(TimeSpan.FromHours(1))
            .Step<MarkActivity>(step => step
                .Input(activity => activity.Value).From(_ => "after-delay")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))
            .Build();
        var started = await Engine(services).RunAsync(definition, new ClockData());

        // The host's clock passed the hour; the store's has not, so a look finds nothing to run.
        clocks.Advance(TimeSpan.FromMinutes(57));
        var early = await Engine(services).RecoverAsync(started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Faulted, early.Status);
        Assert.Equal(WorkflowStatus.Suspended, (await InstanceAsync(services, started.InstanceId)).Status);

        clocks.Advance(TimeSpan.FromMinutes(4));
        await RecoverOnceAsync(services, clocks);
        await FinishedAsync(services, started.InstanceId);
        var saved = await InstanceAsync(services, started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        Assert.Contains("after-delay", saved.WorkflowDataJson);
    }

    [Fact]
    public async Task AnExecutionDeadlineEndsTheRunByTheStoreClock()
    {
        var clocks = new Clocks(-5);
        using var services = Services(clocks);
        var definition = Workflow.Create<ClockData>("StoreClockExecution")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        var started = await Engine(services).RunAsync(definition, new ClockData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromHours(1) });

        clocks.Advance(TimeSpan.FromMinutes(59));
        await RecoverOnceAsync(services, clocks);
        Assert.Equal(WorkflowStatus.Suspended, (await InstanceAsync(services, started.InstanceId)).Status);

        // An hour and a minute on the store's clock; the host's reads fifty-six minutes.
        clocks.Advance(TimeSpan.FromMinutes(2));
        await RecoverOnceAsync(services, clocks);
        await FinishedAsync(services, started.InstanceId);
        Assert.Equal(WorkflowStatus.TimedOut, (await InstanceAsync(services, started.InstanceId)).Status);
    }

    [Fact]
    public async Task AnEventReceivedInTimeBeatsTheDeadlineWhenItsOwnerWasBusy()
    {
        var clocks = Clocks.One();
        using var services = Services(clocks);
        var gate = services.GetRequiredService<Gate>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var definition = Workflow.Create<ClockData>("StoreClockBusyOwner")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.WaitFor<Approval>("approval", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(10))
                    .Output(approval => approval.Decision).To(ctx => ctx.WorkflowData.Outcome)
                    .OnTimeout(path => path.Step<MarkActivity>(step => step
                        .Input(activity => activity.Value).From(_ => "timeout")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))))
            .Build();
        var instanceId = Guid.NewGuid().ToString("N");
        var run = Engine(services).RunAsync(definition, new ClockData(),
            new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while ((await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId, Key: "approval"))).Count == 0)
            await Task.Delay(10);

        // The approval is stored five seconds before the deadline while the owner runs the gated
        // activity; the owner next reads its inbox after the deadline. The clocks move in small steps,
        // so the owner keeps renewing its lease.
        await clocks.AdvanceInStepsAsync(TimeSpan.FromSeconds(595));
        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(ApprovalEvent(instanceId, "a1"))).Status);
        await clocks.AdvanceInStepsAsync(TimeSpan.FromSeconds(10));
        gate.Release.TrySetResult();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("approved", Assert.IsType<ClockData>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task AnObservedCancellationGoesBeforeADeadlineTheStoreFindsDue()
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(new Clocks(0));
        var gate = services.GetRequiredService<Gate>();
        var host = new DueHost(time);
        var definition = Workflow.Create<ClockData>("StoreClockCancelFirst")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.WaitFor<Approval>("approval", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(10))
                    .OnTimeout(path => path.Step<MarkActivity>(step => step
                        .Input(activity => activity.Value).From(_ => "timeout")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))))
            .Build();
        var run = RunExecutorAsync(services, definition, host, time);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while (host.Opened.Count == 0)
            await Task.Delay(10);
        var waitId = Assert.Single(host.Opened);

        // The run learns of a cancellation request and the store finds the wait due, both before the
        // run looks again; then its clock passes the deadline and wakes it.
        host.Cancel();
        time.Advance(TimeSpan.FromMinutes(11));
        gate.Release.TrySetResult();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(WaitCloseReason.Cancelled, host.Closed[waitId]);
        Assert.Equal("", Assert.IsType<ClockData>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task ACancellationLearnedWhileTheInboxIsReadGoesBeforeTheExecutionDeadline()
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(new Clocks(0));
        var gate = services.GetRequiredService<Gate>();
        var host = new DueHost(time);
        var definition = Workflow.Create<ClockData>("StoreClockCancelDuringRead")
            .Step<GatedActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        var run = RunExecutorAsync(services, definition, host, time, TimeSpan.FromMinutes(10));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The execution deadline wakes the run, which reads its inbox; while it reads, the host learns
        // of a cancellation request and the store finds the deadline due.
        var reading = host.HoldReads();
        time.Advance(TimeSpan.FromMinutes(11));
        await reading.WaitAsync(TimeSpan.FromSeconds(10));
        host.Cancel();
        host.ReleaseReads();
        gate.Release.TrySetResult();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWaitTimesOutWhileASiblingBranchRuns(bool answersARequest)
    {
        var clocks = Clocks.One();
        using var services = Services(clocks);
        var gate = services.GetRequiredService<Gate>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var definition = answersARequest
            ? Workflow.Create<ClockData>("SiblingRunsWhileARequestWaits")
                .Step<NoopActivity>()
                .Parallel(parallel => parallel
                    .Do(branch => branch.Step<GatedActivity>())
                    .Do(branch => branch
                        .Request<AskActivity>("approval")
                        .WaitFor<Approval>("approval", configure: OneMinuteTimeout)))
                .Build()
            : Workflow.Create<ClockData>("SiblingRunsWhileAWaitWaits")
                .Step<NoopActivity>()
                .Parallel(parallel => parallel
                    .Do(branch => branch.Step<GatedActivity>())
                    .Do(branch => branch.WaitFor<Approval>("approval", configure: OneMinuteTimeout)))
                .Build();
        var instanceId = Guid.NewGuid().ToString("N");
        var run = Engine(services).RunAsync(definition, new ClockData(), new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // No event comes and the sibling keeps running: the wait still times out on time.
        var waitId = await ArmedWaitIdAsync(store, instanceId);
        await clocks.AdvanceInStepsAsync(TimeSpan.FromSeconds(61));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        StoredWait? wait;
        while ((wait = await store.GetWaitAsync(instanceId, waitId))!.IsOpen && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.Equal(WaitCloseReason.TimedOut, wait.CloseReason);
        Assert.False(run.IsCompleted);

        gate.Release.TrySetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("timeout", Assert.IsType<ClockData>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task AChildWaitsForASlotWhileItsParentHoldsTheOnlyOne()
    {
        var clocks = Clocks.One();
        using var services = Services(clocks, slots: 1);
        var gate = services.GetRequiredService<Gate>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var child = Workflow.Create<ClockData>("SlotlessChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval", configure: OneMinuteTimeout)
            .Build();
        var parent = Workflow.Create<ClockData>("SlotHoldingParent")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.Invoke<ClockData>("SlotlessChild", 1)))
            .Build();
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var instanceId = Guid.NewGuid().ToString("N");
        var run = Engine(services).RunAsync(parent, new ClockData(), new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The child is started, but the parent's run holds the only slot, so the child has not run.
        WorkflowInstance? started = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && (started = (await store.GetWorkflowInstancesByNameAsync("SlotlessChild"))
                   .SingleOrDefault(item => item.ParentInstanceId == instanceId)) == null)
            await Task.Delay(20);
        await Task.Delay(300);
        Assert.Equal(1, (await store.GetWorkflowInstanceAsync(started!.InstanceId))!.Revision);

        // Once the parent waits for it, the slot is free: the child runs, times out and wakes its parent.
        gate.Release.TrySetResult();
        var idle = await run.WaitAsync(TimeSpan.FromSeconds(10));
        await ArmedWaitIdAsync(store, started.InstanceId);
        await clocks.AdvanceInStepsAsync(TimeSpan.FromSeconds(61));
        Assert.Equal(WorkflowExecutionStatus.Success, (await Engine(services).FinishedAsync(idle)).Status);
    }

    [Fact]
    public async Task AChildsWaitTimesOutWhileItsParentRunsASiblingInAnotherSlot()
    {
        var clocks = Clocks.One();
        using var services = Services(clocks, slots: 2);
        var gate = services.GetRequiredService<Gate>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var child = Workflow.Create<ClockData>("StoreClockChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval", configure: OneMinuteTimeout)
            .Build();
        var parent = Workflow.Create<ClockData>("StoreClockParent")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<GatedActivity>())
                .Do(branch => branch.Invoke<ClockData>("StoreClockChild", 1)))
            .Build();
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var instanceId = Guid.NewGuid().ToString("N");
        var run = Engine(services).RunAsync(parent, new ClockData(), new WorkflowOptions { InstanceId = instanceId });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The child runs in the other slot, and its own deadline ends its wait.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorkflowInstance? started = null;
        while ((started = (await store.GetWorkflowInstancesByNameAsync("StoreClockChild"))
                   .SingleOrDefault(item => item.ParentInstanceId == instanceId)) == null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The child was never started");
            await Task.Delay(20);
        }
        await ArmedWaitIdAsync(store, started.InstanceId);
        await clocks.AdvanceInStepsAsync(TimeSpan.FromSeconds(61));
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorkflowInstance? childInstance = null;
        while (DateTime.UtcNow < deadline &&
               (childInstance = (await store.GetWorkflowInstancesByNameAsync("StoreClockChild"))
                   .SingleOrDefault(item => item.ParentInstanceId == instanceId))?.Status != WorkflowStatus.Completed)
            await Task.Delay(20);
        Assert.Equal(WorkflowStatus.Completed, childInstance?.Status);
        Assert.False(run.IsCompleted);

        gate.Release.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Success, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task ARetryScheduledByAHostWithAFastClockIsTakenOnTimeByAnother()
    {
        var store = new FakeTimeProvider(Start);
        var fast = new FakeTimeProvider(Start.AddMinutes(5));
        var correct = new FakeTimeProvider(Start);
        var repository = new InMemoryWorkflowStateRepository(store);
        var failures = new FailOnce();
        var definition = Workflow.Create<ClockData>("StoreClockRetryHandoff")
            .Step<NoopActivity>()
            .Saga(saga => saga.Step<FailOnceActivity>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(new RetryPolicy(
                    TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 1, 1)))))
            .Build();

        // A host five minutes fast runs the step, which fails, and parks the retry for a minute.
        string instanceId;
        using (var first = HostServices(fast, repository, failures))
        {
            instanceId = await Engine(first).StartAsync(definition, new ClockData());
            var parked = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (await repository.GetWorkflowInstanceAsync(instanceId) is not
                   { Status: WorkflowStatus.Running, RunnableAtUtc: { } due } || due < Start.UtcDateTime.AddSeconds(30))
            {
                Assert.True(DateTime.UtcNow < parked, "The retry was never parked");
                await Task.Delay(10);
            }
        }

        // That host is gone. A minute later on the store's clock, a host with the right time retries.
        store.Advance(TimeSpan.FromSeconds(61));
        correct.Advance(TimeSpan.FromSeconds(61));
        using var second = HostServices(correct, repository, failures);
        await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        await RecoverOnceAsync(second, correct);

        Assert.Equal(WorkflowStatus.Completed, (await repository.GetWorkflowInstanceAsync(instanceId))!.Status);
        Assert.Equal(2, failures.Attempts);
    }

    private static ServiceProvider HostServices(TimeProvider time, IWorkflowStateRepository repository,
        FailOnce failures)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddSingleton(repository);
        services.AddIxIFlow();
        services.AddSingleton(failures);
        services.AddTransient<NoopActivity>();
        services.AddTransient<FailOnceActivity>();
        return services.BuildServiceProvider();
    }

    private static void OneMinuteTimeout<TPrevious>(ISuspendSetupBuilder<ClockData, Approval, TPrevious> wait)
        where TPrevious : class => wait
        .TimeoutAfter(TimeSpan.FromMinutes(1))
        .OnTimeout(path => path.Step<MarkActivity>(step => step
            .Input(activity => activity.Value).From(_ => "timeout")
            .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)));

    // The open approval wait, once its deadline is set: a request's wait opens before its WaitFor arms it,
    // and the clock must not move in between.
    private static async Task<string> ArmedWaitIdAsync(IWorkflowStateRepository store, string instanceId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if ((await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId, Key: "approval")))
                .SingleOrDefault() is { DeadlineUtc: not null } open)
                return open.WaitId;
            Assert.True(DateTime.UtcNow < deadline, "The wait was never armed");
            await Task.Delay(10);
        }
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public Task MemoryStoreRefusesEventsThatArriveAfterTheirDeadline() =>
        VerifyLateArrivals(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task SqlStoreRefusesEventsThatArriveAfterTheirDeadline() =>
        VerifyLateArrivals(SqlStore.Value);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task PostgreSqlStoreRefusesEventsThatArriveAfterTheirDeadline() =>
        VerifyLateArrivals(PostgreSqlStore.Value);

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-03")]
    public async Task SqliteStoreRefusesEventsThatArriveAfterTheirDeadline()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<StoreClockTests>.ConnectionString);
        await VerifyLateArrivals(repository);
    }

    private static async Task VerifyLateArrivals(IWorkflowStateRepository store)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            // A wait already past its deadline, one with time left under another key, a reply wait
            // without a deadline yet, and a delay.
            await CommitAsync(store, instanceId, null,
                new OpenedWait("late", WaitKind.Event, "late", "Approval", TimeSpan.Zero),
                new OpenedWait("open", WaitKind.Event, "open", "Approval", TimeSpan.FromHours(1)),
                new OpenedWait("reply", WaitKind.Reply, "reply", "Approval", null),
                new OpenedWait("delay", WaitKind.Delay, "delay", "", TimeSpan.FromHours(1)));

            Assert.Equal(EventRejection.WaitTimedOut, (await store.AppendEventAsync(
                Event(instanceId, "by-token", targetWaitId: "late"))).Rejection);
            Assert.Equal(EventRejection.WaitTimedOut, (await store.AppendEventAsync(
                Event(instanceId, "by-key", key: "late"))).Rejection);
            Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(
                Event(instanceId, "to-reply", targetWaitId: "reply"))).Status);
            // Without a key, the wait with time left could still take it.
            Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(
                Event(instanceId, "any-key"))).Status);
            // No event can answer a delay.
            Assert.Equal(EventRejection.UnknownWait, (await store.AppendEventAsync(
                Event(instanceId, "to-delay", targetWaitId: "delay"))).Rejection);

            // Delays are kept out of listings unless asked for.
            var listed = await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId));
            Assert.DoesNotContain(listed, wait => wait.WaitId == "delay");
            Assert.Equal("delay", Assert.Single(await store.GetOpenWaitsAsync(
                new WaitQuery(InstanceId: instanceId, Kind: WaitKind.Delay))).WaitId);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public Task MemoryStoreFindsDeadlinesDueWithTheEventsReceivedBeforeThem()
    {
        var time = new FakeTimeProvider(Start);
        return VerifyDuePage(new InMemoryWorkflowStateRepository(time), TimeSpan.FromMilliseconds(500), _ =>
        {
            time.Advance(TimeSpan.FromMilliseconds(700));
            return Task.CompletedTask;
        });
    }

    // SQL Server's clock cannot be moved: the deadline leaves room for a slow server, and the test reads
    // until the store finds it due.
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task SqlStoreFindsDeadlinesDueWithTheEventsReceivedBeforeThem() =>
        VerifyDuePage(SqlStore.Value, TimeSpan.FromSeconds(2), async instanceId =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!(await SqlStore.Value.GetPendingEventsAsync(instanceId, 0, 10)).IsDue("short"))
            {
                Assert.True(DateTime.UtcNow < deadline, "The store never found the deadline due");
                await Task.Delay(50);
            }
        });

    // PostgreSQL's clock cannot be moved either.
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task PostgreSqlStoreFindsDeadlinesDueWithTheEventsReceivedBeforeThem() =>
        VerifyDuePage(PostgreSqlStore.Value, TimeSpan.FromSeconds(2), async instanceId =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!(await PostgreSqlStore.Value.GetPendingEventsAsync(instanceId, 0, 10)).IsDue("short"))
            {
                Assert.True(DateTime.UtcNow < deadline, "The store never found the deadline due");
                await Task.Delay(50);
            }
        });

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-03")]
    public async Task SqliteStoreFindsDeadlinesDueWithTheEventsReceivedBeforeThem()
    {
        var time = new FakeTimeProvider(Start);
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<StoreClockTests>.ConnectionString, time);
        await VerifyDuePage(repository, TimeSpan.FromMilliseconds(500), _ =>
        {
            time.Advance(TimeSpan.FromMilliseconds(700));
            return Task.CompletedTask;
        });
    }

    private static async Task VerifyDuePage(IWorkflowStateRepository store, TimeSpan shortDeadline,
        Func<string, Task> passShortDeadline)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            await CommitAsync(store, instanceId, null,
                new OpenedWait("short", WaitKind.Event, "approval", "Approval", shortDeadline),
                new OpenedWait("long", WaitKind.Event, "other", "Approval", TimeSpan.FromHours(1)));
            var before = await store.GetPendingEventsAsync(instanceId, 0, 10);
            Assert.False(before.IsDue("short"));
            Assert.Equal(EventAppendStatus.Stored,
                (await store.AppendEventAsync(Event(instanceId, "in-time", key: "approval"))).Status);

            await passShortDeadline(instanceId);
            var page = await store.GetPendingEventsAsync(instanceId, 0, 10);

            // The page says which deadlines had passed at its time, and holds every event received before them.
            Assert.True(page.IsDue("short"));
            Assert.False(page.IsDue("long"));
            var inTime = Assert.Single(page.Events);
            Assert.True(inTime.ReceivedAtUtc < page.Deadlines!["short"]);
            Assert.True(page.Deadlines["short"] <= page.StoreTimeUtc);
            Assert.Equal(page.Deadlines["short"], (await store.GetWaitAsync(instanceId, "short"))!.DeadlineUtc);
            // An event for that wait now is too late, and never reaches a page.
            Assert.Equal(EventRejection.WaitTimedOut, (await store.AppendEventAsync(
                Event(instanceId, "late", key: "approval"))).Rejection);
            Assert.Single((await store.GetPendingEventsAsync(instanceId, 0, 10)).Events);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task SqlStoreReadsItsTimeOnceTheInstanceLockIsHeld() => VerifyStoreTimeAfterInstanceLock(_ => SqlStore.Value, () => new SqlConnection(SqlTestDatabase<StoreClockTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task PostgreSqlStoreReadsItsTimeOnceTheInstanceLockIsHeld() => VerifyStoreTimeAfterInstanceLock(_ => PostgreSqlStore.Value, () => new NpgsqlConnection(PostgreSqlTestDatabase<StoreClockTests>.ConnectionString));

    [Fact]
    [Trait("Spec", "D-03")]
    public Task MemoryStoreArmsAReplyWaitOnce()
    {
        var time = new FakeTimeProvider(Start);
        return VerifyArming(new InMemoryWorkflowStateRepository(time), () =>
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            return Task.CompletedTask;
        });
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task SqlStoreArmsAReplyWaitOnce() =>
        VerifyArming(SqlStore.Value, () => Task.Delay(TimeSpan.FromMilliseconds(50)));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-03")]
    public Task PostgreSqlStoreArmsAReplyWaitOnce() =>
        VerifyArming(PostgreSqlStore.Value, () => Task.Delay(TimeSpan.FromMilliseconds(50)));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    [Trait("Spec", "D-03")]
    public async Task SqliteStoreArmsAReplyWaitOnce()
    {
        var time = new FakeTimeProvider(Start);
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<StoreClockTests>.ConnectionString, time);
        await VerifyArming(repository, () =>
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            return Task.CompletedTask;
        });
    }

    private static async Task VerifyArming(IWorkflowStateRepository store, Func<Task> letTimePass)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            // The request's start opens its reply wait without a deadline; its WaitFor arms it.
            var instance = await CommitAsync(store, instanceId, null,
                new OpenedWait("reply", WaitKind.Reply, "payment", "Payment", null));
            Assert.Null((await store.GetWaitAsync(instanceId, "reply"))!.DeadlineUtc);
            var arming = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, instance.Revision, commitId: "arm",
                armedWaits: [new ArmedWait("reply", TimeSpan.FromHours(1))]);
            var armed = await store.CommitWorkflowInstanceAsync(arming);
            Assert.Equal(WorkflowCommitStatus.Applied, armed.Status);
            var deadline = (await store.GetWaitAsync(instanceId, "reply"))!.DeadlineUtc;
            Assert.NotNull(deadline);

            // The same commit again, and a later one that arms it again, keep the first deadline.
            await letTimePass();
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(arming)).Status);
            instance.Revision = armed.Revision;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, instance.Revision,
                    armedWaits: [new ArmedWait("reply", TimeSpan.FromHours(2))]))).Status);
            Assert.Equal(deadline, (await store.GetWaitAsync(instanceId, "reply"))!.DeadlineUtc);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    // An idle instance with the given waits, committed without a lease.
    private static async Task<WorkflowInstance> CommitAsync(IWorkflowStateRepository store, string instanceId,
        TimeSpan? wakeAfter, params OpenedWait[] waits)
    {
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = instanceId, WorkflowName = "StoreClock", Status = WorkflowStatus.Suspended,
            WorkflowDataJson = "{}"
        };
        var result = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0,
            openedWaits: waits, wakeAfter: wakeAfter));
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        instance.Revision = result.Revision;
        return instance;
    }

    private static WorkflowEvent Event(string instanceId, string eventId, string? key = null,
        string? targetWaitId = null) =>
        new(instanceId, eventId, "Approval", "{}", key, targetWaitId);

    private static WorkflowEvent ApprovalEvent(string instanceId, string eventId) =>
        new(instanceId, eventId, typeof(Approval).AssemblyQualifiedName!,
            JsonSerializer.Serialize(new Approval { Decision = "approved" }), "approval");

    private static WorkflowDefinition ApprovalWorkflow(string name) =>
        Workflow.Create<ClockData>(name)
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMinutes(10))
                .Output(approval => approval.Decision).To(ctx => ctx.WorkflowData.Outcome)
                .OnTimeout(path => path.Step<MarkActivity>(step => step
                    .Input(activity => activity.Value).From(_ => "timeout")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
            .Build();

    /// <summary>The host's clock and the store's, a fixed number of minutes apart, moving together.</summary>
    private sealed class Clocks
    {
        public Clocks(int hostAheadMinutes)
        {
            Host = new FakeTimeProvider(Start.AddMinutes(hostAheadMinutes));
            Store = new FakeTimeProvider(Start);
        }

        private Clocks(TimerRecordingClock both)
        {
            Host = both;
            Store = both;
            _timers = both;
        }

        /// <summary>
        /// One clock for the host and the store, so they never differ, not even while it moves. It records its
        /// timers, so a step waits for the leases' renewals.
        /// </summary>
        public static Clocks One() => new(new TimerRecordingClock(Start));

        private readonly TimerRecordingClock? _timers;
        private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);

        public FakeTimeProvider Host { get; }
        public FakeTimeProvider Store { get; }

        public void Advance(TimeSpan by)
        {
            Store.Advance(by);
            if (Host != Store)
                Host.Advance(by);
        }

        // Steps shorter than a lease renewal. On one recording clock, each step waits until every lease renewal
        // that fired in it has been sent and the next one set; otherwise a short real yield lets the host's
        // timers run. A renewal fires inside the step's advance, so one whose lease was released first is not
        // waited for.
        public async Task AdvanceInStepsAsync(TimeSpan by)
        {
            var step = TimeSpan.FromSeconds(5);
            for (var moved = TimeSpan.Zero; moved < by; moved += step)
            {
                var length = by - moved < step ? by - moved : step;
                if (_timers is not { } timers)
                {
                    Advance(length);
                    await Task.Delay(5);
                    continue;
                }
                var armedBefore = timers.Armed;
                var renewalsBefore = timers.Armings
                    .Where(timer => timer.Pending && timer.DueTime == RenewalInterval)
                    .Select(timer => timer.Number)
                    .ToHashSet();
                Advance(length);
                var renewalsFired = timers.Armings.Count(timer =>
                    renewalsBefore.Contains(timer.Number) && timer.Firings > 0);
                await timers.WaitUntilAsync(armings => armings.Skip(armedBefore)
                        .Count(timer => timer.DueTime == RenewalInterval) >= renewalsFired,
                    $"{renewalsFired} lease renewals set again after a step to {timers.GetUtcNow():O}");
            }
        }
    }

    private static ServiceProvider Services(Clocks clocks, int? slots = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clocks.Host);
        services.AddSingleton<IWorkflowStateRepository>(new InMemoryWorkflowStateRepository(clocks.Store));
        if (slots is { } count)
            services.AddIxIFlow(options => options.Slots = count);
        else
            services.AddIxIFlow();
        services.AddSingleton<Gate>();
        services.AddTransient<NoopActivity>();
        services.AddTransient<MarkActivity>();
        services.AddTransient<GatedActivity>();
        services.AddTransient<AskActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    private static async Task<WorkflowInstance> InstanceAsync(IServiceProvider services, string instanceId) =>
        (await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(instanceId))!;

    // The host's own timer may start a due run before the scan looks, and the scan leaves an instance that has an
    // owner alone; so a test reads the store once the run has finished, whichever started it.
    private static Task FinishedAsync(IServiceProvider services, string instanceId) =>
        Engine(services).AwaitAsync(instanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));

    private static Task RecoverOnceAsync(IServiceProvider services, Clocks clocks) =>
        RecoverOnceAsync(services, clocks.Host);

    private static async Task RecoverOnceAsync(IServiceProvider services, TimeProvider hostTime)
    {
        using var scanner = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IWorkflowStateRepository>(), NullLogger<WorkflowRecoveryService>.Instance,
            hostTime);
        await scanner.RecoverOnceAsync(CancellationToken.None);
    }

    private static Task<WorkflowExecutionResult> RunExecutorAsync(IServiceProvider services,
        WorkflowDefinition definition, IExecutionHost host, TimeProvider time, TimeSpan? executionTimeout = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var data = new ClockData();
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = typeof(ClockData).AssemblyQualifiedName!,
            WorkflowDataJson = JsonSerializer.Serialize(data),
            Status = WorkflowStatus.Running,
            CreatedAt = now,
            StartedAt = now
        };
        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = new WorkflowScopeCatalog(definition).StructureHash,
            ExecutionTimeout = executionTimeout,
            ExecutionTimerId = executionTimeout != null ? "execution" : null,
            ExecutionDeadlineUtc = now + executionTimeout,
            Continuations = [new ContinuationState { Stack = [new ScopePosition { ScopeId = "root" }] }]
        };
        var executor = new WorkflowExecutor(services, services.GetRequiredService<IActivityExecutor>(), time);
        return Task.Run(() => executor.RunAsync(definition, instance, checkpoint, data, host, CancellationToken.None));
    }

    /// <summary>
    /// A host whose store finds every wait due once <see cref="Cancel"/> is called, and which has then
    /// also learned of a cancellation request. It records the waits commits open and close.
    /// </summary>
    private sealed class DueHost(TimeProvider time) : IExecutionHost
    {
        private readonly object _lock = new();
        private readonly List<string> _opened = [];
        private readonly Dictionary<string, WaitCloseReason> _closed = new(StringComparer.Ordinal);
        private volatile bool _cancelled;
        private long _revision;

        public IReadOnlyList<string> Opened
        {
            get { lock (_lock) return _opened.ToArray(); }
        }

        public IReadOnlyDictionary<string, WaitCloseReason> Closed
        {
            get { lock (_lock) return new Dictionary<string, WaitCloseReason>(_closed); }
        }

        public void Cancel() => _cancelled = true;

        private TaskCompletionSource? _readStarted;
        private TaskCompletionSource? _readRelease;

        /// <summary>Holds the next inbox read until <see cref="ReleaseReads"/>; the task completes when it starts.</summary>
        public Task HoldReads()
        {
            _readRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _readStarted.Task;
        }

        public void ReleaseReads() => _readRelease?.TrySetResult();

        public CancellationToken Drain => CancellationToken.None;
        public long Epoch => 1;
        public ExecutionLimits Limits { get; } = new() { MaxCheckpointBytes = long.MaxValue };
        public bool ChildStartIsCheckpointed => true;
        public bool CanPark => false;
        public Task? OwnershipLoss => null;
        public Task<CancellationReason>? CancellationSignal => null;
        public CancellationReason? ObservedCancellation =>
            _cancelled ? new CancellationReason { ReasonCode = "stop" } : null;
        public long ObservedInboxSequence => 0;
        public Task? InboxChanged => null;

        public void ThrowIfOwnershipLost()
        {
        }

        public Task<CheckpointReply> CommitAsync(CheckpointCommit commit)
        {
            lock (_lock)
            {
                _opened.AddRange(commit.OpenedWaits.Where(wait => wait.Timeout != null).Select(wait => wait.WaitId));
                foreach (var closed in commit.ClosedWaits)
                    _closed[closed.WaitId] = closed.Reason;
            }
            return Task.FromResult(new CheckpointReply(CheckpointReplyKind.Acknowledged,
                Interlocked.Increment(ref _revision)));
        }

        public void ObserveCancellation(CancellationReason reason)
        {
        }

        public async Task<InboxPage> ReadInboxAsync(long afterSequence, int limit)
        {
            if (_readRelease is { } release)
            {
                _readStarted!.TrySetResult();
                await release.Task;
            }
            var now = time.GetUtcNow().UtcDateTime;
            var deadlines = Opened.ToDictionary(id => id, _ => _cancelled ? now.AddHours(-1) : now.AddHours(1));
            return new InboxPage([], afterSequence, Opened.ToDictionary(id => id, _ => 0L), deadlines, now);
        }

        public Task<StoredWait?> ReadWaitAsync(string waitId) => Task.FromResult<StoredWait?>(null);

        public Task<WorkflowDefinition> ResolveChildAsync(WorkflowStep step, bool starting) =>
            throw new NotSupportedException();

        public ChildStart StartChild(WorkflowDefinition definition, object data, WorkflowInstance parent,
            string childInstanceId, string parentWaitId) => throw new NotSupportedException();
    }

    public sealed class ClockData
    {
        public string Outcome { get; set; } = "";
    }

    public sealed class Approval
    {
        public string Decision { get; set; } = "";
    }

    public sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class MarkActivity : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Counts attempts; the first one fails.</summary>
    public sealed class FailOnce
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public bool First() => Interlocked.Increment(ref _attempts) == 1;
    }

    public sealed class FailOnceActivity(FailOnce failures) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            failures.First() ? throw new InvalidOperationException("Planned failure") : Task.CompletedTask;
    }

    /// <summary>A request whose answer never comes.</summary>
    public sealed class AskActivity : IRequestActivity<Approval>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class GatedActivity(Gate gate) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task;
        }
    }
    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteStoreReadsItsTimeOnceTheInstanceLockIsHeld()
    {
        var time = new FakeTimeProvider(Start);
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<StoreClockTests>.ConnectionString, time);
        await VerifyStoreTimeAfterInstanceLock(_ => repository, () => new Microsoft.Data.Sqlite.SqliteConnection(SqliteTestDatabase<StoreClockTests>.ConnectionString), time);
    }

    private static async Task VerifyStoreTimeAfterInstanceLock(Func<TimeProvider, IWorkflowStateRepository> newStore, Func<System.Data.Common.DbConnection> newConnection, FakeTimeProvider? time = null)
    {
        var store = newStore(time ?? TimeProvider.System);
        var instanceId = Guid.NewGuid().ToString("N");
        try
        {
            await CommitAsync(store, instanceId, null,
                new OpenedWait("approval", WaitKind.Event, "approval", "Approval", TimeSpan.FromHours(1)));

            // Another transaction holds the instance row while a read and an append wait for it.
            await using var holder = newConnection();
            await holder.OpenAsync();
            await using var transaction = holder is Microsoft.Data.Sqlite.SqliteConnection sqlite
                ? sqlite.BeginTransaction(deferred: false) : await holder.BeginTransactionAsync();
            await using (var lockRow = holder.CreateCommand())
            {
                lockRow.Transaction = transaction;
                lockRow.CommandText = holder is SqlConnection
                    ? "SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK) WHERE InstanceId = @InstanceId"
                    : holder is NpgsqlConnection
                        ? "SELECT instance_id FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId FOR UPDATE"
                        : "SELECT instance_id FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId";
                var parameter = lockRow.CreateParameter();
                parameter.ParameterName = holder is NpgsqlConnection ? "InstanceId" : "@InstanceId";
                parameter.Value = instanceId;
                lockRow.Parameters.Add(parameter);
                await lockRow.ExecuteScalarAsync();
            }
            var read = time == null ? store.GetPendingEventsAsync(instanceId, 0, 10)
                : Task.Run(() => store.GetPendingEventsAsync(instanceId, 0, 10));
            var append = time == null ? store.AppendEventAsync(Event(instanceId, "held", key: "approval"))
                : Task.Run(() => store.AppendEventAsync(Event(instanceId, "held", key: "approval")));
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(read.IsCompleted || append.IsCompleted);
            DateTime releasedAt;
            if (time != null)
            {
                time.Advance(TimeSpan.FromMilliseconds(500));
                releasedAt = time.GetUtcNow().UtcDateTime;
            }
            else
            {
                await using var clock = holder.CreateCommand();
                clock.Transaction = transaction;
                clock.CommandText = holder is SqlConnection ? "SELECT SYSUTCDATETIME()" : "SELECT clock_timestamp()";
                releasedAt = (DateTime)(await clock.ExecuteScalarAsync())!;
            }
            await transaction.CommitAsync();

            // Both took the store's time after the lock was theirs, not when they started waiting.
            Assert.True((await read).StoreTimeUtc >= releasedAt);
            Assert.Equal(EventAppendStatus.Stored, (await append).Status);
            Assert.True((await store.GetEventAsync(instanceId, "held"))!.ReceivedAtUtc >= releasedAt);
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(instanceId);
        }
    }


}
