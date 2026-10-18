using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Samples;

/// <summary>Small application examples used by the public documentation.</summary>
public static class DocumentationExamples
{
    public static IServiceCollection RetentionServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // snippet: retention
        services.AddIxIFlow(options =>
        {
            options.FinishedInstanceRetention = TimeSpan.FromDays(30);
            options.TombstoneRetention = TimeSpan.FromDays(90);
            options.RetentionScanInterval = TimeSpan.FromHours(1);
            options.RetentionBatchSize = 100;
        });
        // end-snippet: retention
        return services;
    }

    public static void RegisterPublisher(IServiceCollection services)
    {
        // snippet: publisher-registration
        services.AddIxIFlow()
            .ConfigureOutbox(concurrency: 8)
            .AddOutboxPublisher<CompletionLog>();
        // end-snippet: publisher-registration
    }

    // snippet: discoverable-wait
    public static WorkflowDefinition DiscoverableReview() =>
        new WorkflowBuilder<QuoteData, object>([], "DiscoverableReview")
            .WaitFor<ReviewAnswer>("review", configure: wait => wait
                .DefaultPayload(new ReviewAnswer { Customer = "Ada" })
                .Output(answer => answer.Customer).To(ctx => ctx.WorkflowData.Customer))
            .Build();
    // end-snippet: discoverable-wait

    public static async Task<WorkflowExecutionResult> AnswerReviewAsync(IWorkflowClient client,
        string instanceId, string customer)
    {
        // snippet: answer-review
        var waits = await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId));
        var wait = waits.Single();
        return await client.ReplyAsync(wait.Token, new ReviewAnswer { Customer = customer });
        // end-snippet: answer-review
    }
}

// snippet: completion-publisher
public sealed class CompletionLog(ILogger<CompletionLog> logger) : IOutboxPublisher
{
    public string Name => "completion-log";
    public IReadOnlyCollection<string> EventTypes => [OutboxEvent.WorkflowCompleted];

    public Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Completion {EventId}: {Summary}",
            delivery.Event.Id, delivery.Event.DataJson);
        return Task.FromResult(OutboxPublishResult.Delivered());
    }
}
// end-snippet: completion-publisher
