using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeFailureBoundaryTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task ReopenFailureBypassesInnerCatchAndPreservesOriginalAcrossNestedCleanupErrors(bool fork)
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = ScopeActivityTests.Services(probe, verify: true);

        var definition = ScopeActivityTests.Start("BoundedScope" + fork).Try(outside => outside.Scope<ScopeActivityTests.TestScope>(null,
                body => body.Try(inner =>
                    {
                        if (fork)
                            inner.Parallel(p =>
                                p.Do(b => b.WaitFor<ScopeActivityTests.Signal>("go").Step<ScopeActivityTests.Read>())
                                    .Do(b => b.WaitFor<ScopeActivityTests.Signal>("loser")));
                        else
                            inner.WaitFor<ScopeActivityTests.Signal>("go").Step<ScopeActivityTests.Read>();
                    }).Catch<InvalidOperationException>(caught =>
                        caught.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "inner-catch")))
                    .Finally(cleanup => cleanup.Try(x => x.If(c => 10 / c.WorkflowData.Zero > 0, _ =>
                        {
                        }))
                        .Catch<DivideByZeroException>(x =>
                            x.Step<ScopeActivityTests.Mark>(s => s.Input(y => y.Text).From(c => "cleanup-catch"))))))
            .Catch<InvalidOperationException>(outside =>
                outside.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "outer-catch"))).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new ScopeActivityTests.Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        probe.FailuresRemaining = 1;
        var done = await client.ResumeAsync(idle.InstanceId, "go", new ScopeActivityTests.Signal());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.DoesNotContain("inner-catch", probe.Events);
        Assert.Contains("cleanup-catch", probe.Events);
        Assert.Equal("outer-catch", probe.Events.Last());
    }

    [Fact]
    public async Task ReopenFailureCompensatesSagaInReverseWithoutRunningItsAuthoredHandler()
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = ScopeActivityTests.Services(probe, verify: true);

        var definition = ScopeActivityTests.Start("BoundedScopeSaga").Try(outside => outside.Scope<ScopeActivityTests.TestScope>(null,
                body => body.Saga(saga => saga
                        .Step<ScopeActivityTests.Copy>(s =>
                            s.CompensateWith<ScopeActivityTests.Mark>(c => c.Input(x => x.Text).From(c => "undo-first")))
                        .Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "second")
                            .CompensateWith<ScopeActivityTests.Mark>(c => c.Input(x => x.Text).From(c => "undo-second")))
                        .WaitFor<ScopeActivityTests.Signal>("go").Step<ScopeActivityTests.Read>())
                    .OnError<InvalidOperationException>(error =>
                        error.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "inner-handler")))))
            .Catch<InvalidOperationException>(outside =>
                outside.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "outer-catch"))).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new ScopeActivityTests.Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        probe.FailuresRemaining = 1;
        var done = await client.ResumeAsync(idle.InstanceId, "go", new ScopeActivityTests.Signal());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.DoesNotContain("inner-handler", probe.Events);
        Assert.Equal(new[] { "undo-second", "undo-first" }, probe.Events.Where(item => item.StartsWith("undo-")));
        Assert.Equal("outer-catch", probe.Events.Last());
    }

    [Fact]
    public async Task RetryPolicyUsesTheExistingAdditionalRetryBudgetAndKeepsIdentity()
    {
        var probe = new ScopeActivityTests.Probe { FailuresRemaining = 2 };
        await using var services = ScopeActivityTests.Services(probe);

        var definition = ScopeActivityTests.Start("ScopeOpenRetries")
            .Scope<ScopeActivityTests.TestScope>(s => s.Retry(new RetryPolicy(2)), b => b.Step<ScopeActivityTests.Read>()).Build();

        var done = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeActivityTests.Data());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(3, probe.Invocations.Count);
        Assert.Single(probe.Invocations.Distinct());
    }

    [Theory, InlineData(2), InlineData(3)]
    public async Task RepeatedReopenFailureAdvancesCompensationInsteadOfRestartingIt(int failures)
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = ScopeActivityTests.Services(probe, verify: true);

        var definition = ScopeActivityTests.Start("RepeatedScopeFailure" + failures).Try(outside =>
                outside.Scope<ScopeActivityTests.TestScope>(null,
                    body => body.Saga(saga => saga
                            .Step<ScopeActivityTests.Copy>(s =>
                                s.CompensateWith<ScopeActivityTests.Mark>(c => c.Input(x => x.Text).From(c => "undo-first")))
                            .Step<ScopeActivityTests.Copy>(s =>
                                s.CompensateWith<ScopeActivityTests.Mark>(c => c.Input(x => x.Text).From(c => "undo-second")))
                            .WaitFor<ScopeActivityTests.Signal>("go").Step<ScopeActivityTests.Read>())
                        .OnError<InvalidOperationException>(error =>
                            error.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "inner-handler")))))
            .Catch<InvalidOperationException>(outside =>
                outside.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "outer-catch"))).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new ScopeActivityTests.Data());
        probe.FailuresRemaining = failures;
        var done = await client.ResumeAsync(idle.InstanceId, "go", new ScopeActivityTests.Signal()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.DoesNotContain("inner-handler", probe.Events);
        Assert.DoesNotContain("undo-second", probe.Events);
        Assert.Equal(failures == 2, probe.Events.Contains("undo-first"));
        Assert.Equal("outer-catch", probe.Events.Last());
        Assert.Equal(4, probe.Invocations.Count);
    }
}