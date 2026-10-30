using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeRestorationReview5Tests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task RestoredSetterFailureEntersOuterCatchAndBypassesInnerCatch(bool innerCatch)
    {
        var probe = new Probe();
        await using var services = Services(probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(Definition(probe, innerCatch: innerCatch), new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        probe.SetterFailures = 1;
        var done = await client.ResumeAsync(idle.InstanceId, "go", new Signal());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(1, probe.OuterCatches);
        Assert.Equal(0, probe.InnerCatches);
        Assert.Equal(0, probe.Bodies);
        Assert.Equal(1, probe.Opens);
        Assert.Equal(1, probe.Mappings);
        Assert.Contains("set_Argument", probe.Error.StackTrace);
    }

    [Fact]
    public async Task EngineConversionFailureDuringRestorationBypassesEveryCatch()
    {
        var probe = new Probe();
        await using var services = Services(probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(Definition(probe), new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var instance = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
        var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance));

        Assert.Single(state.Scopes).Inputs.Properties[nameof(Container.Argument)] = new()
        {
            Value = new() { Type = WorkflowTypeIdentity.SavedName(typeof(string)), Json = StoredJson.Inline("\"not an integer\"") }
        };

        instance.ExecutionStateJson = JsonSerializer.Serialize(state);
        var eventId = Guid.NewGuid().ToString("N");

        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(instance.InstanceId, eventId,
            WorkflowTypeIdentity.SavedName(typeof(Signal)), "{}", Key: "go"))).Status);

        await store.SeedAsync(instance, wakeAfter: TimeSpan.Zero);
        var sets = probe.Sets;
        var done = await client.ResumeAsync(idle.InstanceId, "go", new Signal(), eventId).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(WorkflowExecutionStatus.Faulted, done.Status);
        Assert.Contains("cannot be converted", done.ErrorMessage);
        Assert.Equal(0, probe.OuterCatches);
        Assert.Equal(0, probe.OtherCatches);
        Assert.Equal(sets, probe.Sets);
        Assert.Equal(1, probe.Opens);
        Assert.Equal(0, probe.Bodies);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task SetterFailureUsesScopeRetryBudgetDuringPreparationAndRestoration(bool restoration)
    {
        var probe = new Probe { SetterFailures = restoration ? 0 : 1 };
        await using var services = Services(probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var done = await client.RunAsync(Definition(probe, retry: true, wait: restoration), new Data());

        if (restoration)
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, done.Status);
            probe.SetterFailures = 1;
            done = await client.ResumeAsync(done.InstanceId, "go", new Signal());
        }

        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(0, probe.OuterCatches);
        Assert.Equal(1, probe.Bodies);
        Assert.Equal(restoration ? 2 : 1, probe.Opens);
        Assert.Equal(restoration ? 1 : 2, probe.Mappings);
        Assert.Single(probe.Invocations.Distinct());
        Assert.Equal(42, probe.BodyArgument);
    }

    [Fact]
    public async Task SetterFailureReopeningAnUnknownAttemptWaitsForResolution()
    {
        var probe = new Probe();
        var store = new FaultInjectingRepository();
        await using var services = Services(probe, store);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Definition(probe, wait: false, retry: true);
        var id = Guid.NewGuid().ToString("N");

        store.FailCommit = instance => probe.Bodies > 0 && ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance))
            .Continuations.All(continuation => continuation.PendingActivity == null);

        await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(definition, new Data(), new() { InstanceId = id }));
        store.FailCommit = _ => false;
        probe.SetterFailures = 1;
        var waiting = await client.RecoverAsync(id);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, waiting.Status);
        var pending = Assert.Single(await client.GetPendingActivitiesAsync(id));
        var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));

        Assert.Contains("could not be re-opened",
            Assert.Single(state.Continuations, c => c.PendingActivity != null).PendingActivity!.ResolutionReason);

        Assert.Equal(0, probe.OuterCatches);
        Assert.Equal(1, probe.Opens);
        var sets = probe.Sets;
        var done = await client.ResolveAsync(id, pending.InvocationId, ActivityResolution.Completed(new Dictionary<string, object?>()));
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(sets, probe.Sets);
        Assert.Equal(1, probe.Bodies);
    }

    private static WorkflowDefinition Definition(Probe probe, bool innerCatch = false, bool retry = false, bool wait = true) =>
        new WorkflowBuilder<Data, object>([], "ScopeRestorationReview5")
            .Try(outer => outer.Scope<Container>(scope =>
            {
                scope.Input(x => x.Argument).From(c => probe.Map());

                if (retry)
                    scope.Retry(new RetryPolicy(1));
            }, body =>
            {
                void Steps(IWorkflowBuilder<Data, Container> steps)
                {
                    if (wait)
                        steps.WaitFor<Signal>("go").Step<Body>();
                    else
                        steps.Step<Body>();
                }

                if (innerCatch)
                    body.Try(Steps).Catch<InvalidOperationException>(caught => caught.Step<InnerCaught>());
                else
                    Steps(body);
            }))
            .Catch<InvalidOperationException>(caught => caught.Step<OuterCaught>())
            .Catch<Exception>(caught => caught.Step<OtherCaught>()).Build();

    private static ServiceProvider Services(Probe probe, IWorkflowStateRepository? store = null)
    {
        var collection = new ServiceCollection().AddSingleton(probe);
        collection.AddIxIFlow(options => options.VerifyCheckpointParts = true);

        if (store != null)
            collection.AddSingleton(store);

        return collection.BuildServiceProvider();
    }

    public sealed class Data;

    public sealed class Signal;

    public sealed class Value(int argument)
    {
        public int Argument { get; } = argument;
    }

    public sealed class Probe
    {
        public int SetterFailures;
        public int Sets;
        public int Mappings;
        public int Opens;
        public int Bodies;
        public int BodyArgument;
        public int OuterCatches;
        public int InnerCatches;
        public int OtherCatches;
        public InvalidOperationException Error { get; } = new("restored input setter failed");
        public List<string> Invocations { get; } = [];

        public int Map()
        {
            Mappings++;

            return 42;
        }
    }

    public sealed class Container(Probe probe) : IScopeActivity<Value>
    {
        private int _argument;

        [ActivityInput, JsonIgnore]
        public int Argument
        {
            get => _argument;
            set
            {
                probe.Sets++;

                if (probe.SetterFailures-- > 0)
                    throw probe.Error;

                _argument = value;
            }
        }

        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        {
            probe.Opens++;
            probe.Invocations.Add(context.InvocationId);

            return Task.FromResult(new Value(Argument));
        }

        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token) => Task.CompletedTask;
    }

    public sealed class Body(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        {
            probe.Bodies++;
            probe.BodyArgument = context.Scope<Value>().Argument;

            return Task.CompletedTask;
        }
    }

    public sealed class OuterCaught(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        {
            probe.OuterCatches++;

            return Task.CompletedTask;
        }
    }

    public sealed class InnerCaught(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        {
            probe.InnerCatches++;

            return Task.CompletedTask;
        }
    }

    public sealed class OtherCaught(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        {
            probe.OtherCatches++;

            return Task.CompletedTask;
        }
    }
}