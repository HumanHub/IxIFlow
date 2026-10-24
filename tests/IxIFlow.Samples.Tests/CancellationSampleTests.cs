using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "cancellation"), Trait("Spec", "W-06")]
public sealed class CancellationSampleTests
{
    internal static WorkflowDefinition GeneratedDefinition() => IxIFlow.Samples.Generated.Workflow_cancellation_approval_workflow.Build();

    [Theory, InlineData(false), InlineData(true)]
    public async Task FluentAndYamlApprovalMatchForCancellationAndCompletion(bool cancel)
    {
        var fluent = CancellationApproval.Definition(); var yaml = GeneratedDefinition();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        foreach (var definition in new[] { fluent, yaml })
        {
            await using var services = new ServiceCollection().AddIxIFlow().Services.BuildServiceProvider();
            var client = services.GetRequiredService<IWorkflowClient>();
            var idle = await client.RunAsync(definition, new CancellationApprovalData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            var done = cancel ? await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "withdraw" }) :
                await client.ResumeAsync(idle.InstanceId, "approval", new CancellationApprovalSignal());
            Assert.Equal(cancel ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Success, done.Status);
            Assert.Equal(cancel ? "withdrawn" : "approved", Assert.IsType<CancellationApprovalData>(done.WorkflowData).State);
        }
    }
}
