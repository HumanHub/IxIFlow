---
title: Engine capabilities
description: IxIFlow execution, persistence, recovery, and host behavior.
---

IxIFlow defines workflows with the [fluent C# API](/docs/fluent-api/) and executes them through `IWorkflowEngine`. The core engine runs in one application process with `AddIxIFlow()`. SQL Server state and host services are optional.

## Workflow execution

- Activities implement `IAsyncActivity`. Typed input mappings set activity properties; output mappings write values to workflow data.
- Definitions contain sequences, conditions, loops, parallel branches, `Try`/`Catch`/`Finally`, event waits, delays, sagas, and child workflow invocations.
- Parallel branches can join with `WaitAll` behavior, `WaitAny`, or a condition. Branches share workflow data, so activities must coordinate conflicting writes.
- Saved continuations preserve active branches, loop and exception frames, waits, saga progress, and `PreviousStep` values. A suspended instance resumes at its saved position, including waits inside nested handlers and child workflows.
- Saga handlers resume with saved fault data and compensation results. Compensation activities run in reverse order when selected by the handler.

## State and recovery

`AddIxIFlow()` registers in-memory workflow state and event storage. These singleton stores retain instances across dependency injection scopes in one process. The SQL Server host registers shared workflow instance state with revisioned checkpoints and renewable execution leases. Its definition registry and event store remain in memory; each resuming host registers the matching workflow definitions and activity code.

An interrupted activity or child workflow with an uncertain external outcome can stop at `NeedsResolution`. `IWorkflowEngine.GetPendingActivitiesAsync` lists pending attempts. `ResolveActivityAsync` records a decision to complete a step with explicit outputs or fail it with a typed exception. External effects require an idempotency or recovery policy in the application.

A caller's `CancellationToken` stops the current engine call and leaves a saved instance recoverable. `CancelWorkflowAsync` requests workflow cancellation and unwinds through saga compensation and `Finally`. Recovery scans claim eligible running instances after an owner's lease expires and apply cancellation requests to idle suspended instances.

## Child workflows

`Invoke<TWorkflow, TChildData>` selects a workflow class. `Invoke<TChildData>(name, version, ...)` selects a registered definition. Both forms use the registered definition for execution and resume, including when a new service provider uses the retained state repository. A child invocation has a stable child instance ID and a saved parent-child wait. A parent awaiting a child can resume after the child completes; an uncertain child activity is resolved before the parent finishes cancellation.

## Coordinator and SQL hosts

The coordinator selects a host for a whole workflow using health, capacity, load, tags, and host preferences. Activities and saga compensations run in the selected host process. The SQL host provides a workflow state repository, host registry, and acknowledged message bus.

SQL message claims renew while handlers run. Queued execute commands carry a stable instance ID and workflow name and version; resume commands carry a delivery ID saved with the checkpoint. Redelivery does not satisfy a later wait with the same key. Failed deliveries retry and can enter dead letter state after the configured attempt limit. Completion events are emitted for terminal results through a checkpointed outbox, and consumers identify duplicates by instance ID.

See [execution model](/docs/execution-model/), [sagas](/docs/sagas/), and [coordinator and hosts](/docs/coordinator-and-hosts/) for API and persistence details.
