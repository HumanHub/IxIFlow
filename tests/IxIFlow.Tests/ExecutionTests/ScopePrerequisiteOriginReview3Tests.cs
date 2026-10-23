using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopePrerequisiteOriginReview3Tests
{
    [Fact]
    public async Task ExhaustedInitialOpenCommitInterruptsInsteadOfRunningBusinessCatch()
    {
        using var harness = new Harness("retry");
        Assert.Equal(AssignmentOutcomeKind.Interrupted, (await harness.RunAsync()).Kind);
        Assert.Equal(3, harness.Host.Faults);
        Assert.NotEqual(WorkflowStatus.Completed, harness.Host.Saved?.Status);
        Assert.Equal(0, harness.Probe.Opens);
        Assert.Equal(0, harness.Probe.Bodies);
        Assert.Equal(0, harness.Probe.Catches);
    }

    [Theory, InlineData("limit", "ResourceLimit"), InlineData("verification", "Verification")]
    public async Task InitialOpenEngineStopKeepsTheLastCheckpointAndBypassesCatch(string fault, string kind)
    {
        using var harness = new Harness(fault);
        Assert.Equal(AssignmentOutcomeKind.Idle, (await harness.RunAsync()).Kind);
        Assert.Equal(WorkflowStatus.NeedsResolution, harness.Host.Saved!.Status);
        var saved = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(harness.Host.Saved));
        Assert.Equal(kind, saved.OperatorStop!.Kind.ToString());
        Assert.Empty(saved.Scopes);
        Assert.Equal(0, harness.Probe.Opens);
        Assert.Equal(0, harness.Probe.Bodies);
        Assert.Equal(0, harness.Probe.Catches);
    }

    [Fact]
    public async Task EngineFailureDuringInitialOpenIsNeverReclassifiedAsAnAuthorFailure()
    {
        using var harness = new Harness("engine");
        Assert.Equal(AssignmentOutcomeKind.Idle, (await harness.RunAsync()).Kind);
        Assert.Equal(WorkflowStatus.Failed, harness.Host.Saved!.Status);
        Assert.Equal(0, harness.Probe.Catches);
        Assert.Equal(0, harness.Probe.Opens);
        Assert.Contains("engine open", harness.Host.Saved.LastError);
    }

    [Theory]
    [InlineData("author-open", false), InlineData("author-close", true)]
    [InlineData("author-input", false), InlineData("author-output", false)]
    [InlineData("author-timeout", true)]
    public async Task AuthorScopeEngineExceptionRemainsCatchable(string fault, bool ranBody)
    {
        using var harness = new Harness(fault);
        Assert.Equal(AssignmentOutcomeKind.Idle, (await harness.RunAsync()).Kind);
        Assert.Equal(WorkflowStatus.Completed, harness.Host.Saved!.Status);
        Assert.Equal(1, harness.Probe.Catches);
        Assert.Equal(ranBody ? 1 : 0, harness.Probe.Bodies);
    }

    [Fact]
    public async Task InvalidLiveValueInScopeConfigurationIsAnEngineFailureAfterOpen()
    {
        using var harness = new Harness("engine-settlement");
        Assert.Equal(AssignmentOutcomeKind.Idle, (await harness.RunAsync()).Kind);
        Assert.Equal(WorkflowStatus.Failed, harness.Host.Saved!.Status);
        Assert.Equal(1, harness.Probe.Opens);
        Assert.Equal(0, harness.Probe.Catches);
        Assert.Equal(0, harness.Probe.Bodies);
        Assert.Contains("holds its live value", harness.Host.Saved.LastError);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task EngineCloseTimeoutBypassesCatchIncludingCleanupAfterAnAuthorFailure(bool cleanup)
    {
        using var harness = new Harness(cleanup ? "engine-cleanup-timeout" : "engine-close-timeout");
        Assert.Equal(AssignmentOutcomeKind.Idle, (await harness.RunAsync()).Kind);
        Assert.Equal(WorkflowStatus.Failed, harness.Host.Saved!.Status);
        Assert.Equal(0, harness.Probe.Catches);
        Assert.Equal(cleanup ? 0 : 1, harness.Probe.Bodies);
        Assert.Contains("Scope close exceeded", harness.Host.Saved.LastError);
    }

    [Theory]
    [InlineData("null-open-task", 0), InlineData("null-open-value", 0), InlineData("null-close-task", 1)]
    public async Task InvalidScopeReturnValuesAreEngineValidationFailures(string fault, int bodies)
    {
        using var harness = new Harness(fault);
        Assert.Equal(AssignmentOutcomeKind.Idle, (await harness.RunAsync()).Kind);
        Assert.Equal(WorkflowStatus.Failed, harness.Host.Saved!.Status);
        Assert.Equal(1, harness.Probe.Opens);
        Assert.Equal(0, harness.Probe.Catches);
        Assert.Equal(bodies, harness.Probe.Bodies);
        Assert.Contains("returned a null", harness.Host.Saved.LastError);
    }

    public sealed class Data
    {
        public int Output
        {
            get => 0;
            set { if (value == 2) throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "author output"); }
        }
    }
    public sealed class Value;
    public sealed class Probe(string fault)
    {
        public string Fault { get; } = fault;
        public int Opens;
        public int Bodies;
        public int Catches;
        public TaskCompletionSource CloseGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Container(Probe probe) : IScopeActivity<Value>
    {
        public int Argument { get; set; }
        public object? Leaked { get; set; }
        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        {
            probe.Opens++;
            if (probe.Fault == "null-open-task") return null!;
            if (probe.Fault == "null-open-value") return Task.FromResult<Value>(null!);
            var value = new Value();
            if (probe.Fault == "engine-settlement") Leaked = value;
            return probe.Fault == "author-open"
                ? Task.FromException<Value>(new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "author open"))
                : Task.FromResult(value);
        }
        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token) =>
            probe.Fault == "null-close-task" ? null! : probe.Fault is "engine-cleanup-timeout" or "engine-close-timeout" ? probe.CloseGate.Task : probe.Fault == "author-close"
                ? Task.FromException(new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "author close"))
                : probe.Fault == "author-timeout" ? Task.FromException(new TimeoutException("author close")) : Task.CompletedTask;
    }
    public sealed class Body(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken token) { probe.Bodies++; return Task.CompletedTask; } }
    public sealed class Caught(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken token) { probe.Catches++; return Task.CompletedTask; } }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly WorkflowExecutor _executor;
        private readonly ExecutionSignals _signals;
        public Host Host { get; }
        public Probe Probe { get; }
        public Harness(string fault)
        {
            Probe = new(fault);
            Host = new(fault);
            var services = new ServiceCollection().AddSingleton(Probe);
            services.AddIxIFlow();
            _services = services.BuildServiceProvider();
            _executor = new(_services, Host);
            _signals = _executor.PrepareAssignment(Guid.NewGuid());
        }
        public async Task<AssignmentOutcome> RunAsync()
        {
            var definition = new WorkflowBuilder<Data, object>([], "ScopePrerequisiteOrigin")
                .Try(b => b.Scope<Container>(s => s.Input(x => x.Argument).From(c => AuthorMapping("author-input"))
                    .Output(x => x.Argument).To(c => c.WorkflowData.Output), body => body.Step<Body>()))
                .Catch<Exception>(b => b.Step<Caught>()).Build();
            await _services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            return (await _executor.ExecuteAsync(new(_signals.AssignmentId, instance, null, checkpoint.StructureHash,
                1, 0, ExecutionLimits.Default with { ScopeCloseTimeout = TimeSpan.FromMilliseconds(100), ReleaseGrace = TimeSpan.FromMilliseconds(100) }, false, []), CancellationToken.None)).Outcome!;
        }
        private int AuthorMapping(string fault) => Probe.Fault == fault
            ? throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, fault) : Probe.Fault is "author-output" or "engine-cleanup-timeout" ? 2 : 1;
        public void Dispose() { Probe.CloseGate.TrySetResult(); _signals.Dispose(); _services.Dispose(); }
    }
    private sealed class Host(string fault) : IExecutionHost
    {
        public WorkflowInstance? Saved { get; private set; }
        public int Faults { get; private set; }
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            var candidate = request.Commit.ReadInstance(Saved);
            var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(candidate));
            if (state.Scopes.Any(scope => scope.Phase == ScopePhase.Opening))
            {
                if (fault == "retry" && Faults++ < 3)
                    return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Retry, Error: "commit unavailable"));
                if (fault == "limit" && Faults++ == 0) throw new CheckpointLimitExceededException(200, 100);
                if (fault == "verification" && Faults++ == 0) throw new CheckpointVerificationException("unverified scope open");
                if (fault == "engine" && Faults++ == 0) throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "engine open");
            }
            Saved = candidate;
            return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Ack, request.Commit.ExpectedRevision + 1));
        }
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) => Task.FromResult(new ReadPayloadReply(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable));
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => Task.FromResult(new ReadInboxReply(request.AssignmentId, new InboxPage([], request.AfterSequence, new Dictionary<string, long>())));
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => Task.FromResult(new ReadWaitReply(request.AssignmentId));
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
    }
}
