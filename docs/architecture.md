---
title: Architecture
description: The authoring, host, executor and storage boundaries.
---

IxIFlow executes workflow instances inside an application through `IWorkflowClient`. Both fluent C# and generated YAML produce a `WorkflowDefinition` and use the same validation and runtime.

## Runtime boundaries

The client submits work to the core `WorkflowHost`. The host admits runs into slots, creates service scopes, owns instance gates and execution leases, and commits changes through `IWorkflowStateRepository`. The executor advances saved continuations and invokes activities in the application process.

The instance row is the durable backlog: `RunnableAtUtc` identifies due work. Local pickup and timers provide prompt execution; recovery polling finds due unowned work. PostgreSQL notifications also wake the scanner. This path needs no separate command bus.

One store supplies instance state, checkpoint parts, inboxes, wait indexes, traces, leases, commit receipts, completion outbox deliveries and tombstones. A checkpoint transaction can settle an event, close a wait, start a child and record progress together. Outgoing publishers run outside execution slots and settle their deliveries separately.

Definition and version registries are in memory. Hosts must load compatible definitions and activity assemblies before recovering their instances. Runtime fingerprints detect incompatible definitions; immutable published bundles and their declared artifact identity are planned.

## Packages

| Package | Responsibility |
| --- | --- |
| `IxIFlow.Abstractions` | Activities, request/recovery contracts, property markers, metadata and combiners |
| `IxIFlow` | Builders, validation, client, core host, executor, memory store, telemetry; packaged generator and analyzers |
| `IxIFlow.Authoring` | Document validation/serialization, translation support and installed activity catalogs |
| `IxIFlow.Sqlite` | A durable SQLite file owned by one host |
| `IxIFlow.SqlServer` | Shared SQL Server storage |
| `IxIFlow.PostgreSql` | Shared PostgreSQL storage and notification wake-ups |
| `IxIFlow.Relational` | Internal shared provider implementation; no public provider extension API |
| `IxIFlow.Webhooks` | Outgoing signed HTTP delivery of outbox events |
| `IxIFlow.Templates` | Immutable template versions, rendering and message activities |
| `IxIFlow.Templates.Liquid` | Liquid template rendering through Fluid |

The generator and analyzers are build-time components bundled in the engine package. The schema ships with that package. See [YAML authoring](yaml-authoring.md) for the compiler pipeline.

## Hosting and planned distribution

The supported executor runs in process. Shared SQL Server or PostgreSQL state allows another compatible host to recover work using fenced leases. SQLite enforces one owner; memory state lives for the process.

Process executors, pipe transport, coordinator discovery, capability/demand placement and affinity are planned.

See [hosting](coordinator-and-hosts.md), [execution model](execution-model.md) and the [roadmap](roadmap.md).
