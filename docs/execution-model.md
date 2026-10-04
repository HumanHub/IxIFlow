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

At suspension, the engine saves current workflow data, the waiting step, and active frames. Resume finds the saved step in the definition and re-enters its enclosing scopes. A loop finishes the interrupted iteration and checks its condition again. A saga resumes its remaining steps and restores completed activity results for compensation. This is covered for direct waits and waits in saga outcome branches, including repeated suspensions. The saved step identifier still selects the path; a single pointer-and-frame interpreter for every control-flow type is unfinished. Concurrent resume is not guarded by an atomic claim. See [sagas](/docs/sagas/) for the practical effect.

`Try/Catch` uses a stack for active nested handlers during local execution. A failure before a `Try` is no longer caught by that later block. Saved handler frames let waits inside `Catch` and `Finally` resume without rerunning completed handler steps. The frame currently saves the exception type and message; custom exception fields need an explicit durable representation.

Parallel branches run with `Task.WhenAll` and a separate `ExecutionState` per branch, but they share the workflow context and mutable workflow data. One waiting branch can resume while completed branches stay completed. Multiple simultaneous waits are not supported yet. Durable parallel execution needs one persisted pointer per waiting branch, a recorded join policy, and a defined way to combine branch outputs.

## Host boundary

`WorkflowHost` manages whole workflow executions on one host. `WorkflowCoordinator` selects a host using health, tags, capacity, and load. Once selected, the host's local engine runs the workflow's activities. The coordinator does not distribute individual saga steps across workers.

The SQL host overload registers a SQL workflow state repository, host registry, and message bus. Queued commands use name and version references, and SQL deliveries require acknowledgement. The definition registry is still local memory, and instances have no atomic claim for concurrent resume. See [coordinator and hosts](/docs/coordinator-and-hosts/).

## What is being built next

The next execution model should keep stable step IDs in an immutable definition and make persisted pointers and frames authoritative for continuation. An instance checkpoint should include workflow data, active branches, loop counters, exception scopes, waits, completed saga steps, and a concurrency version. The scheduler can then claim one instance, advance a step, and commit the next checkpoint without replaying the entire workflow.

The engine also needs a shared state store, persisted attempts, and a persisted compensation stack before it can provide dependable multi-host recovery. These are project goals, not current guarantees. Track them in [status and roadmap](/docs/status-and-roadmap/).
