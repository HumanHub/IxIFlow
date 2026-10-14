---
title: IxIFlow documentation
description: Start here for the IxIFlow workflow engine and fluent C# API.
---

IxIFlow is a code-first workflow engine for .NET. You define a workflow with a fluent C# builder, register its activities with dependency injection, and run it through `IWorkflowEngine`.

The engine supports activities, typed data mapping, conditions, loops, parallel branches, exception handling, waits, child workflows, and saga compensation. Hosts that share SQL state register the same workflow definitions and activity code before resuming an instance.

## Choose a starting point

- [Getting started](/docs/getting-started/) shows a small in-process workflow.
- [Execution model](/docs/execution-model/) explains how definitions, instances, and activities fit together.
- [Architecture](/docs/architecture/) shows the core and optional host infrastructure.
- [Fluent API](/docs/fluent-api/) covers workflow authoring in C#.
- [Custom activity SDK](/docs/activity-sdk/) covers activity metadata and assembly registration.
- [Sagas](/docs/sagas/) explains compensation and saved saga progress.
- [Coordinator and hosts](/docs/coordinator-and-hosts/) describes host selection, SQL state, and command routing.

## Execution boundary

Saga activities execute in the selected host process. The coordinator selects a host for a whole workflow. The default workflow state repository lives in process memory across dependency injection scopes; the SQL Server host registers shared workflow instance state. Each host registers definitions and activity code used by its instances.
