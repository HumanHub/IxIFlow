# Standalone activity authoring

An `*.activity.yaml` file defines one asynchronous activity with its own key and integer version:

<!-- snippet: samples/IxIFlow.Samples/Definitions/double.activity.yaml -->
```yaml
activity:
  key: acme.double
  version: 1
  name: Double
input:
  Value: { type: int, default: 3 }
output:
  Result: { type: int }
body: |
  Result = Value * 2;
```

Read and validate an activity with `WorkflowYamlSerializer.ReadActivity(source)` and
`ActivityDocumentValidator.Validate(document, source)`. JSON editor representations use
`WorkflowJsonSerializer.ReadActivity` and `Write`. `DocumentModel.GetLocation(document, path)`
locates diagnostics such as `input.Value.default`. A supplied `default: null` is distinct
from an omitted default. Body and declarations use YAML literal blocks.

`ActivityCompilation.Compile(source)` returns assembly bytes, portable PDB bytes and
positioned diagnostics. It compiles deterministic Release C#, preserves the authored
source verbatim in the `ixiflow/activity.yaml` assembly resource, and embeds that source
and `activity.g.cs` in the PDB. The generated class implements `IAsyncActivity`; its body
can use `context`, `cancellationToken` and `await`. Optional `declarations` add fields,
constructors, helper methods or nested types.

For a scope implementation, set `activity.scope` to its C# value type. The generated
class implements `IScopeActivity<TValue>`: `body` becomes `OpenAsync`, can use
`opening`, `context` (`IScopeContext`), and `cancellationToken`, and returns the value.
Supply `CloseAsync(TValue value, ScopeClosing closing, IScopeContext context,
CancellationToken cancellationToken)` in `declarations`. The compiler reports a
missing or incompatible close implementation. Public input/output properties are
saved configuration and first-open outputs; the live value belongs in the return
value, never on a public property.

Optional `activity.concurrency: serialized | shared` and
`activity.threading: anyThread | threadAffine` emit the scope attributes. Omitting
them uses serialized/anyThread. Ordinary activities cannot declare these modes
(ACTD017). `activity.requiresScopes` lists enclosing C# value types and emits
`RequiresScope<T>` on either activity kind. Publishing inspects both kinds through
the same descriptor validation and package pipeline without executing lifecycle code.

Compilation uses the bundled, pinned .NET 10 framework reference assemblies and the
shared SDK profile, without requiring an installed SDK on the publishing host. Framework
implementations are used when loading the activity for inspection. Supply prepared
package reference assembly paths and host-declared shared contract assemblies through
`ActivityCompilationOptions`; the compiler does not resolve package imports. Compiling
does not instantiate the activity or execute its body or default converters.

`ActivityPackageIdentity` derives the package and assembly identity and C# type name:
`acme.send-email` version `2` becomes package `acme.send-email` version `2.0.0`, assembly
version `2.0.0.0`, and class `IxIFlow.Activities.Acme.SendEmail`.

The IxIFlow package ships `schemas/activity.schema.json` and exposes its installed path
as the `IxIFlowActivitySchema` MSBuild property. Activity files are excluded from automatic
workflow generation. Explicitly selecting one as an `IxIFlowWorkflow` reports IXIYAML002.

# Publishing workflows and packages

`builder.AddPublishing(options)` registers one `WorkflowPublisher`, `PublicationService` and the hosted `PackageIndexingService` against the builder's catalog and artifacts. Pass an init-only `WorkflowPublisherOptions` instance. Roslyn, NuGet and the Templates API are dependencies of Authoring; template engine services are optional until a template operation. Add `builder.AddIxIFlowTemplates().AddLiquid()` for template publication, validation and preview; absent engines report TPL012.

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#workflow-publication -->
```csharp
var draft = await catalog.SaveDefinitionAsync(
    new WorkflowDraft("Example", 1, WorkflowDraftKinds.Workflow, yaml), expected: null, actor: "author");

var report = await publisher.ValidateAsync(draft, cancellationToken);
var result = await publisher.PublishAsync(draft.Id, actor: "publisher", cancellationToken: cancellationToken);

if (result.Success)
{
    WorkflowVersion workflow = result.Workflow!;
    WorkflowPublicationReceipt receipt = result.Receipt!;
}
```

The typed result has `Kind`, `Workflow`, `Package`, `Template`, diagnostics and a computed receipt. Exactly the matching arm and no errors constitute success. `FailureClass` is independent of the diagnostic code. `WorkflowPublishResult.FromReceipt` decodes the audited stored representation and verifies the full kind/name/version/generation/revision identity. Source is canonical pinned YAML; compilation reparses that exact text before generating C# and symbols.

Activity publication uses the same `PublishAsync` API with an activity draft and returns `result.Package`. The key is its package ID; integer version `n` becomes package `n.0.0`. Test loads the generated class in a collectible context, validates identity and its portable descriptor/defaults, and unloads without constructing the activity or calling its body. Attribute constructors and default converters can execute trusted code under `BuildTimeout`. Invalid defaults/members report `PUB021`; an occupied package identity reports `PUB022` before compilation. Exact full-draft retries return the original audited receipt.

The deterministic nupkg contains ordinal ZIP paths with 1980 timestamps, a nuspec, `lib/net10.0/{id}.dll` and `.pdb`, verbatim authored `ixiflow/activity.yaml`, and `ixiflow/activity.json` with the whole draft ID, compiler/engine/shared profile and digest-pinned dependency closure. NuGet reads back its identity and content before storage. Neutral dependencies and actual referenced SDK packages become exact nuspec dependencies; platform-only reachability stays in `runtime.json`. SDK package dependencies remain explicit when the publishing host also supplies those DLLs through a shared framework. NuGet consumers use NuGet's dependency override/hash semantics; IxIFlow resolution enforces the activity metadata's digest pins and bundles retain their own verified pins.

Set `WorkflowPublisherOptions.SharedContractAssemblies` for publishing and call `builder.ShareContracts(contractAssemblies)` on every consuming host. Supply file-backed default-context assemblies whose non-SDK dependencies are also declared contracts. Each publisher/host freezes its own snapshot on first use; changes after host profile use are refused. Required assembly versions are recorded and incompatible hosts reject the bundle. Contracts are supplied by the application, allowing constructor injection and `context.Services` to use the host's interface identity. Application implementation assemblies are not implicit activity compilation references or privately bundled contract substitutes. Standalone `ActivityCompilationOptions` still accepts explicitly prepared references; it does not create a hosting profile.

YAML workflows consume the package through `imports.catalogs` and its activity key/version. A fluent application restores the nupkg, registers `builder.AddActivities(typeof(GeneratedActivity).Assembly)`, and uses `Step<GeneratedActivity>()`. Verified sibling package PDBs load in both publishing scratch contexts and bundle contexts; absent symbols are valid. Embedded source and .NET exception lines refer to authored activity YAML; workflow debugger stepping still treats the activity body as one step. The [publishing HTTP guide](../../docs/publishing-http.md) documents the authorized NuGet V3 read subset. [PublishedActivities](../../samples/IxIFlow.Samples/PublishedActivities.cs) includes tested YAML and fluent consumers.

`ValidateAsync` accepts stored or unsaved drafts. It runs feed-backed Scratch resolution, document validation, translation, compilation and Build/Test. It performs no artifact puts (including duplicate-put age refreshes), catalog registrations, draft, receipt or attempt writes. Validation can execute trusted package/author code. Cancellation or timeout stops waiting; arbitrary .NET code may continue on an abandoned background thread until process restart.

An `*.template.yaml` has a `template` header with required `key`, positive integer `version`, `engine: liquid`, `format: text` or `html`, and a full C# `model` expression. The root has required `content`, optional `cultures` text keyed by culture, and `imports.catalogs` for model packages. Workflow imports report TPL008. Read it with `WorkflowYamlSerializer.ReadTemplate` or `WorkflowJsonSerializer.ReadTemplate`; pure rules are `TemplateDocumentValidator.Validate`. The shipped `schemas/template.schema.json` is exposed by the `IxIFlowTemplateSchema` MSBuild property. Literal-block Liquid diagnostics map to content lines; folded blocks use their scalar start.

The same publisher binds the complete model expression with Roslyn, including closed generic arguments, then validates every variant against the runtime type in a collectible scratch context under `BuildTimeout`. Supported roots include visible closed classes, records, structs, public nested types and typed abstract/interface read surfaces. Scalars, object/dynamic, arrays, collections, dictionaries, open generics and inaccessible roots are refused with TPL005. Publication returns `result.Template`, records every model/type-argument package and its transitive closure, and atomically consumes the full draft identity. TPL007 identifies an occupied template identity; exact retries return the first audited receipt.

`PreviewTemplateAsync(draft, new TemplatePreviewRequest(culture, modelJson), token)` performs the full Scratch validation, deserializes JSON into the declared model type and renders in that same scope. It persists no draft, attempt, artifact or template. Model construction errors report TPL021; model and UTF-8 output limits report TPL018. Both limits default to 1 MiB and are configured with `MaxPreviewModelBytes`/`MaxPreviewOutputBytes`; Liquid execution limits and `BuildTimeout` also apply. Preview can execute trusted constructors, converters, setters and getters; cancellation/timeout stops waiting and can abandon in-process work. Abstract/interface previews require actual JSON construction support. [MapWorkflowPublishing](../IxIFlow.AspNetCore/README.md) exposes this operation with the separate required `WorkflowPublishingOptions.PreviewPolicy`; validation uses ExecutePolicy.

For durable work, call `PublicationService.PublishAsync(draft.Id, wait, actor, requestToken)`. The request token cancels waiting only. The service owns work and heartbeats independently; `wait` is bounded to 0..1 day. A Running return is suitable for a later HTTP 202 response. Retrieve the exact attempt using `GetPublicationAsync(id, scope)`; history and stale abandonment remain store operations. Each service has a process-local owner ID. Heartbeat/stale defaults are 10/90 seconds and stale-after must exceed heartbeat. A failed persistence acknowledgement leaves the receipt recoverable by the next owner. Graceful service stop cancels cooperative work; an interrupted attempt becomes eligible for stale recovery. There is no client cancellation API.

`ReadPackageAsync` verifies bounded archive bytes into `PackageUploadCandidate`. `StorePackageAsync(candidate, actor, token)` revalidates the candidate, prepares with the root seeded offline, and returns `PackageRegistration(Stored, Activities, Created)`. `UploadPackageAsync` combines these calls. Use `Stored` for the first winning audit, normalized identity and monotonic origin. Missing dependencies may be accepted Pending; the hosted indexing service revisits them after dependencies arrive and after restart. Immutable-byte conflicts use InvalidState; malformed archives use InvalidRequest. Verified restoration bypasses new-authoring resolution of the withdrawn root and reuses its recorded extraction/closure; the package store owns the restore transaction.

The AspNetCore package references Authoring. `builder.AddWorkflowPublishing(webOptions, publisherOptions)` adds HTTP upload orchestration. `WorkflowPackageUploadService.UploadAsync(http, catalogPath)` reads/verifies, applies the required application `ICatalogQueryScope`, authorizes the proposed normalized package under PublishPolicy and only then stores. Actor delegation requires ActForPolicy. The path is the mapped group's catalog path; the service includes PathBase when returning the stored package Location. Map the registered resource model with `app.MapWorkflowPublishing("/publishing")`; see the AspNetCore README for routes, policies and conditional requests.

HTTP admission defaults to two concurrent uploads and covers reading, expansion, verification, resolver preparation and storage. Excess admission returns 429 with Retry-After 5. Content-Length and chunked bodies share the library archive limit; expanded bytes, individual entries, entry count, nuspec and closure budgets also apply. Size host memory for multiple compressed copies during verification/store plus expanded entries and the selected resolution closure per admitted upload; MaxArchiveBytes alone is not the memory budget. Set PackageReadLimits, MaxPackageResolutionBytes and MaxConcurrentUploads together for the authoring host.

Unexpected errors have fixed public text and are logged on the server (publication logs carry the attempt ID). Generic conflicts, artifacts, bounds and timeouts use PUB040/041/042/043; PUB009 through PUB013 retain their callee/inline meanings. The AspNetCore route group supplies policy/scope validation, preconditions, scoped outcome projections and verified downloads. The group also exposes template preview, template/step catalog reads, activity/template sources, package withdrawal/holders and the NuGet read subset; see the [HTTP guide](../../docs/publishing-http.md).

[PublishedArtifacts](../../samples/IxIFlow.Samples/PublishedArtifacts.cs) publishes templates with `renderTemplate` and both `use` and `useType`. Typed callees must be registered as code-defined catalog versions before the caller is published. The resulting caller carries verified inline executable payloads and template bindings, and cold execution does not resolve packages or live template versions.

HTTP NuGet feed resolutions share a process-wide credential gate for the full resolution. This isolates NuGet credential state between callers; HTTP resolutions are serialized even when upload admission allows several requests. Folder-feed resolutions do not take this gate.
