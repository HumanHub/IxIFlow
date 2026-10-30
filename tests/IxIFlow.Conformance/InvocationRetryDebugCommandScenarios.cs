using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using static IxIFlow.Tests.Conformance.InvocationRetryRecoveryScenarios;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryDebugCommandScenarios
{
    public static IEnumerable<object[]> Commands => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.Select(step => new object[] { profile, step }));

    [Theory, MemberData(nameof(Commands))]
    public async Task InvocationRetryDebugResumeKeepsItsWakeWhenTheImmediateClaimIsRefused(string profile, bool step)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var fault = new InvocationRetryFault();
        var host = await fixture.Open(fault: fault);
        var child = Workflow.Create<Data>("DebugCommandRetryChild").Step<FailOnce>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "DebugCommandRetryParent").WaitFor<Signal>("go")
            .Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build();

        var id = (await host.Client.RunAsync(parent, new Data())).InstanceId;
        Assert.Equal(WorkflowExecutionStatus.Paused, (await host.Operations.PauseAsync(id, "pause")).Status);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await host.Client.ResumeAsync(id, "go", new Signal())).Status);
        var paused = ExecutionCheckpoint.Read((await host.Store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);

        // Claims may return no grant for a live owner or a concurrent store lock. Refuse exactly
        // the immediate attempt; no recovery service, second command or manual Recover may rescue it.
        fault.RefuseNextClaim = 1;

        if (step)
            await host.Operations.StepAsync(id, Assert.Single(paused.Continuations).Id, DebugStepKind.Into, "resume");
        else
            await host.Operations.ContinueAsync(id, "resume");

        Assert.Equal(0, Volatile.Read(ref fault.RefuseNextClaim));

        if (step)
        {
            await InvocationRetryProfile.Until(host.Store, id, saved => saved.Status == WorkflowStatus.Paused &&
                ExecutionCheckpoint.Read(saved.ExecutionStateJson).Debug.PauseGeneration > paused.Debug.PauseGeneration);
        }
        else
            await Success(host.Client, id);

        var commands = (await host.Store.GetDebugStatesAsync([id]))[id].Commands;
        var resume = Assert.Single(commands, command => command.Kind == (step ? DebugCommandKind.Step : DebugCommandKind.Continue));
        Assert.Equal("resume", resume.Actor);
        Assert.NotNull(resume.AcknowledgedRevision);

        if (step)
        {
            await host.Operations.RequestContinueAsync(id, "finish");
            await Success(host.Client, id);
        }

        await InvocationTraceAssertions.PrunedAsync(host.Store, id);
        Assert.Equal(2, (await InvocationTraceAssertions.AttemptsAsync(host.Store, id, parent)).Length);
        Assert.Equal(2, fixture.Ledger.Calls);
    }
}