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
- `IMessageBus` has a SQL implementation for queued commands.

`IxIFlow.Distributed` supplies the custom `AddIxIFlowHost(options)` overload and expects you to register the host registry and message bus yourself. `IxIFlow.Distributed.SqlServer` supplies `AddIxIFlowHost(options, connectionString)`, which registers a SQL host registry and SQL message bus. Both overloads add background queue and health services. Neither is required for local `AddIxIFlow()` execution.

## What the SQL overload does not provide

`AddIxIFlowHost(connectionString)` first calls `AddIxIFlow()`. That method registers singleton in-memory `IWorkflowStateRepository` and `IEventStore` implementations. They work across scopes in the same process, but the SQL overload does **not** replace them with a SQL workflow state repository or a durable workflow definition registry. It therefore does not make suspended workflow instances recoverable after a process restart or on another host.

The current SQL message consumer marks messages processed before yielding them to their handlers. A handler failure can lose a command because it has already been marked processed. The execution command also contains a `WorkflowDefinition` with `System.Type` values, which is not ready for reliable transport as JSON.

## Practical status

The host selection, registry, and queue code are useful infrastructure, but the end-to-end distributed path is experimental. Treat it as a development surface, not a production reliability guarantee. The coordinator does not turn an in-process saga into a distributed saga.

The next steps are shared instance state, serializable published definitions, acknowledged delivery with retry, and integration tests against a real SQL service. See [status and roadmap](/docs/status-and-roadmap/).
