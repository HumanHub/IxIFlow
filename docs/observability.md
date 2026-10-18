---
title: Tracing and observability
description: Committed traces, snapshots, OpenTelemetry and structured logs.
---

IxIFlow exposes durable execution history through its store and runtime telemetry through standard .NET diagnostics. They answer different questions: saved history explains acknowledged progress; logs and spans can also describe work that never committed.

## Committed trace history

Trace entries are appended separately from checkpoints, in the transaction that records their progress. The store assigns increasing sequence numbers. Retrying a commit does not append the entries twice; entries from a pass that never commits are discarded.

History includes workflow/activity lifecycle, control-flow entry and choices, loop iterations, branches and joins, ForEach item boundaries, catch/finally entry, compensation, retries, waits/timeouts/delays, event decisions and operator resolution/cancellation/release. Typed payloads identify steps, scopes, continuations, indexes, invocation IDs and decision reasons.

Transition and operator payloads avoid copying workflow/event values, resolution outputs/notes and exception contents. Activity history can carry errors and attempt information. This is not a redaction system; configure logging/exporters and application data handling accordingly.

`IWorkflowClient.GetTraceAsync(id)` returns committed history across runs. A foreground run result's `TraceEntries` contains the entries collected for that run; a result read back from stored state does not reload history into itself. Repository `GetTraceAsync` pages by sequence. Retention removes history with the instance.

## Detail and bounds

| Setting | Behavior |
| --- | --- |
| `WorkflowOptions.EnableTracing` | Controls saved tracing for that instance |
| `IxIFlowOptions.TraceDetail` | `Full` by default; `Activities` omits control-flow detail while retaining activity, event, operator and cancellation entries |
| `MaxTraceEntriesPerCommit` | 1,000 by default; overflow uses the final slot for a truncation marker counting omissions |
| `MaxTraceEntriesPerInstance` | Optional budget; records a budget marker and stops further control-flow detail while other entry families continue |

The per-commit limit resets after acknowledgement. It bounds pending diagnostics independently of checkpoint size; trace history does not grow the checkpoint. Entries distinguish host and store clocks where applicable, so sequence/commit ordering is the durable ordering.

## Snapshots

`GetSnapshotAsync(id)` derives saved continuation positions, scopes, waits and pending activities from the checkpoint and registered definition. It shows saved state, not a live debugger pause.

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
