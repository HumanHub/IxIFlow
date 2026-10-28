# IxIFlow.AspNetCore

Authorized workflow triggers, atomic idempotent starts, CloudEvents ingress and workflow operations for ASP.NET Core on .NET 10.

`app.MapWorkflowOperations("/ixiflow/ops", options => ...)` exposes instance search, waits, inspection, operator decisions and outbox administration. Every route requires an explicit read, reply, inspect, write or outbox policy, and search requires an application `IInstanceQueryScope`. Inspect also requires the reply policy. Treat [reply tokens](../../docs/reply-tokens.md) as capabilities and protect token-bearing listings. See the [HTTP operations guide](../../docs/operations-http.md) for endpoint contracts, resource authorization, actor delegation and bounded mutation waits.

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

## Publishing and catalog HTTP

See the [publishing HTTP guide](../../docs/publishing-http.md) for compiled registration and the route/policy map.

Register `services.AddIxIFlow().AddWorkflowPublishing(httpOptions, publisherOptions)` and an application
`ICatalogQueryScope`, then call `app.MapWorkflowPublishing("/ixiflow/publishing")`. The scope is required even
when the application returns `CatalogScope.Unrestricted`; scoped services are resolved per request.
Set all seven named policies explicitly: Read, Author, Execute, Preview, Publish, Lifecycle and Download.
`ActForPolicy` optionally permits `X-IxIFlow-Actor`; otherwise the authenticated principal supplies the audit actor.
Mapping and host startup check policies, services and limits. Template engines remain optional.

The group exposes drafts (`/drafts/{kind}/{name}/{version}`), full no-write validation (`/validate`), publication
(`/publish`), exact durable attempts (`/publications/{id}`), draft attempt history (`/publications` under the draft),
workflow lifecycle, package upload, workflow/package/activity catalog reads, verified workflow source/bundles and
package nupkgs, and `/schemas/{kind}`. Workflow versions use
`/catalog/workflows/{name}/versions/{version}`, packages use `/catalog/packages/{id}/{version}`. Activity detail
requires `packageId`, `packageVersion` and `build`. Pages use `after` and `limit` (1–1000, default 100), with cursors
bound to the query and application scope. Outside-scope resources return 404; an in-scope resource policy denial is 403.

Save UTF-8 YAML with PUT and `If-None-Match: *` for creation. Save preserves parseable source verbatim and checks its
single root/route identity; semantic validation is a separate full publisher dry run. Update/delete/publish require
the save/detail ETag (`"d-{generation:N}-{revision}"`) in `If-Match`. Workflow deprecate/retire require the detail
lifecycle ETag (`"l-{revision}"`). Missing preconditions return 428, malformed values 400, atomic stale values 412,
and immutable identities or illegal transitions 409. Draft metadata contains no source. Every source or byte download
requires DownloadPolicy separately from metadata ReadPolicy.

Publication work survives request cancellation. An unfinished attempt returns 202, a resolvable Location and
`Retry-After: 5`; GET retrieves that exact attempt from another HTTP host. History remains readable for 30 days after
draft consumption/deletion. Published outcomes project scoped catalog metadata, never internal receipt JSON.
Validation returns a 200 report, including invalid authoring diagnostics. Publication failures map Authoring/Conflict/
Unavailable/Limit/Internal to 422/409/503/413/500. Web enum members are strings; `AddOpenApi`/`MapOpenApi` expose
the supplied operation and schema annotations, policies, selectors, conditional headers and response variants.

Uploads to POST `/catalog/packages` return 201 for the first accepted identity, 200 for identical bytes, 409 for
conflicting bytes and a Location resolving to package detail. Raw and chunked bytes, expanded entries and closure
work are bounded by publisher package limits. Upload admission defaults to two concurrent calls (429 with
`Retry-After: 5` when full); budget memory for compressed buffers, expanded entries and verification/store copies.
Draft input defaults to 1 MiB, publication wait to 10 seconds. Validation and publication execute trusted authored
constructors/Build/Test code under the publisher timeout and belong on a trusted authoring host.

Workflow metadata pages use `W/"{revision}-{scopeAndQueryHash}"` and `private, no-cache`. Availability, host,
attempt and mutation responses use `no-store`. Detail lifecycle ETags are precondition tokens, not availability
validators. Downloads verify digest, archive size and expanded bounds into a delete-on-close temporary stream
before writing the response; unavailable/corrupt bytes return a safe 503 problem, including on conditional reads.
Bundle limits come from `WorkflowBundleOptions.ReadLimits` (default bundle limits if hosting is not configured);
nupkg limits come from `WorkflowPublisherOptions.PackageReadLimits`.

Activity and template drafts use the same kind-keyed IDs, validation, publication receipts and history as workflows.
POST `/drafts/template/{name}/{version}/preview` accepts JSON `{culture,model}` under its own PreviewPolicy.
It runs the full Scratch pipeline and writes no drafts, attempts, packages, artifacts or templates. Preview can execute
trusted constructors, converters, getters and rendering code. Publisher `MaxPreviewModelBytes` and
`MaxPreviewOutputBytes` default to 1 MiB; `BuildTimeout` bounds waiting. The HTTP JSON envelope allows the model
limit plus 4 KiB of overhead and is bounded before parsing. Preview returns content/format/diagnostics, with failures
using the publication status classification. Timeout stops waiting; in-process work may continue until it cooperates.
Register template engines for template operations; absent engines report TPL012.

Read `/catalog/templates`, `/{key}/versions`, `/{key}/versions/{version}` and `/catalog/steps` under ReadPolicy.
Template metadata omits variant content and masks related model packages. Template source at
`/catalog/templates/{key}/versions/{version}/source` requires DownloadPolicy and reconstructs deterministic YAML,
with exact model identity and package digests in comments. Package `/source` returns the verified authored
`ixiflow/activity.yaml` entry, or 404 when absent. Every package source/nupkg/nuspec response uses the same verifier.

DELETE `/catalog/packages/{id}/{version}` requires LifecyclePolicy and the detail lifecycle ETag. It withdraws the
retained row, returns `{package,holders}`, and succeeds with holders. GET the same path plus `/holders` uses
LifecyclePolicy. Holder identities are scope-masked with `outsideScope`; stale withdrawal CAS returns 412.
Same-byte verified upload restores a withdrawn identity. Existing pinned consumers retain their bytes.

The DownloadPolicy-protected NuGet V3 subset is `/nuget/index.json`, `/nuget/flatcontainer/{id}/index.json` and
`/nuget/flatcontainer/{id}/{version}/{file}`. The last accepts only normalized `id.version.nupkg` or `id.nuspec`.
The version index includes authorized, available withdrawn versions so fresh-cache external restore continues;
IxIFlow new-authoring selection and resolution refuse withdrawn versions. Index responses use no-store because
authorization and artifact availability can change. Feed clients must authenticate as required by the application.
