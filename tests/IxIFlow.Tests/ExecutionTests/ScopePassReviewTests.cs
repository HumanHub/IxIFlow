using System.Collections.Concurrent;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopePassReviewTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task RecycleDuringInitialOpenPersistsTheBodyWithoutScopeDispatchAuthorization()
    {
        var probe = new Probe { HoldOpen = true };
        using var harness = new Harness(probe);
        var definition = new WorkflowBuilder<Data, object>([], "ScopeRecycleOpen")
            .Scope<Container>(null, b => b.Step<Body>()).Build();
        var run = harness.StartAsync(definition);
        await probe.Opening.Task.WaitAsync(Patience);
        harness.Executor.Deliver(new Recycle(harness.Signals.AssignmentId));
        probe.Release.TrySetResult();
        Assert.Equal(AssignmentOutcomeKind.Drained, (await run.WaitAsync(Patience)).Kind);
        var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(harness.Host.Saved!));
        Assert.Null(Assert.Single(state.Continuations).AuthorizedDispatch);
        Assert.Equal(FrameKind.Scope, state.Continuations[0].Stack[^1].Kind);
        Assert.Equal(0, probe.Bodies);
        Assert.Equal(AssignmentOutcomeKind.Idle, (await harness.RecoverAsync(definition)).Kind);
        Assert.Equal(1, probe.Bodies);
        Assert.Equal(WorkflowStatus.Completed, harness.Host.Saved!.Status);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task TerminationDuringInnerSuspensionCloseReleasesOuterWithoutStartingItsClose(bool affine)
    {
        var probe = new Probe { HoldInnerClose = true };
        using var harness = new Harness(probe);
        var definition = new WorkflowBuilder<Data, object>([], "ScopeTerminateSuspension" + affine)
            .Scope<Container>(s => s.Input(x => x.Name).From(c => "outer")
                    .Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
                b => b.Scope<Container>(s => s.Input(x => x.Name).From(c => "inner"),
                    x => x.WaitFor<ScopeActivityTests.Signal>("hold"))).Build();
        var run = harness.StartAsync(definition);
        await probe.Closing.Task.WaitAsync(Patience);
        harness.Executor.Deliver(new Cancel(harness.Signals.AssignmentId, CancelKind.Workflow,
            new CancellationReason { ReasonCode = "terminate", Mode = WorkflowControlMode.Terminate }));
        probe.Release.TrySetResult();
        await run.WaitAsync(Patience);
        Assert.Equal(new[] { "inner:Suspended" }, probe.Closes);
        Assert.Equal(new[] { "outer" }, probe.Disposals);
    }

    public sealed class Data;
    public sealed class Probe
    {
        public bool HoldOpen; public bool HoldInnerClose; public int Bodies;
        public TaskCompletionSource Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Closes { get; } = new();
        public ConcurrentQueue<string> Disposals { get; } = new();
    }
    public sealed class Value(string name, Probe probe) : IDisposable
    { public void Dispose() => probe.Disposals.Enqueue(name); }
    public sealed class Container(Probe probe) : IScopeActivity<Value>
    {
        public string Name { get; set; } = "scope";
        public async Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        { probe.Opening.TrySetResult(); if (probe.HoldOpen) await probe.Release.Task; return new(Name, probe); }
        public async Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token)
        {
            probe.Closes.Enqueue(Name + ":" + closing.Reason);
            if (probe.HoldInnerClose && Name == "inner") { probe.Closing.TrySetResult(); await probe.Release.Task; }
        }
    }
    public sealed class Body(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken token) { probe.Bodies++; return Task.CompletedTask; } }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        public WorkflowExecutor Executor { get; }
        public ExecutionSignals Signals { get; private set; }
        public Host Host { get; } = new();
        public Harness(Probe probe)
        {
            var collection = new ServiceCollection().AddSingleton(probe);
            collection.AddIxIFlow(options => options.VerifyCheckpointParts = true);
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
                1, 0, ExecutionLimits.Default, false, []), CancellationToken.None)).Outcome!;
        }
        public async Task<AssignmentOutcome> RecoverAsync(WorkflowDefinition definition)
        {
            Signals.Dispose(); Signals = Executor.PrepareAssignment(Guid.NewGuid());
            return (await Executor.ExecuteAsync(new(Signals.AssignmentId, Host.Saved!, null,
                WorkflowDefinitionValidator.Inspect(definition), 2, 0, ExecutionLimits.Default, false, []), CancellationToken.None)).Outcome!;
        }
        public void Dispose() { Signals.Dispose(); _services.Dispose(); }
    }
    private sealed class Host : IExecutionHost
    {
        public WorkflowInstance? Saved { get; private set; }
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        { Saved = request.Commit.ReadInstance(Saved); return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Ack, request.Commit.ExpectedRevision + 1)); }
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) => Task.FromResult(new ReadPayloadReply(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable));
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => Task.FromResult(new ReadInboxReply(request.AssignmentId, new InboxPage([], request.AfterSequence, new Dictionary<string, long>())));
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => Task.FromResult(new ReadWaitReply(request.AssignmentId));
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
    }
}
