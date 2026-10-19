---
title: Pausing and breakpoints
description: Pause durable workflow instances, set breakpoints, and continue on an enabled host.
---

# Pausing and breakpoints

Enable debugging on hosts that will accept operator commands:

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#configure -->
```csharp
services.AddIxIFlow(options => options.Debugging = true);
```

Debugging is off by default. The application must authorize access to each instance and operation before calling `IWorkflowOperations`. The actor is an audit label supplied by that authorized application.

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#operate -->
```csharp
var operations = provider.GetRequiredService<IWorkflowOperations>();
await operations.PauseAsync(instanceId, actor: "operator@example.com");
await operations.ContinueAsync(instanceId, actor: "operator@example.com");
```

A pause request is saved before it is acknowledged. For a running instance, `PauseAsync` returns the current state while the owner drains: it starts no new steps, lets activities already in flight finish, and commits their results before committing `Paused`. Use `IWorkflowClient.AwaitAsync` or `GetStatusAsync` to observe the committed pause. An idle instance pauses immediately under its execution lease.

A committed pause has no runnable time and releases its lease. Commands live outside the checkpoint, so an owner crash cannot discard a pending pause. Each command has an identity, an order, a pause-generation precondition and a durable acknowledgement. Applying a command and acknowledging it is one transaction; a Continue arriving during a pause commit keeps its wake.

`ContinueAsync` obtains a fresh lease and records the continuation before executing more workflow code. A different enabled host can continue the instance. Every host honors the paused state; a host with debugging disabled refuses both operations and leaves a pending Continue held. A cancellation ends a pause on any host.

Events for existing waits are appended while paused and remain pending until continue. Normal arrival validation still applies. Timers keep advancing; due deadlines are handled when execution continues. Pausing a parent does not pause its children.

`WorkflowDefinition.StepTable` lists each executable step's catalog ID, author ID, name, kind, scope path and source location. Builder calls capture their file and line automatically. Source locations do not affect the workflow structure hash. Snapshot pointers expose the catalog address as `StepId` and the author identity as `AuthorStepId`; frames include loop iteration and ForEach item index. Activity trace entries expose the catalog address as `StepId`.

## Breakpoints

`SetBreakpointsAsync` replaces the instance's entire breakpoint set. Supply stable, unique breakpoint IDs, an actor, and either an author step ID, a catalog path, or a source path and line:

```csharp
var verified = await operations.SetBreakpointsAsync(instanceId,
[
    new Breakpoint("charge", "charge-card"),
    new Breakpoint("third-item", "root/2/foreach/0", HitCondition: "== 3"),
    new Breakpoint("source", Source: new BreakpointSource("order.yaml", 20))
], actor: "operator@example.com");
var current = await operations.GetBreakpointsAsync(instanceId);
```

A function address must resolve uniquely. A source address resolves to the first step in that file at or after the requested line, including generated YAML steps. The returned `StepId` is the resolved catalog path; `Source` retains the requested location, `VerifiedSource` answers the actual line, and `StructureHash` pins the definition used for resolution. Unresolved or ambiguous addresses and invalid hit conditions are refused before saving the change.

An enabled breakpoint counts each visit before its step runs. `HitCondition` accepts `"== n"` (the nth visit), `">= n"` (the nth and subsequent visits), or `"% n"` (every nth visit), with positive `n`. Omit it to stop on every visit. Counts survive recovery, and an abandoned execution pass abandons its uncommitted counts. Updating an existing ID at the same catalog step preserves its count; removing it and later adding it resets the count. Disabled breakpoints neither count nor pause. Expression conditions are not available yet.

On a hit, all branches stop admitting new work. Activities already in flight finish and commit before `Paused` is saved and the lease released. The committed stop names the branch, step and breakpoint, and the trace records `BreakpointHit`. Continue records a durable skip for that stopped occurrence before dispatch; the first pick of that branch at that step skips all breakpoints once without incrementing counts. Later loop visits and ForEach items can stop again.

Changes use the same ordered, acknowledged command rows and watch as pause and continue, so a change submitted on another host reaches the running owner. The set returned by the operations read includes the latest requested configuration and committed counts; command acknowledgement marks when the owner has applied it. A configuration accepted just before an automatic breakpoint pause remains applicable after that pause. Setting breakpoints does not continue a paused instance; passing an empty list removes them. Debug-disabled hosts ignore breakpoint matching, refuse breakpoint writes, and continue to honor durable pauses.
