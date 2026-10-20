# IxIFlow

IxIFlow is a durable workflow engine for .NET 10 and C#. Define activities and control flow with a typed fluent API or supported YAML documents, then run instances through `IWorkflowClient`. A durable store lets instances survive process restarts, wait for external answers and resume from their saved position.

This repository is being built toward 1.0. Features described below are implemented. The [status and roadmap](#status-and-roadmap) separates the remaining 1.0 work from what you can use today.

## Features

- Typed activity inputs and outputs, previous-step values, enforced activity contracts and .NET designer metadata.
- Sequences, conditions, `WhileDo`/`DoWhile`, sequential and parallel `ForEach`, concurrency caps and ordered item results.
- Parallel branches with all, any or conditional joins. Branches work on isolated data copies; joins merge independent changes or use explicit combiners for shared properties.
- `Try`/`Catch`/`Finally`, typed saved fault data, sagas with reverse compensation, selective compensation and retry policies.
- Durable delays, correlated events, request/reply tokens, subjects, deadlines, timeout paths and discoverable open waits.
- Child workflows with separate persisted instances and mapped inputs and outputs; durable cancellation with compensation and `Finally` unwinding.
- Saved activity attempts with invocation and attempt IDs, retry history, errors and notes. Recovery contracts and operator resolution handle uncertain external outcomes.
- Revision-checked checkpoints, fenced execution leases, resource limits and crash quarantine. External effects still need application idempotency or recovery contracts.
- An injected workflow clock, expression purity validation, compile-time contract and purity analyzers, stable step IDs and source locations.
- YAML source generation for every fluent construct, with local references, defaults, expression assets, strict reading and structure-hash/result equivalence tests.
- In-memory, SQLite, SQL Server and PostgreSQL stores; DI, a generic host, recovery scanning and graceful drain.
- Durable completion outbox, per-publisher delivery and outgoing signed webhooks; retained terminal results, bounded cleanup and tombstones.
- Committed trace history outside checkpoints, snapshots, structured logging scopes, and an `ActivitySource` and `Meter` both named `IxIFlow` for OpenTelemetry integration.
- Immutable, versioned Liquid templates with culture fallback, typed model validation, rendering and message sending through an application-supplied sender.

See the [sample feature map](samples/README.md) for runnable examples and the [YAML guide](docs/yaml-authoring.md) for every document construct.

## Packages

These are the package projects in `src/`. All runtime packages target .NET 10.

| Package | Purpose |
| --- | --- |
| `IxIFlow` | Engine, fluent builders, client, host, memory store; bundles the YAML generator, analyzers and code fixes |
| `IxIFlow.Abstractions` | Activity interfaces, recovery contracts, contract/metadata attributes and combiners |
| `IxIFlow.Authoring` | Document serialization and validation, translation support and installed activity package catalog |
| `IxIFlow.Sqlite` | SQLite file store for one host with in-process execution |
| `IxIFlow.SqlServer` | SQL Server instance state, inbox, waits, traces, leases and completion outbox |
| `IxIFlow.PostgreSql` | PostgreSQL instance state and `LISTEN`/`NOTIFY` wake-ups |
| `IxIFlow.Relational` | Shared internal storage implementation; installed as a provider dependency, with no public extension API |
| `IxIFlow.Webhooks` | Outgoing signed webhook delivery of outbox events |
| `IxIFlow.Templates` | Versioned template store, publisher, renderer and message activities |
| `IxIFlow.Templates.Liquid` | Liquid engine using Fluid |

`IxIFlow.Generators`, `IxIFlow.Analyzers` and `IxIFlow.Analyzers.CodeFixes` are build-time projects targeting `netstandard2.0`, bundled in `IxIFlow`; they are not separate packable projects.

## Installation

Use a .NET 10 project. Add the engine package, then the provider and template packages you need:

```sh
dotnet add package IxIFlow
dotnet add package IxIFlow.Sqlite
dotnet add package IxIFlow.Templates.Liquid
```

Activity libraries can reference only `IxIFlow.Abstractions`. Add `IxIFlow.Authoring` when the application needs document serialization or the installed activity catalog.

## Quick start

The complete activity and data types are in [Quote.cs](samples/IxIFlow.Samples/Quote.cs); copy them into your application or run the sample project. `AddFee` declares a required customer input, an in-out amount and a typed decimal default of 2.50 for its fee. The same amount is mapped into and out of the activity:

<!-- snippet: tests/IxIFlow.Samples.Tests/ReadmeExamples.cs -->
```csharp
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddIxIFlow();
await using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IWorkflowClient>();

var definition = Workflow.Create<QuoteData>("QuickStart")
    .Step<AddFee>(step => step
        .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
        .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount))
    .Build();
WorkflowDefinitionValidator.Validate(definition);

var result = await client.RunAsync(definition, new QuoteData());
if (result.Status != WorkflowExecutionStatus.Success)
    throw new InvalidOperationException(result.ErrorMessage);
Console.WriteLine(((QuoteData)result.WorkflowData!).Amount); // 12.50
```

This snippet is compiled and exercised in [ReadmeExamples.cs](tests/IxIFlow.Samples.Tests/ReadmeExamples.cs). Parameterless activities can be constructed by the executor; register activities with constructor dependencies in DI.

`RunAsync` returns when the instance is idle, including at a wait or when operator resolution is needed. Check `Status` and read the returned `WorkflowData`. `StartAsync` normally returns an instance ID after saving the start; inside a host slot it runs inline until the nested run ends. `AwaitAsync` waits for idle or terminal state. A caller's cancellation token can interrupt the current run while leaving saved state recoverable; `CancelAsync` requests durable workflow cancellation. For a queued `StartAsync`, the host owns the run and the token only stops waiting for acceptance.

## YAML authoring

The engine package includes `*.workflow.yaml` and `*.workflow.yml` as compiler additional files automatically. This delay example is [reminder.workflow.yaml](samples/IxIFlow.Samples/Definitions/reminder.workflow.yaml), compiled in the sample project and compared with its C# twin:

<!-- snippet: samples/IxIFlow.Samples/Definitions/reminder.workflow.yaml -->
```yaml
# Park for an hour, then stamp the reminder with the workflow clock.
workflow:
  name: Reminder
  dataType: IxIFlow.Samples.ReminderData
code:
  now:
    language: csharp-expression
    source: ctx.UtcNow
definitions:
  - kind: delay
    duration: '01:00:00'
  - kind: activity
    activity: samples.stamp-time
    input:
      - target: At
        from: { kind: function, function: now }
    output:
      - source: At
        to: { path: workflow.SentAt }
```

The generator emits a static `Build()` method under `<RootNamespace>.Generated`; this file becomes `Workflow_reminder_workflow.Build()`. Activity keys resolve from `[WorkflowActivity]` metadata; fully qualified CLR types also work. YAML and fluent definitions go through the same builder rules. Paths name CLR members, such as `workflow.Amount` and `previousStep.Receipt`.

YAML covers every fluent control-flow construct: activities and references, sequences, conditions, `WhileDo`/`DoWhile`, general parallel branches and combiners, sequential or parallel `forEach` with results, typed catch/finally blocks, sagas with outcome branches, compensation and retry, delays, requests with replies and timeouts, event waits and both named and typed child invocations. Waits can include `defaultPayload` as a discovery UI suggestion; the engine never answers it automatically. Local templates accept parameters and defaults; expression assets use `language: csharp-expression`. YAML number literals with a fraction are doubles; decimal expressions can use a C# expression asset, while activity attribute defaults retain their declared types.

Hosting, store selection, telemetry, template publication and client operations use application C# APIs. Any nonempty catalog or workflow import list fails the source-generator build with `IXIYAML001` at `imports`; the generator does not resolve imports. Named `invokeWorkflow` steps require an explicit `version`, because the generator registers only `name@version` references. See the [generator guide](src/IxIFlow.Generators/README.md) for custom filenames, MSBuild settings and project-reference setup, and [samples](samples/README.md) for tested pairs.

The reader is strict: an unknown or wrongly cased key fails the build with IXIYAML001 at that key. The package ships a JSON Schema for workflow YAML at `schemas/workflow.schema.json`, so editors complete and check documents as you type; [editor support](src/IxIFlow.Generators/README.md#editor-support) shows how to bind it. The [YAML guide](docs/yaml-authoring.md) documents every step kind, mapping and expression, including scope rules and planned publishing features.

## Stores

A [reply token](docs/reply-tokens.md) is a capability: holding it is the right to answer its wait. Share it only with the intended replier and keep it out of logs.

Choose one store through the builder returned by `AddIxIFlow()`. All provider extensions use `IxIFlow.Extensions`; install the corresponding package.

| Store | Registration | Use |
| --- | --- | --- |
| Memory | `services.AddIxIFlow()` | Default; retains instances across scopes in one process, loses them on restart |
| SQLite | `services.AddIxIFlow().UseSqlite(connectionString)` | Durable file, WAL, one owning host; no multiple-host or coordinator support |
| SQL Server | `services.AddIxIFlow().UseSqlServer(connectionString)` | Shared durable state, inbox, waits, traces, leases, outbox and tombstones |
| PostgreSQL | `services.AddIxIFlow().UsePostgreSql(connectionString)` | Shared durable state with the same contract and notification wake-ups alongside polling |

[Hosting.cs](samples/IxIFlow.Samples/Hosting.cs) compiles all four registrations. Provider schemas are created on first use. SQLite requires a file connection string and exclusive ownership; asynchronously dispose its service provider before reopening the file. SQL Server and PostgreSQL use database time for deadlines; memory and SQLite use the registered `TimeProvider`.

Before 1.0, any store schema change requires a fresh database. Schema creation supplies whole tables and their indexes; it does not upgrade existing tables or repair their missing indexes. Recreate databases from an earlier schema build. SQL Server IDs use `Latin1_General_100_BIN2` to preserve case distinctions; see [stores](docs/stores.md) for the affected columns.

Workflow versions and draft definitions are stored in the selected repository. Code-defined executable definitions are registered locally on each host that may run them. Published versions are fetched by their immutable bundle digest when bundle hosting is enabled. Workflow identity is the declared `(name, version)`; names are required. A compiler-free `StructureHash` checks checkpoint compatibility and refuses changed structure at registration, start, resume, recovery and child resolution. It includes ordered steps, types, waits, joins, combiners, loops, handlers, children and expression shapes; it ignores delegate bodies, captured values and presentation metadata. Change the version when changing behavior. See [identity and expressions](docs/fluent-api.md#identity-and-expressions). Checkpoint schema 4 is a one-time break: older saved instances are refused without migration; reset development state and restart hosts together.

Enable published workflows by calling `UseWorkflowBundles(cacheDirectory, maxCachedBundles: 16)` on the builder returned by `AddIxIFlow()`. The selected repository's artifact table is used by default; `UseFileSystemArtifacts` can replace it. Start a published version with `client.StartAsync("Approval", version: 1, workflowData: data)`.

A configured bundle host advertises `bundle-host` and compatible catalog workflow/version structure hashes. Capabilities refresh on heartbeats and local catalog changes. An unmet workflow demand also triggers a refresh, at most once per heartbeat interval; a failed refresh leaves the demand unmet and hands execution off. A published start saves both demands and its `BundleDigest`; recovery and child instances use their own saved pins. Downloads and cached archives are verified before loading, and shared assembly identities/versions must match the manifest's compatibility profile. The runtime set comes from framework deps files in `APP_CONTEXT_DEPS_FILES`, or only `runtimepack.*` libraries in the application's deps file for self-contained deployments, including ASP.NET. Application assemblies remain private even beside runtime DLLs or under a directory named `shared`. Unreadable runtime metadata disables bundle compatibility without failing host startup. The profile declares only runtime/SDK assemblies referenced by the workflow and its private closure, plus CoreLib and the runtime facade. Private dependencies come only from verified archive entries. Activity-package imports remain unsupported.

Each digest has its own collectible load context. `maxCachedBundles` bounds the loaded-context cache; active operations, queued starts and activity tasks retain leases until they finish. Set it to zero to release a context after its last operation. Loaded contexts are reused without rereading disk. Cache files contain whole verified archives and are written atomically under a per-digest process lock, so hosts can share a directory; archive disk usage is managed separately from the loaded-context bound. The cache directory must be trusted: grant write access only to the hosts that use it. A corrupt cache entry, including an oversized file with the wrong digest, is rejected before loading with `ArtifactMismatch` and removed; a later operation can fetch it again. Cache write failures still allow verified downloads to run. Missing/download failures use `ArtifactUnavailable`. Both interrupt execution and keep the committed checkpoint.

The `UseWorkflowBundles(WorkflowBundleOptions)` overload configures `FetchTimeout` and `CacheLockTimeout` (each 30 seconds by default) and `ReadLimits`: maximum archive bytes (64 MiB), manifest bytes (1 MiB), entry count (1,024, including the manifest), bytes per other entry (32 MiB), and total expanded bytes (256 MiB). Reads enforce these bounds while streaming and before expanding entries. Caller cancellation propagates as cancellation. Cache-lock waiting has its own cancellable deadline; on expiry the host bypasses the cache and downloads verified bytes. Only downloading and verification consume the fetch budget. Fetch timeouts and independent artifact-store cancellation are `ArtifactUnavailable` interruptions and preserve the checkpoint. Archive limits report `ResourceExhausted` and leave digest-valid cached archives intact. An inspection GET that hits a bundle read limit returns HTTP 503 naming the bundle limit.

Unloading is cooperative: a static field, event handler, delegate, `Type`, or returned data object holding a bundle type keeps that context loaded. Runtime serializer caches can also delay collection briefly. Engine reflection caches use weak ownership and serializer profiles are scoped to their load context. Saved data/event/value type names now use the recursive `ixi-type:1:` format; recreate earlier development databases and republish earlier bundles rather than migrating their type names.

All four stores implement inbox, trace, outbox and retention semantics; memory records last only for the process lifetime. See [stores](docs/stores.md) for deployment details.

## Publishers and retention

A terminal checkpoint atomically writes an immutable `OutboxEvent.WorkflowCompleted` envelope. Register singleton `IOutboxPublisher` implementations with `AddOutboxPublisher`; each stable publisher name has its own leased, at-least-once delivery state. New names receive future events unless `ReplayRetainedOutboxEvents(name)` is requested. Explicitly retire durable subscriptions with `UnregisterOutboxPublisher(name)`. Consumers deduplicate by event ID.

`IxIFlow.Webhooks` adds outgoing HTTP delivery through `AddWebhook(WebhookEndpoint)`, with structured CloudEvents JSON and Standard Webhooks v1 HMAC signatures. It retries independently per endpoint; HTTP 410 disables an endpoint durably. The completion summary excludes workflow data and exception text. Incoming HTTP triggers remain planned. See [outbox and publishers](docs/outbox-and-publishers.md).

Terminal results remain readable for `FinishedInstanceRetention` (30 days by default). Hosted cleanup runs bounded passes every hour by default, skipping pending outbox deliveries and nonterminal children. Removal deletes operational history and reserves the ID for `TombstoneRetention` (90 days from removal by default). During that period client reads distinguish `Removed`, and writes reject the reserved ID. See [retention and tombstones](docs/retention-and-tombstones.md).

## Hosting

`AddIxIFlow()` registers the in-process host, recovery scanner, outbox dispatcher and retention cleanup as hosted services. Use the generic host for background recovery and graceful shutdown:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#hosted-run -->
```csharp
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddIxIFlow(options => options.Slots = 8);
using var host = builder.Build();
await host.StartAsync();
try
{
    var client = host.Services.GetRequiredService<IWorkflowClient>();
    var result = await client.RunAsync(Quote.Definition(), new QuoteData());
    Console.WriteLine(result.Status);
}
finally
{
    await host.StopAsync();
}
```

This host lifecycle is compiled in [DocumentationExamples.cs](samples/IxIFlow.Samples/DocumentationExamples.cs) and exercised by [DocumentationTests.cs](tests/IxIFlow.Samples.Tests/DocumentationTests.cs). `Slots` limits host work slots; nested calls can run inline in their parent's slot. Due work is recovered through local timers and store polling. Long retry backoffs park the instance and release the slot. Shutdown drains work before its configured timeout. This is the current in-process host; process executors and the coordinator are future work.

For time-dependent mappings and conditions, use `ctx.UtcNow`. Tests can register a `FakeTimeProvider`, as in [the sample tests](tests/IxIFlow.Samples.Tests/SampleTests.cs). Attach OpenTelemetry to source/meter `IxIFlow`, or use the standard .NET listeners shown in [Observability.cs](samples/IxIFlow.Samples/Observability.cs).

## Status and roadmap

The following work is planned for 1.0 and is **not yet available**, or has an implemented subset with the remaining work stated here:

| Area | Remaining 1.0 work |
| --- | --- |
| Execution and isolation | Public executor message contract; local-first process slots, pipe transport, process liveness and resource limits, fenced process crash reports; commit resend backoff and host validation of message size/metadata |
| Coordinator and placement | Cross-host scheduling through stateless coordinators, workflow demands, matching and affinity, visible unschedulable work, coordinator discovery, multiple coordinators, loss handling and secured links; batched host lease renewal and process-slot activity accounting. The store-backed member registry, heartbeats and automatic/configured host capabilities are implemented |
| Events and integrations | Incoming webhook/HTTP trigger package and conformance; child placement demands. Durable inboxes, reply tokens, child instances, suggested wait payloads, retained results, public outbox publishers and outgoing signed webhooks are implemented |
| Publishing and versioning | Activity-package resolution/loading and HTTP publishing APIs. Immutable bundles, artifact stores, draft publishing, version lifecycle, verified host loading, per-version isolation, digest pins and shared compatibility checks are implemented |
| Authoring and catalog | Broader catalog API, code-defined workflows in the catalog, published code activities, inline calls, explicit workflow input/output contracts, YAML code activities and planned local registration support beyond the current installed-assembly registry; Native AOT publish/run validation. Full YAML construct coverage is implemented |
| Editor support | The JSON Schema for workflow YAML ships in the package. A visual editor and debugger integration remain planned |
| Debugger and audit | Breakpoints, all-stop pause, stepping including loops, audited data edits, paused timer behavior, Debug Adapter Protocol and replay. Separate committed transition/event/operator traces, snapshots, scoped logs and OpenTelemetry already work; transitions that never commit do not enter saved history |
| Management and operations | Unused bundle retention, large-payload storage, residency policies and redaction. [HTTP management endpoints](docs/operations-http.md), [instance search, wait inspection, trace paging, hard termination and outbox administration](docs/operations.md), reply capabilities, instance retention, tombstones and outbox age/delivery telemetry are implemented |
| Verification | Complete cross-configuration conformance and fault injection for the coordinator, process executors and nested joins as those paths land |

After 1.0: broker publisher/consumer samples for RabbitMQ or Azure Service Bus, and a distributed map that pages external input, runs bounded child work and stores results outside the parent checkpoint. These are outside the 1.0 scope. SQLite single-host durability has moved into 1.0 and is implemented.

## Documentation

Start with [getting started](docs/getting-started.md) or [samples and their feature map](samples/README.md). The [docs index](docs/index.md) links every guide, including the [fluent API](docs/fluent-api.md), [YAML](docs/yaml-authoring.md), [execution model](docs/execution-model.md), [activity SDK](docs/activity-sdk.md), [hosting](docs/coordinator-and-hosts.md), [publishers](docs/outbox-and-publishers.md) and [roadmap](docs/roadmap.md).

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [MIT license](LICENSE).

Workflow classes declare identity once with `[Workflow("Name", Version = n)]` in `IxIFlow.Core`. `RegisterWorkflow<TWorkflow,TData>()` and `Invoke<TWorkflow,TData>(setup)` read it from the type without constructing the class to discover identity; missing attributes are refused. Typed YAML children need only `workflowType`, with no duplicate name/version field.
