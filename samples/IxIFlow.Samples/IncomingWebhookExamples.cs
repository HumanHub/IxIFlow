using System.Security.Claims;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples;

public static class IncomingWebhookExamples
{
    public static void Run(WebApplicationBuilder builder)
    {
        // snippet: incoming-host
        builder.Services.AddIxIFlow(); // Or choose a persistent store with the returned builder.
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("orders-api", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("order-instance", policy => policy.RequireAssertion(context =>
                context.Resource is WorkflowOwnership resource &&
                (resource.OwnerKind == WorkflowOwnerKind.SignedSender
                    ? resource.Owner == context.User.FindFirstValue("ixiflow:sender")
                    : resource.Owner == context.User.FindFirstValue(ClaimTypes.NameIdentifier) ||
                      resource.Tenant != null && resource.Tenant == context.User.FindFirstValue("tenant"))));
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorkflowTriggers(triggers =>
        {
            triggers.Start<OrderData>("orders.create", "/orders/{orderId}", OrderWorkflow.Definition)
                .FromBody<OrderRequest>((body, request) => new OrderData
                {
                    OrderId = request.Route("orderId")!,
                    Amount = body.Amount
                })
                .Authorize("orders-api")
                .ProjectResult(data => new { data.OrderId, data.ReceiptNumber });

            triggers.Events("orders.events")
                .Allow<PaymentReceived>("com.example.payment.received")
                .Authorize("orders-api")
                .AuthorizeInstance("order-instance");

            triggers.Status()
                .Authorize("orders-api")
                .AuthorizeInstance("order-instance");
        });
        app.Run();
        // end-snippet: incoming-host
    }

    public static void SignedTrigger(WorkflowTriggerBuilder triggers, string currentSecret, string previousSecret)
    {
        // snippet: incoming-signed
        triggers.Start<OrderData>("vendor.orders", "/vendor/orders", OrderWorkflow.Definition)
            .FromBody<OrderRequest>((body, request) => new OrderData { Amount = body.Amount })
            .VerifyStandardWebhook("billing-vendor", currentSecret, previousSecret)
            .Authorize("billing-vendor-policy");
        // end-snippet: incoming-signed
    }

    public static void Map(WebApplication app)
    {
        // snippet: incoming-map
        app.MapWorkflowTriggers(triggers =>
        {
            triggers.Start<OrderData>("orders.create", "/orders", OrderWorkflow.Definition)
                .FromBody<OrderRequest>((body, context) => new OrderData { Amount = body.Amount })
                .Authorize("orders-api")
                .ProjectResult(data => new { data.ReceiptNumber });

            triggers.Events("orders.events")
                .Allow<PaymentReceived>("com.example.payment.received")
                .Authorize("orders-api")
                .AuthorizeInstance("order-instance");

            triggers.Status().Authorize("orders-api").AuthorizeInstance("order-instance");
        });
        // end-snippet: incoming-map
    }

    public sealed class OrderRequest { public decimal Amount { get; set; } }
    public sealed class PaymentReceived { public decimal Amount { get; set; } }
    public sealed class OrderData
    {
        public string OrderId { get; set; } = "";
        public decimal Amount { get; set; }
        public string ReceiptNumber { get; set; } = "";
    }
    public static class OrderWorkflow
    {
        public static WorkflowDefinition Definition { get; } = Workflow.Create<OrderData>("WebhookOrder")
            .Step<AssignReceipt>(step => step
                .Input(activity => activity.OrderId).From(context => context.WorkflowData.OrderId)
                .Output(activity => activity.Receipt).To(context => context.WorkflowData.ReceiptNumber))
            .WaitFor<PaymentReceived>("payment")
            .Build();
    }
    public sealed class AssignReceipt : IAsyncActivity
    {
        public string OrderId { get; set; } = "";
        public string Receipt { get; private set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Receipt = "receipt-" + OrderId;
            return Task.CompletedTask;
        }
    }
}
