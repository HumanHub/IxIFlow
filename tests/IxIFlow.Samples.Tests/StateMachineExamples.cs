using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples.Tests;

public sealed class StateMachineExamples
{
    [Fact]
    public async Task ApprovalStateMachineRunsTheDocumentedReplyExitActionAndFinalEntry()
    {
        var mailbox = new StateMachines.Mailbox();
        var collection = new ServiceCollection().AddLogging();
        collection.AddIxIFlow();
        collection.AddSingleton(mailbox);
        await using var services = collection.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(StateMachines.Definition(), new StateMachines.Approval());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var result = await client.ReplyAsync(mailbox.ReplyToken, new StateMachines.Decision { Approved = true });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<StateMachines.Approval>(result.WorkflowData);
        Assert.True(data.Approved && data.ExitRecorded && data.DecisionRecorded && data.Notified);
    }
}