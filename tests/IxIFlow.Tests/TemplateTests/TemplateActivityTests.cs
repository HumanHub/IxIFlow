using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Templates;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.TemplateTests.TemplateFixtures;

namespace IxIFlow.Tests.TemplateTests;

/// <summary>
/// <c>RenderTemplate&lt;TModel&gt;</c> renders a template against a typed model with no effects, so a render a crash
/// interrupted runs again on recovery; sending is a separate activity whose message ID, its idempotency key, is the
/// same for every attempt of the step (spec H-02).
/// </summary>
[Trait("Feature", "templates")]
[Trait("Spec", "H-02")]
public sealed class TemplateActivityTests
{
    [Fact]
    public async Task AWorkflowRendersAndSends()
    {
        var sender = new RecordingSender();
        using var services = Services(sender: sender);
        await services.PublishAsync<Order>("order-confirmation", 1, "liquid", TemplateFormat.Text, Base(
            "Order {{ Number }} for {{ Customer.Name }}: {% for line in Lines %}{{ line.Quantity }} {{ line.Sku }}" +
            "{% unless forloop.last %}, {% endunless %}{% endfor %}"));

        var result = await services.GetRequiredService<IWorkflowClient>()
            .RunAsync(RenderAndSend("order-confirmation"), new OrderData());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = (OrderData)result.WorkflowData!;
        Assert.Equal("Order A-17 for Ana: 2 pen, 1 ink", data.Body!.Content);
        var message = Assert.Single(sender.Calls);
        Assert.Equal(data.MessageId, message.MessageId);
        Assert.Equal(["ana@example.com"], message.Recipients);
        Assert.Equal("email", message.Channel);
        Assert.Equal("Your order A-17", message.Subject);
        Assert.Equal(data.Body, message.Body);
    }

    [Fact]
    public async Task ARenderACrashInterruptedRendersAgainOnRecovery()
    {
        var store = new FaultInjectingRepository();
        var engine = new CountingEngine();
        using var services = Services(store, engine: engine);
        await services.PublishAsync<Order>("note", 1, "liquid", TemplateFormat.Text, Base("Note {{ Number }}"));
        var definition = Workflow.Create<OrderData>("RenderRecovery")
            .Step<RenderTemplate<Order>>(step => step
                .Input(activity => activity.Template).From(_ => new TemplateReference("note", 1))
                .Input(activity => activity.Model).From(ctx => ctx.WorkflowData.Order)
                .Output(activity => activity.Rendered).To(ctx => ctx.WorkflowData.Body))
            .Build();
        var instanceId = Guid.NewGuid().ToString("N");
        var client = services.GetRequiredService<IWorkflowClient>();

        // The host dies as it saves the render's end.
        store.FailCommit = _ => engine.Renders == 1;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            client.RunAsync(definition, new OrderData(), new WorkflowOptions { InstanceId = instanceId }));
        store.FailCommit = _ => false;
        var result = await client.RecoverAsync(instanceId);

        // Not left for an operator: the render has no effect to check, so it simply runs again.
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(2, engine.Renders);
        Assert.Equal("Note A-17", ((OrderData)result.WorkflowData!).Body!.Content);
    }

    [Fact]
    public async Task ASendACrashInterruptedIsSentAgainWithTheSameMessageId()
    {
        var store = new FaultInjectingRepository();
        var sender = new RecordingSender();
        using var services = Services(store, sender);
        await services.PublishAsync<Order>("order-confirmation", 1, "liquid", TemplateFormat.Text,
            Base("Order {{ Number }}"));
        var instanceId = Guid.NewGuid().ToString("N");
        var client = services.GetRequiredService<IWorkflowClient>();

        // The host dies after the message went out, as it saves the send's end.
        store.FailCommit = _ => sender.Calls.Count == 1;
        await Assert.ThrowsAnyAsync<Exception>(() => client.RunAsync(RenderAndSend("order-confirmation"),
            new OrderData(), new WorkflowOptions { InstanceId = instanceId }));
        store.FailCommit = _ => false;
        var result = await client.RecoverAsync(instanceId);

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(2, sender.Calls.Count);
        Assert.Single(sender.Calls.Select(call => call.MessageId).Distinct());
        Assert.Single(sender.Delivered);
        Assert.Equal(sender.Calls.First().MessageId, ((OrderData)result.WorkflowData!).MessageId);
    }

    [Fact]
    public async Task ARetriedSendKeepsItsMessageId()
    {
        var sender = new RecordingSender { FailAfterDelivery = 1 };
        using var services = Services(sender: sender);
        await services.PublishAsync<Order>("order-confirmation", 1, "liquid", TemplateFormat.Text,
            Base("Order {{ Number }}"));
        var definition = Workflow.Create<OrderData>("RetriedSend")
            .Step<RenderTemplate<Order>>(step => step
                .Input(activity => activity.Template).From(_ => new TemplateReference("order-confirmation", 1))
                .Input(activity => activity.Model).From(ctx => ctx.WorkflowData.Order)
                .Output(activity => activity.Rendered).To(ctx => ctx.WorkflowData.Body))
            .Saga(saga => saga.Step<SendTemplateMessage>(step => step
                .Input(activity => activity.Channel).From(_ => "email")
                .Input(activity => activity.Recipients).From(ctx => new List<string> { ctx.WorkflowData.Order.Customer.Email })
                .Input(activity => activity.Body).From(ctx => ctx.WorkflowData.Body!)
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(1))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new OrderData());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(2, sender.Calls.Count);
        Assert.Single(sender.Calls.Select(call => call.MessageId).Distinct());
        Assert.Single(sender.Delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachItemOfALoopSendsItsOwnMessage(bool parallel)
    {
        var sender = new RecordingSender();
        using var services = Services(sender: sender);
        await services.PublishAsync<Order>("order-confirmation", 1, "liquid", TemplateFormat.Html,
            Base("<p>Order {{ Number }}</p>"));
        var definition = Workflow.Create<OrderData>("SendPerRecipient")
            .Step<RenderTemplate<Order>>(step => step
                .Input(activity => activity.Template).From(_ => new TemplateReference("order-confirmation", 1))
                .Input(activity => activity.Model).From(ctx => ctx.WorkflowData.Order)
                .Output(activity => activity.Rendered).To(ctx => ctx.WorkflowData.Body))
            .ForEach(ctx => ctx.WorkflowData.Recipients, (body, recipient) => body
                .Step<SendTemplateMessage>(step => step
                    .Input(activity => activity.Channel).From(_ => "email")
                    .Input(activity => activity.Recipients).From(ctx => new List<string> { ctx.Item(recipient) })
                    .Input(activity => activity.Body).From(ctx => ctx.WorkflowData.Body!)),
                setup =>
                {
                    // In parallel, each item runs as a branch of its own.
                    if (parallel)
                        setup.Parallel();
                })
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition,
            new OrderData { Recipients = ["ana@example.com", "dan@example.com"] });

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(2, sender.Delivered.Count);
        Assert.Equal(["ana@example.com", "dan@example.com"],
            sender.Calls.SelectMany(call => call.Recipients).Order());
        Assert.All(sender.Calls, call => Assert.Equal("<p>Order A-17</p>", call.Body.Content));
    }

    private static WorkflowDefinition RenderAndSend(string template) =>
        Workflow.Create<OrderData>("RenderAndSend")
            .Step<RenderTemplate<Order>>(step => step
                .Input(activity => activity.Template).From(_ => new TemplateReference(template, 1))
                .Input(activity => activity.Model).From(ctx => ctx.WorkflowData.Order)
                .Input(activity => activity.Culture).From(ctx => ctx.WorkflowData.Culture)
                .Output(activity => activity.Rendered).To(ctx => ctx.WorkflowData.Body))
            .Step<SendTemplateMessage>(step => step
                .Input(activity => activity.Channel).From(_ => "email")
                .Input(activity => activity.Recipients).From(ctx => new List<string> { ctx.WorkflowData.Order.Customer.Email })
                .Input(activity => activity.Subject).From(ctx => $"Your order {ctx.WorkflowData.Order.Number}")
                .Input(activity => activity.Body).From(ctx => ctx.WorkflowData.Body!)
                .Output(activity => activity.MessageId).To(ctx => ctx.WorkflowData.MessageId))
            .Build();
}
