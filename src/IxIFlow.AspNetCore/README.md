# IxIFlow.AspNetCore

Authorized workflow triggers, atomic idempotent starts, and CloudEvents ingress for ASP.NET Core on .NET 10.

<!-- snippet: samples/IxIFlow.Samples/IncomingWebhookExamples.cs#incoming-map -->
```csharp
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
```

Configure authentication and the named ASP.NET Core policies before mapping routes. The instance policy receives a `WorkflowOwnership` resource containing the owner and tenant saved at admission. Every route requires a policy; status and events also require an instance policy.

Starts acknowledge durable admission with `202` and `Location`, or use a bounded `.Synchronous(...)` wait. `Idempotency-Key` is scoped to the authenticated sender and stable trigger identity, with a default 24-hour store-clock retention. The key, first checkpoint, pinned version, and ownership metadata are saved atomically in every supported store. Repeated requests return the winning instance; conflicting request fingerprints give `409`.

`.VerifyStandardWebhook(sender, currentSecret, previousSecret)` verifies the original bytes and timestamp, supports rotation, establishes a sender identity, and deduplicates signed deliveries by `webhook-id`. Signed CloudEvents use structured mode. Other authenticated event routes accept structured or binary JSON CloudEvents, with explicit type allowlists and durable inbox acceptance.

Status exposes only a public DTO and the application's result projection. Active removal tombstones give `410`; unknown instances give `404`. No internal workflow result, trace, stack, or reply token is serialized.

The database schema is declared at creation and requires recreation of existing databases. See the [incoming webhooks guide](../../docs/incoming-webhooks.md) for policies, signatures, retention, event identities, response contracts, and examples.
