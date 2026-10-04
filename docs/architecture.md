---
title: Architecture
description: The local workflow core, optional host infrastructure, and intended provider separation.
---

IxIFlow's core can run inside one .NET application. Call `AddIxIFlow()` to register the engine, create a definition with the fluent API, and execute it through `IWorkflowEngine`. Activities run in that application process. This local mode does not require the coordinator or SQL host infrastructure.

## Current local mode

<div class="arch-diagram" role="img" aria-label="Application calls the locally registered workflow engine. The engine runs activities and uses singleton in-memory state and event stores within the process.">
  <div class="arch-node"><strong>Application</strong><small>Builds and starts a workflow</small></div>
  <div class="arch-connector"></div>
  <div class="arch-node"><strong>Core engine</strong><small>Runs steps in process</small></div>
  <div class="arch-connector"></div>
  <div class="arch-node"><strong>Activities</strong><small>Application code and services</small></div>
</div>

The default `IWorkflowStateRepository` and `IEventStore` implementations are singleton in-memory services. They retain state across scopes within one process. They do not provide durable recovery after a process restart or across hosts.

## Current optional host path

`AddIxIFlowHost(connectionString)` adds a coordinator, host, registry, SQL message bus, and background services. The coordinator selects a host for a **whole workflow**. That host calls its local engine; saga steps still run in process there.

<div class="arch-diagram" role="img" aria-label="Coordinator selects a workflow host using the SQL host registry. A SQL message bus supports commands. The selected host still runs the core engine with in-memory workflow instance state by default.">
  <div class="arch-node"><strong>Coordinator</strong><small>Selects a healthy host</small></div>
  <div class="arch-connector"></div>
  <div class="arch-node"><strong>Workflow host</strong><small>Runs the local engine</small></div>
  <div class="arch-connector"></div>
  <div class="arch-node"><strong>Activities</strong><small>Run on that host</small></div>
</div>

The SQL connection configures the host registry and message bus. It does **not** replace the singleton in-memory workflow state repository or event store. SQL-backed host routing should not be read as SQL-durable workflow instances.

## Project boundaries

| Project | Current responsibility |
| --- | --- |
| `IxIFlow` | Fluent C# API, execution engine, saga and suspension runtime, and in-memory state/event defaults. `AddIxIFlow()` is sufficient for local execution. |
| `IxIFlow.Authoring` | Preliminary document model, validation, and compiler. It does not parse YAML yet. |
| `IxIFlow.Distributed` | Coordinator, workflow host, host client, transport and registry interfaces, and background services. |
| `IxIFlow.Distributed.SqlServer` | SQL Server message bus and host registry for the optional host path. |

The projects form one-way dependencies: authoring and distributed hosting depend on the core; the SQL Server host integration depends on distributed hosting. The core does not reference hosting or SQL Server libraries.

Instance persistence and command transport are separate concerns. There is **no SQL workflow state or event-store provider** in these projects yet. Custom `IWorkflowStateRepository` and `IEventStore` implementations can be registered through the core DI hooks. The SQL Server integration only supplies host registration and a message bus. Distributed saga recovery is a target, not a current capability.
