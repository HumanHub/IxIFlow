using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "J-02"), Trait("Spec", "J-03"), Trait("Feature", "recovery")]
public sealed class CoordinatorFaultScenarios
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [MemberData(nameof(StoreProfiles.Coordinated), MemberType = typeof(StoreProfiles))]
    public async Task PollClaimsOnceWhenTheWokenHostDiesBeforeItsClaim(string profileName)
    {
        await using var profile = await StoreProfiles.CreateAsync(profileName);
        var probe = new Probe();
        probe.Finish.TrySetResult();
        var registry = new WorkflowVersionRegistry();
        var id = await SeedAsync(profile, registry, probe);
        await using var polling = Host(profile, registry, probe, linked: false);
        await using var doomed = Host(profile, registry, probe, linked: true);
        doomed.Fault.BeforeClaim = () => throw new IOException("Host died before acquiring the lease");
        await profile.StartHostLinksAsync(doomed.Provider);
        await polling.StartScannerAsync();
        await doomed.StartScannerAsync();

        await NotifyAsync(doomed.Provider, id);
        await doomed.Fault.ClaimEntered.Task.WaitAsync(Patience);
        await doomed.StopAsync();
        Assert.Empty(doomed.Fault.Grants);
        Assert.Equal(0, probe.Executions);
        Assert.Contains(await profile.NewRepository().GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(polling.Provider)),
            instance => instance.InstanceId == id);

        // This host has no coordinator link or store wake source. Only its poll clock moves.
        polling.Clock.Advance(polling.Settings.PollInterval);
        await UntilCompletedAsync(profile, id);
        Assert.Equal(1, probe.Executions);
        Assert.Single(polling.Fault.Grants);
        Assert.Equal(1, polling.Fault.Attempts);
        await AssertConsumedOnceAsync(profile, id);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.Coordinated), MemberType = typeof(StoreProfiles))]
    public async Task TheLeaseSettlesAWokenHostAndAPollingHostClaimingTogether(string profileName)
    {
        await using var profile = await StoreProfiles.CreateAsync(profileName);
        var probe = new Probe();
        var registry = new WorkflowVersionRegistry();
        var id = await SeedAsync(profile, registry, probe);
        await using var polling = Host(profile, registry, probe, linked: false);
        await using var woken = Host(profile, registry, probe, linked: true);
        var releaseClaims = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        polling.Fault.BeforeClaim = woken.Fault.BeforeClaim = () => releaseClaims.Task.WaitAsync(Patience);
        await profile.StartHostLinksAsync(woken.Provider);
        await polling.StartScannerAsync();
        await woken.StartScannerAsync();

        try
        {
            await NotifyAsync(woken.Provider, id);
            await woken.Fault.ClaimEntered.Task.WaitAsync(Patience);
            polling.Clock.Advance(polling.Settings.PollInterval);
            await polling.Fault.ClaimEntered.Task.WaitAsync(Patience);
            Assert.Equal(0, probe.Executions);
            releaseClaims.TrySetResult();
            await Task.WhenAll(polling.Fault.ClaimReturned.Task, woken.Fault.ClaimReturned.Task).WaitAsync(Patience);

            Assert.Equal(1, polling.Fault.Attempts);
            Assert.Equal(1, woken.Fault.Attempts);
            var grant = Assert.Single(polling.Fault.Grants.Concat(woken.Fault.Grants));
            Assert.True(grant.Epoch > 0);
            Assert.NotEqual(polling.Fault.Token, woken.Fault.Token);
            // Keep the winner inside the activity until both lease calls have returned: the
            // loser must lose to the live lease, rather than observe an already completed row.
            probe.Finish.TrySetResult();
            await UntilCompletedAsync(profile, id);
            Assert.Equal(1, probe.Executions);
            await AssertConsumedOnceAsync(profile, id);
        }
        finally { releaseClaims.TrySetResult(); probe.Finish.TrySetResult(); }
    }

    private static async Task<string> SeedAsync(ProfileStore profile, IWorkflowVersionRegistry registry, Probe probe)
    {
        var definition = Workflow.Create<Data>("CoordinatorFault")
            .Step<Noop>().WaitFor<Approval>("approval").Step<Count>().Build();
        await registry.RegisterWorkflowAsync(definition);
        await using var seed = Host(profile, registry, probe, linked: false);
        var result = await seed.Provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        return result.InstanceId;
    }

    private static TestHost Host(ProfileStore profile, IWorkflowVersionRegistry registry, Probe probe, bool linked)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        var repository = DispatchProxy.Create<IWorkflowStateRepository, ClaimFault>();
        var fault = (ClaimFault)repository;
        fault.Inner = profile.NewRepository();
        services.AddSingleton(repository);
        services.AddSingleton(registry);
        services.AddSingleton(new InProcessInstanceGate());
        services.AddSingleton(probe);
        services.AddTransient<Noop>();
        services.AddTransient<Count>();
        if (linked) profile.ConfigureHost(services);
        return new(profile, services.BuildServiceProvider(), fault);
    }

    private static async Task NotifyAsync(IServiceProvider writer, string id)
    {
        var appended = await writer.GetRequiredService<IWorkflowStateRepository>().AppendEventAsync(new(id,
            "approval-once", typeof(Approval).AssemblyQualifiedName!, JsonSerializer.Serialize(new Approval()), Key: "approval"));
        Assert.Equal(EventAppendStatus.Stored, appended.Status);
        // Real production notification delivery, real authenticated stream, real Wake -> scanner.
        await writer.GetRequiredService<IRunnableNotifier>().NotifyAsync(appended.Notice!, CancellationToken.None);
    }

    private static async Task UntilCompletedAsync(ProfileStore profile, string id)
    {
        using var patience = new CancellationTokenSource(Patience);
        var repository = profile.NewRepository();
        while ((await repository.GetWorkflowInstanceSummaryAsync(id))?.Status != WorkflowStatus.Completed)
            await Task.Delay(10, patience.Token);
    }

    private static async Task AssertConsumedOnceAsync(ProfileStore profile, string id)
    {
        var repository = profile.NewRepository();
        Assert.Equal(EventOutcome.Consumed, (await repository.GetEventAsync(id, "approval-once"))!.Outcome);
        Assert.Single(await repository.GetTraceAsync(id), entry => entry.Entry.EntryType == TraceEntryType.EventReceived);
        Assert.Empty((await repository.GetPendingEventsAsync(id, 0, 100)).Events);
    }

    private sealed class TestHost(ProfileStore profile, ServiceProvider provider, ClaimFault fault) : IAsyncDisposable
    {
        internal ServiceProvider Provider => provider;
        internal ClaimFault Fault => fault;
        internal HostSettings Settings => provider.GetRequiredService<HostSettings>();
        internal TimerRecordingClock Clock { get; } = new();
        private WorkflowRecoveryService? _scanner;

        internal async Task StartScannerAsync()
        {
            var state = provider.GetRequiredService<CoordinationState>();
            _scanner = new(provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IWorkflowStateRepository>(), NullLogger<WorkflowRecoveryService>.Instance,
                Clock, Settings, coordination: state);
            await _scanner.StartAsync(CancellationToken.None);
            await Clock.WaitForTimerAsync(timer => timer.Pending && timer.DueTime ==
                (state.Connected ? state.PollInterval : Settings.PollInterval));
        }

        internal async Task StopAsync()
        {
            if (_scanner != null)
            {
                await _scanner.StopAsync(CancellationToken.None);
                _scanner.Dispose();
                _scanner = null;
            }
            await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await profile.StopHostLinksAsync(provider);
        }

        public async ValueTask DisposeAsync() { await StopAsync(); await provider.DisposeAsync(); }
    }

    public class ClaimFault : DispatchProxy
    {
        internal IWorkflowStateRepository Inner = null!;
        internal Func<Task>? BeforeClaim;
        internal TaskCompletionSource ClaimEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ClaimReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<ExecutionLeaseGrant> Grants { get; } = [];
        internal string? Token;
        internal int Attempts;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync))
                return ClaimAsync(targetMethod, args!);
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }

        private async Task<ExecutionLeaseGrant?> ClaimAsync(MethodInfo method, object?[] args)
        {
            Interlocked.Increment(ref Attempts);
            Token = (string)args[1]!;
            ClaimEntered.TrySetResult();
            if (BeforeClaim != null) await BeforeClaim();
            var grant = await (Task<ExecutionLeaseGrant?>)method.Invoke(Inner, args)!;
            if (grant != null) Grants.Add(grant);
            ClaimReturned.TrySetResult();
            return grant;
        }
    }

    public sealed class Data;
    public sealed class Approval;
    public sealed class Probe
    {
        public int Executions;
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Count(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Executions); await probe.Finish.Task.WaitAsync(cancellationToken); }
    }
}
