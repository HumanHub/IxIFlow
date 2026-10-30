---
title: Engine capabilities
description: Available execution, persistence and integration features.
---

This page summarizes IxIFlow 1.0. The [roadmap](roadmap.md) describes features after 1.0.

| Area | Available in 1.0 |
| --- | --- |
| [Authoring](fluent-api.md) | Typed fluent definitions; YAML forms for every fluent construct; shared build rules, source locations, stable step IDs and contract/purity analyzers |
| [Activities](activity-sdk.md) | Direction markers, required inputs, typed defaults, .NET display metadata, DI, saved invocation/attempt IDs and recovery contracts |
| [Control flow](execution-model.md) | Sequences, conditions, loops, typed catch/finally, delays, independent child instances and durable cancellation |
| [Parallel data](execution-model.md#parallel-data-and-joins) | Isolated branch copies, atomic joins, explicit combiners, sequential/parallel ForEach and ordered item results |
| [Sagas](sagas.md) | Reverse and selective compensation, saved handler progress and faults, step and saga retries |
| [Events](execution-model.md#inbox-waits-and-replies) | Durable inbox, event deduplication, [activation-specific reply tokens](reply-tokens.md), subjects, deadlines, timeout paths and discoverable waits with suggested payloads |
| [Persistence](stores.md) | Memory, SQLite, SQL Server and PostgreSQL; fenced leases, revision-checked commits and recovery |
| [Hosting](coordinator-and-hosts.md) | In-process host, public executor/host message contract, scoped slots, local timers, recovery scanning, resource limits, crash quarantine and graceful drain |
| [Placement](coordinators.md) | Member registry, automatic/configured capabilities, demands with child inheritance, pins, affinity, unschedulable-work reads, coordinator discovery, secured links and polling fallback |
| [Publishing](yaml-authoring.md#publishing-and-boundaries) | Kind-keyed drafts, code-activity NuGet packages, inline closures, bundled templates, immutable digest pins, verified host loading, lifecycle and retention |
| [Catalog and publishing HTTP](publishing-http.md) | Scoped workflow/activity/package/template/step metadata, validation, publication history, preview, upload, withdrawal, downloads and NuGet content reads |
| [Integrations](outbox-and-publishers.md) | Completion outbox, per-publisher durable deliveries, custom publishers, outgoing signed webhooks, [incoming HTTP triggers](incoming-webhooks.md) and versioned Liquid messages |
| [Operations](operations.md) | Authorized [HTTP management](operations-http.md), instance search, safe wait listings, trace paging, hard termination, retained results, cleanup, tombstones and pending-activity resolution |
| [Observability](observability.md) | Separate committed trace history, typed transition/event/operator entries, snapshots, scoped logs, OpenTelemetry signals and [redaction](redaction.md) with privileged raw diagnostics |

## Deployment boundary

All activity and compensation code executes inside the host process in 1.0. Durable shared state supports recovery by a compatible host, with placement checked on claims that run workflow code. Termination can bypass demands and pins while retaining lease fencing. Code-defined workflows need matching definitions and activity code on eligible hosts. Published workflows use `UseWorkflowBundles` to fetch, verify, cache and load the saved bundle and package digests. Templates use the selected durable catalog; bundled renders use their embedded pinned content. [Regional data residency](coordinator-and-hosts.md#placement-and-data-residency) requires separate clusters and stores.

Expressions and combiners must be pure, and external effects need application idempotency or explicit recovery. The engine cannot make a remote service participate in its checkpoint transaction.

Outgoing webhooks, incoming HTTP triggers, management endpoints and bundle publishing are built. Interactive debugger controls and large-payload storage by reference are available. Process isolation and Native AOT are after 1.0.
