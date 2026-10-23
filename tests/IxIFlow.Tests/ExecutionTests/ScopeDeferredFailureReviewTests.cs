using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeDeferredFailureReviewTests
{
    [Fact]
    public async Task HeldJoinPersistsItsBoundedFaultPropertiesAndRoutesThemAfterProviderRecovery()
    {
        var store = new InMemoryWorkflowStateRepository();
        var probe = new Probe();
        var definition = new WorkflowBuilder<Data, object>([], "ScopeDeferredFault").WaitFor<Signal>("start")
            .Try(b => b.Scope<Container>(null, body => body.Parallel(p =>
                p.Do(x => x.Step<ScopeLifetimeTests.Noop>().WithId("selected"))
                    .Do(x => x.Step<ScopeLifetimeTests.Noop>()))))
            .Catch<CodedException, Fault>(b => b.Step<Copy>(s => s.Input(x => x.Code).From(c => c.Fault.Code)
                .Output(x => x.Code).To(c => c.WorkflowData.Code))).Build();
        string id;
        using (var services = Services(store, probe))
        {
            var client = services.GetRequiredService<IWorkflowClient>();
            var operations = services.GetRequiredService<IWorkflowOperations>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            await operations.SetBreakpointsAsync(id, [new("selected", "selected")], "set");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new Signal())).Status);
            var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));
            var selected = state.Debug.Stop!.BranchId;
            probe.FailReopen = true;
            Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.StepAsync(id, selected, DebugStepKind.Run, "run-selected")).Status);
            state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));
            var failure = state.Continuations.Single(c => c.Id == selected).DeferredFailure!;
            Assert.NotNull(failure);
            Assert.Equal("reopen-fault", failure.Message);
            Assert.NotEmpty(failure.Error.Properties);
            Assert.Contains(failure.Error.Properties.Values, property => property.Json == "73");
            Assert.False(failure.Error.IsRestorable);
            Assert.Equal(Assert.Single(state.Scopes).Id, failure.ScopeBoundary!.BodyActivationId);
            Assert.Equal(Assert.Single(state.Scopes).ContinuationId, failure.ScopeBoundary.OwnerContinuationId);
        }
        using var recovered = Services(store, probe);
        await recovered.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var result = await recovered.GetRequiredService<IWorkflowOperations>().ContinueAsync(id, "continue");
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Code);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, Probe probe)
    {
        var collection = new ServiceCollection().AddSingleton(store).AddSingleton(probe);
        collection.AddIxIFlow(options => { options.Debugging = true; options.VerifyCheckpointParts = true; });
        return collection.BuildServiceProvider();
    }
    public sealed class Data { public int Code { get; set; } }
    public sealed class Signal;
    public sealed class Probe { public bool FailReopen; }
    public sealed class Fault { public int Code { get; set; } }
    public sealed class CodedException(string message, int number) : Exception(message) { public int Code { get; } = number; }
    public sealed class Copy : IAsyncActivity
    { public int Code { get; set; } public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask; }
    public sealed class Container(Probe probe) : IScopeActivity<ScopeBuildRuleTests.Value>
    {
        public Task<ScopeBuildRuleTests.Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        {
            if (opening.Reopen && probe.FailReopen) { probe.FailReopen = false; throw new CodedException("reopen-fault", 73); }
            return Task.FromResult(new ScopeBuildRuleTests.Value());
        }
        public Task CloseAsync(ScopeBuildRuleTests.Value value, ScopeClosing closing, IScopeContext context, CancellationToken token) => Task.CompletedTask;
    }
}
