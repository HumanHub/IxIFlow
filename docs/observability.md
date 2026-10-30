---
title: Tracing and observability
description: Committed traces, snapshots, OpenTelemetry and structured logs.
---

IxIFlow exposes durable execution history through its store and runtime telemetry through standard .NET diagnostics. They answer different questions: saved history explains acknowledged progress; logs and spans can also describe work that never committed.

## Committed trace history

Trace entries are appended separately from checkpoints, in the transaction that records their progress. The store assigns increasing sequence numbers. Retrying a commit does not append the entries twice; entries from a pass that never commits are discarded.

History includes workflow/activity lifecycle, control-flow entry and choices, loop iterations, branches and joins, ForEach item boundaries, catch/finally entry, compensation, retries, waits/timeouts/delays, event decisions and operator resolution/cancellation/release. Typed `TracePayload` records identify steps, scopes, continuations, indexes, invocation IDs and decision reasons; JSON uses a `Type` discriminator. Timeout, event and child-cancellation payloads use `WaitRef`, not raw wait IDs or reply tokens. `WaitReference.Of(instanceId, waitId)` is the first 16 lowercase hex characters of SHA-256 over UTF-8 `instanceId + ":" + waitId`.

Transition and operator payloads avoid copying workflow/event values, resolution outputs/notes and exception contents. Activity history can carry errors and attempt information. Execution-facing results and raw store reads need application authorization. Inspection projects values and free text through the [redaction policy](redaction.md); configure logging and exporters for the data they receive.

`IWorkflowClient.GetTraceAsync(id)` returns committed history across runs. A foreground run result's `TraceEntries` contains the entries collected for that run; a result read back from stored state does not reload history into itself. Repository `GetTraceAsync` pages by sequence. Retention removes history with the instance.

## Detail and bounds

| Setting | Behavior |
| --- | --- |
| `WorkflowOptions.EnableTracing` | Controls saved tracing for that instance |
| `IxIFlowOptions.TraceDetail` | `Full` by default; `Activities` omits control-flow entries, including wait timeouts, delays, retries, compensation and catch/finally entry, while retaining activity, event, operator and cancellation entries |
| `MaxTraceEntriesPerCommit` | 1,000 by default; overflow uses the final slot for a truncation marker counting omissions |
| `MaxTraceEntriesPerInstance` | Optional budget; records a budget marker and stops further control-flow detail while other entry families continue |

The per-commit limit resets after acknowledgement. It bounds pending diagnostics independently of checkpoint size; trace history does not grow the checkpoint. Mandatory data-edit audit entries bypass tracing switches and capacity limits. These budgets apply to ordinary executor-buffered entries; a store-side event refusal is appended in the inbox transaction and is not counted against those budgets. An untargeted event sent after completion can therefore add another refusal trace for each call. Entries distinguish host and store clocks where applicable, so sequence/commit ordering is the durable ordering.

## Snapshots

`GetSnapshotAsync(id)` derives each live continuation's saved position and scope frames from the checkpoint and registered definition. It lists neither waits nor pending activities; use `GetOpenWaitsAsync` and `GetPendingActivitiesAsync` for those. It includes projected data and errors and describes saved state. Use inspection and debugger operations for pause information.

[Observability.cs](../samples/IxIFlow.Samples/Observability.cs) compiles this inspection method with `IxIFlow.Core` imported:

<!-- snippet: samples/IxIFlow.Samples/Observability.cs -->
```csharp
public static async Task<(IReadOnlyList<ExecutionTraceEntry> Trace, WorkflowExecutionSnapshot? Snapshot)>
    InspectAsync(IWorkflowClient client, string instanceId) =>
    (await client.GetTraceAsync(instanceId), await client.GetSnapshotAsync(instanceId));
```

The same sample attaches standard .NET activity and meter listeners; [SampleTests.cs](../tests/IxIFlow.Samples.Tests/SampleTests.cs) exercises the signals and inspection.

## OpenTelemetry

The engine's `ActivitySource` and `Meter` are both named `IxIFlow`. Add that source and meter to the application's OpenTelemetry SDK configuration and choose its exporters. The engine does not require an OpenTelemetry exporter to run.

Spans cover run passes, activity attempts and checkpoint commits. They include workflow/instance identity and, where applicable, step, invocation and attempt identity. Metrics cover active runs, activity duration, commit duration/size, leases lost, backlog age, outbox age/delivery outcomes/settlement failures, and retention skipped for never-attempted deliveries.

Metric tags use workflow name/version and relevant bounded dimensions such as activity type, outcome and publisher name; they avoid a series per instance. [Telemetry.cs](../src/IxIFlow/Core/Runtime/Telemetry.cs) defines the exact instruments.

## Structured logging and actors

Run scopes include `InstanceId` and `WorkflowName`. Activity/child attempt scopes add `StepId` and `InvocationId`; loggers resolved through activity DI participate in the same scopes. Publisher delivery scopes add publisher name, event ID and attempt number.

Operator calls record the supplied actor, falling back to `IxIFlowOptions.Actor`. Cancellation uses `CancellationReason.RequestedBy`; resolution uses `decidedBy`; release accepts an actor. These strings are audit metadata. The application decides who is authorized to make the call.

## Debugging and Switch selection

Breakpoints, all-stop pause, stepping, audited data edits, the Debug Adapter Protocol, inspection and replay are described in [debugging](debugging.md) and [the debug adapter](debug-adapter.md). They build on the committed transition traces, event and operator entries, snapshots, redaction, logging and OpenTelemetry described above.

A body switch records one `StepEntered` pick and one value-free `SwitchSelected` selection (`Case` with an index, `Default`, or `NoMatch`, and the selected scope when present). Selection facts contain no label or selector result. Replay stops at `StepEntered`, folds saved positions and never evaluates the selector. Inspection names the active arm with a `SwitchCase` frame. Into visits the first real child pick; Over stops after the switch; an empty or unmatched body reaches the following pick. `WorkflowDefinition.SwitchTable` and `IWorkflowOperations.GetSwitchTableAsync` provide detached selector-type and canonical case/default descriptors beside the step catalog.

## Invocation attempt correlation

Invocation `ActivityStarted`, `ActivityCompleted` and `ActivityFailed` entries carry typed `InvocationTrace` facts: logical invocation ID, policy-attempt ID/number, nullable started child ID and terminal child status. Preparation failures have no started child. `InvocationPolicyApplied` records the selected policy/action and retry, ignore, exhaustion or propagation disposition. `StepRetryScheduled` includes the same invocation facts and absolute due time. `ActivityResolutionTrace.PolicyAttemptId` is the attempt actually decided by the operator.

These facts contain no mapped values, wait capabilities or exception text. Errors use diagnostic references. Tracing can be disabled or truncated independently of the invocation records needed for recovery. Inspection exposes those retained records; completed unreferenced occurrences are removed from checkpoints, so their historical visibility depends on the trace.

## Assignment picks

Assign is a real executable pick. A breakpoint before it sees old data; stepping to the next pick sees the assigned data. Full tracing records `StepEntered` and a `DataAssigned` completion with the structural destination and step identity. Computed keys, values and expression text are omitted. Activities-only tracing can omit this control-flow completion. Replay stops at `StepEntered` and never evaluates either expression. The step catalog includes the destination and declared value type; ordinary data inspection supplies redacted values.

## Scope lifecycle

`ScopeOpened`, `ScopeClosed` and `ScopeCloseFailed` record the scope step, continuation and activation identity, whether an open is a re-open, the recorded-open count, and close reason/cause. Their payloads carry identifiers and reasons rather than resource values or configuration. A lost pass may later be recorded as interrupted. Re-open and close are not extra successful commit boundaries. Correlate these events with [inspection](inspection-and-replay.md#scope-activities) to see saved, redacted configuration and modes.

See [inspection and replay](inspection-and-replay.md) for saved positions, durable invocation records and trace navigation, and [debugging](debugging.md) for breakpoints, stepping and audited edits.
