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
        using var watch = new CancellationWatch(store, time);
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiresAt = time.GetUtcNow() + Settings.Duration;
        await using var lease = await WorkflowExecutionLease.TryAcquireAsync(store, instance, Settings, watch, Claim,
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
    public async Task RefusalWithoutConfirmedTerminationStillLosesImmediately(bool gracefulCancellation, bool unreadableCancellation)
    {
        var time = new TimerRecordingClock();
        var store = new DelayedWatchStore(time) { RefuseRenewal = true, FailCancellationRead = unreadableCancellation };
        var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        using var watch = new CancellationWatch(store, time);
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = await WorkflowExecutionLease.TryAcquireAsync(store, instance, Settings, watch, Claim,
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
            if (unreadableCancellation) Assert.IsType<IOException>(error);
            else Assert.IsType<InvalidOperationException>(error);
            Assert.Throws<ExecutionOwnershipLostException>(lease.ThrowIfLost);
            Assert.False(cancelled.Task.IsCompleted);
            Assert.Equal(time.Start + Settings.RenewalInterval, time.GetUtcNow());
        }
        finally { store.WatchReply.TrySetResult(new Dictionary<string, CancellationReason>()); }
    }

    [Fact]
    public async Task CancellationReadAfterRefusalCannotOutlastTheLease()
    {
        var time = new TimerRecordingClock();
        var store = new DelayedWatchStore(time) { RefuseRenewal = true, DelayCancellationRead = true };
        var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        using var watch = new CancellationWatch(store, time);
        var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expiresAt = time.GetUtcNow() + Settings.Duration;
        await using var lease = await WorkflowExecutionLease.TryAcquireAsync(store, instance, Settings, watch, Claim,
            error => lost.TrySetResult(error), reason => cancelled.TrySetResult(reason), _ => { }, time);
        Assert.NotNull(lease);
        try
        {
            await store.WatchStarted.Task.WaitAsync(Patience);
            time.Advance(Settings.RenewalInterval);
            await store.CancellationReadStarted.Task.WaitAsync(Patience);
            await time.WaitForTimerDueAtAsync(expiresAt);
            Assert.False(lost.Task.IsCompleted);
            time.Advance(expiresAt - time.GetUtcNow());

            Assert.IsType<TimeoutException>(await lost.Task.WaitAsync(Patience));
            Assert.Throws<ExecutionOwnershipLostException>(lease.ThrowIfLost);
            store.CancellationReply.TrySetResult(new CancellationReason { Mode = WorkflowControlMode.Terminate });
            Assert.False(cancelled.Task.IsCompleted);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "fallback", Settings.Duration, Claim));
        }
        finally
        {
            store.CancellationReply.TrySetResult(null);
            store.WatchReply.TrySetResult(new Dictionary<string, CancellationReason>());
        }
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

    // The shared batch watch is held independently of the renewal's single-instance lookup.
    private sealed class DelayedWatchStore(TimeProvider time) : FaultInjectingRepository(time)
    {
        public TaskCompletionSource WatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyDictionary<string, CancellationReason>> WatchReply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RenewalRefused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CancellationReason?> CancellationReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<CheckpointCommit> AppliedCommits { get; } = new();
        public bool RefuseRenewal { get; init; }
        public bool DelayCancellationRead { get; init; }
        public bool FailCancellationRead { get; init; }
        private int _renewalCalls;
        public int RenewalCalls => Volatile.Read(ref _renewalCalls);

        public override Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
            IReadOnlyCollection<string> instanceIds)
        {
            WatchStarted.TrySetResult();
            return WatchReply.Task;
        }

        public override Task<CancellationReason?> GetCancellationRequestAsync(string instanceId)
        {
            CancellationReadStarted.TrySetResult();
            if (FailCancellationRead) throw new IOException("Cancellation request unavailable");
            return DelayCancellationRead ? CancellationReply.Task : base.GetCancellationRequestAsync(instanceId);
        }

        public override async Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration)
        {
            Interlocked.Increment(ref _renewalCalls);
            var renewed = !RefuseRenewal && await base.RenewExecutionLeaseAsync(instanceId, token, duration);
            if (!renewed) RenewalRefused.TrySetResult();
            return renewed;
        }

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.Applied) AppliedCommits.Enqueue(commit);
            return result;
        }
    }
}
