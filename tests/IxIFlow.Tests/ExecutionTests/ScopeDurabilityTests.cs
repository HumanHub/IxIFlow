using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeDurabilityTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task UnknownOutcomeWaitsForResolutionAndOperatorDecisionNeedsNoReopen(bool failed)
    {
        var probe = new ScopeActivityTests.Probe();
        var store = new FaultInjectingRepository();
        var collection = new ServiceCollection();
        collection.AddIxIFlow(options => options.VerifyCheckpointParts = true);
        collection.AddSingleton<IWorkflowStateRepository>(store);
        collection.AddSingleton(probe);
        await using var services = collection.BuildServiceProvider();

        var definition = ScopeActivityTests.Start("ScopeOperatorResolution" + failed)
            .Try(b => b.Scope<ScopeActivityTests.TestScope>(null, x => x.Step<ScopeActivityTests.Read>()))
            .Catch<Exception>(b => b.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "caught"))).Build();

        store.FailCommit = instance => probe.Events.Contains("read:opened") &&
            ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance)).Continuations.All(c => c.PendingActivity == null);

        var client = services.GetRequiredService<IWorkflowClient>();
        var id = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            client.RunAsync(definition, new ScopeActivityTests.Data(), new() { InstanceId = id }));

        store.FailCommit = _ => false;
        probe.FailOpen = true;
        var waiting = await client.RecoverAsync(id);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, waiting.Status);
        var pending = Assert.Single(await client.GetPendingActivitiesAsync(id));
        var saved = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));

        Assert.Contains("could not be re-opened",
            Assert.Single(saved.Continuations, c => c.PendingActivity != null).PendingActivity!.ResolutionReason);

        var calls = probe.Invocations.Count;

        var done = await client.ResolveAsync(id, pending.InvocationId, failed
            ? ActivityResolution.Failed("confirmed failure")
            : ActivityResolution.Completed(new Dictionary<string, object?>()));

        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(calls, probe.Invocations.Count);
        Assert.Equal(failed, probe.Events.Contains("caught"));
        Assert.Single(probe.Events, e => e == "read:opened");
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ControlFlowThatSkipsRemainingBodyDoesNotReopen(bool affine)
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = ScopeActivityTests.Services(probe, verify: true);

        var definition = ScopeActivityTests.Start("ScopeLazyControl" + affine).Scope<ScopeActivityTests.TestScope>(
                s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
                b => b.WaitFor<ScopeActivityTests.Signal>("go").If(c => c.WorkflowData.Zero > 0, x => x.Step<ScopeActivityTests.Read>()))
            .Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new ScopeActivityTests.Data());
        probe.FailOpen = true;
        var done = await client.ResumeAsync(idle.InstanceId, "go", new ScopeActivityTests.Signal());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Single(probe.Invocations);

        Assert.Contains((await client.GetTraceAsync(idle.InstanceId)),
            entry => entry.Payload is ScopeTrace { Cause: ScopeTraceCause.Interrupted });
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task NullRequiredInputFailsBeforeAnyOpenCallback(bool affine)
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = ScopeActivityTests.Services(probe);

        var definition = ScopeActivityTests.Start("ScopeRequiredInput" + affine).Try(b => b.Scope<Required>(
                s => s.Input(x => x.Text).From(c => (string)null!)
                    .Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
                x => x.Step<ScopeActivityTests.Read>()))
            .Catch<Exception>(x => x.Step<ScopeActivityTests.Mark>(s => s.Input(y => y.Text).From(c => "caught"))).Build();

        var done = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeActivityTests.Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, done.Status);
        Assert.Contains("Required.Text", done.ErrorMessage);
        Assert.Empty(probe.Invocations);
        Assert.Empty(probe.Events);
    }

    public sealed class Required(ScopeActivityTests.Probe probe) : IScopeActivity<ScopeActivityTests.Resource>
    {
        [Required, ActivityInput] public string Text { get; set; } = "";

        public Task<ScopeActivityTests.Resource> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken)
        {
            probe.Invocations.Enqueue(context.InvocationId);

            return Task.FromResult(new ScopeActivityTests.Resource(Text, probe));
        }

        public Task CloseAsync(ScopeActivityTests.Resource value, ScopeClosing closing, IScopeContext context,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task IngressRefusesARequiringRootBeforeAdmission()
    {
        await using var services = ScopeActivityTests.Services(new());

        var definition = Workflow.Create<ScopeActivityTests.Data>("ScopeIngressRequirement")
            .RequiresScope<ScopeActivityTests.Resource>().Build();

        var admission = new WorkflowAdmission("sender", "trigger", "key", new string('a', 64), "owner");

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => services.GetRequiredService<IWorkflowIngressClient>()
            .AdmitAsync(definition, new ScopeActivityTests.Data(), admission));

        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Null(await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowAdmissionAsync("sender", "trigger", "key"));
    }
}