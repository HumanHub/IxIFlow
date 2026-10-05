using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Tests.SyntaxTests;

public class WorkflowBuilderIdentityTests
{
    [Fact]
    public void CatchChainKeepsWorkflowNameAndVersion()
    {
        var definition = Workflow.Create<WorkflowData>("OrderApproval", 2)
            .Step<StartActivity>()
            .Try(body => body.Step<StartActivity>(_ => { }))
            .Catch<InvalidOperationException>(body => body.Step<StartActivity>(_ => { }))
            .Build();

        Assert.Equal("OrderApproval", definition.Name);
        Assert.Equal(2, definition.Version);
    }

    [Fact]
    public void FinallyChainKeepsWorkflowNameAndVersion()
    {
        var definition = Workflow.Create<WorkflowData>("OrderApprovalFinally", 3)
            .Step<StartActivity>()
            .Try(body => body.Step<StartActivity>(_ => { }))
            .Catch<InvalidOperationException>(body => body.Step<StartActivity>(_ => { }))
            .Finally(body => body.Step<StartActivity>(_ => { }))
            .Build();

        Assert.Equal("OrderApprovalFinally", definition.Name);
        Assert.Equal(3, definition.Version);
    }

    public sealed class WorkflowData { }

    public sealed class StartActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
