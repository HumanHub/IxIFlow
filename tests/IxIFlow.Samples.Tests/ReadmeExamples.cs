using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;

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
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.ErrorMessage);
        Console.WriteLine(((QuoteData)result.WorkflowData!).Amount); // 12.50
        Assert.Equal(12.5m, Assert.IsType<QuoteData>(result.WorkflowData).Amount);
    }

    [Fact]
    public async Task RootHostingExampleRuns()
    {
        var result = await Hosting.RunHostedAsync();
        Assert.True(result.IsSuccess, result.ErrorMessage);
    }
}
