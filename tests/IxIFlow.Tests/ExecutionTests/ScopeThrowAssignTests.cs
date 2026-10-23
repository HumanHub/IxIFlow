using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26"), Trait("Spec", "W-05"), Trait("Spec", "W-04")]
public sealed class ScopeThrowAssignTests
{
    [Fact]
    public async Task ThrowInScopeClosesAsFailedWithTheOriginalFaultBeforeOuterCatch()
    {
        var probe = new Probe();
        await using var services = Services(probe);
        var definition = new WorkflowBuilder<Data, object>([], "ScopeThrow")
            .Try(t => t.Scope<Container>(null, body => body
                .Throw<BusinessError>(c => new("original") { Code = 73 })
                .Assign(c => c.WorkflowData.Value, c => 999)))
            .Catch<BusinessError, Fault>(caught => caught.Step<ObserveCatch>()
                .Assign(c => c.WorkflowData.Value, c => c.Fault.Code))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data()).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Value);
        var closing = Assert.Single(probe.Closings);
        Assert.Equal(ScopeCloseReason.Failed, closing.Reason);
        var error = Assert.IsType<BusinessError>(closing.Error);
        Assert.Equal("original", error.Message);
        Assert.Equal(73, error.Code);
        Assert.Equal(new[] { "close", "catch" }, probe.Events);
    }

    [Fact]
    public async Task RethrowAcrossScopePreservesFaultAfterCatchMutationAndRestart()
    {
        var probe = new Probe();
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "ScopeRethrow")
            .Try(outer => outer.Try(inner => inner.Throw<BusinessError>(c => new("original") { Code = 73 }))
                .Catch<BusinessError, Fault>(caught => caught.Scope<Container>(null, body => body
                    .Step<MutateFault>(s => s.Input(x => x.Fault).From(c => c.Fault))
                    .WaitFor<Signal>("go")
                    .Step<Touch>()
                    .Rethrow()
                    .Assign(c => c.WorkflowData.Value, c => 999))))
            .Catch<BusinessError, Fault>(caught => caught.Assign(c => c.WorkflowData.Value, c => c.Fault.Code))
            .Build();
        string id;
        await using (var first = Services(probe, store, catalog))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data()).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
        }
        await using var recovered = Services(probe, store, catalog);
        var result = await recovered.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "go", new Signal()).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Value);
        var closing = Assert.Single(probe.Closings, item => item.Reason == ScopeCloseReason.Failed);
        var error = Assert.IsType<IxIFlow.Core.Runtime.DurableWorkflowException>(closing.Error);
        Assert.Equal(73, System.Text.Json.JsonSerializer.Deserialize<int>(Assert.Single(error.Saved.Properties).Value.Json!));
        Assert.Equal("original", error.Message);
        var trace = await recovered.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
        var raised = Assert.IsType<FaultTrace>(Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.FaultRaised).Entry.Payload);
        var rethrown = Assert.IsType<FaultTrace>(Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.FaultRethrown).Entry.Payload);
        Assert.Equal(raised.OriginalLocation, rethrown.OriginalLocation);
        Assert.Equal(raised.OriginalLocation, error.Saved.OriginalFaultLocation);
    }

    [Fact]
    public async Task AssignInsideScopeRetainsPreviousAndClosesNormally()
    {
        var probe = new Probe();
        await using var services = Services(probe);
        var definition = new WorkflowBuilder<Data, object>([], "ScopeAssign")
            .Scope<Container>(null, body => body.Assign(c => c.WorkflowData.Value, c => c.PreviousStep.Number + 5)
                .Assign(c => c.WorkflowData.Value, c => c.WorkflowData.Value + c.PreviousStep.Number))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data()).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(19, Assert.IsType<Data>(result.WorkflowData).Value);
        Assert.Equal(ScopeCloseReason.Completed, Assert.Single(probe.Closings).Reason);
    }

    private static ServiceProvider Services(Probe probe, InMemoryWorkflowStateRepository? store = null, IWorkflowCatalog? catalog = null)
    {
        var services = new ServiceCollection().AddSingleton(probe);
        services.AddIxIFlow(o => o.VerifyCheckpointParts = true);
        if (store != null) services.AddSingleton<IWorkflowStateRepository>(store);
        if (catalog != null) services.AddSingleton(catalog);
        return services.BuildServiceProvider();
    }

    public sealed class Data { public int Value { get; set; } }
    public sealed class Signal;
    public sealed class Fault { public int Code { get; set; } }
    public sealed class BusinessError(string message) : Exception(message) { public int Code { get; init; } }
    public sealed class Probe
    {
        public List<ScopeClosing> Closings { get; } = [];
        public List<string> Events { get; } = [];
    }
    public sealed class ObserveCatch(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        { probe.Events.Add("catch"); return Task.CompletedTask; }
    }
    public sealed class MutateFault : IAsyncActivity
    {
        [ActivityInput] public Fault Fault { get; set; } = new();
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        { Fault.Code = 999; return Task.CompletedTask; }
    }
    public sealed class Touch : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        { Assert.NotNull(context.Scope<Resource>()); return Task.CompletedTask; }
    }
    public sealed class Resource;
    public sealed class Container(Probe probe) : IScopeActivity<Resource>
    {
        public int Number { get; set; }
        public Task<Resource> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        { Number = 7; return Task.FromResult(new Resource()); }
        public Task CloseAsync(Resource value, ScopeClosing closing, IScopeContext context, CancellationToken token)
        { probe.Closings.Add(closing); probe.Events.Add("close"); return Task.CompletedTask; }
    }
}
