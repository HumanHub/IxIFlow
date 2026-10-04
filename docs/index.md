---
title: IxIFlow documentation
description: Start here for the IxIFlow workflow engine, its current API, and its limits.
---

IxIFlow is a code-first workflow engine for .NET. You define a workflow with a fluent C# builder, register its activities with dependency injection, and run it through `IWorkflowEngine`.

The current engine supports activities, typed data mapping, conditions, parallel branches, exception handling, suspension, and saga compensation. These features have automated tests. Some resume and distributed-host paths have known reliability gaps.

## Choose a starting point

- [Getting started](/docs/getting-started/) shows a small in-process workflow.
- [Execution model](/docs/execution-model/) explains how definitions, instances, and activities fit together.
- [Architecture](/docs/architecture/) shows the local core, host infrastructure, and the intended provider boundary.
- [Fluent API](/docs/fluent-api/) covers the current authoring surface.
- [Sagas](/docs/sagas/) explains compensation and its current process boundary.
- [Coordinator and hosts](/docs/coordinator-and-hosts/) describes the existing host infrastructure and its limits.
- [Status and roadmap](/docs/status-and-roadmap/) separates working behavior from planned work.

## Current boundary

Saga activities execute in the selected host process. The coordinator can select a host for a whole workflow, but it does not dispatch each saga activity as a durable remote task. The default workflow state repository lives in process memory across dependency injection scopes. SQL host registration and messaging do not make workflow instance state durable.

Use IxIFlow for evaluation and controlled in-process workloads while the reliability work continues. Validate the specific control flow and resume paths your application uses before relying on them.
