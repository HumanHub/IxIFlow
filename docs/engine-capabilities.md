---
title: Engine capabilities
description: Available execution, persistence and integration features.
---

This page summarizes implemented behavior. The [roadmap](roadmap.md) identifies the remaining 1.0 work.

| Area | Available now |
| --- | --- |
| [Authoring](fluent-api.md) | Typed fluent definitions; YAML forms for every fluent construct; shared build rules, source locations, stable step IDs and contract/purity analyzers |
| [Activities](activity-sdk.md) | Direction markers, required inputs, typed defaults, .NET display metadata, DI, saved invocation/attempt IDs and recovery contracts |
| [Control flow](execution-model.md) | Sequences, conditions, loops, typed catch/finally, delays, independent child instances and durable cancellation |
| [Parallel data](execution-model.md#parallel-data-and-joins) | Isolated branch copies, atomic joins, explicit combiners, sequential/parallel ForEach and ordered item results |
| [Sagas](sagas.md) | Reverse and selective compensation, saved handler progress and faults, step and saga retries |
| [Events](execution-model.md#inbox-waits-and-replies) | Durable inbox, event deduplication, activation-specific reply tokens, subjects, deadlines, timeout paths and discoverable waits with suggested payloads |
| [Persistence](stores.md) | Memory, SQLite, SQL Server and PostgreSQL; fenced leases, revision-checked commits and recovery |
| [Hosting](coordinator-and-hosts.md) | In-process host, scoped slots, local timers, recovery scanning, resource limits, crash quarantine and graceful drain |
| [Integrations](outbox-and-publishers.md) | Completion outbox, per-publisher durable deliveries, custom publishers, outgoing signed webhooks and versioned Liquid messages |
| [Operations](retention-and-tombstones.md) | Retained terminal results, bounded cleanup, tombstones and explicit pending-activity resolution |
| [Observability](observability.md) | Separate committed trace history, typed transition/event/operator entries, snapshots, scoped logs and OpenTelemetry signals |

## Deployment boundary

All activity and compensation code currently executes inside the host process. Durable shared state supports recovery by a compatible host; it does not supply workflow artifact deployment or coordinator placement. Definition/version registries and the default template store are in memory.

Expressions and combiners must be pure, and external effects need application idempotency or explicit recovery. The engine cannot make a remote service participate in its checkpoint transaction.

Outgoing webhooks are built. Incoming HTTP triggers, management endpoints, process executors, published workflow bundles and interactive debugger controls remain planned.
