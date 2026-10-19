---
title: Roadmap
description: Available foundations, remaining 1.0 work and later features.
---

This roadmap matches the [README status section](../README.md#status-and-roadmap). “Planned” means not yet available. A working subset is identified separately from what remains.

## Available foundations

The in-process host, client, slots, checkpointed runner, fenced leases, inbox, reply tokens, independent children, parallel isolation/joins, ForEach, sagas, resource stops and recovery are implemented. Memory, SQLite, SQL Server and PostgreSQL stores are available.

YAML covers every fluent construct with strict reading and a packaged schema. Activity direction markers, .NET metadata, mapping validation and analyzers are available. Declared workflow names and versions, compiler-free structure hashes and generated/fluent parity tests exist; these are not yet immutable published artifact identities.

Completion outbox publishers and outgoing signed webhooks are implemented, as are retained results, instance cleanup and tombstones. Committed control-flow, event and operator traces live outside checkpoints; snapshots, scoped logging and OpenTelemetry signals are available. Versioned Liquid templates and application-supplied message senders also work.

## Remaining 1.0 work

| Area | Planned work |
| --- | --- |
| Execution and process isolation | Public executor message contract, process slots and entry point, pipe transport, process liveness/resource accounting and fenced process crash reports; commit resend backoff and host message size/metadata validation |
| Coordinator and placement | Cross-host scheduling through stateless coordinators; capabilities and demands; matching and affinity; visible unschedulable work; discovery/registry, multiple-coordinator and loss handling, secured links; batched host lease renewal and process-slot accounting |
| Incoming events and children | Incoming webhook/HTTP trigger package and conformance; child placement demands |
| Workflow publishing/versioning | Immutable bundles, artifact storage/verification, dependency resolution/pinning, version-isolated assemblies, host bundle fetching, bundle digests and compatibility/migration policy; draft/publish and lifecycle APIs |
| Catalog and authoring services | Broader catalog API, code-defined workflows in the catalog, published code activities, inline workflow calls, explicit workflow input/output contracts, YAML-defined code activities and local registration beyond installed-assembly metadata |
| Runtime validation | Native AOT publish/run verification; broader conformance and fault injection as coordinator/process paths land |
| Editor and debugger | Visual editor, breakpoints, all-stop pause, stepping through loops, paused timer rules, audited data edits, Debug Adapter Protocol and inspection/replay tooling |
| Management and operations | Management API/operator listings, unused bundle retention, large-payload storage, residency policies and redaction |

The outgoing `IxIFlow.Webhooks` publisher does not provide incoming trigger endpoints. Instance retention is complete; bundle retention depends on the publishing work. The current in-memory version registry supports registration, exact name/version lookup and deactivation for installed definitions; the durable publishing lifecycle remains planned.

## After 1.0

Broker publisher/consumer samples for RabbitMQ or Azure Service Bus and a distributed map over paged external input are later work. The distributed map is intended to run bounded children and store results outside the parent's checkpoint.

SQLite single-host durability has already been implemented in the 1.0 scope. Ordinary parallel branches and ForEach are available now; neither requires that future distributed map.

See [engine capabilities](engine-capabilities.md) for today's feature summary and [hosting](coordinator-and-hosts.md) for the current runtime boundary.
