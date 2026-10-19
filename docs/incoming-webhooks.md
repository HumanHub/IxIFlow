# Incoming workflow webhooks

Install `IxIFlow.AspNetCore` in a .NET 10 ASP.NET Core application. It references the ASP.NET Core shared framework and the workflow engine. Choose the engine's memory, SQLite, SQL Server, or PostgreSQL store normally. Each store supports the same atomic admission protocol.

## Configure policies and routes

Configure your application's authentication scheme first. Each authenticated principal must have a stable `ClaimTypes.NameIdentifier`. The following policies allow an authenticated caller to submit work and an owner or a member of the saved tenant to read or signal an instance:

<!-- snippet: samples/IxIFlow.Samples/IncomingWebhookExamples.cs#incoming-host -->
```csharp
using System.Security.Claims;
using IxIFlow.AspNetCore;
using IxIFlow.Core;
using IxIFlow.Extensions;

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
```

`OrderWorkflow`, its workflow data, and the request and event types above are application types. Routes belong to the web application. Their configuration does not change the workflow definition's fingerprint.

The stable trigger identity, such as `orders.create`, must survive a route rename. Identities must be unique. Conflicting HTTP routes, missing policies, unknown policies, absent typed mappings, and empty event allowlists are refused during registration or by the workflow host's startup lifecycle validation, before `ApplicationStarted`. Conflicts with ordinary application endpoints also fail startup before any request is needed. Register exactly one status endpoint. Status and event endpoints require both a general policy and an instance policy. Named policies are checked by the endpoint filter after signature verification. Authentication middleware and any application-wide authorization rules still run before the endpoint filter.

## Start a workflow

Send JSON or a structured or binary JSON CloudEvent to the start route. `FromBody<T>` deserializes the plain JSON body or the envelope's `data` and gives the mapper validated CloudEvents metadata through `request.CloudEvent`. `request.Route(name)` and `request.Query(name)` expose mapping inputs. The mapping context exposes no `HttpContext`, raw request, or headers; CloudEvents metadata comes only from the validated envelope.

```http
POST /orders/42
Content-Type: application/json
Idempotency-Key: purchase-42
Authorization: Bearer ...

{"amount":25.50}
```

A start replies only after the first runnable checkpoint, ownership metadata, and idempotency record are committed together:

```http
HTTP/1.1 202 Accepted
Location: /workflows/2a63...
Content-Type: application/json

{"id":"2a63...","status":"Running","result":null}
```

The workflow name and version in that first checkpoint are pinned. Recovery runs that saved version. A request disconnect or synchronous timeout does not cancel admitted work.

Add `.Synchronous(TimeSpan.FromSeconds(10))` to a start to await a terminal result for up to that limit. The maximum permitted limit is 30 seconds. Completion gives `200` with the same public status DTO and its projected result. A workflow still running, waiting for an event, or stopped when the limit expires gives `202` and a status location. A terminal failure gives `200` with `status: "Failed"` and a null result. Internal exception details are never returned.

## Idempotency and expiry

An ordinary authenticated sender's namespace is derived from the principal's subject, issuer, and tenant. The key is scoped to that sender and the stable trigger identity. A request without `Idempotency-Key` is a new admission. Identities and keys must contain 1–128 characters without surrounding whitespace.

The unique store key is `(sender, trigger, key)`. Concurrent callers can propose different generated instance IDs; exactly one first checkpoint wins. A loser reads that winner's record. The adapter compares the request fingerprint: a match returns the original ID, while a mismatch gives `409` problem details. This also handles a lost response. There is no separate key reservation that could survive without its instance.

Fingerprints cover the original body bytes, all route values and query values, the content type, and every CloudEvents header in binary mode. Changing JSON whitespace therefore changes the fingerprint. The literal route path is not a namespace: renaming a route with the same mapping inputs preserves retries.

Keys expire after 24 hours by default, according to the store's clock. Change a start's duration with `.IdempotencyRetention(TimeSpan.FromHours(48))`. The normal retention scan deletes expired keys and signed event delivery records in bounded batches. Expiry permits a fresh admission with a fresh generated ID, even if the previous instance remains retained. Retries after expiry no longer have the original idempotency guarantee. A start retried while its original instance is tombstoned returns `410` without a status location.

Owner and tenant metadata remain with the instance and its active removal tombstone independently of key expiry. `WorkflowOwnership.OwnerKind` separates `SignedSender` owners from authenticated `Principal` owners. The endpoint enforces this boundary before the application's instance policy: a bearer principal can never access a sender-owned instance, even with the same subject or tenant, and a signed sender cannot access a principal-owned instance. Metadata is bound to the instance incarnation, so reuse of an old instance ID cannot inherit its earlier owner's access.

## Verify Standard Webhooks

Configure the stable sender and its current and previous signing secrets on each signed route. Each trigger requires its own secrets; sharing any current or rotation secret across triggers is refused at mapping time:

<!-- snippet: samples/IxIFlow.Samples/IncomingWebhookExamples.cs#incoming-signed -->
```csharp
triggers.Start<OrderData>("vendor.orders", "/vendor/orders", OrderWorkflow.Definition)
    .FromBody<OrderRequest>((body, request) => new OrderData { Amount = body.Amount })
    .VerifyStandardWebhook("billing-vendor", currentSecret, previousSecret)
    .Authorize("billing-vendor-policy");
```

Define `billing-vendor-policy` with `RequireClaim("ixiflow:sender", "billing-vendor")`, plus any application requirements. A verified signature establishes an authenticated principal whose name identifier and `ixiflow:sender` claim contain that configured sender. Its default owner is the sender, with `OwnerKind` set to `SignedSender`. Bearer admissions use `Principal`. For a trusted tenant association, use `.OwnedBy(http => (owner, tenant))`; derive these values from application configuration or authenticated claims.

The filter verifies the exact original bytes using `webhook-id`, `webhook-timestamp`, and a supported v1 signature before JSON parsing or mapping. It accepts any configured rotation key, compares signatures in constant time, and checks both old and future timestamps against a five-minute tolerance. `.TimestampTolerance(...)` changes that bound, up to one hour. Signed starts require idempotency retention to be at least this tolerance, regardless of builder call order. Invalid signatures or timestamps give `401`. Both `webhook-id` and `Idempotency-Key` accept 1-128 characters without surrounding whitespace; oversized signing IDs fail verification.

For signed starts the signed `webhook-id` is the idempotency key; an unsigned `Idempotency-Key` header cannot override it. Signed event deliveries also have a durable receipt keyed by sender, trigger, and `webhook-id`, saved in the inbox transaction. Reusing that delivery ID for a different CloudEvents identity or target instance gives `409`. Receipts default to 24-hour retention; configure event triggers with `.DeliveryRetention(TimeSpan.FromHours(48))`. CloudEvents identity duplicates keep their recorded outcome even when the receipt expires, for as long as the instance and inbox are retained.

Use structured CloudEvents on signature-protected routes. Standard Webhooks signs the body and its three signing fields; binary CloudEvents metadata lives in other headers. Signed binary requests are refused with `400` because those metadata headers would lack signature integrity. Authenticated routes without this filter accept binary CloudEvents.

## Send events

`POST /workflows/{id}/events/{key}` checks the general policy and then the saved owner or tenant through the instance policy. Each external type must be explicitly mapped with `.Allow<TEvent>("external.type")`.

Structured example:

```http
POST /workflows/2a63.../events/payment
Content-Type: application/cloudevents+json
Authorization: Bearer ...

{"specversion":"1.0","id":"payment-123","source":"urn:billing","type":"com.example.payment.received","data":{"amount":25.50}}
```

Binary example:

```http
POST /workflows/2a63.../events/payment
Content-Type: application/json
ce-specversion: 1.0
ce-id: payment-123
ce-source: urn:billing
ce-type: com.example.payment.received
Authorization: Bearer ...

{"amount":25.50}
```

Both forms require a valid CloudEvents 1.0 envelope and JSON data. Structured `data_base64` is supported when the decoded data is JSON. Core attributes have the required string types. Malformed envelopes, unsupported content, mixed content modes, and types outside the allowlist give `400`. Body size is limited to 1 MiB by default; `.MaxBodyBytes(...)` accepts a bound up to 16 MiB. Oversized requests give `413`.

Event identity is the pair of CloudEvents `source` and `id`, encoded without ambiguity and hashed to the engine's event ID. It is scoped to the target instance. A redelivery with that same identity is a duplicate, regardless of JSON formatting, query string, wait key, payload, or other metadata; it returns the original event's recorded outcome and never replaces the original event. Start admission still compares the full request fingerprint.

`202` means the inbox has durably accepted the event for evaluation; the owning workflow may decide it later. A known refusal gives `409` with a reason such as `NoOpenWait`, `WaitTimedOut`, `InstanceFinished`, or `InstanceRemoved`. A repeated event returns its recorded outcome. A key-only event can answer only a wait that was open at arrival. Request-token replies and privileged wait discovery are separate engine APIs and are not exposed by this package.

## Read public status

`GET /workflows/{id}` requires the configured policy and resource authorization against `WorkflowOwnership`. It returns:

| Instance state | Response |
| --- | --- |
| Running, waiting, or stopped | `200`, public status and null result |
| Retained terminal instance | `200`, public status and application-projected result on success |
| Active removal tombstone | `410`, `Removed`, null result, still authorized against saved ownership |
| Unknown ID, expired tombstone, or instance without ingress ownership | `404` problem details |
| Authenticated caller outside the instance's access scope | `404` problem details, matching an unknown ID |

Public statuses are `Running`, `Waiting`, `Stopped`, `Completed`, `Failed`, `Cancelled`, `TimedOut`, and `Removed`. Results are null unless `.ProjectResult(...)` is configured. The adapter never serializes `WorkflowExecutionResult`, workflow data, traces, error messages, stack traces, or reply tokens. The application chooses the public fields in its projection. If an old trigger is no longer mapped, its retained status remains readable with a null projection.

The schema is declared at database creation. An existing instance table with missing current tables or columns produces a clear engine exception explaining that the database was created by an earlier build and must be recreated; the package performs no in-place upgrade. Custom stores must implement `IWorkflowAdmissionStore` explicitly. Acceptance-only engine integration is available through `IWorkflowIngressClient`; it shares the existing commit and inbox paths and wakes the normal host backlog.

Protocol references: [Standard Webhooks specification](https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md), [CloudEvents specification](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md), and [CloudEvents HTTP binding](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/bindings/http-protocol-binding.md).

For token-addressed replies, send `X-IxIFlow-Reply-Token`. The engine checks the token format and route instance before any store access, then checks the addressed wait. Authentication and instance authorization still apply. See [reply tokens](reply-tokens.md) for capability handling.
