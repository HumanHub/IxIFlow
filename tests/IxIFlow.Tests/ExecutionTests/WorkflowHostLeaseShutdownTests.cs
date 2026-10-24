using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Coordinator;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class WorkflowHostLeaseShutdownTests : IClassFixture<SqliteTestDatabase<WorkflowHostLeaseShutdownTests>>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("foreground", false)]
    [InlineData("timer", false)]
    [InlineData("poll", false)]
    [InlineData("coordinator", false)]
    [InlineData("without-slot", false)]
    [InlineData("foreground", true)]
    public async Task DisposalWaitsForLateGrantAndSuccessorCanClaimImmediately(string entry, bool sqlite)
    {
        var time = new FakeTimeProvider();
        var inner = Store(sqlite, time);
        var store = DispatchProxy.Create<IWorkflowStateRepository, ClaimBarrier>();
        var barrier = (ClaimBarrier)(object)store;
        barrier.Inner = inner;
        var catalog = new WorkflowCatalog();
        var definition = Definition();
        await catalog.RegisterWorkflowAsync(definition);
        ServiceProvider? server = null;
        CoordinatorService? coordinator = null;
        ServiceProvider? first = null;
        Task? attempt = null, disposal = null;
        try
        {
            if (entry == "coordinator")
            {
                var services = new ServiceCollection();
                services.AddSingleton(inner);
                services.AddIxIFlow().AddCoordinator(o =>
                { o.AllowInsecure = true; o.ListenEndpoint = new("http://127.0.0.1:0"); });
                server = services.BuildServiceProvider();
                coordinator = server.GetRequiredService<CoordinatorService>();
                await coordinator.StartAsync(CancellationToken.None);
            }
            first = Services(store, catalog, time, coordinator?.Endpoint);
            var client = first.GetRequiredService<IWorkflowClient>();
            var host = first.GetRequiredService<WorkflowHost>();
            var idle = await client.RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            if (coordinator != null)
            {
                await first.GetRequiredService<CoordinatorLinks>().StartAsync(CancellationToken.None);
                await EventuallyAsync(() => first.GetRequiredService<CoordinatorLinks>().ConnectedCount == 1);
                // Finish the initial empty poll. With the clock frozen, only the link's Wake
                // can start the next pass after the event is appended.
                var pass = Signal();
                var state = first.GetRequiredService<CoordinationState>();
                state.ClaimPassCompleted += Completed;
                await first.GetServices<IHostedService>().OfType<WorkflowRecoveryService>().Single().StartAsync(CancellationToken.None);
                await pass.Task.WaitAsync(Patience);
                state.ClaimPassCompleted -= Completed;
                void Completed() => pass.TrySetResult();
            }
            await AppendAsync(inner, idle.InstanceId);
            barrier.PauseGrant = true;
            attempt = entry switch
            {
                "foreground" => client.RecoverAsync(idle.InstanceId),
                "without-slot" => host.ReadAsync(slot => slot.RecoverAsync(idle.InstanceId)),
                "poll" => first.GetServices<IHostedService>().OfType<WorkflowRecoveryService>().Single().RecoverOnceAsync(CancellationToken.None),
                _ => null
            };
            if (entry == "timer")
            {
                host.Schedule(idle.InstanceId, time.GetUtcNow().UtcDateTime.AddSeconds(1));
                time.Advance(TimeSpan.FromSeconds(1));
            }
            if (coordinator != null)
                server!.GetRequiredService<CoordinatorMatcher>().Notify(new(idle.InstanceId, true, false,
                    (await inner.GetWorkflowDemandsAsync(idle.InstanceId)).ToArray(), null));
            await barrier.Entered.Task.WaitAsync(Patience);
            Assert.NotNull(barrier.Grant);
            var stoppedHost = first.GetRequiredService<HostSettings>().Id;
            Assert.Equal(stoppedHost, barrier.HostId);

            // Dispose the provider directly, exactly as in the failing conformance scenario.
            // The store has granted ownership but has not yet returned it to the host.
            disposal = first.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            barrier.Resume.TrySetResult();
            await disposal.WaitAsync(Patience);
            if (attempt != null) await ObserveAsync(attempt);

            Assert.Equal(ExecutionLeaseRenewalStatus.Refused,
                (await inner.RenewExecutionLeasesAsync([new(idle.InstanceId, barrier.Token!, TimeSpan.FromSeconds(30))]))[0].Status);
            await using var second = Services(inner, catalog, time);
            Assert.NotEqual(stoppedHost, second.GetRequiredService<HostSettings>().Id);
            Assert.Contains(await inner.GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(second)), row => row.InstanceId == idle.InstanceId);
            var done = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId).WaitAsync(Patience);
            Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
            Assert.Equal(1, Assert.IsType<Data>(done.WorkflowData).Count);
            Assert.Equal(time.Start.AddSeconds(entry == "timer" ? 1 : 0), time.GetUtcNow());
        }
        finally
        {
            barrier.Resume.TrySetResult();
            if (attempt != null) await ObserveAsync(attempt);
            if (disposal != null) await disposal.WaitAsync(Patience);
            if (first != null) await first.DisposeAsync();
            if (coordinator != null) await coordinator.StopAsync(CancellationToken.None);
            if (server != null) await server.DisposeAsync();
            if (inner is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShutdownRefusesAClaimStillPreparingAtTheStoreBoundary(bool sqlite, bool drain)
    {
        var time = new FakeTimeProvider();
        var inner = Store(sqlite, time);
        var store = DispatchProxy.Create<IWorkflowStateRepository, ClaimBarrier>();
        var barrier = (ClaimBarrier)(object)store;
        barrier.Inner = inner;
        var catalog = new WorkflowCatalog();
        var definition = Definition();
        await catalog.RegisterWorkflowAsync(definition);
        await using var first = Services(store, catalog, time);
        Task? attempt = null;
        try
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var host = first.GetRequiredService<WorkflowHost>();
            var idle = await client.RunAsync(definition, new Data());
            await AppendAsync(inner, idle.InstanceId);
            var claims = barrier.Claims;
            barrier.PausePreparation = true;
            attempt = client.RecoverAsync(idle.InstanceId);
            await barrier.Entered.Task.WaitAsync(Patience);
            var shutdown = drain ? host.StopAsync(CancellationToken.None) : ((IAsyncDisposable)host).DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            barrier.Resume.TrySetResult();
            await shutdown.WaitAsync(Patience);
            await ObserveAsync(attempt);
            Assert.Equal(claims, barrier.Claims);
            await using var second = Services(inner, catalog, time);
            Assert.Equal(WorkflowExecutionStatus.Success,
                (await second.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId).WaitAsync(Patience)).Status);
            Assert.Equal(time.Start, time.GetUtcNow());
        }
        finally
        {
            barrier.Resume.TrySetResult();
            if (attempt != null) await ObserveAsync(attempt);
            await first.DisposeAsync();
            if (inner is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseSetupFailureReleasesTheStoreGrant(bool sqlite)
    {
        var time = new FakeTimeProvider();
        var store = Store(sqlite, time);
        var catalog = new WorkflowCatalog();
        var definition = Definition();
        await catalog.RegisterWorkflowAsync(definition);
        try
        {
            await using var provider = Services(store, catalog, time);
            var idle = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            await AppendAsync(store, idle.InstanceId);
            var instance = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
            await using var watch = new CancellationWatch(store, time);
            await using var renewer = new ExecutionLeaseRenewer(store, time);
            watch.Dispose();
            var query = await StoreTestSupport.ClaimAsync(provider);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => WorkflowExecutionLease.TryAcquireAsync(store,
                instance, new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)), watch, renewer, query,
                _ => { }, _ => { }, _ => { }, time));
            var token = Guid.NewGuid().ToString("N");
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, token, TimeSpan.FromSeconds(30),
                new ClaimQuery(Guid.NewGuid(), query.Capabilities)));
            await store.ReleaseExecutionLeaseAsync(instance.InstanceId, token);
            Assert.Equal(time.Start, time.GetUtcNow());
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    private static WorkflowDefinition Definition() => new WorkflowBuilder<Data, object>([], "ShutdownClaim")
        .WaitFor<Go>("go").Assign(c => c.WorkflowData.Count, c => c.WorkflowData.Count + 1).Build();

    private static IWorkflowStateRepository Store(bool sqlite, TimeProvider time) => sqlite
        ? new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowHostLeaseShutdownTests>.ConnectionString, time)
        : new InMemoryWorkflowStateRepository(time);

    private static ServiceProvider Services(IWorkflowStateRepository store, IWorkflowCatalog catalog, TimeProvider time, Uri? endpoint = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(time);
        var builder = services.AddIxIFlow(o => o.PollInterval = TimeSpan.FromHours(1));
        services.AddSingleton(catalog);
        if (endpoint != null) builder.UseCoordinators(o => { o.AllowInsecure = true; o.Coordinators.Add(endpoint); });
        var provider = services.BuildServiceProvider();
        // Match generic-host resolution order, before the first slot lazily resolves its lease services.
        _ = provider.GetServices<IHostedService>().ToArray();
        return provider;
    }

    private static async Task AppendAsync(IWorkflowStateRepository store, string id) =>
        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(id, Guid.NewGuid().ToString("N"),
            WorkflowTypeIdentity.SavedName(typeof(Go)), JsonSerializer.Serialize(new Go()), Key: "go"))).Status);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ObserveAsync(Task task)
    {
        try { await task.WaitAsync(Patience); }
        catch (OperationCanceledException) { }
        catch (WorkflowEngineException) { } // The unfixed setup failure is secondary to the lease assertion.
        catch (ObjectDisposedException) { }
    }
    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(elapsed.Elapsed < Patience, "Coordinator link did not connect");
            await Task.Delay(10);
        }
    }

    public class ClaimBarrier : DispatchProxy
    {
        internal IWorkflowStateRepository Inner = null!;
        internal bool PauseGrant, PausePreparation;
        internal int Claims;
        internal string? Token;
        internal Guid HostId;
        internal ExecutionLeaseGrant? Grant;
        internal readonly TaskCompletionSource Entered = Signal(), Resume = Signal();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync))
            {
                Interlocked.Increment(ref Claims);
                if (PauseGrant)
                {
                    PauseGrant = false;
                    Token = (string)args![1]!;
                    HostId = ((ClaimQuery)args[3]!).HostId;
                    return HoldGrantAsync((Task<ExecutionLeaseGrant?>)Forward(method, args)!);
                }
            }
            if (method.Name == nameof(IWorkflowStateRepository.GetPendingEventsAsync) && PausePreparation)
            {
                PausePreparation = false;
                return HoldPreparationAsync((Task<InboxPage>)Forward(method, args)!);
            }
            return Forward(method, args);
        }

        private object? Forward(MethodInfo method, object?[]? args)
        {
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
        private async Task<ExecutionLeaseGrant?> HoldGrantAsync(Task<ExecutionLeaseGrant?> task)
        {
            Grant = await task;
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(Patience);
            return Grant;
        }
        private async Task<InboxPage> HoldPreparationAsync(Task<InboxPage> task)
        {
            var page = await task;
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(Patience);
            return page;
        }
    }

    public sealed class Data { public int Count { get; set; } }
    public sealed class Go;
}
