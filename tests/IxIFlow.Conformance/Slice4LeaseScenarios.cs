using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "durability")]
[Trait("Spec", "A-03")]
[Trait("Spec", "B-08")]
[Trait("Spec", "J-03")]
public sealed class Slice4LeaseScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnInFlightOwnerLosesItsRetainedLeaseAndASuccessorRecoversExactlyOneEffect(string profile)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, clock);
        var repository = database.NewRepository(clock);
        var registry = new WorkflowCatalog();
        var probe = new Probe();
        var retained = FlightStore.Wrap(repository);
        var formerStore = (FlightStore)retained;
        await using var former = Services(retained, registry, probe);

        // SQLite has one owning repository per local process. Server hosts use separate repositories.
        var successorRepository = database.Profile == StoreProfiles.LocalSqlite
            ? repository
            : database.NewRepository(clock);

        var successorStore = SwitchScenarios.LeaseProbe.Wrap(successorRepository, retainRelease: false);
        await using var successor = Services(successorStore, registry, probe);
        await database.StartHostLinksAsync(former);
        await database.StartHostLinksAsync(successor);
        var definition = Workflow.Create<Data>("InFlightTakeover").Step<BlockedEffect>().Build();
        var id = Guid.NewGuid().ToString("N");
        var run = former.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new() { InstanceId = id });

        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, probe.Effects);
            var prior = formerStore.Grant!;
            Assert.NotNull(prior);
            var interrupted = (await repository.GetWorkflowInstanceAsync(id))!;

            Assert.Null(
                await repository.TryAcquireExecutionLeaseAsync(
                    id, "too-early", TimeSpan.FromMinutes(1),
                    await StoreTestSupport.ClaimAsync(registry)));

            formerStore.RefuseRenewals = true;
            Assert.False((await retained.ReleaseExecutionLeaseAsync(id, formerStore.Token)).Released);
            Assert.True(formerStore.ReleaseAttempts > 0);

            Assert.Null(
                await repository.TryAcquireExecutionLeaseAsync(
                    id, "still-too-early", TimeSpan.FromMinutes(1),
                    await StoreTestSupport.ClaimAsync(registry)));

            if (profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite)
                clock.Advance(TimeSpan.FromMinutes(6));
            else
            {
                await database.ExpireExecutionLeaseAsync(id);
                clock.Advance(TimeSpan.FromMinutes(6));
            }

            await probe.OwnershipCancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var result = await successor.GetRequiredService<IWorkflowClient>().RecoverAsync(id).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(1, probe.Effects);
            Assert.Equal(1, probe.Recoveries);
            var next = ((SwitchScenarios.LeaseProbe)successorStore).Grant!;
            Assert.Equal(1, next.CrashCount);
            Assert.True(next.Epoch > prior.Epoch);

            Assert.Equal(
                WorkflowCommitStatus.StaleEpoch,
                (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(interrupted, prior.Epoch, interrupted.Revision)))
                .Status);

            Assert.Equal(WorkflowStatus.Completed, (await repository.GetWorkflowInstanceAsync(id))!.Status);

            var completedRevision = (await repository.GetWorkflowInstanceAsync(id))!.Revision;
            probe.Release.TrySetResult();
            var formerResult = await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WorkflowExecutionStatus.Success, formerResult.Status);
            Assert.Equal(completedRevision, (await repository.GetWorkflowInstanceAsync(id))!.Revision);
            Assert.True(formerStore.ReleaseAttempts >= 2);
            Assert.Equal(1, probe.Effects);
        }
        finally
        {
            probe.Release.TrySetResult();
            await database.StopHostLinksAsync(former);
            await database.StopHostLinksAsync(successor);
        }

        ServiceProvider Services(IWorkflowStateRepository store, IWorkflowCatalog catalog, Probe activity)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow(options => options.DrainTimeout = TimeSpan.FromMilliseconds(30));
            services.AddSingleton(store);
            services.AddSingleton(new InProcessInstanceGate());
            services.AddSingleton(catalog);
            services.AddSingleton(activity);
            services.AddTransient<BlockedEffect>();

            services.AddSingleton<TimeProvider>(clock);
            database.ConfigureHost(services);

            return services.BuildServiceProvider();
        }
    }

    public sealed class Data;

    public sealed class Probe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OwnershipCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Effects;
        public int Recoveries;
    }

    public sealed class BlockedEffect(Probe probe) : IRecoverableActivity<bool>
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Effects);
            probe.Entered.TrySetResult();
            using var cancellation = cancellationToken.Register(() => probe.OwnershipCancelled.TrySetResult());
            await probe.Release.Task;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(
            bool state,
            IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Recoveries);

            return Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Completed());
        }
    }

    public class FlightStore : DispatchProxy
    {
        private IWorkflowStateRepository _inner = null!;
        public bool RefuseRenewals;
        public string Token { get; private set; } = "";
        public ExecutionLeaseGrant? Grant { get; private set; }
        public int ReleaseAttempts;

        public static IWorkflowStateRepository Wrap(IWorkflowStateRepository inner)
        {
            var proxy = Create<IWorkflowStateRepository, FlightStore>();
            ((FlightStore)proxy)._inner = inner;

            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.ReleaseExecutionLeaseAsync))
            {
                Interlocked.Increment(ref ReleaseAttempts);

                return Task.FromResult(new ExecutionLeaseReleaseResult(false));
            }

            if (RefuseRenewals && method.Name == nameof(IWorkflowStateRepository.RenewExecutionLeasesAsync))
            {
                var leases = (IReadOnlyList<ExecutionLeaseRenewal>)args![0]!;

                return Task.FromResult<IReadOnlyList<ExecutionLeaseRenewalResult>>(
                    leases
                        .Select(_ => new ExecutionLeaseRenewalResult(ExecutionLeaseRenewalStatus.Refused)).ToArray());
            }

            try
            {
                var result = method.Invoke(_inner, args);

                if (method.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync))
                {
                    Token = (string)args![1]!;

                    return Acquired((Task<ExecutionLeaseGrant?>)result!);
                }

                return result;
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();

                throw;
            }
        }

        private async Task<ExecutionLeaseGrant?> Acquired(Task<ExecutionLeaseGrant?> task)
        {
            Grant = await task;

            return Grant;
        }
    }
}