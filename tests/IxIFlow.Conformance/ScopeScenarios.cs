using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeScenarios
{
    public static IEnumerable<object[]> Modes =>
        StoreProfiles.ProfileNames.SelectMany(profile => new object[][] { [profile, false], [profile, true] });

    [Theory, MemberData(nameof(Modes))]
    public async Task RestartInsideScopeReopensOnTheNextHostAndKeepsInvocationIdentity(string profile, bool affine)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var catalog = new WorkflowCatalog();
        var probe = new Probe();

        var definition = Start("ScopeHostMove").Scope<Container>(
            s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
            b => b.Step<Read>().WaitFor<Signal>("continue").Step<Read>()).Build();

        string id;

        await using (var first = Services(store, repository, catalog, probe, "first"))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
        }

        await using var second = Services(store, repository, catalog, probe, "second");
        var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "continue", new Signal());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(new[] { "first", "second" }, probe.Opens.Select(item => item.Host));
        Assert.Single(probe.Opens.Select(item => item.Invocation).Distinct());
        Assert.Equal(new[] { false, true }, probe.Opens.Select(item => item.Reopen));
        Assert.Equal(new[] { ScopeCloseReason.Suspended, ScopeCloseReason.Completed }, probe.Closes);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task OpeningIntentRecoversWithTheSavedIdentityAndInputs(string profile, bool affine)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var faults = new FaultRepository(repository);
        var catalog = new WorkflowCatalog();
        var probe = new Probe();

        faults.FailCommit = instance =>
            ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance)).Scopes.Any(entry => entry.RecordedOpens > 0);

        var definition = Start("ScopeOpeningRecovery")
            .Scope<Container>(s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread), b => b.Step<Read>())
            .Build();

        var id = Guid.NewGuid().ToString("N");

        await using (var first = Services(store, faults, catalog, probe, "first"))
        {
            await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new() { InstanceId = id }));

            Assert.Single(probe.Opens);
        }

        faults.FailCommit = _ => false;
        await using var second = Services(store, repository, catalog, probe, "second");
        var done = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(2, probe.Opens.Count);
        Assert.Single(probe.Opens.Select(item => item.Invocation).Distinct());

        Assert.All(probe.Opens, item =>
        {
            Assert.False(item.Reopen);
            Assert.Equal(0, item.Recorded);
        });
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task PendingAttemptReopensBeforeItsRecoveryCallbackOnAnotherHost(string profile, bool affine)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var faults = new FaultRepository(repository);
        var catalog = new WorkflowCatalog();
        var probe = new Probe();

        faults.FailCommit = instance => probe.Executed > 0 && ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance)).Continuations
            .All(c => c.PendingActivity == null);

        var definition = Start("ScopePendingRecovery")
            .Scope<Container>(s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread), b => b.Step<Read>())
            .Build();

        var id = Guid.NewGuid().ToString("N");

        await using (var first = Services(store, faults, catalog, probe, "first"))
            await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new() { InstanceId = id }));

        faults.FailCommit = _ => false;
        await using var second = Services(store, repository, catalog, probe, "second");
        var done = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(1, probe.Executed);
        Assert.Equal(1, probe.Recovered);
        Assert.Equal("second", probe.ReadHosts.Last());
        Assert.True(probe.Opens.Last().Reopen);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ScopeValuesFlowThroughAllStructuredBodies(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var probe = new Probe();
        await using var services = Services(store, store.NewRepository(), new WorkflowCatalog(), probe, "host");

        var definition = Start("ScopeConstructs").Scope<Container>(s => s.Threading(ScopeThreading.ThreadAffine), b => b
                .Sequence(x => x.Step<Read>())
                .Parallel(p => p.Do(x => x.Step<Read>()).Do(x => x.Step<Read>()))
                .ForEach(c => c.WorkflowData.Items,
                    (x, item) => x.Switch(c => c.Item(item), cases => cases.Case(1, y => y.Step<Read>()).Default(y => y.Step<Read>())),
                    o => o.Parallel())
                .Use<InlineBody, Data>()
                .Flowchart(f => f.Start("node").Node("node", x => x.Step<Read>()))
                .StateMachine(m =>
                    m.Start("state").State("state", s => s.Entry(x => x.Step<Read>()).On<Signal>("done", t => t.When(c => true, "end")))))
            .Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        Assert.Equal(8, probe.Executed);
        var done = await client.ResumeAsync(idle.InstanceId, "done", new Signal());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Single(probe.Opens);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ServerDeadlinesUseStoreClockEvenWhenTheHostClockDiffers(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe();
        var hostClock = new FakeTimeProvider(new DateTimeOffset(2080, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var services = Services(store, repository, new WorkflowCatalog(), probe, "host", hostClock);

        var definition = Start("ScopeStoreClock")
            .Scope<Container>(null, b => b.Step<Read>().Delay(TimeSpan.FromMilliseconds(100)).Step<Read>()).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var wait = Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId, Kind: WaitKind.Delay)));
        Assert.NotNull(wait.DeadlineUtc);
        Assert.Equal(TimeSpan.FromMilliseconds(100), wait.DeadlineUtc!.Value - wait.OpenedAtUtc);
        Assert.True(wait.DeadlineUtc.Value.Year < 2080);
        await Task.Delay(150);
        var done = await client.RecoverAsync(idle.InstanceId);
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(2, probe.Opens.Count);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FailureAndCancellationCloseValuesBeforeOutsideCleanup(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var probe = new Probe();
        await using var services = Services(store, store.NewRepository(), new WorkflowCatalog(), probe, "host");

        var definition = Start("ScopeFailure").Try(b => b.Scope<Container>(null, x => x.Step<Fail>()))
            .Catch<InvalidOperationException>(b => b.Step<Cleanup>()).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RunAsync(definition, new Data())).Status);
        Assert.Equal(new[] { "close:Failed", "outside" }, probe.Order);
        var waiting = Start("ScopeCancellation").Scope<Container>(null, b => b.WaitFor<Signal>("hold")).Build();
        var idle = await client.RunAsync(waiting, new Data());
        var cancelled = await client.CancelAsync(idle.InstanceId, new CancellationReason { ReasonCode = "test" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal(2, probe.Opens.Count);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ScopeRetryDeadlineIsSavedOnTheStoreClockBeforeParking(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe { OpenFailures = 1 };
        var hostClock = new FakeTimeProvider(new DateTimeOffset(2080, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var services = Services(store, repository, new WorkflowCatalog(), probe, "host", hostClock);

        var definition = Start("ScopeRetryStoreClock").Scope<Container>(
            s => s.Retry(new RetryPolicy(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), 1, 1)), b => b.Step<Read>()).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(definition, new Data());
        ExecutionCheckpoint saved;

        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                saved = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await repository.GetWorkflowInstanceAsync(id))!));

                if (saved.Scopes.Any(scope => scope.OpenAfterUtc != null))
                    break;

                await Task.Delay(10, timeout.Token);
            }

        var entry = Assert.Single(saved.Scopes);
        Assert.Equal(ScopePhase.Opening, entry.Phase);
        Assert.Equal(1, entry.OpenFailures);
        Assert.Equal(entry.OpenAfterUtc, Assert.Single(saved.Continuations).RetryAfterUtc);
        Assert.InRange(entry.OpenAfterUtc!.Value, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));
        await client.CancelAsync(id, new CancellationReason { ReasonCode = "test" });

        Assert.Equal(WorkflowExecutionStatus.Cancelled,
            (await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RequiringRootStartsAreInvalidRequestsAndChildrenAreRefusedAtBuild(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var services = Services(store, store.NewRepository(), new WorkflowCatalog(), new Probe(), "host");
        var root = Workflow.Create<Data>("ScopeRequiredRoot").RequiresScope<Value>().Step<Read>().Build();

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            services.GetRequiredService<IWorkflowClient>().RunAsync(root, new Data()));

        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Throws<WorkflowEngineException>(() => Start("ScopeRequiredChild").Invoke<InlineBody, Data>().Build());
    }

    private static WorkflowBuilder<Data, object> Start(string name) => new([], name);

    private static ServiceProvider Services(ProfileStore store, IWorkflowStateRepository repository, IWorkflowCatalog catalog, Probe probe,
        string name, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(options =>
        {
            options.VerifyCheckpointParts = true;
            options.MaxActivitiesPerRun = 1;
        });

        services.AddSingleton(repository);
        services.AddSingleton(catalog);
        services.AddSingleton(probe);
        services.AddSingleton(new HostIdentity(name));

        if (time != null)
            services.AddSingleton(time);

        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();

        try
        {
            store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

            return provider;
        }
        catch
        {
            provider.DisposeAsync().AsTask().GetAwaiter().GetResult();

            throw;
        }
    }

    public sealed class Data
    {
        public List<int> Items { get; set; } = [1, 2];
    }

    public sealed class Signal;

    public sealed record HostIdentity(string Name);

    public sealed record OpenFact(string Host, string Invocation, bool Reopen, int Recorded);

    public sealed class Probe
    {
        public ConcurrentQueue<OpenFact> Opens { get; } = new();
        public ConcurrentQueue<ScopeCloseReason> Closes { get; } = new();
        public ConcurrentQueue<string> ReadHosts { get; } = new();
        public ConcurrentQueue<string> Order { get; } = new();
        public int Executed;
        public int Recovered;
        public int OpenFailures;
    }

    public sealed record Value(string Host, int Thread);

    public sealed class Container(Probe probe, HostIdentity identity) : IScopeActivity<Value>
    {
        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref probe.OpenFailures) > 0 && Interlocked.Decrement(ref probe.OpenFailures) >= 0)
                throw new InvalidOperationException("open failed");

            probe.Opens.Enqueue(new(identity.Name, context.InvocationId, opening.Reopen, opening.RecordedOpens));

            return Task.FromResult(new Value(identity.Name, Environment.CurrentManagedThreadId));
        }

        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken)
        {
            probe.Closes.Enqueue(closing.Reason);
            probe.Order.Enqueue("close:" + closing.Reason);

            return Task.CompletedTask;
        }
    }

    public sealed class Read(Probe probe) : IRecoverableActivity<int>
    {
        public int CaptureRecoveryState(IActivityContext context)
        {
            _ = context.Scope<Value>();

            return 1;
        }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            await Task.Yield();
            probe.ReadHosts.Enqueue(context.Scope<Value>().Host);
            Interlocked.Increment(ref probe.Executed);
        }

        public async Task<ActivityRecoveryResult<int>> RecoverAsync(int state, IActivityContext context,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            probe.ReadHosts.Enqueue(context.Scope<Value>().Host);
            Interlocked.Increment(ref probe.Recovered);

            return new ActivityRecoveryResult<int>.Completed();
        }
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("failed");
    }

    public sealed class Cleanup(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            probe.Order.Enqueue("outside");

            return Task.CompletedTask;
        }
    }

    [Workflow("ScopeInlineBody")]
    public sealed class InlineBody : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.RequiresScope<Value>().Step<Read>();
    }
}