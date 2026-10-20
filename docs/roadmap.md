---
title: Roadmap
description: Available foundations, remaining 1.0 work and later features.
---

This roadmap matches the [README status section](../README.md#status-and-roadmap). “Planned” means not yet available. A working subset is identified separately from what remains.

## Available foundations

The in-process host, client, slots, checkpointed runner, public executor/host message contract, fenced leases, inbox, reply tokens, independent children, parallel isolation/joins, ForEach, sagas, resource stops and recovery are implemented. Memory, SQLite, SQL Server and PostgreSQL stores are available. Hosts register with store-clock heartbeats and publish automatic and configured capabilities.

YAML covers every fluent construct with strict reading and a packaged schema. Activity direction markers, .NET metadata, mapping validation and analyzers are available. Declared workflow names and versions, compiler-free structure hashes and generated/fluent parity tests exist.

The persistent catalog stores drafts and immutable version metadata, supports latest-published selection and guarded retirement, and registers code-defined workflows. Import-free YAML drafts can be published in process as immutable bundles identified by archive digest, with manifest and entry verification. Artifact stores for memory, SQLite, SQL Server, PostgreSQL and the file system, plus bounded retention of unreferenced artifacts, are implemented. Executable definitions still have to be loaded on each host; host bundle loading remains below.

[Placement](coordinator-and-hosts.md) combines definition, registration and start demands, explicit inheritance to children, expiring pins, matching and last-host affinity. [Coordinators](coordinators.md) supply stateless matching, discovery, multiple links, authenticated TLS and polling fallback. Unschedulable work is visible through client/operator reads, telemetry and `GET /unschedulable`. Demands place compute; [full data residency](coordinator-and-hosts.md#placement-and-data-residency) requires a separate cluster and store per region.

Completion outbox publishers, outgoing signed webhooks and [incoming HTTP triggers](incoming-webhooks.md) with atomic idempotent admission are implemented, as are retained results, instance cleanup and tombstones. Committed control-flow, event and operator traces live outside checkpoints; snapshots, scoped logging and OpenTelemetry signals are available. [Redaction](redaction.md) protects inspection values and error text, with privileged reads for raw diagnostics. Versioned Liquid templates and application-supplied message senders also work.

Instance search, safe operator wait listings, trace paging, hard termination and outbox administration are available through [IWorkflowOperations](operations.md) and the authorized [HTTP operations mapping](operations-http.md). Termination can stop an instance even when no live host satisfies its demands or pin. [Reply tokens](reply-tokens.md) are capabilities for answering one wait; HTTP trace and snapshot reads require both inspect and reply authorization.

Conformance covers memory, SQLite and distributed SQL Server/PostgreSQL, including zero, one and two coordinators. The existing fault categories cover lost acknowledgements, stale owners, a host dying after a wake, coordinator/poll races, event timing, nested-join recovery, parent/child cancellation and repeated host crashes through quarantine.

## Remaining 1.0 work

| Area | Planned work |
| --- | --- |
| Execution and scheduling | Commit resend backoff, batched host lease renewal and completion of demand-aware local-first start/resume admission; execution-claim skip-locked policy remains open |
| Workflow publishing/versioning | Activity-package dependency resolution and digest pinning, template dependencies, isolated bundle assemblies and type resolution per version, host fetching/caching and verification at load, shared-assembly compatibility checks at load; draft, upload and publishing HTTP APIs |
| Catalog and authoring services | Unified workflow/activity catalog API with input/output metadata, remaining placement and inspection parity for code-defined workflows, published activity packages and YAML code activities, inline workflow calls, explicit workflow input/output contracts and local activity registration |
| Debugger and audit | Breakpoints and their commits, all-stop pause, stepping through loops, audited data edits, Debug Adapter Protocol and inspection/replay tooling. Timers will keep running while paused; edits will record actor, time, revision and paths |
| Large payloads | Opt-in storage by reference for workflow data, events and child inputs/results, with transactional reference tracking and collection; bodies stay in the state store |
| Verification | Identity-test traceability and conformance, fault-injection and commit-budget coverage for the remaining publishing, debugger and payload paths |

The catalog and publishing library APIs are available; their broader HTTP services and automatic deployment to hosts remain planned. There is no checkpoint migration before 1.0. The debugger and large-payload work are not yet available.

## After 1.0

Process isolation is after 1.0: process slots, application-built executor programs, pipe transport, liveness, hard timeouts, process resource accounting, fenced crash reports and the process-isolation conformance axis. Process memory limits belong to the deployment. Native AOT support for compiled-in workflows, including trimming and publish/run verification, is also after 1.0.

Broker publisher/consumer samples for RabbitMQ or Azure Service Bus and a distributed map over paged external input are later work. The distributed map is intended to run bounded children and store results outside the parent's checkpoint.

SQLite single-host durability has already been implemented in the 1.0 scope. Ordinary parallel branches and ForEach are available now; neither requires that future distributed map.

See [engine capabilities](engine-capabilities.md) for today's feature summary and [hosting](coordinator-and-hosts.md) for the current runtime boundary.
