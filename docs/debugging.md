---
title: Pausing and continuing
description: Pause durable workflow instances and continue them on an enabled host.
---

# Pausing and continuing

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
