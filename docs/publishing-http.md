---
title: Publishing and catalog over HTTP
description: Authorized drafts, publication, catalogs, previews, package upload and NuGet reads.
---

The ASP.NET Core publishing group uses the selected workflow catalog and artifact store. A publishing host may compile and execute trusted authoring code. Execution hosts consume verified digest-pinned bundles separately.

## Registration

Configure application authentication before using these policies. This compiled example requires a named permission claim for every operation and an explicit scope implementation:

<!-- snippet: samples/IxIFlow.Samples/PublishingHttpExamples.cs#publishing-register -->
```csharp
public static void Register(IServiceCollection services)
{
    services.AddAuthorization(options =>
    {
        foreach (var permission in new[] { "read", "author", "execute", "preview", "publish", "lifecycle", "download" })
            options.AddPolicy("publishing-" + permission, policy => policy
                .RequireAuthenticatedUser().RequireClaim("publishing", permission));
    });

    services.AddScoped<ICatalogQueryScope, SampleCatalogScope>();

    services.AddIxIFlow().AddWorkflowPublishing(new WorkflowPublishingOptions
    {
        ReadPolicy = "publishing-read",
        AuthorPolicy = "publishing-author",
        ExecutePolicy = "publishing-execute",
        PreviewPolicy = "publishing-preview",
        PublishPolicy = "publishing-publish",
        LifecyclePolicy = "publishing-lifecycle",
        DownloadPolicy = "publishing-download"
    });
}
```

<!-- snippet: samples/IxIFlow.Samples/PublishingHttpExamples.cs#publishing-map -->
```csharp
public static void Map(WebApplication app) => app.MapWorkflowPublishing("/ixiflow/publishing");
```

[SampleCatalogScope](../samples/IxIFlow.Samples/PublishingHttpExamples.cs) permits the published-artifact sample identities, workflow names starting with `Sample`, and activity, package and template identities starting with `samples.`. Use the principal to select your own identity sets. `CatalogScope.Unrestricted` is an explicit deployment choice. Scope filtering precedes counts and pagination and does not replace resource authorization.

## Route and policy map

Paths below are relative to `/ixiflow/publishing`. `kind` is `workflow`, `activity` or `template`.

| Method and path | Policy | Purpose |
| --- | --- | --- |
| `GET /drafts`, `GET /drafts/{kind}/{name}/{version}` | Read | Draft metadata and state tokens |
| `PUT`, `DELETE /drafts/{kind}/{name}/{version}` | Author | Save parseable source or delete the exact draft |
| `GET /drafts/{kind}/{name}/{version}/source` | Download | Verbatim saved YAML |
| `POST /drafts/{kind}/{name}/{version}/validate` | Execute | Full no-write validation of saved or supplied YAML |
| `POST /drafts/{kind}/{name}/{version}/publish` | Publish | Publish the exact saved draft |
| `GET /publications/{id}`, `GET /drafts/{kind}/{name}/{version}/publications` | Read | Exact durable attempt and paged history |
| `POST /drafts/template/{name}/{version}/preview` | Preview | Full no-write template preview |
| `GET /catalog/workflows`, `/{name}/versions`, `/{name}/versions/{version}` | Read | Workflow metadata |
| `GET /catalog/workflows/{name}/versions/{version}/hosts` | Read | Current compatible hosts |
| `POST /catalog/workflows/{name}/versions/{version}/deprecate`, `/retire` | Lifecycle | Forward-only state transition |
| `GET /catalog/workflows/{name}/versions/{version}/source`, `/bundle` | Download | Verified canonical YAML or bundle |
| `GET /catalog/packages`, `/{id}/versions`, `/{id}/{version}`, `/{id}/{version}/activities`, `/{id}/{version}/hosts` | Read | Package metadata, scoped activities and hosts |
| `POST /catalog/packages` | Publish | Verified nupkg upload or same-byte restore |
| `DELETE /catalog/packages/{id}/{version}`, `GET /catalog/packages/{id}/{version}/holders` | Lifecycle | Withdrawal and scoped holders |
| `GET /catalog/packages/{id}/{version}/source`, `/nupkg` | Download | Authored activity source or exact nupkg |
| `GET /catalog/activities`, `/{key}/versions`, `/{key}/versions/{version}` | Read | Activity metadata and provider selection |
| `GET /catalog/templates`, `/{key}/versions`, `/{key}/versions/{version}` | Read | Template metadata without variant text |
| `GET /catalog/templates/{key}/versions/{version}/source` | Download | Reconstructed template document |
| `GET /catalog/steps`, `GET /schemas/{kind}` | Read | Built-in editor steps and document schemas |
| `GET /nuget/index.json`, `/nuget/flatcontainer/{id}/index.json`, `/nuget/flatcontainer/{id}/{version}/{file}` | Download | NuGet V3 read subset |

Activity detail requires `packageId`, `packageVersion` and `build` query selectors. The activity version is its declaration version; the provider package version is a separate value. `build` is a digest for artifact providers or a fingerprint for Local providers.

## Preconditions, outcomes and limits


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

The NuGet surface implements the service index and flat-container content reads. It does not implement NuGet search, registration metadata, push or delete protocols. Use package upload and lifecycle routes for publication administration. Configure the application credentials in the consumer's NuGet client; ReadPolicy alone grants no downloads.

[PublishingHttpSampleTests](../tests/IxIFlow.Samples.Tests/PublishingHttpSampleTests.cs) starts the mapped sample in process and checks policy denial, scope masking, preconditions, source and audit. [Activity and package lifecycle](activity-catalog.md#publication-withdrawal-and-retention) explains withdrawal, restoration and retained bytes.
