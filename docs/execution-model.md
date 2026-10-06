---
title: Execution model
description: How IxIFlow definitions, instances, steps, and hosts work.
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

At suspension, the engine saves workflow data, active continuations and scope frames, and each waiting step. Resume matches an event to one saved wait and advances that continuation. A loop finishes the interrupted iteration and checks its condition again. A saga restores completed activity results for compensation. Memory and SQL repositories use revisioned checkpoints and renewable execution leases so another host can recover a stopped worker after its lease expires. An interrupted ordinary activity may still need manual resolution; external effects are not automatically exactly once. See [sagas](/docs/sagas/) for the practical effect.

`Try/Catch` uses saved frames for active nested handlers. A failure before a `Try` is not caught by that later block. Waits inside `Catch` and `Finally` resume without rerunning completed handler steps. Custom exception data is exposed through a declared durable fault projection.

Parallel branches have separate saved continuations and waits. They share mutable workflow data, so activities must synchronize conflicting updates. `WaitAll`, `WaitAny`, and conditional joins determine when the parent can continue. Completed branches do not rerun when another branch resumes.

## Host boundary

`WorkflowHost` manages whole workflow executions on one host. `WorkflowCoordinator` selects a host using health, tags, capacity, and load. Once selected, the host's local engine runs the workflow's activities. The coordinator does not distribute individual saga steps across workers.

The SQL host overload registers a SQL workflow state repository, host registry, and message bus. Queued commands use name and version references, and SQL deliveries require acknowledgement. The definition registry is still local memory; each host must register the same definition and activity code. SQL state uses revisioned checkpoints and renewable execution leases. See [coordinator and hosts](/docs/coordinator-and-hosts/).

## Checkpoints

The scheduler saves workflow data, active branches, loop and exception frames, waits, saga progress, and a checkpoint revision. The SQL host scans for interrupted running instances. An uncertain external activity outcome stops for an explicit resolution decision; inspect pending attempts and resolve them through `IWorkflowEngine` before continuing.
