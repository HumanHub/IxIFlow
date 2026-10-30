using System.Data.Common;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "waits"), Trait("Spec", "D-02")]
public sealed class EventDecisionProgressScenarios
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public static IEnumerable<object[]> Deliveries => StoreProfiles.ProfileNames
        .Where(profile => profile != StoreProfiles.LocalSqlite)
        .SelectMany(profile => new[] { "resume", "reply" }.Select(delivery => new object[] { profile, delivery }));

    [Theory(Timeout = 60000), MemberData(nameof(Deliveries))]
    public Task RefusedClaimsRetryAfterStoreContention(string profile, string delivery) =>
        DecideAfterRefusalAsync(profile, delivery, heldLease: false);

    [Theory(Timeout = 60000), MemberData(nameof(Deliveries))]
    public Task RefusedClaimsRetryAfterALeaseReleaseWithoutAHint(string profile, string delivery) =>
        DecideAfterRefusalAsync(profile, delivery, heldLease: true);

    private static async Task DecideAfterRefusalAsync(string profile, string delivery, bool heldLease)
    {
        var counter = new Counter();
        await using var cluster = await DecisionCluster.CreateAsync(profile, counter);
        var first = cluster.AddHost(out var firstGate);
        var second = cluster.AddHost(out var secondGate);

        var definition = Workflow.Create<Data>("EventDecisionProgress").Step<Noop>()
            .WaitFor<Signal>("approval").Step<Count>().WaitFor<Signal>("approval").Build();

        await cluster.Registry.RegisterWorkflowAsync(definition);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var started = await first.RunAsync(definition, new Data(), cancellationToken: cancellation.Token).WaitAsync(Patience);
        var id = started.InstanceId;
        var store = cluster.Store.NewRepository();
        var initial = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(WorkflowStatus.Suspended, initial.Status);
        Assert.Equal(1, firstGate.FirstEpoch);
        Assert.Equal(0, firstGate.ReadThrough);
        var wait = Assert.Single(await first.GetOpenWaitsAsync(new(InstanceId: id), cancellation.Token));
        var query = await StoreTestSupport.ClaimAsync(cluster.Registry);
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        var ownerHeld = false;
        Task<WorkflowExecutionResult>? sendingFirst = null;
        Task<WorkflowExecutionResult>? sendingSecond = null;

        try
        {
            if (heldLease)
            {
                // A real owner, with a real pending event. Release directly through the store:
                // its hint may be lost (for example if the writer exits before publishing it).
                Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(id, "delivery-1",
                    WorkflowTypeIdentity.SavedName(typeof(Signal)), "{}", Key: delivery == "reply" ? null : "approval",
                    TargetWaitId: delivery == "reply" ? wait.WaitId : null))).Status);

                Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(id, "owner", TimeSpan.FromMinutes(1), query));
                ownerHeld = true;
            }

            firstGate.Armed = secondGate.Armed = true;
            sendingFirst = SendAsync(first);
            sendingSecond = SendAsync(second);
            await Task.WhenAll(firstGate.BeforeClaim.Task, secondGate.BeforeClaim.Task).WaitAsync(Patience);

            if (!heldLease)
            {
                if (profile == StoreProfiles.LocalMemory)
                {
                    Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(id, "owner", TimeSpan.FromMinutes(1), query));
                    ownerHeld = true;
                }
                else
                {
                    connection = cluster.Store.NewConnection();
                    await connection.OpenAsync(cancellation.Token);
                    transaction = await connection.BeginTransactionAsync(cancellation.Token);

                    await connection.ExecuteAsync(cluster.Store.Profile == StoreProfiles.DistributedPostgreSql
                            ? "SELECT pg_advisory_xact_lock(hashtextextended(@Id COLLATE \"C\", 0))"
                            : "SELECT InstanceId FROM dbo.IxIFlowWorkflowInstances WITH (UPDLOCK, ROWLOCK) WHERE InstanceId=@Id",
                        new { Id = id }, transaction);
                }
            }

            firstGate.ContinueClaim.TrySetResult();
            secondGate.ContinueClaim.TrySetResult();
            await Task.WhenAll(firstGate.Refused.Task, secondGate.Refused.Task).WaitAsync(Patience);
            Assert.Equal(0, counter.Executions);
            Assert.False(sendingFirst.IsCompleted);
            Assert.False(sendingSecond.IsCompleted);
            var pending = (await store.GetEventAsync(id, "delivery-1"))!;
            Assert.Equal(EventOutcome.Pending, pending.Outcome);
            Assert.Equal(1, pending.Sequence);
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(WorkflowStatus.Suspended, saved.Status);
            Assert.Equal(initial.Revision, saved.Revision);
            Assert.Equal(pending.ReceivedAtUtc, saved.RunnableAtUtc);
            Assert.Equal(0, firstGate.ReadThrough);

            if (ownerHeld)
                Assert.Empty(await store.GetRunnableInstancesAsync(query));
            else
                Assert.Contains(await store.GetRunnableInstancesAsync(query), item => item.InstanceId == id);

            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellation.Token);
                await transaction.DisposeAsync();
                transaction = null;
            }

            if (ownerHeld)
            {
                var released = await store.ReleaseExecutionLeaseAsync(id, "owner");
                ownerHeld = false;
                Assert.True(released.Released);
                Assert.True(released.UnreadEvents);
                Assert.True(released.Claimable);
            }

            Assert.Contains(await store.GetRunnableInstancesAsync(query), item => item.InstanceId == id);

            // Neither a scan nor a host wake runs here. Only the waiting callers can make progress.
            firstGate.ContinueRetry.TrySetResult();
            secondGate.ContinueRetry.TrySetResult();
            var decided = await Task.WhenAll(sendingFirst, sendingSecond).WaitAsync(Patience);

            Assert.All(decided, result =>
            {
                Assert.Equal(EventOutcome.Consumed, result.Event!.Outcome);
                Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            });

            Assert.Equal(1, counter.Executions);
            Assert.Equal(EventOutcome.Consumed, (await store.GetEventAsync(id, "delivery-1"))!.Outcome);
            Assert.True(firstGate.SuccessfulRetries + secondGate.SuccessfulRetries > 0);

            // A retry of a decided event must not feed the next wait or run its activity again.
            Assert.Equal(EventOutcome.Consumed, (await SendAsync(first).WaitAsync(Patience)).Event!.Outcome);
            Assert.Equal(1, counter.Executions);

            Assert.Equal(WorkflowExecutionStatus.Success, (await second.ResumeAsync(id, "approval", new Signal(),
                "delivery-2", cancellation.Token).WaitAsync(Patience)).Status);
        }
        finally
        {
            firstGate.ContinueClaim.TrySetResult();
            secondGate.ContinueClaim.TrySetResult();
            firstGate.ContinueRetry.TrySetResult();
            secondGate.ContinueRetry.TrySetResult();

            if (transaction != null)
                await transaction.DisposeAsync();

            if (connection != null)
                await connection.DisposeAsync();

            if (ownerHeld)
                await store.ReleaseExecutionLeaseAsync(id, "owner");

            await cancellation.CancelAsync();

            foreach (var task in new[] { sendingFirst, sendingSecond })
                if (task != null)
                    try
                    {
                        await task.WaitAsync(Patience);
                    }
                    catch (OperationCanceledException)
                    {
                    }
        }

        Task<WorkflowExecutionResult> SendAsync(IWorkflowClient client) => delivery == "reply"
            ? client.ReplyAsync(wait.Token, new Signal(), "delivery-1", cancellation.Token)
            : client.ResumeAsync(id, "approval", new Signal(), "delivery-1", cancellation.Token);
    }

    public class ClaimGate : DispatchProxy
    {
        private IWorkflowStateRepository _inner = null!;
        private int _attempts;
        public bool Armed;
        public long FirstEpoch;
        public long? ReadThrough;
        public int SuccessfulRetries;
        public readonly TaskCompletionSource BeforeClaim = SignalSource();
        public readonly TaskCompletionSource ContinueClaim = SignalSource();
        public readonly TaskCompletionSource Refused = SignalSource();
        public readonly TaskCompletionSource ContinueRetry = SignalSource();

        public static IWorkflowStateRepository Wrap(IWorkflowStateRepository inner, out ClaimGate gate)
        {
            var proxy = Create<IWorkflowStateRepository, ClaimGate>();
            gate = (ClaimGate)proxy;
            gate._inner = inner;

            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync))
                return ClaimAsync(method, args);

            if (method.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync) && !Armed)
                ReadThrough = ((CheckpointCommit)args![0]!).InboxReadThrough;

            return Forward(method, args);
        }

        private object? Forward(MethodInfo method, object?[]? args)
        {
            try
            {
                return method.Invoke(_inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();

                throw;
            }
        }

        private async Task<ExecutionLeaseGrant?> ClaimAsync(MethodInfo method, object?[]? args)
        {
            if (!Armed)
            {
                var original = await (Task<ExecutionLeaseGrant?>)Forward(method, args)!;

                if (original != null)
                    FirstEpoch = original.Epoch;

                return original;
            }

            if (Interlocked.Increment(ref _attempts) == 1)
            {
                BeforeClaim.TrySetResult();
                await ContinueClaim.Task.WaitAsync(Patience);
                var refused = await (Task<ExecutionLeaseGrant?>)Forward(method, args)!;
                Assert.Null(refused);
                Refused.TrySetResult();

                return refused;
            }

            await ContinueRetry.Task.WaitAsync(Patience);
            var grant = await (Task<ExecutionLeaseGrant?>)Forward(method, args)!;

            if (grant != null)
                Interlocked.Increment(ref SuccessfulRetries);

            return grant;
        }
    }

    // The profile's real store and links, with no recovery service, as in ConformanceCluster.
    // Retain ProfileStore here to hold a real provider lock without changing existing test helpers.
    private sealed class DecisionCluster(ProfileStore store, Counter counter) : IAsyncDisposable
    {
        private readonly List<ServiceProvider> _hosts = [];
        public ProfileStore Store => store;
        public IWorkflowCatalog Registry { get; } = new WorkflowCatalog();

        public static async Task<DecisionCluster> CreateAsync(string profile, Counter counter) =>
            new(await StoreProfiles.CreateAsync(profile), counter);

        public IWorkflowClient AddHost(out ClaimGate gate)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow();
            var repository = ClaimGate.Wrap(store.NewRepository(), out gate);
            services.Replace(ServiceDescriptor.Singleton(repository));
            services.Replace(ServiceDescriptor.Singleton(Registry));
            services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
            services.AddSingleton(counter);
            services.AddTransient<Noop>();
            services.AddTransient<Count>();
            store.ConfigureHost(services);
            var provider = services.BuildServiceProvider();
            _hosts.Add(provider);
            var client = provider.GetRequiredService<IWorkflowClient>();
            store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

            return client;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var host in _hosts)
            {
                await host.GetRequiredService<WorkflowHost>().StopAsync(default);
                await host.DisposeAsync();
            }

            await store.DisposeAsync();
        }
    }

    private static TaskCompletionSource SignalSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public sealed class Data;

    public sealed class Signal;

    public sealed class Counter
    {
        public int Executions;
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Count(Counter counter) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref counter.Executions);

            return Task.CompletedTask;
        }
    }
}