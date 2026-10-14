---
title: Roadmap
description: The target shape of the IxIFlow engine, what works today, and the remaining work.
---

This page records where the engine is going. Each area states the target, what works today, and what remains. Pages such as [engine capabilities](/docs/engine-capabilities/) describe only current behavior.

## Durable execution core

**Target:** every workflow construct runs through one checkpointed runner that survives restarts, resumes at the saved position, and never hides an uncertain external effect.

**Today:** activities, sequences, conditions, loops, parallel joins, `Try`/`Catch`/`Finally`, sagas, waits, delays, deadlines, retries, child workflows, cancellation, recovery, and activity resolution all run through the structured runner with revisioned checkpoints and leases.

**Remaining:**

- Treat transient store and child-infrastructure errors inside the run loop as recoverable instead of failing the instance.
- Report child-workflow infrastructure failures separately from child business failures, so a parent does not compensate while the child is still running.
- Remove finished parallel continuations and accepted delivery IDs from the checkpoint, so loops containing `Parallel` do not grow it without bound.
- Stop rewriting a parent checkpoint every few seconds while it waits for a child, and prune commit receipts.
- Apply child output mappings on the runner thread, as activity outputs are.
- Park instances during retry backoff instead of waiting in process.

## Coordinator and hosts

**Target:** the coordinator routes a whole workflow execution to a host chosen by health, capacity, current load, tags, and host preferences. Hosts register themselves, report health and load, and accept immediate or queued executions over HTTP or the message bus. A resumed instance prefers the host that ran it. Completion events reach the caller.

**Today:** SQL state with leases and recovery, the SQL message bus with acknowledgement, dead letters and a completion outbox, the SQL host registry, health reporting, and host selection by health, tags, and capacity. A host runs queued execute, resume, and cancel commands addressed to it.

**Remaining:**

- A coordinator API that selects a host and publishes the command to it.
- HTTP host endpoints matching `HttpWorkflowHostClient`, or removal of that client.
- `AddIxIFlowHost` registering its own `HttpClient`.
- Load accounting for queued executions, so selection balances by current load.
- Host affinity when selecting a host for resume.
- A completion consumer or notification API.
- Count crashed claims toward the dead-letter limit, so a message that kills its host is eventually dead-lettered.
- Process queued commands concurrently, so one long workflow does not block a host's queue.
- End-to-end tests that run the coordinator, bus, and hosted queue services together.

## Debugging and observability

**Target:** a developer can follow any instance step by step, set breakpoints, inspect saved state, and correlate engine logs, traces, and metrics.

**Today:** the runner records activity and child-workflow start and end entries, with duration, error, and attempt details, in the instance checkpoint and returns them as `TraceEntries`.

**Remaining:**

- A separate trace store with retention, so history does not grow the checkpoint.
- Workflow start, completion, and failure entries, and activity inputs and outputs in trace entries.
- Calling `IWorkflowTracer` from the runner, or removing it.
- Structured logging in the runner and engine with instance and step scopes.
- OpenTelemetry traces and metrics.
- Instance and snapshot inspection APIs on the engine.
- Breakpoints and step-through execution (`EnableDebugging`).

## Workflow documents

**Target:** JSON as the canonical document form and YAML as an alternate syntax, both compiling to the same definitions as the fluent API, with reusable activities, step templates, and a publish and version flow.

**Today:** `IxIFlow.Authoring` parses YAML documents and compiles activities, activity references, step references, sequences, conditions, suspension, and child workflow invocation.

**Remaining:**

- Parallel, loop, `Try`/`Catch`/`Finally`, saga, and delay steps.
- A JSON schema and JSON documents.
- Compile-equivalence tests between documents and fluent definitions.
- Reusable activity and step template publishing and versioning.
- Controlled expression-backed and code-backed activities behind a policy.

## Management and operations

**Target:** operators can inspect instances, manage dead letters, and start workflows from triggers without writing code against the engine.

**Remaining:**

- A management API for instances, pending activities, and cancellations.
- Dead-letter inspection and requeue.
- Start triggers.

## Storage

**Target:** pluggable durable stores for state, definitions, and events, with more than one database and broker.

**Today:** SQL Server stores instance state, host registrations, and bus messages. Definitions, the version registry, and the event store are in memory on each host.

**Remaining:**

- Persisted definition, version, and event stores.
- PostgreSQL and SQLite adapters.
- Message broker adapters.

## Code health

**Remaining:**

- Split `StructuredWorkflowRunner` into focused components, as `TryScopeTransitions` and `SagaScopeTransitions` already are.
- Remove legacy types replaced by the structured runner, including the unused `IErrorHandler` execution methods and commented-out builders.
- Add instance and step IDs to runner exception messages.
- Replace reflection-based child workflow invocation with typed factories.
- Register core services once, with one lifetime.

## Editor

**Target:** a visual workflow editor and debugger built on the document model. The UI framework will be chosen after the document, validation, publish, and trace APIs are stable.
