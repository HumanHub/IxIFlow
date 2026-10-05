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

`AddIxIFlowHost(connectionString)` replaces the memory workflow state repository with `SqlWorkflowStateRepository`. The `IEventStore` and `IWorkflowVersionRegistry` still use memory. A saved instance needs its exact definition registered on the resuming host. The SQL repository atomically claims a matching suspended instance before resume. General saves still lack checkpoint versions, and a worker that dies after claiming an instance leaves no recoverable lease.

The SQL bus claims a message and marks it processed only after the queue handler acknowledges it. An unacknowledged delivery is released when its consumer stops; an abandoned claim expires after five minutes. The queued execution command carries a workflow name and version, which the receiving host resolves locally. The HTTP host client's immediate and queue request bodies still contain a `WorkflowDefinition` and need the same reference-based transport design.

## Practical status

The host selection, registry, and queue code are useful infrastructure, but the end-to-end distributed path is experimental. Treat it as a development surface, not a production reliability guarantee. The coordinator does not turn an in-process saga into a distributed saga.

The next steps are durable published definitions, versioned checkpoints and recoverable instance leases, command idempotency, claim renewal for long handlers, and host-loss integration tests. SQL state persistence and message redelivery have been tested against a disposable SQL Server container. See [status and roadmap](/docs/status-and-roadmap/).
