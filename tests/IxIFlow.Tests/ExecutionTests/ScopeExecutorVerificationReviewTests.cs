using System.Collections.Concurrent;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Tests.Infrastructure;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeExecutorVerificationReviewTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task CancelledQueuedScopeOpenSettlesWithoutAuthorEntryAndCompletesCancellation()
    {
        var probe = new Probe();
        using var harness = new Harness(probe);
        var definition = new WorkflowBuilder<Data, object>([], "QueuedScopeOpen")
            .Scope<Container>(s => s.Threading(ScopeThreading.ThreadAffine), b => b
                .Parallel(p => p.Do(x => x.Step<Block>())
                    .Do(x => x.Scope<UnenteredScope>(_ => { }, body => body.Step<UnenteredBody>()))
                    .Do(x => x.Step<Marker>())))
            .Build();
        var run = harness.StartAsync(definition);
        try
        {
            await probe.Blocking.Task.WaitAsync(Patience);
            // Preparing the third branch proves the nested open is queued behind Block.
            await probe.Prepared.Task.WaitAsync(Patience);
            harness.Executor.Deliver(new Cancel(harness.Signals.AssignmentId,
                CancelKind.Workflow, new CancellationReason { ReasonCode = "stop" }));
            await probe.Cancelled.Task.WaitAsync(Patience);
            probe.Gate.Set();
            Assert.Equal(AssignmentOutcomeKind.Idle, (await run.WaitAsync(Patience)).Kind);
            Assert.Equal(WorkflowStatus.Cancelled, harness.Host.Saved!.Status);
            Assert.Equal(0, probe.Opens);
            Assert.Equal(0, probe.Executions);
            Assert.Equal(ScopeCloseReason.Cancelled, Assert.Single(probe.Closes).Reason);
            var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(harness.Host.Saved));
            Assert.Empty(state.Scopes);
            Assert.Null(state.UnhandledError);
            Assert.All(state.Continuations, branch => Assert.Null(branch.AuthorizedDispatch));
        }
        finally { probe.Gate.Set(); }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task QueuedRecoveryCaptureChecksCancellationBeforeAuthorEntryAndCreatesNoClaim(bool abandon)
    {
        var probe = new Probe();
        using var harness = new Harness(probe);
        var definition = new WorkflowBuilder<Data, object>([], "QueuedScopeCapture" + abandon)
            .Scope<Container>(s => s.Threading(ScopeThreading.ThreadAffine), b => b
                .Parallel(p => p.Do(x => x.Step<Block>()).Do(x => x.Step<Recoverable>()).Do(x => x.Step<Marker>())))
            .Build();
        var run = harness.StartAsync(definition);
        try
        {
            // The third preparation proves the second branch's capture was queued while
            // the first branch held the shared affine thread.
            await probe.Blocking.Task.WaitAsync(Patience);
            await probe.Prepared.Task.WaitAsync(Patience);
            harness.Executor.Deliver(new Cancel(harness.Signals.AssignmentId,
                abandon ? CancelKind.Run : CancelKind.Workflow, new CancellationReason { ReasonCode = "stop" }));
            await probe.Cancelled.Task.WaitAsync(Patience);
            if (abandon)
                Assert.Equal(AssignmentOutcomeKind.RunCancelled, (await run.WaitAsync(Patience)).Kind);
            probe.Gate.Set();
            if (!abandon)
            {
                Assert.Equal(AssignmentOutcomeKind.Idle, (await run.WaitAsync(Patience)).Kind);
                Assert.Equal(WorkflowStatus.Cancelled, harness.Host.Saved!.Status);
            }
            await probe.Disposed.Task.WaitAsync(Patience);
            Assert.Equal(0, probe.Captures);
            Assert.Equal(0, probe.Executions);
            Assert.DoesNotContain(harness.Host.Starts, name => name == nameof(Recoverable));
            var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(harness.Host.Saved!));
            var captureStep = definition.StepTable.Single(step => step.Kind == WorkflowStepType.Activity && step.Name == nameof(Recoverable));
            Assert.DoesNotContain(state.Continuations, branch => branch.PendingActivity?.StepId == captureStep.CatalogId);
        }
        finally { probe.Gate.Set(); }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task UnhandledParallelFailureReachesTheAncestorScopeCloseWithItsOriginalError(bool affine)
    {
        var probe = new Probe();
        using var harness = new Harness(probe);
        var definition = new WorkflowBuilder<Data, object>([], "AncestorScopeFailure" + affine)
            .Scope<Container>(s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread)
                , b => b.Parallel(p => p.Do(x => x.Step<Fail>()).Do(x => x.Step<Marker>())))
            .Build();
        Assert.Equal(AssignmentOutcomeKind.Idle, (await harness.StartAsync(definition).WaitAsync(Patience)).Kind);
        Assert.Equal(WorkflowStatus.Failed, harness.Host.Saved!.Status);
        var closing = Assert.Single(probe.Closes);
        Assert.Equal(ScopeCloseReason.Failed, closing.Reason);
        Assert.Same(probe.Error, closing.Error);
    }

    public sealed class Data;
    public sealed class Probe
    {
        public TaskCompletionSource Blocking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Gate { get; } = new();
        public InvalidOperationException Error { get; } = new("original branch error");
        public ConcurrentQueue<ScopeClosing> Closes { get; } = new();
        public int Captures;
        public int Opens;
        public int Executions;
    }
    public sealed class Value(Probe probe) : IDisposable
    { public void Dispose() => probe.Disposed.TrySetResult(); }
    [ScopeConcurrency(ScopeConcurrency.Shared)]
    public sealed class Container(Probe probe) : IScopeActivity<Value>
    {
        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token) => Task.FromResult(new Value(probe));
        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token)
        { probe.Closes.Enqueue(closing); value.Dispose(); return Task.CompletedTask; }
    }
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        {
            using var registration = token.Register(() => probe.Cancelled.TrySetResult());
            probe.Blocking.TrySetResult();
            Assert.True(probe.Gate.Wait(Patience));
            return Task.CompletedTask;
        }
    }
    public sealed class UnenteredScope(Probe probe) : IScopeActivity<object>
    {
        public Task<object> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        { Interlocked.Increment(ref probe.Opens); return Task.FromResult(new object()); }
        public Task CloseAsync(object value, ScopeClosing closing, IScopeContext context, CancellationToken token) =>
            throw new InvalidOperationException("An unentered scope cannot close");
    }
    public sealed class UnenteredBody(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        { Interlocked.Increment(ref probe.Executions); return Task.CompletedTask; }
    }
    public sealed class Marker : IAsyncActivity
    {
        public Marker(Probe probe) => probe.Prepared.TrySetResult();
        public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask;
    }
    public sealed class Fail(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.FromException(probe.Error); }
    public sealed class Recoverable(Probe probe) : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) { Interlocked.Increment(ref probe.Captures); return "captured"; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken token) { Interlocked.Increment(ref probe.Executions); return Task.CompletedTask; }
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context, CancellationToken token) =>
            Task.FromResult<ActivityRecoveryResult<string>>(new ActivityRecoveryResult<string>.Completed());
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        public Host Host { get; } = new();
        public WorkflowExecutor Executor { get; }
        public ExecutionSignals Signals { get; }
        public Harness(Probe probe)
        {
            var collection = new ServiceCollection().AddSingleton(probe);
            collection.AddIxIFlow();
            _services = collection.BuildServiceProvider();
            Executor = new(_services, Host);
            Signals = Executor.PrepareAssignment(Guid.NewGuid());
        }
        public async Task<AssignmentOutcome> StartAsync(WorkflowDefinition definition)
        {
            await _services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            return (await Executor.ExecuteAsync(new(Signals.AssignmentId, instance, null, checkpoint.StructureHash,
                1, 0, ExecutionLimits.Default with { MaxActivitiesPerRun = 4, ReleaseGrace = TimeSpan.FromMilliseconds(100) },
                false, []), CancellationToken.None)).Outcome!;
        }
        public void Dispose() { Signals.Dispose(); _services.Dispose(); }
    }
    private sealed class Host : IExecutionHost
    {
        public WorkflowInstance? Saved { get; private set; }
        public List<string> Starts { get; } = [];
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            Starts.AddRange(request.Commit.ReadTraceEntries().Where(entry => entry.EntryType == TraceEntryType.ActivityStarted).Select(entry => entry.ActivityName!));
            Saved = request.Commit.ReadInstance(Saved);
            return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Ack, request.Commit.ExpectedRevision + 1));
        }
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) => Task.FromResult(new ReadPayloadReply(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable));
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => Task.FromResult(new ReadInboxReply(request.AssignmentId, new InboxPage([], request.AfterSequence, new Dictionary<string, long>())));
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => Task.FromResult(new ReadWaitReply(request.AssignmentId));
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
    }
}
