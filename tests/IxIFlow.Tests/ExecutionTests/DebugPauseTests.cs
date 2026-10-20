using IxIFlow.Builders;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class DebugPauseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task InFlightActivityCommitsBeforePauseAndTheLeaseIsReleased()
    {
        var store = new ProbeStore();
        var probe = new Probe();
        await using var services = Services(store, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("DrainPause").Step<Block>(s => s.Output(a => a.Value).To(c => c.WorkflowData.Value))
            .Step<After>().Build();
        var run = client.RunAsync(definition, new Data(), new() { InstanceId = "drain" });
        await probe.Started.Task.WaitAsync(Timeout);
        await ops.PauseAsync("drain", "operator");
        await store.PauseObserved.Task.WaitAsync(Timeout);
        Assert.False(run.IsCompleted);
        Assert.Null((await store.GetDebugStatesAsync(["drain"]))["drain"].Commands.Single().AcknowledgedRevision);
        probe.Finish.TrySetResult();
        var paused = await run.WaitAsync(Timeout);
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        Assert.Equal(42, Assert.IsType<Data>(paused.WorkflowData).Value);
        Assert.Equal(0, probe.AfterCount);
        var saved = await store.GetWorkflowInstanceAsync("drain");
        Assert.Equal(WorkflowStatus.Paused, saved!.Status);
        Assert.Null(saved.RunnableAtUtc);
        Assert.DoesNotContain(await store.GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(services, 20)), r => r.InstanceId == "drain");
        var grant = await store.TryAcquireExecutionLeaseAsync("drain", "inspection", TimeSpan.FromSeconds(10),
            await StoreTestSupport.ClaimAsync(services), ExecutionClaimKind.Operator);
        Assert.NotNull(grant);
        await store.ReleaseExecutionLeaseAsync("drain", "inspection");
        var trace = (await store.GetTraceAsync("drain")).Select(e => e.Entry).ToList();
        Assert.True(trace.FindIndex(e => e.EntryType == TraceEntryType.ActivityCompleted) < trace.FindIndex(e => e.EntryType == TraceEntryType.Paused));
        Assert.Equal("operator", Assert.IsType<DebugCommandTrace>(trace.Single(e => e.EntryType == TraceEntryType.Paused).Payload).Actor);
        Assert.All(trace.Where(e => e.EntryType is TraceEntryType.ActivityStarted or TraceEntryType.ActivityCompleted), e => Assert.Equal("root/0", e.StepId));
    }

    [Fact]
    public async Task IdleInstancePausesUnderLeaseAndEventsWaitForContinueOnAnotherHost()
    {
        var store = new ProbeStore(); var probe = new Probe();
        await using var first = Services(store, probe);
        await using var second = Services(store, probe);
        var definition = Workflow.Create<Data>("IdlePause").Step<Noop>().WaitFor<Go>("go").Step<After>().Build();
        var client = first.GetRequiredService<IWorkflowClient>();
        var run = await client.RunAsync(definition, new Data());
        var paused = await first.GetRequiredService<IWorkflowOperations>().PauseAsync(run.InstanceId, "pauser");
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        var revision = (await store.GetWorkflowInstanceAsync(run.InstanceId))!.Revision;
        var appended = await client.ResumeAsync(run.InstanceId, "go", new Go(), "held");
        Assert.Equal(WorkflowExecutionStatus.Paused, appended.Status);
        Assert.Equal(EventOutcome.Pending, appended.Event!.Outcome);
        Assert.Equal(revision, (await store.GetWorkflowInstanceAsync(run.InstanceId))!.Revision);
        Assert.Null((await store.GetWorkflowInstanceAsync(run.InstanceId))!.RunnableAtUtc);
        var continued = await second.GetRequiredService<IWorkflowOperations>().ContinueAsync(run.InstanceId, "resumer");
        Assert.Equal(WorkflowExecutionStatus.Success, continued.Status);
        Assert.Equal(1, probe.AfterCount);
        Assert.Equal(EventOutcome.Consumed, (await store.GetEventAsync(run.InstanceId, "held"))!.Outcome);
        var trace = await store.GetTraceAsync(run.InstanceId);
        Assert.Equal("resumer", Assert.IsType<DebugCommandTrace>(trace.Single(e => e.Entry.EntryType == TraceEntryType.Continued).Entry.Payload).Actor);
    }

    [Fact]
    public async Task CancellationEndsAPauseOnADebugDisabledHost()
    {
        var store = new ProbeStore(); var probe = new Probe();
        await using var first = Services(store, probe);
        await using var second = Services(store, probe, debugging: false);
        var client = first.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(Waiting("CancelPause"), new Data());
        await first.GetRequiredService<IWorkflowOperations>().PauseAsync(result.InstanceId, "operator");
        var cancelled = await second.GetRequiredService<IWorkflowClient>().CancelAsync(result.InstanceId, new() { ReasonCode = "cancel" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal(WorkflowStatus.Cancelled, (await store.GetWorkflowInstanceAsync(result.InstanceId))!.Status);
    }

    [Fact]
    public async Task PauseIntentSurvivesAnOwnerCrashAfterTheActivityCommit()
    {
        var store = new ProbeStore { FailPause = true }; var probe = new Probe();
        await using var first = Services(store, probe);
        await using var second = Services(store, probe);
        var definition = Workflow.Create<Data>("CrashPause").Step<Block>(s => s.Output(a => a.Value).To(c => c.WorkflowData.Value)).Step<After>().Build();
        var run = first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new() { InstanceId = "crash" });
        await probe.Started.Task.WaitAsync(Timeout);
        await first.GetRequiredService<IWorkflowOperations>().PauseAsync("crash", "operator");
        await store.PauseObserved.Task.WaitAsync(Timeout);
        probe.Finish.TrySetResult();
        await Assert.ThrowsAsync<WorkflowEngineException>(() => run.WaitAsync(Timeout));
        Assert.Equal(0, probe.AfterCount);
        Assert.Equal(42, System.Text.Json.JsonSerializer.Deserialize<Data>((await store.GetWorkflowInstanceAsync("crash"))!.WorkflowDataJson.InlineText)!.Value);
        Assert.Null((await store.GetDebugStatesAsync(["crash"]))["crash"].Commands.Single().AcknowledgedRevision);
        store.FailPause = false;
        var recovered = await second.GetRequiredService<IWorkflowClient>().RecoverAsync("crash");
        Assert.Equal(WorkflowExecutionStatus.Paused, recovered.Status);
        Assert.Equal(1, probe.BlockCount);
        var continued = await second.GetRequiredService<IWorkflowOperations>().ContinueAsync("crash", "recovery");
        Assert.Equal(WorkflowExecutionStatus.Success, continued.Status);
        Assert.Equal(1, probe.AfterCount);
    }

    [Fact]
    public async Task OwnershipLossDominatesAPause()
    {
        var store = new ProbeStore(); var probe = new Probe();
        await using var services = Services(store, probe, shortLease: true);
        var run = services.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("LostPause").Step<Block>().Step<After>().Build(), new Data(), new() { InstanceId = "lost" });
        await probe.Started.Task.WaitAsync(Timeout);
        await services.GetRequiredService<IWorkflowOperations>().PauseAsync("lost", "operator");
        await store.PauseObserved.Task.WaitAsync(Timeout);
        store.LoseLease = true;
        await store.LeaseLost.Task.WaitAsync(Timeout);
        probe.Finish.TrySetResult();
        var result = await run.WaitAsync(Timeout);
        Assert.Equal(WorkflowExecutionStatus.Running, result.Status);
        Assert.Equal(WorkflowStatus.Running, (await store.GetWorkflowInstanceAsync("lost"))!.Status);
        Assert.DoesNotContain(await store.GetTraceAsync("lost"), e => e.Entry.EntryType == TraceEntryType.Paused);
        Assert.Null((await store.GetDebugStatesAsync(["lost"]))["lost"].Commands.Single().AcknowledgedRevision);
    }

    [Fact]
    public async Task ContinueDuringThePauseCommitKeepsItsWakeAndAStaleGenerationIsRefused()
    {
        var store = new ProbeStore { HoldPause = true }; var probe = new Probe();
        await using var first = Services(store, probe); await using var second = Services(store, probe);
        var run = first.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("WakePause").Step<Block>().Step<After>().Build(), new Data(), new() { InstanceId = "wake" });
        await probe.Started.Task.WaitAsync(Timeout);
        await first.GetRequiredService<IWorkflowOperations>().PauseAsync("wake", "operator");
        await store.PauseObserved.Task.WaitAsync(Timeout);
        probe.Finish.TrySetResult();
        await store.PauseCommitting.Task.WaitAsync(Timeout);
        var accepted = await store.RequestDebugCommandAsync("wake", new("continue", DebugCommandKind.Continue, 1, "other"));
        Assert.Equal(DebugCommandStatus.Accepted, accepted.Status);
        store.ReleasePause.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await run.WaitAsync(Timeout)).Status);
        Assert.Contains(await store.GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(first, 20)), r => r.InstanceId == "wake");
        var stale = await store.RequestDebugCommandAsync("wake", new("stale", DebugCommandKind.Continue, 0, "old"));
        Assert.Equal(DebugCommandStatus.StaleGeneration, stale.Status);
        var continued = await second.GetRequiredService<IWorkflowClient>().RecoverAsync("wake");
        Assert.Equal(WorkflowExecutionStatus.Success, continued.Status);
        Assert.All((await store.GetDebugStatesAsync(["wake"]))["wake"].Commands, c => Assert.NotNull(c.AcknowledgedRevision));
    }

    [Fact]
    public async Task DisabledHostsRefuseOperationsAndKeepPendingContinueHeld()
    {
        var store = new ProbeStore(); var probe = new Probe();
        await using var first = Services(store, probe); await using var disabled = Services(store, probe, debugging: false);
        var result = await first.GetRequiredService<IWorkflowClient>().RunAsync(Waiting("DisabledPause"), new Data());
        var ops = disabled.GetRequiredService<IWorkflowOperations>();
        await Assert.ThrowsAsync<WorkflowEngineException>(() => ops.PauseAsync(result.InstanceId, "actor"));
        await Assert.ThrowsAsync<WorkflowEngineException>(() => ops.ContinueAsync(result.InstanceId, "actor"));
        await first.GetRequiredService<IWorkflowOperations>().PauseAsync(result.InstanceId, "actor");
        await store.RequestDebugCommandAsync(result.InstanceId, new("continue", DebugCommandKind.Continue, 1, "actor"));
        var recovered = await disabled.GetRequiredService<IWorkflowClient>().RecoverAsync(result.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Paused, recovered.Status);
        Assert.Null((await store.GetDebugStatesAsync([result.InstanceId]))[result.InstanceId].Commands.Last().AcknowledgedRevision);
    }

    [Fact]
    public async Task CancellationEndsAPauseAfterTheLastActivityFinished()
    {
        var store = new ProbeStore(); var probe = new Probe();
        await using var services = Services(store, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var run = client.RunAsync(Workflow.Create<Data>("FinishedButPaused").Step<Block>().Build(), new Data(),
            new() { InstanceId = "last" });
        await probe.Started.Task.WaitAsync(Timeout);
        await services.GetRequiredService<IWorkflowOperations>().PauseAsync("last", "operator");
        await store.PauseObserved.Task.WaitAsync(Timeout);
        probe.Finish.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await run.WaitAsync(Timeout)).Status);
        var cancelled = await client.CancelAsync("last", new() { ReasonCode = "cancel" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
    }

    private static WorkflowDefinition Waiting(string name) => Workflow.Create<Data>(name).Step<Noop>().WaitFor<Go>("go").Build();
    private static ServiceProvider Services(ProbeStore store, Probe probe, bool debugging = true, bool shortLease = false)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => options.Debugging = debugging);
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton(probe);
        services.AddSingleton<IWorkflowCatalog>(probe.Registry);
        if (shortLease) services.AddSingleton(new ExecutionLeaseSettings(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)));
        return services.BuildServiceProvider();
    }
    public sealed class Data { public int Value { get; set; } }
    public sealed class Go;
    public sealed class Probe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkflowCatalog Registry { get; } = new();
        public int BlockCount; public int AfterCount;
    }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        [ActivityOutput] public int Value { get; set; }
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.BlockCount); probe.Started.TrySetResult();
            await probe.Finish.Task.WaitAsync(cancellationToken); Value = 42;
        }
    }
    public sealed class After(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.AfterCount); return Task.CompletedTask; }
    }
    private sealed class ProbeStore : InMemoryWorkflowStateRepository
    {
        public bool FailPause; public bool HoldPause; public bool LoseLease;
        public TaskCompletionSource PauseObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PauseCommitting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LeaseLost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> ids)
        {
            var states = await base.GetDebugStatesAsync(ids);
            if (states.Values.Any(s => s.Commands.Any(c => c.Kind == DebugCommandKind.Pause && c.AcknowledgedRevision == null))) PauseObserved.TrySetResult();
            return states;
        }
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            if (commit.Instance.Status == WorkflowStatus.Paused)
            {
                PauseCommitting.TrySetResult();
                if (FailPause) throw new IOException("Injected owner crash before pause commit");
                if (HoldPause) await ReleasePause.Task.WaitAsync(Timeout);
            }
            return await base.CommitWorkflowInstanceAsync(commit);
        }
        public override Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases)
        {
            if (!LoseLease) return base.RenewExecutionLeasesAsync(leases);
            LeaseLost.TrySetResult();
            return Task.FromResult<IReadOnlyList<ExecutionLeaseRenewalResult>>(
                leases.Select(_ => new ExecutionLeaseRenewalResult(ExecutionLeaseRenewalStatus.Refused)).ToArray());
        }
    }
}


