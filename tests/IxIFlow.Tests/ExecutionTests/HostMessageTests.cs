using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "A-02")]
public sealed class HostMessageTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WorkflowCancellationRunsFinallyAndCommitsCancellation()
    {
        using var run = new Harness();
        var running = run.Start(Cancellable());
        await run.Probe.Started.Task.WaitAsync(Patience);
        run.Executor.Deliver(new Cancel(run.Signals.AssignmentId, CancelKind.Workflow,
            new CancellationReason { ReasonCode = "withdrawn" }));
        var outcome = await running.WaitAsync(Patience);
        Assert.Equal(AssignmentOutcomeKind.Idle, outcome.Kind);
        Assert.Equal(WorkflowStatus.Cancelled, run.Host.Commits.Last().ReadTrunk().Status);
        Assert.Equal(1, run.Probe.Cleanups);
        Assert.Equal("withdrawn", run.Instance!.CancellationReason!.ReasonCode);
    }

    [Fact]
    public async Task RunCancellationKeepsTheInstanceRecoverableWithoutFinally()
    {
        using var run = new Harness();
        var running = run.Start(Cancellable());
        await run.Probe.Started.Task.WaitAsync(Patience);
        run.Executor.Deliver(new Cancel(run.Signals.AssignmentId, CancelKind.Run));
        Assert.Equal(AssignmentOutcomeKind.RunCancelled, (await running.WaitAsync(Patience)).Kind);
        Assert.All(run.Host.Commits, commit => Assert.Equal(WorkflowStatus.Running, commit.ReadTrunk().Status));
        Assert.Equal(0, run.Probe.Cleanups);
        Assert.Null(run.Instance!.CancellationReason);
    }

    [Fact]
    public async Task OwnershipCancellationAbandonsActivitiesWithoutAnotherCommit()
    {
        using var run = new Harness();
        var running = run.Start(Cancellable());
        await run.Probe.Started.Task.WaitAsync(Patience);
        var before = run.Host.Commits.Count;
        run.Executor.Deliver(new Cancel(run.Signals.AssignmentId, CancelKind.Ownership));
        Assert.Equal(AssignmentOutcomeKind.OwnershipLost, (await running.WaitAsync(Patience)).Kind);
        Assert.Equal(before, run.Host.Commits.Count);
        Assert.Equal(0, run.Probe.Cleanups);
    }

    [Fact]
    public async Task OwnershipLossDuringRunCancellationCleanupPreventsSettlement()
    {
        using var run = new Harness();
        run.Probe.IgnoreCancellation = true;
        var running = run.Start(Cancellable());
        await run.Probe.Started.Task.WaitAsync(Patience);
        var before = run.Host.Commits.Count;
        run.Executor.Deliver(new Cancel(run.Signals.AssignmentId, CancelKind.Run));
        await run.Probe.Cancelled.Task.WaitAsync(Patience);
        run.Executor.Deliver(new Cancel(run.Signals.AssignmentId, CancelKind.Ownership));
        run.Probe.Release.TrySetResult();
        Assert.Equal(AssignmentOutcomeKind.OwnershipLost, (await running.WaitAsync(Patience)).Kind);
        Assert.Equal(before, run.Host.Commits.Count);
    }

    [Fact]
    public async Task OwnershipLossDuringWorkflowFinallyPreventsItsCompletionCommit()
    {
        using var run = new Harness();
        run.Probe.HoldCleanup = true;
        var running = run.Start(Cancellable());
        await run.Probe.Started.Task.WaitAsync(Patience);
        run.Executor.Deliver(new Cancel(run.Signals.AssignmentId, CancelKind.Workflow,
            new CancellationReason { ReasonCode = "withdrawn" }));
        await run.Probe.CleanupStarted.Task.WaitAsync(Patience);
        var before = run.Host.Commits.Count;
        run.Executor.Deliver(new Cancel(run.Signals.AssignmentId, CancelKind.Ownership));
        Assert.Equal(AssignmentOutcomeKind.OwnershipLost, (await running.WaitAsync(Patience)).Kind);
        Assert.Equal(before, run.Host.Commits.Count);
        Assert.DoesNotContain(run.Host.Commits, commit => commit.ReadTrunk().Status == WorkflowStatus.Cancelled);
    }

    [Fact]
    public async Task RecycleWaitsForTheActivityCommitAndStartsNoNextStep()
    {
        using var run = new Harness();
        var running = run.Start(Workflow.Create<Data>("RecycleBoundary")
            .Step<Block>().Step<Cleanup>().Build());
        await run.Probe.Started.Task.WaitAsync(Patience);
        var before = run.Host.Commits.Count;
        run.Executor.Deliver(new Recycle(run.Signals.AssignmentId));
        Assert.False(running.IsCompleted);
        run.Probe.Release.TrySetResult();
        Assert.Equal(AssignmentOutcomeKind.Drained, (await running.WaitAsync(Patience)).Kind);
        Assert.True(run.Host.Commits.Count > before);
        Assert.Equal(WorkflowStatus.Running, run.Host.Commits.Last().ReadTrunk().Status);
        Assert.Equal(0, run.Probe.Cleanups);
        var saved = run.Host.Saved!;
        Assert.Equal("finished", JsonSerializer.Deserialize<Data>(saved.WorkflowDataJson.InlineText)!.Value);
    }

    [Fact]
    public async Task CompletionCanWinTheRaceWithRecycle()
    {
        using var run = new Harness();
        run.Host.OnCommit = commit =>
        {
            if (commit.ReadTrunk().Status == WorkflowStatus.Completed)
                run.Executor.Deliver(new Recycle(run.Signals.AssignmentId));
        };
        var outcome = await run.Start(Workflow.Create<Data>("RecycleCompleted").Step<Cleanup>().Build());
        Assert.Equal(AssignmentOutcomeKind.Idle, outcome.Kind);
        Assert.Equal(WorkflowStatus.Completed, run.Instance!.Status);
    }

    [Fact]
    public async Task ArrivalsWakeAnInboxReadButCannotInventAnEventOrForceAnEndlessRead()
    {
        using var run = new Harness();
        var running = run.Start(Workflow.Create<Data>("ArrivalHint").Step<Cleanup>()
            .Parallel(parallel => parallel.Do(branch => branch.Step<Block>())
                .Do(branch => branch.WaitFor<Event>("go"))).Build());
        await run.Probe.Started.Task.WaitAsync(Patience);
        run.Host.NextRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Executor.Deliver(new Arrivals(run.Signals.AssignmentId, 100));
        await run.Host.NextRead.Task.WaitAsync(Patience);
        run.Executor.Deliver(new Arrivals(run.Signals.AssignmentId, 99));
        run.Executor.Deliver(new Arrivals(run.Signals.AssignmentId, 100));
        Assert.Equal(100, run.Signals.InboxSequence);
        run.Probe.Release.TrySetResult();
        Assert.Equal(AssignmentOutcomeKind.Idle, (await running.WaitAsync(Patience)).Kind);
        Assert.Equal(WorkflowStatus.Suspended, run.Instance!.Status);
        Assert.All(run.Host.Commits, commit => Assert.Empty(commit.Settlements));
        Assert.InRange(run.Host.Reads, 2, 4);
    }

    [Fact]
    public async Task MessagesForAnEndedRunCannotReachTheNextRunOnTheSameExecutor()
    {
        using var run = new Harness();
        var oldId = run.Signals.AssignmentId;
        await run.Start(Workflow.Create<Data>("FirstRun").Step<Cleanup>().Build());
        run.Probe.Cleanups = 0;
        run.Signals = run.Executor.PrepareAssignment(Guid.NewGuid());
        var running = run.Start(Cancellable());
        await run.Probe.Started.Task.WaitAsync(Patience);
        foreach (var kind in Enum.GetValues<CancelKind>())
            run.Executor.Deliver(new Cancel(oldId, kind, new CancellationReason { ReasonCode = "late" }));
        run.Executor.Deliver(new Recycle(oldId));
        run.Executor.Deliver(new Arrivals(oldId, long.MaxValue));
        Assert.Null(run.Signals.Cancellation);
        Assert.False(run.Signals.RunCancelled);
        Assert.False(run.Signals.Recycle);
        Assert.Equal(0, run.Signals.InboxSequence);
        run.Probe.Release.TrySetResult();
        Assert.Equal(AssignmentOutcomeKind.Idle, (await running.WaitAsync(Patience)).Kind);
        Assert.Equal(WorkflowStatus.Completed, run.Instance!.Status);
    }

    [Fact]
    public async Task ConcurrentDeliveriesCoalesceMonotonically()
    {
        using var run = new Harness();
        await Task.WhenAll(Enumerable.Range(1, 128).Select(sequence => Task.Run(() =>
            run.Executor.Deliver(new Arrivals(run.Signals.AssignmentId, sequence)))));
        Assert.Equal(128, run.Signals.InboxSequence);
    }

    [Fact]
    public void ASlotRecyclesAllItsRunsIncludingOneRegisteredAfterShutdownBegan()
    {
        using var first = new Harness();
        using var second = new Harness();
        using var late = new Harness();
        var control = new RunControl();
        using var firstRegistration = control.Register(first.Signals.AssignmentId, first.Executor.Deliver);
        using var secondRegistration = control.Register(second.Signals.AssignmentId, second.Executor.Deliver);
        control.Recycle();
        using var lateRegistration = control.Register(late.Signals.AssignmentId, late.Executor.Deliver);
        Assert.True(first.Signals.Recycle);
        Assert.True(second.Signals.Recycle);
        Assert.True(late.Signals.Recycle);
    }

    [Fact]
    public async Task ResultsCopySavedDataIncludingNestedMembers()
    {
        using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var data = new Data { Values = ["saved"], Unsaved = "live" };
        var definition = Workflow.Create<Data>("ResultCopy").Step<Cleanup>().Build();
        var result = await client.RunAsync(definition, data);
        var copy = Assert.IsType<Data>(result.WorkflowData);
        Assert.NotSame(data, copy);
        Assert.NotSame(data.Values, copy.Values);
        Assert.Null(copy.Unsaved);
        data.Values.Add("after run");
        copy.Values.Add("result mutation");
        var repeated = await client.RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = result.InstanceId });
        Assert.Equal(["saved"], Assert.IsType<Data>(repeated.WorkflowData).Values);
    }

    [Fact]
    public async Task TheHostArmsItsTimerFromStoreReceiptTimes()
    {
        var time = new TimerRecordingClock();
        using var services = Services(time, new SchedulingStore(time));
        var definition = Workflow.Create<Data>("StoreReceiptTimer").Step<Cleanup>()
            .Delay(TimeSpan.FromHours(1)).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        await time.WaitForTimerDueAtAsync(time.GetUtcNow() + TimeSpan.FromSeconds(17));
    }

    [Fact]
    public async Task ReplayedAcknowledgementsDoNotDuplicateTheResultTrace()
    {
        var store = new LostAcknowledgementStore();
        using var services = Services(store: store);
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("LostTraceAck").Step<Cleanup>().Build(), new Data());
        var trace = (await services.GetRequiredService<IWorkflowClient>().GetTraceAsync(result.InstanceId));
        Assert.NotEmpty(result.TraceEntries);
        Assert.Equal(trace.Select(WorkflowStoreRules.SerializeTrace),
            result.TraceEntries.Select(WorkflowStoreRules.SerializeTrace));
    }

    private static WorkflowDefinition Cancellable() => Workflow.Create<Data>("MessageCancellation")
        .Step<Noop>().Try(body => body.Step<Block>()).Finally(body => body.Step<Cleanup>()).Build();

    private static ServiceProvider Services(TimeProvider? time = null, IWorkflowStateRepository? store = null)
    {
        var services = new ServiceCollection().AddLogging();
        if (time != null) services.AddSingleton(time);
        if (store != null) services.AddSingleton(store);
        services.AddIxIFlow();
        services.AddSingleton<Probe>();
        services.AddTransient<Noop>();
        services.AddTransient<Block>();
        services.AddTransient<Cleanup>();
        return services.BuildServiceProvider();
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services = Services();
        public WorkflowExecutor Executor { get; }
        public ExecutionSignals Signals { get; set; }
        public Probe Probe => _services.GetRequiredService<Probe>();
        public MessageHost Host { get; } = new();
        public WorkflowInstance? Instance => Host.Saved;

        public Harness()
        {
            Executor = new(_services, Host);
            Signals = Executor.PrepareAssignment(Guid.NewGuid());
        }

        public async Task<AssignmentOutcome> Start(WorkflowDefinition definition)
        {
            var data = new Data();
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, data,
                new WorkflowOptions(), DateTime.UtcNow);
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            await _services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
            var ready = await Executor.ExecuteAsync(new(Signals.AssignmentId, instance, null,
                checkpoint.StructureHash, 1, 0, ExecutionLimits.Default, false, []), CancellationToken.None);
            return ready.Outcome;
        }

        public void Dispose() { Signals.Dispose(); _services.Dispose(); }
    }

    private sealed class MessageHost : IExecutionHost
    {
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) =>
            Task.FromResult(new ReadPayloadReply(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable));
        public ConcurrentQueue<CheckpointCommit> Commits { get; } = new();
        public WorkflowInstance? Saved { get; private set; }
        public Action<CheckpointCommit>? OnCommit { get; set; }
        public TaskCompletionSource? NextRead { get; set; }
        public int Reads;
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            var commit = request.Commit;
            Saved = commit.ReadInstance(Saved);
            Commits.Enqueue(commit);
            OnCommit?.Invoke(commit);
            return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Ack, commit.ExpectedRevision + 1));
        }
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request)
        {
            Interlocked.Increment(ref Reads);
            NextRead?.TrySetResult();
            return Task.FromResult(new ReadInboxReply(request.AssignmentId, new InboxPage([], request.AfterSequence, new Dictionary<string, long>())));
        }
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => Task.FromResult(new ReadWaitReply(request.AssignmentId));
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
    }

    private sealed class SchedulingStore(TimeProvider time) : FaultInjectingRepository(time)
    {
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            var storeNow = time.GetUtcNow().UtcDateTime.AddDays(3);
            return result with { StoreTimeUtc = storeNow, RunnableAtUtc = storeNow.AddSeconds(17) };
        }
    }

    private sealed class LostAcknowledgementStore : FaultInjectingRepository
    {
        private bool _lost;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (!_lost && commit.TraceEntries.Count > 0)
            {
                _lost = true;
                throw new IOException("The store applied this commit but its reply was lost");
            }
            return result;
        }
    }

    public sealed class Data
    {
        public string Value { get; set; } = "";
        public List<string> Values { get; set; } = [];
        [JsonIgnore] public string? Unsaved { get; set; }
    }
    public sealed class Event;
    public sealed class Probe
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CleanupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreCancellation;
        public bool HoldCleanup;
        public int Cleanups;
    }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => probe.Cancelled.TrySetResult());
            probe.Started.TrySetResult();
            await probe.Release.Task.WaitAsync(probe.IgnoreCancellation ? CancellationToken.None : cancellationToken);
            ((Data)context.WorkflowData).Value = "finished";
        }
    }
    public sealed class Cleanup(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref probe.Cleanups) > 0 && probe.HoldCleanup)
            {
                probe.CleanupStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }
}
