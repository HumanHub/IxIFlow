using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "H-04")]
public sealed class TerminationRenewalRefusalTests
{
    private static readonly ExecutionLeaseSettings Settings =
        new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly ClaimQuery Claim = new(Guid.Empty, new Dictionary<string, string>());

    [Fact]
    public async Task RunningOwnerReportsTerminatedWhenRenewalIsRefusedBeforeTheWatchReturns()
    {
        var time = new TimerRecordingClock();
        var store = new DelayedWatchStore(time);
        var probe = new Probe();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(Settings);
        services.AddSingleton(new InProcessInstanceGate());
        services.AddSingleton(probe);
        services.AddTransient<Block>();
        await using var host = services.BuildServiceProvider();
        var definition = Workflow.Create<OperationsTests.Data>("RefusedTerminationRenewal").Step<Block>().Build();
        const string id = "refused-termination-renewal";
        var running = host.GetRequiredService<IWorkflowClient>().RunAsync(definition, new OperationsTests.Data(),
            new WorkflowOptions { InstanceId = id });
        try
        {
            await probe.Started.Task.WaitAsync(Patience);
            await store.WatchStarted.Task.WaitAsync(Patience);
            var ownerEpoch = store.AppliedCommits.Last().Epoch;
            Assert.True((await store.RequestTerminationAsync(id, new CancellationReason { ReasonCode = "stop" })).Accepted);

            time.Advance(Settings.RenewalInterval);
            await store.RenewalRefused.Task.WaitAsync(Patience);
            // Both ownership loss and the termination path cancel the in-flight activity before
            // awaiting it. Wait for that signal so the refusal is handled before releasing it.
            await probe.Cancelled.Task.WaitAsync(Patience);
            Assert.False(store.WatchReply.Task.IsCompleted);
            probe.Release.TrySetResult();

            Assert.Equal(WorkflowExecutionStatus.Terminated, (await running.WaitAsync(Patience)).Status);
            var terminal = Assert.Single(store.AppliedCommits, commit => commit.Instance.Status == WorkflowStatus.Terminated);
            Assert.Equal(ownerEpoch, terminal.Epoch);
            Assert.Equal(WorkflowStatus.Terminated, (await store.GetWorkflowInstanceAsync(id))!.Status);
            Assert.Single(await store.GetTraceAsync(id), entry => entry.Entry.EntryType == TraceEntryType.WorkflowTerminated);
        }
        finally
        {
            probe.Release.TrySetResult();
            store.WatchReply.TrySetResult(new Dictionary<string, CancellationReason>());
            await running.WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task PendingTerminationCancelsWithoutLosingUntilTheUnrenewedLeaseExpires()
    {
        var time = new TimerRecordingClock();
        var store = new DelayedWatchStore(time);
        var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiresAt = time.GetUtcNow() + Settings.Duration;
        await using var lease = await WorkflowExecutionLease.TryAcquireAsync(store, instance, Settings, watch, renewer, Claim,
            error => lost.TrySetResult(error), reason => cancelled.TrySetResult(reason), _ => { }, time);
        Assert.NotNull(lease);
        try
        {
            await store.WatchStarted.Task.WaitAsync(Patience);
            Assert.True((await store.RequestTerminationAsync(instance.InstanceId,
                new CancellationReason { ReasonCode = "stop" })).Accepted);
            var request = await store.GetCancellationRequestAsync(instance.InstanceId);
            time.Advance(Settings.RenewalInterval);

            Assert.Same(cancelled.Task, await Task.WhenAny(cancelled.Task, lost.Task).WaitAsync(Patience));
            Assert.Equivalent(request, await cancelled.Task, strict: true);
            Assert.False(lost.Task.IsCompleted);
            lease.ThrowIfLost();
            time.Advance(expiresAt - time.GetUtcNow() - TimeSpan.FromTicks(1));
            lease.ThrowIfLost();
            Assert.False(lost.Task.IsCompleted);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "fallback", Settings.Duration,
                Claim, ExecutionClaimKind.Termination));

            time.Advance(TimeSpan.FromTicks(1));
            Assert.Throws<ExecutionOwnershipLostException>(lease.ThrowIfLost);
            Assert.IsType<TimeoutException>(await lost.Task.WaitAsync(Patience));
            var fallback = await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "fallback", Settings.Duration,
                Claim, ExecutionClaimKind.Termination);
            Assert.NotNull(fallback);
            Assert.True(fallback.Epoch > lease.Epoch);
            Assert.Equal(1, store.RenewalCalls);
        }
        finally { store.WatchReply.TrySetResult(new Dictionary<string, CancellationReason>()); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RefusalWithoutConfirmedTerminationStillLosesImmediately(bool gracefulCancellation, bool terminationWithoutReason)
    {
        var time = new TimerRecordingClock();
        var store = new DelayedWatchStore(time) { RefuseRenewal = true, TerminationWithoutReason = terminationWithoutReason };
        var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = await WorkflowExecutionLease.TryAcquireAsync(store, instance, Settings, watch, renewer, Claim,
            error => lost.TrySetResult(error), reason => cancelled.TrySetResult(reason), _ => { }, time);
        Assert.NotNull(lease);
        try
        {
            await store.WatchStarted.Task.WaitAsync(Patience);
            if (gracefulCancellation)
                Assert.True((await store.RequestCancellationAsync(instance.InstanceId,
                    new CancellationReason { ReasonCode = "graceful" })).Accepted);
            time.Advance(Settings.RenewalInterval);

            var error = await lost.Task.WaitAsync(Patience);
            Assert.IsType<InvalidOperationException>(error);
            Assert.Throws<ExecutionOwnershipLostException>(lease.ThrowIfLost);
            Assert.False(cancelled.Task.IsCompleted);
            Assert.Equal(time.Start + Settings.RenewalInterval, time.GetUtcNow());
        }
        finally { store.WatchReply.TrySetResult(new Dictionary<string, CancellationReason>()); }
    }

    [Fact]
    public async Task ARefusalLosesOwnershipWithoutAFollowUpCancellationRead()
    {
        var time = new TimerRecordingClock();
        var store = new DelayedWatchStore(time) { RefuseRenewal = true };
        var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = await WorkflowExecutionLease.TryAcquireAsync(store, instance, Settings, watch, renewer, Claim,
            error => lost.TrySetResult(error), reason => cancelled.TrySetResult(reason), _ => { }, time);
        Assert.NotNull(lease);
        try
        {
            await store.WatchStarted.Task.WaitAsync(Patience);
            time.Advance(Settings.RenewalInterval);
            Assert.IsType<InvalidOperationException>(await lost.Task.WaitAsync(Patience));
            Assert.Throws<ExecutionOwnershipLostException>(lease.ThrowIfLost);
            Assert.Equal(0, store.CancellationReads);
            Assert.False(store.WatchReply.Task.IsCompleted);
            Assert.False(cancelled.Task.IsCompleted);
            Assert.Equal(time.Start + Settings.RenewalInterval, time.GetUtcNow());
        }
        finally { store.WatchReply.TrySetResult(new Dictionary<string, CancellationReason>()); }
    }

    [Fact]
    public async Task ATerminationRefusingRenewalEscalatesAGracefulRequestAlreadyDelivered()
    {
        var time = new TimerRecordingClock();
        var store = new DelayedWatchStore(time);
        var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reasons = new ConcurrentQueue<CancellationReason>();
        var graceful = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = await WorkflowExecutionLease.TryAcquireAsync(store, instance, Settings, watch, renewer, Claim,
            error => lost.TrySetResult(error), reason =>
            {
                reasons.Enqueue(reason);
                (reason.Mode == WorkflowControlMode.Terminate ? terminated : graceful).TrySetResult();
            }, _ => { }, time);
        Assert.NotNull(lease);
        try
        {
            await store.WatchStarted.Task.WaitAsync(Patience);
            var request = new CancellationReason { ReasonCode = "graceful" };
            Assert.True((await store.RequestCancellationAsync(instance.InstanceId, request)).Accepted);
            store.WatchReply.TrySetResult(new Dictionary<string, CancellationReason> { [instance.InstanceId] = request });
            await graceful.Task.WaitAsync(Patience);

            // The operator escalates while the owner runs its graceful cleanup; the watch no longer reads for it.
            Assert.True((await store.RequestTerminationAsync(instance.InstanceId,
                new CancellationReason { ReasonCode = "stop" })).Accepted);
            time.Advance(Settings.RenewalInterval);

            await terminated.Task.WaitAsync(Patience);
            Assert.Equal([WorkflowControlMode.Cancel, WorkflowControlMode.Terminate], reasons.Select(reason => reason.Mode));
            Assert.False(lost.Task.IsCompleted);
            lease.ThrowIfLost();
        }
        finally { store.WatchReply.TrySetResult(new Dictionary<string, CancellationReason>()); }
    }

    [Fact]
    public void ATerminationReplacesAGracefulRequestButNothingReplacesATermination()
    {
        var id = Guid.NewGuid();
        using var signals = new ExecutionSignals(id, _ => { });
        var graceful = new CancellationReason { ReasonCode = "graceful" };
        var terminate = new CancellationReason { ReasonCode = "stop", Mode = WorkflowControlMode.Terminate };

        var first = signals.Changed;
        signals.Deliver(new Cancel(id, CancelKind.Workflow, graceful));
        Assert.True(first.IsCompleted);
        Assert.Same(graceful, signals.Cancellation);

        var second = signals.Changed;
        signals.Deliver(new Cancel(id, CancelKind.Workflow, new CancellationReason { ReasonCode = "again" }));
        Assert.False(second.IsCompleted);
        Assert.Same(graceful, signals.Cancellation);

        signals.Deliver(new Cancel(id, CancelKind.Workflow, terminate));
        Assert.True(second.IsCompleted);
        Assert.Same(terminate, signals.Cancellation);

        var third = signals.Changed;
        signals.Deliver(new Cancel(id, CancelKind.Workflow, graceful));
        signals.Deliver(new Cancel(id, CancelKind.Workflow, new CancellationReason { Mode = WorkflowControlMode.Terminate }));
        Assert.False(third.IsCompleted);
        Assert.Same(terminate, signals.Cancellation);
    }

    public sealed class Probe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => probe.Cancelled.TrySetResult());
            probe.Started.TrySetResult();
            await probe.Release.Task;
        }
    }

    // The shared watch is held independently of reading the renewal batch's per-lease outcome.
    private sealed class DelayedWatchStore(TimeProvider time) : FaultInjectingRepository(time)
    {
        public TaskCompletionSource WatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyDictionary<string, CancellationReason>> WatchReply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RenewalRefused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<CheckpointCommit> AppliedCommits { get; } = new();
        public bool RefuseRenewal { get; init; }
        public bool TerminationWithoutReason { get; init; }
        public int CancellationReads;
        private int _renewalCalls;
        public int RenewalCalls => Volatile.Read(ref _renewalCalls);

        public override async Task<WorkflowControlRequests> GetControlRequestsAsync(
            IReadOnlyCollection<string> instanceIds)
        {
            WatchStarted.TrySetResult();
            return new(await WatchReply.Task, new Dictionary<string, WorkflowDebugState>());
        }

        public override Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
        {
            Interlocked.Increment(ref CancellationReads);
            return base.GetCancellationRequestAsync(instanceId);
        }

        public override async Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(
            IReadOnlyList<ExecutionLeaseRenewal> leases)
        {
            Interlocked.Increment(ref _renewalCalls);
            if (!RefuseRenewal)
            {
                var results = await base.RenewExecutionLeasesAsync(leases);
                if (results.Any(result => result.Status != ExecutionLeaseRenewalStatus.Renewed)) RenewalRefused.TrySetResult();
                return results;
            }
            RenewalRefused.TrySetResult();
            return leases.Select(_ => new ExecutionLeaseRenewalResult(TerminationWithoutReason
                ? ExecutionLeaseRenewalStatus.TerminationRequested : ExecutionLeaseRenewalStatus.Refused)).ToArray();
        }

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.Applied) AppliedCommits.Enqueue(commit);
            return result;
        }
    }
}
