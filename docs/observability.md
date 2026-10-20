---
title: Tracing and observability
description: Committed traces, snapshots, OpenTelemetry and structured logs.
---

IxIFlow exposes durable execution history through its store and runtime telemetry through standard .NET diagnostics. They answer different questions: saved history explains acknowledged progress; logs and spans can also describe work that never committed.

## Committed trace history

Trace entries are appended separately from checkpoints, in the transaction that records their progress. The store assigns increasing sequence numbers. Retrying a commit does not append the entries twice; entries from a pass that never commits are discarded.

History includes workflow/activity lifecycle, control-flow entry and choices, loop iterations, branches and joins, ForEach item boundaries, catch/finally entry, compensation, retries, waits/timeouts/delays, event decisions and operator resolution/cancellation/release. Typed `TracePayload` records identify steps, scopes, continuations, indexes, invocation IDs and decision reasons; JSON uses a `Type` discriminator. Timeout, event and child-cancellation payloads use `WaitRef`, not raw wait IDs or reply tokens. `WaitReference.Of(instanceId, waitId)` is the first 16 lowercase hex characters of SHA-256 over UTF-8 `instanceId + ":" + waitId`.

Transition and operator payloads avoid copying workflow/event values, resolution outputs/notes and exception contents. Typed activity notes use the [redaction projection](redaction.md); string and operator notes are wholly sensitive. Errors expose safe types/codes and diagnostic IDs. Spans omit messages and exception events; ordinary engine logs omit exception objects. Raw diagnostics require an application-authorized read, and full exception logging requires `IxIFlowOptions.EnableSensitiveDiagnostics`; category filters alone cannot enable emission. Raw records live beside the trace, inserted atomically with the recording commit and never rewritten by subsequent commits. Their [bounded retention](redaction.md#privileged-raw-diagnostics) preserves operator-authored records as well as pending failures and the terminal failure, with only one recent failure record for completed attempts; trace identities may refer to expired raw records. Instance deletion and retention purge remove these rows.

`IWorkflowClient.GetTraceAsync(id)` returns committed history across runs. A foreground run result's `TraceEntries` contains the entries collected for that run; a result read back from stored state does not reload history into itself. Repository `GetTraceAsync` pages by sequence. Retention removes history with the instance.

## Detail and bounds

| Setting | Behavior |
| --- | --- |
| `WorkflowOptions.EnableTracing` | Controls saved tracing for that instance |
| `IxIFlowOptions.TraceDetail` | `Full` by default; `Activities` omits control-flow entries, including wait timeouts, delays, retries, compensation and catch/finally entry, while retaining activity, event, operator and cancellation entries |
| `MaxTraceEntriesPerCommit` | 1,000 by default; overflow uses the final slot for a truncation marker counting omissions |
| `MaxTraceEntriesPerInstance` | Optional budget; records a budget marker and stops further control-flow detail while other entry families continue |

The per-commit limit resets after acknowledgement. It bounds pending diagnostics independently of checkpoint size; trace history does not grow the checkpoint. These budgets apply to executor-buffered entries; a store-side event refusal is appended in the inbox transaction and is not counted against those budgets. An untargeted event sent after completion can therefore add another refusal trace for each call. Entries distinguish host and store clocks where applicable, so sequence/commit ordering is the durable ordering.

## Snapshots

`GetSnapshotAsync(id)` derives each live continuation's saved position and scope frames from the checkpoint and registered definition. Its `Data` contains workflow data projected through redaction. It lists neither waits nor pending activities; use `GetOpenWaitsAsync` and `GetPendingActivitiesAsync` for those. Open-wait default payloads use the same projection. The snapshot shows saved state, not a live debugger pause.

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

## Planned debugger work

Breakpoints, all-stop pause, stepping, paused timer policies, audited data edits, Debug Adapter Protocol and replay remain planned. Committed transition traces, event/operator entries, snapshots, logging and OpenTelemetry are already implemented. See the [roadmap](roadmap.md).
