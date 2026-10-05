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

<div class="arch-diagram" role="img" aria-label="Coordinator selects a workflow host using the SQL host registry. A SQL message bus supports commands. The SQL host registers shared workflow instance state, while the selected host runs activities in process.">
  <div class="arch-node"><strong>Coordinator</strong><small>Selects a healthy host</small></div>
  <div class="arch-connector"></div>
  <div class="arch-node"><strong>Workflow host</strong><small>Runs the local engine</small></div>
  <div class="arch-connector"></div>
  <div class="arch-node"><strong>Activities</strong><small>Run on that host</small></div>
</div>

The SQL overload also registers `SqlWorkflowStateRepository`, so workflow instance records are shared in SQL Server. The event store and definition registry remain in process memory. After a process restart, the definition must be registered again before a saved instance can resume.

## Project boundaries

| Project | Current responsibility |
| --- | --- |
| `IxIFlow` | Fluent C# API, execution engine, saga and suspension runtime, and in-memory state/event defaults. `AddIxIFlow()` is sufficient for local execution. |
| `IxIFlow.Authoring` | Preliminary document model, validation, and compiler. It does not parse YAML yet. |
| `IxIFlow.Distributed` | Coordinator, workflow host, host client, transport and registry interfaces, and background services. |
| `IxIFlow.Distributed.SqlServer` | SQL Server workflow state repository, message bus, and host registry for the optional host path. |

The projects form one-way dependencies: authoring and distributed hosting depend on the core; the SQL Server host integration depends on distributed hosting. The core does not reference hosting or SQL Server libraries.

Instance persistence and command transport are separate concerns. The SQL Server integration supplies workflow state, an atomic suspended-instance claim, host registration, and claimed message delivery. It does not supply a durable definition registry or event store. Custom `IWorkflowStateRepository` and `IEventStore` implementations can be registered through the core DI hooks. Distributed saga recovery still needs versioned checkpoints, recoverable instance leases, and host-loss tests.
