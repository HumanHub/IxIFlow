---
title: Status and roadmap
description: Shipped IxIFlow behavior, known limits, and planned work.
---

IxIFlow is under active development. The fluent C# API is the current way to define workflows. The engine has automated tests for local activities, parallel waits, exception handling, recovery, and sagas. The remaining work is concentrated in distributed hosting, authoring parity, and operations.

## Available for evaluation

| Area | Current state |
| --- | --- |
| Fluent C# definitions | Implemented and covered by syntax and execution tests. |
| Local activity execution | Implemented in the workflow host process. |
| Conditions, parallel work, and exceptions | Implemented, including multiple simultaneous waits, `WaitAll`, `WaitAny`, conditional joins, and waits in `Catch` and `Finally`. Loop failure propagation and nested `PreviousStep` pass-through have regression tests. |
| In-process saga compensation | Waits and error handlers resume with saved compensation results. Retry after a wait has success and exhaustion coverage. |
| SQL host and state | Shared SQL Server instance state, revisioned checkpoints, renewable execution leases, recovery scanning, host registry, and acknowledged message delivery with claim renewal are implemented. Cross-host resume, live-owner exclusion, expired-worker takeover, and stable start IDs have integration tests. |
| Workflow editor exercise | Structured, collapsible activity frames with inline fields, editable titles and annotations, manifest-driven icons, and draft YAML editing. It does not execute workflows. The earlier Vue Flow canvas remains a Flowchart reference. |
| Custom activity prototype | A .NET package registry matches manifest activity keys to installed `IAsyncActivity` types. The YAML document compiler checks package and activity versions. Studio can load a local manifest into its toolbox. |

## Known gaps

- The default state repository and event store are singleton in-process memory. The optional SQL Server host registers shared workflow state, but its event store and definition registry remain in memory.
- Local execution uses saved continuations and scope frames for initial execution, waits, and recovery. Crash recovery can leave a nonrecoverable activity at `NeedsResolution`; a recoverable activity supplies its own verification policy. More fault-injection coverage is needed for host termination during nested cancellation and external effects.
- Parallel branches share mutable workflow data. Developers must synchronize conflicting updates; the engine does not merge branch-local copies.
- Memory and SQL state repositories fence stale checkpoint writers with a revision and renewable execution lease. The optional SQL host scans running instances and requests recovery; an activity with an uncertain external outcome stops at `NeedsResolution` for operator action.
- Saga error handlers can wait and resume. Cancellation behavior has targeted tests, but a complete matrix across nested loops, handlers, sagas, and parallel joins is still a release gate.
- Typed resume events and correlation exist, but there is no published start-trigger registry for HTTP, schedules, or messages. General event matching still scans suspended instances. Targeted event-template updates now resume only their specified instance, and the default memory template store survives DI scopes.
- SQL message claims renew during long handlers. Execute commands carry a stable instance ID, so redelivery reuses the saved instance. Completion events can still be delivered more than once; consumers need to deduplicate by instance ID.
- The distributed cancellation command currently publishes a cancellation event without changing engine state. It must be connected to a durable cancellation request before it can be offered as an operational control.
- Each host must register the same code-defined workflow version and activity code. The SQL host does not persist executable C# definitions or distribute activity packages.
- Named child-workflow invocation resolves the registered definition and version. Queued commands carry a name and version, but the HTTP host client still sends unserializable workflow definitions.
- The document compiler preserves conditionals, bindings, and a small Boolean expression set. A basic YAML workflow can compile and execute with a registered custom activity. The document model still lacks several fluent constructs, and Studio's draft YAML shape is not the engine's canonical document. A runtime-connected Studio is not built.
- Execution traces and checkpoint snapshots exist, but there is no debugger API for breakpoints, step controls, watch values, or a live Studio execution view.

The current regression suite passes with an isolated SQL Server test database. Without `IXIFLOW_TEST_SQL_CONNECTION_STRING`, SQL integration tests are skipped.

## Roadmap to a first-class engine

The engine must remain useful as an in-process library. `AddIxIFlow()` uses memory state and events by default. SQL, a coordinator, brokers, HTTP APIs, and Studio are optional layers. They must not change the meaning of a workflow. In-process correctness does not depend on a database.

| Stage | Work | Exit gate |
| --- | --- | --- |
| 0. Lock down behavior | Turn the existing critical and high-priority regressions into an execution contract. Add a compact matrix for each control-flow feature on fresh execution, suspend and resume, and failure recovery using the memory provider. Define step identity, scope, parallel data, compensation, and version rules. In a separate test track, start a disposable Docker database for the existing SQL reliability tests. | The intended behavior is documented, known failing tests are accounted for, and the local tests require no database. SQL tests run on an isolated container rather than being skipped. |
| 1. Reliable local execution | Drive initial execution and continuation from the same persisted pointers and nested frames. Fix loop, branch, `Try`/`Catch`/`Finally`, saga, and child-workflow scope. Preserve the compensation stack across suspension. | All local control-flow and saga regression tests pass, including multiple suspensions in nested constructs. In-process execution still works without a database or coordinator. |
| 2. Durable instances | Extend the existing suspended-instance claim with versioned checkpoints, recoverable worker leases, wakeup indexes, immutable published definitions, and explicit retry/idempotency behavior. Keep memory as the default; bring the existing SQL Server provider under that contract, then add optional embedded SQLite and shared PostgreSQL providers. | An instance resumes after process restart with a durable provider, concurrent resume attempts have one owner, and a failed worker can be recovered without losing its position or compensation history. |
| 3. Complete authoring | Design the YAML schema and visual editor model together. Make the document model cover supported fluent control flow, including loops, parallel work, exception handling, sagas, variables, waits, and invocation. Add validation and YAML parsing/printing over that model. Use stable identifiers and preserve published versions. | Representative fluent and YAML definitions compile to equivalent executable graphs; unsupported code-only features are identified clearly. The visual editor reads and writes the same YAML definition without silently changing behavior. |
| 4. Distributed execution | Finish serializable work commands, durable dispatch, acknowledgement and retry, host health, leases, and recovery. Connect the coordinator to the durable instance store. Add a broker adapter after the transport contract works; RabbitMQ is the first cross-platform candidate, with Azure Service Bus as an optional adapter. | A suspended saga can resume on another machine; host loss, duplicate delivery, and broker redelivery pass integration tests. |
| 5. Management API and Studio | Expose definitions, versions, instances, waits, traces, failures, and controlled retry/resume through an optional ASP.NET Core API. Build a Studio UI for both operations and visual authoring. Prototype its authoring experience during stage 3; complete publishing after validation and versioning are ready. | Operators can see where an instance is, why it stopped, and what action is safe. A designed workflow validates and publishes through the same compiler as YAML. |
| 6. Release hardening | Run the conformance matrix on Windows and Linux, exercise real provider and broker integration tests with disposable Docker containers, add fault injection and performance baselines, and publish versioning and upgrade guidance. | All critical and high-priority tests pass; examples and docs match shipped behavior; provider and recovery guarantees are explicit. |

### Work that can run in parallel

- **Core execution** is the critical path through stages 0 to 2.
- **Authoring and Studio design** can design the canonical document, YAML grammar, and editor interactions during stage 1, then verify semantic parity as the runtime contract settles.
- **Coordinator** can develop command envelopes, host registration, routing, health, and acknowledgement contracts during stage 1. Cross-machine resume integration depends on stage 2's versioned checkpoints and recoverable instance lease.
- **Studio implementation** can prototype instance inspection and API contracts during stages 1 and 2. Visual editing and YAML serialization must share stage 3's schema and validation.

### One authoring model

The fluent API, YAML, and visual editor should produce the same versioned executable definition graph. YAML is the portable source format for document-authored workflows. The editor edits that document and stores optional layout information separately from execution meaning; it must not invent a private workflow format. The current JSON document model is only a starting point. Arbitrary C# lambdas in fluent definitions cannot be reconstructed as editable YAML or visual expressions, so the editor must show those as code-backed activities with a stable reference.

The designer target is closer to the Windows Workflow Foundation editing experience than a generic node canvas: searchable activity toolbox, nested sequence and control-flow containers, editable conditions, typed variables and arguments, input/output mapping, activity properties, inline validation, and a view of the active node and execution history. Add `Try`/`Catch`/`Finally`, saga compensation, waits, and child workflows as first-class visual constructs. Assess flowcharts, state machines, and event races separately against runtime semantics before promising them in the editor.

Debugger support belongs at the execution boundary, independent of Studio. Give every executable node a stable ID and emit ordered lifecycle events with its instance, branch, scope, input and output summaries, and failure details. A local debug controller should pause before or after a node and support continue, step into, step over, and step out. Studio can display those events and controls through an adapter. Durable and distributed debugging needs a persisted pause reason and lease-aware ownership; arbitrary activity code can use the normal .NET debugger in the host process. A workflow-level step command cannot step through lines inside a user activity.

Use one expression contract for YAML and Studio. For a .NET audience, C# expressions for conditions and mappings are a strong candidate, with compiler diagnostics shown in the editor. Custom code activities should be versioned .NET artifacts available on every executing host. If inline C# editing is added, compilation and artifact distribution must be part of publishing; a text field containing code is not enough for durable or distributed execution.

Checkpointing does not make arbitrary external effects exactly once. Activities that call outside systems need idempotency keys or another explicit recovery policy. Broker acknowledgement must follow durable state changes, not precede execution.

Docker is available for repeatable integration runs. Use isolated PostgreSQL and SQL Server containers for state and existing SQL transport tests, then RabbitMQ and the Azure Service Bus emulator when those adapters exist. The host-recovery suite should kill a worker between an external effect, checkpoint, and message acknowledgement, then assert the documented retry behavior.

### Immediate engine gates

1. Complete the cancellation and fault-injection matrix across nested scopes and external effects.
2. Add operator controls for `NeedsResolution` and an indexed recovery query for large SQL installations.
3. Connect distributed cancellation to engine state, then test delayed delivery and worker termination across hosts, including duplicate completion events and recovery of expired running instances.
4. Make the YAML compiler and editor cover the same supported constructs as the fluent API, then add debugger and management APIs.

This is a dependency order, not a release date. Check the repository's tests and changes before depending on a specific capability.
