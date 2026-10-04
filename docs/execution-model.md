---
title: Execution model
description: How IxIFlow definitions, instances, steps, and hosts work today.
---

IxIFlow has a definition, an execution engine, and an instance record. A definition contains ordered steps and the workflow data type. `IWorkflowEngine.ExecuteWorkflowAsync` runs that definition with a data object and returns a `WorkflowExecutionResult`.

## Normal execution

1. Your application builds or registers a workflow definition.
2. The engine creates an instance and executes its steps.
3. An activity runs in the current host process through `IActivityExecutor`.
4. Input mappings populate activity properties. Output mappings copy values back to workflow data.
5. The engine returns a status, data, and trace entries.

Conditions and parallel branches are represented as workflow steps with nested child steps. A saga step contains a sequence of activities and compensation information. The engine can also suspend an instance and resume it from an event.

## State and resume

The default `AddIxIFlow()` registration uses singleton `InMemoryWorkflowStateRepository` and `InMemoryEventStore`. They retain data across dependency injection scopes within one process. They are not shared or durable state stores: a process restart or another host loses access to those instances.

Resume currently reconstructs the continuation from the definition and saved instance data. The snapshot records pointers and nested frames, but the continuation path does not execute from them yet. It flattens the remaining steps after a suspension. For example, a suspend inside a loop can resume the rest of that loop body and then complete without checking the loop again. The saga resume path also has known gaps: remaining saga steps can be skipped, and compensation history from before suspension can be lost. Concurrent resume is not guarded by an atomic claim. See [sagas](/docs/sagas/) for the practical effect.

`Try/Catch` uses a stack for active nested handlers during local execution. A regression test currently shows that an exception from a step before a `Try` can be handled by that later block. Exception handling needs to be tied to the active frame that owned the failed step.

Parallel branches run with `Task.WhenAll` and a separate `ExecutionState` per branch, but they share the workflow context and mutable workflow data. Durable parallel execution needs one persisted pointer per branch, a recorded join policy, and a defined way to combine branch outputs.

## Host boundary

`WorkflowHost` manages whole workflow executions on one host. `WorkflowCoordinator` selects a host using health, tags, capacity, and load. Once selected, the host's local engine runs the workflow's activities. The coordinator does not distribute individual saga steps across workers.

The SQL host overload registers a SQL host registry and SQL message bus. It does not register a SQL workflow state repository or durable definition registry. Message handling also needs acknowledgement and retry hardening. See [coordinator and hosts](/docs/coordinator-and-hosts/).

## What is being built next

The next execution model should keep stable step IDs in an immutable definition and make persisted pointers and frames authoritative for continuation. An instance checkpoint should include workflow data, active branches, loop counters, exception scopes, waits, completed saga steps, and a concurrency version. The scheduler can then claim one instance, advance a step, and commit the next checkpoint without replaying the entire workflow.

The engine also needs a shared state store, persisted attempts, and a persisted compensation stack before it can provide dependable multi-host recovery. These are project goals, not current guarantees. Track them in [status and roadmap](/docs/status-and-roadmap/).
