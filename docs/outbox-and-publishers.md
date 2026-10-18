---
title: Outbox and publishers
description: Durable completion delivery, custom publishers, outgoing webhooks and templates.
---

A terminal checkpoint writes an immutable completion event in the same transaction as the terminal state. Publishers deliver it afterward, with independent durable delivery state for each publisher. This works with all four stores; memory deliveries survive only while that process lives.

## Completion events

`OutboxEvent.WorkflowCompleted` is `io.ixiflow.workflow.completed`. Its envelope includes event ID/type, instance ID, workflow name/version, correlation ID, store-stamped occurrence time and `DataJson`.

The completion summary contains instance/workflow identity, correlation, terminal status, revision and a bounded error kind. It excludes workflow data, checkpoint internals and exception text. Retrieve a retained result through the client when application data is needed.

The event ID and body stay stable across retries. Delivery is at least once: a publish can succeed before settlement is saved. Consumers must deduplicate by event ID. Each publisher can independently deliver, retry or give up.

## Register a publisher

Implement `IOutboxPublisher` with a stable `Name`, exact `EventTypes` and `PublishAsync`. The host registers it as a singleton. This logging example compiles in [DocumentationExamples.cs](../samples/IxIFlow.Samples/DocumentationExamples.cs), with `IxIFlow.Core` and `Microsoft.Extensions.Logging` imported:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#completion-publisher -->
```csharp
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
```

It may log the same event again after an interrupted settlement. A publisher performing an external effect needs durable receiver-side deduplication.

In application service setup, with `IxIFlow.Extensions` imported:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#publisher-registration -->
```csharp
services.AddIxIFlow()
    .ConfigureOutbox(concurrency: 8)
    .AddOutboxPublisher<CompletionLog>();
```

The source compiles in the samples project; [DocumentationTests.cs](../tests/IxIFlow.Samples.Tests/DocumentationTests.cs) checks registration and the delivery result. Start the [hosted services](coordinator-and-hosts.md) to run delivery workers.

`OutboxPublishContext` supplies only the event, claim attempt count and sampled store time. Return `Delivered()`, `RetryLater(error, delay)` or `GiveUp(error, disablePublisher)`. Exceptions request a retry. Without an explicit delay, custom publishers use exponential delays starting at five seconds and capped at one hour.

## Subscription and delivery lifecycle

Names are unique per host and identify durable subscriptions across restarts/hosts. A new name receives future events by default. `ReplayRetainedOutboxEvents(name)` explicitly includes retained matching events. Registration does not reset deliveries or re-enable a disabled subscription.

Removing a publisher from DI does not retire its durable subscription. Use `UnregisterOutboxPublisher(name)` at startup, or the store's `IWorkflowOutbox.UnregisterPublisherAsync`, to remove it and give up pending deliveries. An absent worker can otherwise leave pending deliveries that block retention.

Workers claim deliveries with renewable leases. Attempt counts include claims lost with their host. Default outbox concurrency is eight, independent of workflow slots; default lease duration is 30 seconds. A 30-day retry horizon runs from the first claim. A delivery no worker ever claims is given up once the horizon has passed since the event occurred, so a publisher missing from every host cannot keep instances from retention forever.

There is no in-process publisher: the host that commits an instance's end completes the awaits on that host itself, without a delivery record. Awaits on other hosts poll the store. The store also exposes publisher/delivery inspection. There is no management HTTP API yet.

## Outgoing webhooks

Install `IxIFlow.Webhooks` and import `IxIFlow.Webhooks`. Call `AddWebhook` on the engine builder with a `WebhookEndpoint` containing a stable endpoint name, absolute HTTP(S) URL, secret and event types such as `OutboxEvent.WorkflowCompleted`. This adds a named `IHttpClientFactory` client and an outbox publisher named `webhook:<endpoint-name>`.

Configure endpoints in trusted application configuration. A signing secret is `whsec_` followed by base64 for 24–64 random bytes. Endpoint string representations redact it. For replay or retirement, use the full publisher name, for example `webhook:orders`.

The publisher POSTs structured CloudEvents JSON with content type `application/cloudevents+json`. It signs the exact UTF-8 body with HMAC-SHA256 using `webhook-id`, `webhook-timestamp` and `webhook-signature` headers in the Standard Webhooks v1 format. Event ID/body remain fixed; each claim uses fresh store time for the signature timestamp.

| Outcome | Action |
| --- | --- |
| Any 2xx | Mark delivered |
| HTTP 410 | Disable the endpoint durably and give up its pending deliveries |
| Other status or transport failure | Retry |
| Redirect | Treated as a failure; automatic redirects are disabled |

Delivery starts immediately. Retry delays are five seconds, five minutes, 30 minutes, two hours, five hours, ten hours, 14 hours, 20 hours and 24 hours, plus up to 5% positive jitter capped at one minute. Failure of attempt ten gives up the delivery. `Retry-After` can extend a delay up to 30 days, bounded by the host's remaining retry horizon. Requests time out after 30 seconds; leases renew while delivery is in flight.

These details come from [WebhookOutboxPublisher.cs](../src/IxIFlow.Webhooks/WebhookOutboxPublisher.cs) and [WebhookBuilderExtensions.cs](../src/IxIFlow.Webhooks/WebhookBuilderExtensions.cs). **Incoming HTTP triggers are planned**; the outgoing package does not create endpoints that start workflows or receive replies.

## Versioned message templates

`IxIFlow.Templates` and `IxIFlow.Templates.Liquid` provide immutable template versions, typed model checks, rendering and sending activities. A `TemplateReference` pins key/version. Variants resolve from requested culture through parent cultures to the required base variant.

`RenderTemplate<TModel>` is recoverable by rendering again. `SendTemplateMessage` calls the application's `ITemplateMessageSender` with the activity invocation ID as message ID; the sender must deduplicate it. These are workflow activities, separate from outbox completion delivery.

[QuoteMessage.cs](../samples/IxIFlow.Samples/QuoteMessage.cs) compiles and exercises publication, culture fallback and sending. The default template store is memory. Persistent workflow bundles, artifact storage and additional broker examples are [planned](roadmap.md).
