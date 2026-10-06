---
title: Coordinator and hosts
description: What the IxIFlow coordinator, workflow hosts, registry, and message bus do today.
---

IxIFlow includes infrastructure for selecting a host and routing a **whole workflow execution** to it. This is separate from the saga executor, which runs each saga activity locally inside the selected host process.

## Current components

- `WorkflowCoordinator` selects a healthy host using required or preferred host IDs, tags, capacity, and load.
- `WorkflowHost` registers itself, reports health, tracks local executions, and calls the local workflow engine.
- `IWorkflowHostClient` has an HTTP implementation for host requests.
- `IHostRegistry` has a SQL implementation for host registration.
- `IMessageBus` has a SQL implementation for queued commands, with explicit acknowledgement after handling.

`IxIFlow.Distributed` supplies the custom `AddIxIFlowHost(options)` overload and expects you to register the host registry and message bus yourself. `IxIFlow.Distributed.SqlServer` supplies `AddIxIFlowHost(options, connectionString)`, which registers a SQL workflow state repository, host registry, and message bus. Both overloads add background queue and health services. Neither is required for local `AddIxIFlow()` execution.

## What the SQL overload does not provide

`AddIxIFlowHost(connectionString)` replaces the memory workflow state repository with `SqlWorkflowStateRepository`. The `IEventStore` and `IWorkflowVersionRegistry` still use memory. A saved instance needs its exact definition and activity code registered on the resuming host. The SQL repository uses revisioned checkpoints and renewable execution leases. A stopped worker's lease expires so another host can request recovery.

The SQL bus claims a message, renews the claim while the handler runs, and marks it processed only after acknowledgement. An unacknowledged delivery is released when its consumer stops; an abandoned claim expires after five minutes by default. Queued execute commands use a stable instance ID and a workflow name and version resolved on the receiving host. Queued resume commands can include a wait key. The HTTP host client's immediate and queue request bodies still contain a `WorkflowDefinition` and need reference-based transport.

## Practical status

The host selection, registry, and queue code are useful infrastructure, but the end-to-end distributed path is experimental. Treat it as a development surface, not a production reliability guarantee. The coordinator does not turn an in-process saga into a distributed saga.

The next steps are host-loss integration tests, discovery of expired running instances, distributed cancellation, HTTP transport cleanup, and durable definition registration. SQL state persistence and message redelivery have been tested against a disposable SQL Server container. See [status and roadmap](/docs/status-and-roadmap/).
