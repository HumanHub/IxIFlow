---
title: Coordinator and hosts
description: IxIFlow coordinator, workflow hosts, registry, and message bus.
---

IxIFlow includes infrastructure for selecting a host and routing a **whole workflow execution** to it. This is separate from the saga executor, which runs each saga activity locally inside the selected host process.

## Components

- `WorkflowCoordinator` selects a healthy host using required or preferred host IDs, tags, capacity, and load.
- `WorkflowHost` registers itself, reports health, tracks local executions, and calls the local workflow engine.
- `IWorkflowHostClient` has an HTTP implementation for host requests.
- `IHostRegistry` has a SQL implementation for host registration.
- `IMessageBus` has a SQL implementation for queued commands, with explicit acknowledgement after handling.

`IxIFlow.Distributed` supplies the custom `AddIxIFlowHost(options)` overload and expects you to register the host registry and message bus yourself. `IxIFlow.Distributed.SqlServer` supplies `AddIxIFlowHost(options, connectionString)`, which registers a SQL workflow state repository, host registry, and message bus. Both overloads add background queue and health services. Neither is required for local `AddIxIFlow()` execution.

## SQL host state

`AddIxIFlowHost(connectionString)` replaces the memory workflow state repository with `SqlWorkflowStateRepository`. The `IEventStore` and `IWorkflowVersionRegistry` still use memory. A saved instance needs its exact definition and activity code registered on the resuming host. The SQL repository uses revisioned checkpoints and renewable execution leases. The SQL host scans running instances and requests recovery after an owner's lease expires.

The SQL bus claims a message, renews the claim while the handler runs, and marks it processed only after acknowledgement. An unacknowledged delivery is released when its consumer stops; an abandoned claim expires after five minutes by default. Queued execute commands use a stable instance ID and a workflow name and version resolved on the receiving host. Queued resume commands can include a wait key. The HTTP host client's immediate and queue request bodies carry a `WorkflowDefinition`.

## Execution boundary

The coordinator routes a whole workflow to a host. Its activities and saga compensations run in that host process. SQL state persistence, expired-worker takeover, and message redelivery are covered by integration tests. A host resuming an instance must have its definition and activity code registered. External effects with an uncertain outcome require an explicit resolution decision through the engine.
