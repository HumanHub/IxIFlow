using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeAccessorReview4Tests
{
    [Theory, InlineData("setter"), InlineData("engine-exception-setter"), InlineData("getter")]
    public async Task AuthoredScopeAccessorsEnterCatchWithTheOriginalException(string fault)
    {
        using var harness = new Harness(fault);
        await harness.RunAsync();
        Assert.Equal(WorkflowStatus.Completed, harness.Host.Saved!.Status);
        Assert.Equal(1, harness.Probe.Catches);
        Assert.True(IsAuthorFailure(harness.Probe.Error));
        Assert.Contains(nameof(InputValidatedScope), harness.Probe.Error.StackTrace);
        Assert.Equal(fault == "getter" ? 1 : 0, harness.Probe.Opens);
        Assert.Equal(0, harness.Probe.Bodies);
    }

    [Fact]
    public async Task EngineInputConversionFailureBypassesCatchAndNeverCallsTheSetter()
    {
        using var harness = new Harness("conversion");
        await harness.RunAsync();
        Assert.Equal(WorkflowStatus.Failed, harness.Host.Saved!.Status);
        Assert.Contains("takes Int32", harness.Host.Saved.LastError);
        Assert.Equal(0, harness.Probe.Catches);
        Assert.Equal(0, harness.Probe.Sets);
        Assert.Equal(0, harness.Probe.Opens);
        Assert.Equal(0, harness.Probe.Bodies);
    }

    [Fact]
    public async Task DefaultInputSetterAlsoEntersCatch()
    {
        using var harness = new Harness("setter");
        await harness.RunAsync(defaultInput: true);
        Assert.Equal(WorkflowStatus.Completed, harness.Host.Saved!.Status);
        Assert.Equal(1, harness.Probe.Catches);
        Assert.Equal(1, harness.Probe.Sets);
        Assert.Equal(0, harness.Probe.Opens);
    }

    [Fact]
    public void RestoredInputSetterPreservesTheOriginalAuthorException()
    {
        using var harness = new Harness("setter");
        var snapshot = Snapshot(nameof(InputValidatedScope.Argument), typeof(int), "1");
        var error = Assert.Throws<InvalidOperationException>(() => snapshot.Restore(new InputValidatedScope(harness.Probe), harness.Services));
        Assert.Same(harness.Probe.Error, error);
        Assert.True(IsAuthorFailure(error));
        Assert.Contains("set_Argument", error.StackTrace);
    }

    [Fact]
    public void RestoredOutputSetterPreservesTheOriginalAuthorException()
    {
        using var harness = new Harness("setter");
        var saved = new SerializedValue
        {
            Type = WorkflowTypeIdentity.SavedName(typeof(OutputWithSetter)),
            Json = StoredJson.Inline("{\"Result\":1}")
        };
        var error = Assert.Throws<InvalidOperationException>(() => saved.Read(harness.Services));
        Assert.Same(harness.Probe.Error, error);
        Assert.True(IsAuthorFailure(error));
        Assert.Contains("set_Result", error.StackTrace);
    }

    [Fact]
    public void MappedOutputGetterPreservesTheOriginalAuthorException()
    {
        using var harness = new Harness("getter");
        var definition = new WorkflowBuilder<Data, object>([], "ScopeOutputAccessor")
            .Scope<InputValidatedScope>(s => s.Input(x => x.RequiredText).From(c => "present")
                .Output(x => x.Result).To(c => c.WorkflowData.Output), _ => { }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => WorkflowValueBinding.ApplyActivityOutputs(
            definition.Steps[0], new InputValidatedScope(harness.Probe), new Data()));
        Assert.Same(harness.Probe.Error, error);
        Assert.True(IsAuthorFailure(error));
        Assert.Contains("get_Result", error.StackTrace);
    }

    [Theory, InlineData("missing"), InlineData("conversion"), InlineData("serialization"), InlineData("validation")]
    public void EngineRestorationAndValidationErrorsStayOutsideTheAuthorBoundary(string fault)
    {
        using var harness = new Harness("none");
        var activity = new InputValidatedScope(harness.Probe);
        var snapshot = fault switch
        {
            "missing" => Snapshot("Unavailable", typeof(int), "1"),
            "conversion" => Snapshot(nameof(InputValidatedScope.Argument), typeof(string), "\"wrong type\""),
            "serialization" => Snapshot(nameof(InputValidatedScope.Argument), typeof(int), "invalid json"),
            _ => Snapshot(nameof(InputValidatedScope.RequiredText), typeof(string), "\"\"")
        };
        var error = Assert.ThrowsAny<Exception>(() =>
        {
            snapshot.Restore(activity, harness.Services);
            if (fault == "validation") ActivityContract.ValidateRequired(activity, snapshot, harness.Services);
        });
        Assert.False(IsAuthorFailure(error));
        Assert.IsNotType<System.Reflection.TargetInvocationException>(error);
        if (fault == "validation") Assert.Equal(WorkflowErrorKind.InvalidRequest, Assert.IsType<WorkflowEngineException>(error).Kind);
    }

    private static ActivityInputSnapshot Snapshot(string property, Type type, string json) => new()
    {
        Properties = new() { [property] = new() { Value = new() { Type = WorkflowTypeIdentity.SavedName(type), Json = StoredJson.Inline(json) } } }
    };

    private static bool IsAuthorFailure(Exception error) => (bool)typeof(WorkflowEngineException)
        .GetMethod("IsFromWorkflowCode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(null, [error])!;

    public sealed class Data { public int Output { get; set; } }
    public sealed class Value;
    public sealed class Probe(string fault)
    {
        public string Fault { get; } = fault;
        public Exception Error { get; } = fault == "engine-exception-setter"
            ? new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "authored setter failure")
            : new InvalidOperationException("authored accessor failure");
        public int Sets;
        public int Opens;
        public int Bodies;
        public int Catches;
    }
    public sealed class InputValidatedScope(Probe probe) : IScopeActivity<Value>
    {
        private int _argument;
        [ActivityInput, DefaultValue(1)]
        public int Argument
        {
            get => _argument;
            set
            {
                probe.Sets++;
                if (probe.Fault is "setter" or "engine-exception-setter") throw probe.Error;
                _argument = value;
            }
        }
        [ActivityInput, Required] public string RequiredText { get; set; } = "present";
        [ActivityOutput] public int Result
        {
            get => probe.Fault == "getter" ? throw probe.Error : _argument;
            set => _argument = value;
        }
        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        { probe.Opens++; return Task.FromResult(new Value()); }
        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token) => Task.CompletedTask;
    }
    public sealed class OutputWithSetter(Probe probe) : IAsyncActivity
    {
        [ActivityOutput] public int Result { get => 0; set => throw probe.Error; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask;
    }
    public sealed class Body(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken token) { probe.Bodies++; return Task.CompletedTask; } }
    public sealed class Caught(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken token) { probe.Catches++; return Task.CompletedTask; } }

    private sealed class Harness : IDisposable
    {
        public Probe Probe { get; }
        public ServiceProvider Services { get; }
        public Host Host { get; } = new();
        public Harness(string fault)
        {
            Probe = new(fault);
            var services = new ServiceCollection().AddSingleton(Probe);
            services.AddIxIFlow();
            Services = services.BuildServiceProvider();
        }
        public async Task RunAsync(bool defaultInput = false)
        {
            var definition = new WorkflowBuilder<Data, object>([], "ScopeAccessorReview4")
                .Try(b => b.Scope<InputValidatedScope>(s =>
                {
                    if (!defaultInput) s.Input(x => x.Argument).From(c => 1);
                    s.Input(x => x.RequiredText).From(c => "present");
                    s.Output(x => x.Result).To(c => c.WorkflowData.Output);
                }, body => body.Step<Body>()))
                .Catch<Exception>(b => b.Step<Caught>()).Build();
            if (Probe.Fault == "conversion")
                new WorkflowScopeCatalog(definition).AllSteps.Select(pair => pair.Value).Single(step => step.StepType == WorkflowStepType.Scope)
                    .InputMappings.Single(mapping => mapping.TargetProperty == nameof(InputValidatedScope.Argument)).SourceFunction = _ => "invalid integer";
            await Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            var executor = new WorkflowExecutor(Services, Host);
            using var signals = executor.PrepareAssignment(Guid.NewGuid());
            var reply = await executor.ExecuteAsync(new(signals.AssignmentId, instance, null, checkpoint.StructureHash,
                1, 0, ExecutionLimits.Default, false, []), CancellationToken.None);
            Assert.Equal(AssignmentOutcomeKind.Idle, reply.Outcome!.Kind);
        }
        public void Dispose() => Services.Dispose();
    }
    private sealed class Host : IExecutionHost
    {
        public WorkflowInstance? Saved { get; private set; }
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            Saved = request.Commit.ReadInstance(Saved);
            return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Ack, request.Commit.ExpectedRevision + 1));
        }
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) => Task.FromResult(new ReadPayloadReply(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable));
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => Task.FromResult(new ReadInboxReply(request.AssignmentId, new InboxPage([], request.AfterSequence, new Dictionary<string, long>())));
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => Task.FromResult(new ReadWaitReply(request.AssignmentId));
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
    }
}
