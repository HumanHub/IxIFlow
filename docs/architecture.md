---
title: Architecture
description: The authoring, host, executor and storage boundaries.
---

IxIFlow executes workflow instances inside an application through `IWorkflowClient`. Both fluent C# and generated YAML produce a `WorkflowDefinition` and use the same validation and runtime.

## Runtime boundaries

The client submits work to the core `WorkflowHost`. The host admits runs into slots, creates service scopes, owns instance gates and execution leases, and commits changes through `IWorkflowStateRepository`. The public `WorkflowExecutor` advances saved continuations and invokes activities in the application process through a public message contract with JSON serialization support; the host owns store access, results and runnable timers.

The instance row is the durable backlog: `RunnableAtUtc` identifies due work. Local pickup and timers provide prompt execution; recovery polling finds due unowned work. PostgreSQL notifications also wake the scanner. This path needs no separate command bus.

One store supplies instance state, checkpoint parts, inboxes, wait indexes, traces, leases, commit receipts, completion outbox deliveries and tombstones. A checkpoint transaction can settle an event, close a wait, start a child and record progress together. Outgoing publishers run outside execution slots and settle their deliveries separately.

`IWorkflowCatalog` persists drafts and version metadata in the selected store, while code-defined executable definitions remain local to each host. Hosts must load compatible code definitions and activity assemblies before recovering their instances. Declared `(name, version)` identifies a workflow; names are required. A compiler-free structure hash refuses incompatible checkpoints and duplicate registrations with changed structure. It excludes delegate bodies, captured values and presentation metadata. Schema 4 refuses older saved instances without migration. YAML publishing creates verified immutable bundles in digest-addressed artifact storage; hosts configured with `UseWorkflowBundles` fetch, verify and load a published version by its saved bundle digest, each digest in its own collectible load context.

## Packages

| Package | Responsibility |
| --- | --- |
| `IxIFlow.Abstractions` | Activities, request/recovery contracts, property markers, metadata and combiners |
| `IxIFlow` | Builders, validation, client, core host, executor, memory store, telemetry; packaged generator and analyzers |
| `IxIFlow.Authoring` | Document validation/serialization, YAML translation and publishing, and installed activity catalogs |
| `IxIFlow.Sqlite` | A durable SQLite file owned by one host |
| `IxIFlow.SqlServer` | Shared SQL Server storage |
| `IxIFlow.PostgreSql` | Shared PostgreSQL storage and notification wake-ups |
| `IxIFlow.Relational` | Internal shared provider implementation; no public provider extension API |
| `IxIFlow.Coordinator` | Stateless matching, host discovery and authenticated coordinator links |
| `IxIFlow.AspNetCore` | Authorized incoming HTTP triggers, CloudEvents and management endpoints |
| `IxIFlow.Webhooks` | Outgoing signed HTTP delivery of outbox events |
| `IxIFlow.Templates` | Immutable template versions, rendering and message activities |
| `IxIFlow.Templates.Liquid` | Liquid template rendering through Fluid |

The generator and analyzers are build-time components bundled in the engine package. The schema ships with that package. See [YAML authoring](yaml-authoring.md) for the compiler pipeline.

## Hosting and distribution

The supported executor runs in process. Shared SQL Server or PostgreSQL state allows another compatible host to recover work using fenced leases. SQLite enforces one owner; memory state lives for the process.

Coordinator discovery, authenticated links, capability/demand placement, expiring pins, last-host affinity and unschedulable-work inspection are implemented. Coordinators improve wake latency; fenced store claims enforce placement and polling provides fallback. Demands place compute only; [full data residency](coordinator-and-hosts.md#placement-and-data-residency) needs a separate cluster and store per region.

1.0 runs workflows only in the host process. Process isolation and pipe transport are after 1.0, as is Native AOT support.

See [hosting](coordinator-and-hosts.md), [execution model](execution-model.md) and the [roadmap](roadmap.md).
