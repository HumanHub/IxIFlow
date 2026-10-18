using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples;

public sealed class MessageData
{
    public QuoteData Quote { get; set; } = new();
    public RenderedTemplate? Body { get; set; }
    public string? MessageId { get; set; }
}

/// <summary>Publishes immutable culture variants, pins version one, and sends with an invocation idempotency key.</summary>
public static class QuoteMessage
{
    public static void Configure(IServiceCollection services, ITemplateMessageSender sender)
    {
        services.AddIxIFlowTemplates().AddLiquid();
        services.AddSingleton(sender);
    }

    public static Task PublishAsync(IServiceProvider services) =>
        services.GetRequiredService<TemplatePublisher>().PublishAsync<QuoteData>(
            TemplateVersion.Create<QuoteData>("quote", 1, "liquid", TemplateFormat.Text,
                new TemplateVariant("", "Quote for {{ Customer }}"),
                new TemplateVariant("fr", "Devis pour {{ Customer }}")));

    public static WorkflowDefinition Definition() =>
        Workflow.Create<MessageData>("QuoteMessage")
            .Step<RenderTemplate<QuoteData>>(step => step
                .Input(activity => activity.Template).From(ctx => new TemplateReference("quote", 1))
                .Input(activity => activity.Model).From(ctx => ctx.WorkflowData.Quote)
                .Input(activity => activity.Culture).From(ctx => "fr-CA")
                .Output(activity => activity.Rendered).To(ctx => ctx.WorkflowData.Body))
            .Step<SendTemplateMessage>(step => step
                .Input(activity => activity.Channel).From(ctx => "email")
                .Input(activity => activity.Recipients).From(ctx => new List<string> { "ada@example.com" })
                .Input(activity => activity.Body).From(ctx => ctx.WorkflowData.Body!)
                .Output(activity => activity.MessageId).To(ctx => ctx.WorkflowData.MessageId))
            .Build();
}
