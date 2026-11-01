using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Samples.Tests;

public sealed class ReadmeExamples
{
    [Fact]
    public async Task RootQuickStartRuns()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<QuoteData>("QuickStart")
            .Step<AddFee>(step => step
                .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount))
            .Build();

        WorkflowDefinitionValidator.Validate(definition);

        var result = await client.RunAsync(definition, new QuoteData());

        if (result.Status != WorkflowExecutionStatus.Success)
            throw new InvalidOperationException(result.ErrorMessage);

        Console.WriteLine(((QuoteData)result.WorkflowData!).Amount); // 12.50
        Assert.Equal(12.5m, Assert.IsType<QuoteData>(result.WorkflowData).Amount);
    }

    [Fact]
    public async Task RootApprovalWaitsForAReply()
    {
        await using var provider = SampleTests.Services();
        var client = SampleTests.Client(provider);

        var started = await client.RunAsync(OrderApproval.Definition(), new OrderData());

        // started.Status is Suspended: the instance is saved and waits for a decision.

        var wait = (await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId))).Single();
        var finished = await client.ReplyAsync(wait.Token, new OrderDecision { Approved = true, By = "manager" });

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.True(finished.Status == WorkflowExecutionStatus.Success, finished.ErrorMessage);
        Assert.Equal("approved", Assert.IsType<OrderData>(finished.WorkflowData).Outcome);
    }

    [Fact]
    public async Task RootApprovalEscalatesWhenNobodyAnswers()
    {
        var time = new FakeTimeProvider();
        await using var provider = SampleTests.Services(time);
        var client = SampleTests.Client(provider);

        var started = await client.RunAsync(OrderApproval.Definition(), new OrderData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        time.Advance(TimeSpan.FromDays(2));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var finished = await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished, deadline.Token);
        Assert.True(finished.Status == WorkflowExecutionStatus.Success, finished.ErrorMessage);
        Assert.Equal("escalated", Assert.IsType<OrderData>(finished.WorkflowData).Outcome);
    }

    [Fact]
    public async Task RootHostingExampleRuns()
    {
        var result = await Hosting.RunHostedAsync();
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
    }
}