# IxIFlow.AspNetCore

Authorized workflow triggers, atomic idempotent starts, CloudEvents ingress and workflow operations for ASP.NET Core on .NET 10.

`app.MapWorkflowOperations("/ixiflow/ops", options => ...)` exposes instance search, waits, inspection, operator decisions and outbox administration. Every route requires an explicit read, reply, inspect, write or outbox policy, and search requires an application `IInstanceQueryScope`. Inspect and reply permissions are independent. Configure the engine's [shared signing key ring](../../docs/reply-tokens.md) before starting the host. See the [HTTP operations guide](../../docs/operations-http.md) for endpoint contracts, resource authorization, actor delegation and bounded mutation waits.

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

Configure authentication and the named ASP.NET Core policies before mapping routes. The instance policy receives a `WorkflowOwnership` resource containing the owner kind, owner and tenant saved at admission. Signed senders and authenticated principals occupy separate owner namespaces; cross-kind access and non-owner access return `404`. Every route requires a policy; status and events also require an instance policy.

Starts acknowledge durable admission with `202` and `Location`, or use a bounded `.Synchronous(...)` wait. `Idempotency-Key` is scoped to the authenticated sender and stable trigger identity, with a default 24-hour store-clock retention. The key, first checkpoint, pinned version, and ownership metadata are saved atomically in every supported store. Repeated requests return the winning instance; conflicting request fingerprints give `409`.

`.VerifyStandardWebhook(sender, currentSecret, previousSecret)` verifies the original bytes and timestamp, supports rotation, establishes a sender identity, and deduplicates signed deliveries by `webhook-id`. Secrets cannot be shared across triggers, and signed start idempotency retention must cover the timestamp tolerance. Both signing IDs and idempotency keys are limited to 128 characters. Signed CloudEvents use structured mode. Other authenticated event routes accept structured or binary JSON CloudEvents, with explicit type allowlists and durable inbox acceptance. CloudEvents deduplicate by `source` plus `id`, regardless of formatting or query string. Signed event receipts default to 24-hour retention, configurable with `.DeliveryRetention(...)`. Body mappings receive only validated envelope metadata and route/query values.

Status exposes only a public DTO and the application's result projection. Active removal tombstones give `410`; unknown instances give `404`. A start retried while its original instance is tombstoned gives `410`. Conflicting routes fail host startup. No internal workflow result, trace, stack, or reply token is serialized.

The database schema is declared at creation and requires recreation of existing databases. See the [incoming webhooks guide](../../docs/incoming-webhooks.md) for policies, signatures, retention, event identities, response contracts, and examples.
