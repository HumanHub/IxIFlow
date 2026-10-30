using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeDebugTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task BreakpointPrecedesReopenAndContinueConsumesAuthorizationOnce(bool affine)
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = Services(probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();

        var definition = ScopeActivityTests.Start("ScopeBreakpoint").WaitFor<ScopeActivityTests.Signal>("start")
            .Scope<ScopeActivityTests.TestScope>(s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
                b => b.Step<ScopeActivityTests.Read>().WithId("body")).WithId("container").Build();

        var idle = await client.RunAsync(definition, new ScopeActivityTests.Data());
        await operations.SetBreakpointsAsync(idle.InstanceId, [new("inside", "body")], "set");
        var paused = await client.ResumeAsync(idle.InstanceId, "start", new ScopeActivityTests.Signal());
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        Assert.Equal("close:Suspended", probe.Events.Last());
        var inspection = (await operations.InspectAsync(idle.InstanceId))!;
        var scope = Assert.Single(inspection.Scopes);
        Assert.Contains(scope.ActivationId, Assert.Single(inspection.Threads).ScopesInForce);
        Assert.Equal(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread, scope.Threading);
        Assert.Equal("opened", scope.Outputs.GetProperty("Text").GetString());
        Assert.Equal(1, Assert.Single(inspection.Breakpoints).HitCount);
        var result = await operations.ContinueAsync(idle.InstanceId, "continue");
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Contains("open:True:1", probe.Events);
        Assert.Equal(1, Assert.Single((await operations.InspectAsync(idle.InstanceId))!.Breakpoints).HitCount);
    }

    [Theory, InlineData(DebugStepKind.Into, "body"), InlineData(DebugStepKind.Over, "after")]
    public async Task SteppingIntoOrOverIncludesLifecycleWithoutExtraStops(DebugStepKind kind, string expected)
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = Services(probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();

        var definition = ScopeActivityTests.Start("ScopeStepping").WaitFor<ScopeActivityTests.Signal>("start")
            .Scope<ScopeActivityTests.TestScope>(s => s.Threading(ScopeThreading.ThreadAffine),
                b => b.Step<ScopeActivityTests.Read>().WithId("body")).WithId("container")
            .Step<ScopeActivityTests.Copy>().WithId("after").Build();

        var idle = await client.RunAsync(definition, new ScopeActivityTests.Data());
        await operations.SetBreakpointsAsync(idle.InstanceId, [new("container", "container")], "set");

        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await client.ResumeAsync(idle.InstanceId, "start", new ScopeActivityTests.Signal())).Status);

        Assert.Empty(probe.Invocations);
        await operations.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        var thread = Assert.Single((await operations.InspectAsync(idle.InstanceId))!.Threads);

        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await operations.StepAsync(idle.InstanceId, thread.ContinuationId, kind, "step")).Status);

        Assert.Equal(expected, Assert.Single((await operations.InspectAsync(idle.InstanceId))!.Threads).AuthorStepId);
        Assert.Equal(kind == DebugStepKind.Into ? "close:Suspended" : "close:Completed", probe.Events.Last());
    }

    [Fact]
    public async Task SelectedInheritedBranchCanReopenItsHeldOwnersAffineScopeAtCapOne()
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = Services(probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();

        var definition = ScopeActivityTests.Start("ScopeHeldParent").WaitFor<ScopeActivityTests.Signal>("start")
            .Scope<ScopeActivityTests.TestScope>(s => s.Threading(ScopeThreading.ThreadAffine), b => b.Parallel(p => p
                .Do(x => x.Step<ScopeActivityTests.Read>().WithId("selected"))
                .Do(x => x.WaitFor<ScopeActivityTests.Signal>("held")))).Build();

        var idle = await client.RunAsync(definition, new ScopeActivityTests.Data());
        await operations.SetBreakpointsAsync(idle.InstanceId, [new("selected", "selected")], "set");

        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await client.ResumeAsync(idle.InstanceId, "start", new ScopeActivityTests.Signal())).Status);

        var selected = Assert.Single((await operations.InspectAsync(idle.InstanceId))!.Threads,
            thread => thread.AuthorStepId == "selected");

        await operations.SetBreakpointsAsync(idle.InstanceId, [], "clear");

        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await operations.StepAsync(idle.InstanceId, selected.ContinuationId, DebugStepKind.Over, "step")).Status);

        Assert.Contains("open:True:1", probe.Events);
        Assert.Contains("read:opened", probe.Events);
    }

    private static ServiceProvider Services(ScopeActivityTests.Probe probe)
    {
        var services = new ServiceCollection();
        services.AddSingleton(probe);

        services.AddIxIFlow(options =>
        {
            options.Debugging = true;
            options.MaxActivitiesPerRun = 1;
            options.VerifyCheckpointParts = true;
        });

        return services.BuildServiceProvider();
    }
}