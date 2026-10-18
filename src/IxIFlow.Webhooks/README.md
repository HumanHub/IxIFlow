# IxIFlow.Webhooks

Outgoing webhook delivery for IxIFlow outbox events. This is a separate package because core already uses hosting but does not depend on Microsoft.Extensions.Http; webhooks add that dependency for IHttpClientFactory. Core's dependencies stay unchanged.

~~~csharp
services.AddIxIFlow()
    .ConfigureOutbox(concurrency: 8)
    .AddWebhook(new WebhookEndpoint(
        "orders", new Uri("https://receiver.example/workflow-events"),
        configuration["Webhooks:Orders:Secret"]!, [OutboxEvent.WorkflowCompleted]));
~~~

Import IxIFlow.Core, IxIFlow.Extensions and IxIFlow.Webhooks. Names are durable endpoint identities and must be unique on a host. A disabled endpoint remains disabled on re-registration, including after a host restart. Configuration is trusted application configuration. Secrets use whsec_ plus base64 for 24–64 random bytes. Endpoints select exact event type names. New publisher names receive only future completions. Use ReplayRetainedOutboxEvents("webhook:orders") on the builder for explicit replay, or UnregisterOutboxPublisher("webhook:orders") to retire a subscription and give up its pending deliveries.

The publisher signs the exact UTF-8 CloudEvents structured JSON body, using webhook-id, webhook-timestamp and webhook-signature (v1 HMAC-SHA256). The event ID and body stay fixed through retries; each claim supplies fresh store time for the attempt timestamp. The event contains a completion summary: instance, workflow, version, correlation, terminal status, revision and a bounded error kind. It excludes exception text, checkpoint internals and workflow data. Endpoint string representations redact the secret.

Delivery starts immediately, then retries after 5 seconds, 5 minutes, 30 minutes, 2 hours, 5 hours, 10 hours, 14 hours, 20 hours and 24 hours. Retries add positive random jitter of at most 5%, capped at one minute, through an injectable Random. Failure of the tenth attempt gives up that delivery. Retry-After can extend a delay, up to 30 days. All 2xx responses deliver; 410 atomically disables the endpoint in the store and gives up its pending deliveries. Other statuses and transport failures retry. Requests time out after 30 seconds; the builder disables HTTP redirects. Each attempt creates a named client from IHttpClientFactory; handlers use a five-minute pooled connection lifetime. The host renews delivery leases while requests run.

The schedule and signing format follow [Standard Webhooks](https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md). The body follows [CloudEvents JSON format 1.0.2](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/formats/json-format.md).
