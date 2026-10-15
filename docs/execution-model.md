---
title: Execution model
description: How IxIFlow definitions, instances, steps, and hosts work.
---

IxIFlow has a definition, an execution engine, and an instance record. A definition contains ordered steps and the workflow data type. `IWorkflowClient.RunAsync` runs that definition with a data object and returns a `WorkflowExecutionResult`.

## Normal execution

1. Your application builds or registers a workflow definition.
2. The engine creates an instance and executes its steps.
3. An activity runs in the current host process through `IActivityExecutor`.
4. Input mappings populate activity properties. Output mappings copy values back to workflow data.
5. The engine returns a status, data, and trace entries.

Conditions and parallel branches are represented as workflow steps with nested child steps. A saga step contains a sequence of activities and compensation information. The engine can also suspend an instance and resume it from an event.

## State and resume

The default `AddIxIFlow()` registration uses singleton `InMemoryWorkflowStateRepository` and `InMemoryEventStore`. They retain data across dependency injection scopes within one process. They are not shared or durable state stores: a process restart or another host loses access to those instances.

`WorkflowOptions.PersistState = false` runs without saving an instance. Use it for workflows that finish in one execution call. The engine rejects waits, delays, and child workflow invocation in this mode because each can leave work that needs a saved parent position.

At suspension, the engine saves workflow data, active continuations and scope frames, and each waiting step. Resume matches an event to one saved wait and advances that continuation. A loop finishes the interrupted iteration and checks its condition again. A saga restores completed activity results for compensation. Memory and SQL repositories use revisioned checkpoints and renewable execution leases so another host can recover a stopped worker after its lease expires. An interrupted ordinary activity may still need manual resolution; external effects are not automatically exactly once. See [sagas](/docs/sagas/) for the practical effect.

`Try/Catch` uses saved frames for active nested handlers. A failure before a `Try` is not caught by that later block. Waits inside `Catch` and `Finally` resume without rerunning completed handler steps. Custom exception data is exposed through a declared durable fault projection.

Parallel branches have separate saved continuations and waits. They share mutable workflow data, so activities must synchronize conflicting updates. `WaitAll`, `WaitAny`, and conditional joins determine when the parent can continue. Completed branches do not rerun when another branch resumes.

## Host boundary

A host runs a whole workflow execution through its local engine when it receives a queued command addressed to it. `WorkflowCoordinator` selects a host using health, tags, and capacity; your application publishes the command. Individual saga steps are not distributed across workers.

The SQL host overload registers a SQL workflow state repository, host registry, and message bus. Queued commands use name and version references, and SQL deliveries require acknowledgement. The definition registry is still local memory; each host must register the same definition and activity code. SQL state uses revisioned checkpoints and renewable execution leases. See [coordinator and hosts](/docs/coordinator-and-hosts/).

## Checkpoints

The scheduler saves workflow data, active branches, loop and exception frames, waits, saga progress, and a checkpoint revision. The SQL host scans for interrupted running instances. An uncertain external activity outcome stops for an explicit resolution decision; inspect pending attempts and resolve them through `IWorkflowClient` before continuing.

A checkpoint is saved at effect boundaries: when the instance starts, before and after each activity or child workflow, when a wait is saved or a scheduled retry is set, when a branch is cancelled, and when the run stops. Control flow between them, such as `If`, loops and scope changes, is not saved; after an interruption it is evaluated again from the last checkpoint. Conditions, mappings, wait predicates and parallel completion conditions may therefore run more than once. Keep them pure: read the workflow data and the previous step, perform no I/O, and read time only through the injected `TimeProvider`.

A checkpoint larger than `IxIFlowOptions.MaxCheckpointBytes` (4 MiB of workflow data and execution state by default) is never saved. The instance stops at `NeedsResolution` with its last checkpoint and the limit as its error; raise the limit and call `ReleaseAsync`, or cancel it. A start, event or resolution that would exceed the limit is refused with a `WorkflowEngineException` of kind `ResourceExhausted`.

An owner that loses a running instance, because its process dies or its lease expires, counts one crash for the instance. Any saved progress resets the count; starting an activity or child workflow does not. When `IxIFlowOptions.MaxCrashes` owners in a row (3 by default) have lost the instance, the next host to claim it does not run it: the instance is quarantined at `NeedsResolution` with its last checkpoint, and no host claims it again. `RecoverAsync`, including a parent checking its child, leaves a stopped instance stopped. Call `ReleaseAsync` to run it again with a fresh count, or cancel it. A graceful shutdown releases its leases and never counts.

Two more limits keep one instance from taking over a host. `IxIFlowOptions.MaxActiveBranches` (1,000 by default) caps the live continuations of an instance: the root, every parallel branch, and every parent waiting at its join; branches that are being cancelled do not count. A parallel step that would go over it does not fork: the run starts nothing new, lets its running activities finish and saves them, then stops at `NeedsResolution` like a checkpoint over the size limit. Raise the limit and call `ReleaseAsync`. `IxIFlowOptions.MaxActivitiesPerRun` (100 by default) caps the activities one run has in flight; further activities wait for one to finish while other branches keep advancing. Branches that wait on each other's activities need room to run together.

The SQL store keeps a separate connection pool for leases and cancellation (16 connections by default, the `ownershipPoolSize` argument of `SqlWorkflowStateRepository`), so a host whose runs exhaust the main pool still renews its leases and delivers cancellation. A lease that cannot be renewed before it may have expired is given up at that moment, not when the store call finally returns. Enable `READ_COMMITTED_SNAPSHOT` on the database so that reads of cancellation requests do not wait behind commits of other instances.

## Interrupted activities

The runner saves an attempt before it invokes an activity and saves the observed result afterward. A process can stop after an external effect but before the result is saved. On recovery, the engine must decide whether to run that activity again.

An activity that implements `IRecoverableActivity<TState>` makes that decision itself. `CaptureRecoveryState` runs before the first invocation and must not perform an external effect; its value is saved with the attempt. After an interruption, `RecoverAsync` receives the saved value and returns one of these results:

| Result | Effect |
| --- | --- |
| `Completed` | The effect happened. Restore the activity's output properties before returning. |
| `Execute` | The effect did not happen. The engine runs the activity again. |
| `Unresolved(reason)` | The outcome cannot be established. The instance stops at `NeedsResolution`. |
| `Faulted(error)` | The effect failed. The error follows the normal `Catch` and saga rules. |

```csharp
public sealed class ChargeCardActivity(IPaymentLedger ledger) : IRecoverableActivity<string>
{
    public string OrderId { get; set; } = "";
    public string? ChargeId { get; private set; }

    public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;

    public async Task<ActivityRecoveryResult<string>> RecoverAsync(
        string invocationId, IActivityContext context, CancellationToken cancellationToken = default)
    {
        ChargeId = await ledger.FindChargeAsync(invocationId, cancellationToken);
        return ChargeId != null
            ? new ActivityRecoveryResult<string>.Completed()
            : new ActivityRecoveryResult<string>.Execute();
    }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        ChargeId = await ledger.ChargeAsync(OrderId, context.GetRecoveryState<string>(), cancellationToken);
}
```

`IPaymentLedger` is an application service. Passing the saved invocation ID as an idempotency key lets the external system recognize a repeated request.

An activity without this contract, or one that returns `Unresolved`, stops at `NeedsResolution`. An operator lists the pending attempts and records a decision. A completed decision supplies the activity's output properties; a failed decision supplies an error. The decision is saved before execution continues.

```csharp
var pending = await client.GetPendingActivitiesAsync(instanceId);
var attempt = pending.Single();

await client.ResolveAsync(instanceId, attempt.InvocationId,
    ActivityResolution.Completed(
        new Dictionary<string, object?> { ["ChargeId"] = "ch_123" },
        note: "Confirmed in the payment dashboard",
        decidedBy: "ops@example.com"));
```

Saga compensation uses the same rule. A forward step whose outcome is unknown stops for resolution, and earlier steps are not compensated until it is resolved. The engine does not promise exactly-once effects in an external system.
