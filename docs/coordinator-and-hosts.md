---
title: Coordinator and hosts
description: IxIFlow workflow hosts, host selection, SQL state, and the message bus.
---

`IxIFlow.Distributed` and `IxIFlow.SqlServer` let several processes share workflow instance state and receive queued workflow commands. A whole workflow execution runs in one host process; saga activities and compensations run in that same process. Local `AddIxIFlow()` execution needs neither package.

## Components

- `IWorkflowStateRepository` has a SQL implementation with revisioned checkpoints and renewable execution leases.
- `IMessageBus` has a SQL implementation for queued commands, with explicit acknowledgement after handling.
- `IHostRegistry` has a SQL implementation for host registration, health, and metrics.
- `WorkflowQueueService` consumes execute, resume, and cancel commands addressed to its host and runs them through the local `IWorkflowClient`.
- `HostHealthService` registers the host and reports its health on an interval.
- `WorkflowCoordinator` selects a host using required or preferred host IDs, tags, health, and capacity.

## Register a SQL host

`AddIxIFlowHost(options, connectionString)` registers the SQL state repository, host registry, and message bus, and adds the queue and health background services. Register an `HttpClient` as well; the host client used for capacity checks depends on it.

```csharp
services.AddIxIFlow();
services.AddHttpClient();
services.AddIxIFlowHost(options =>
{
    options.HostId = "workflow-node-01";
    options.MaxConcurrentWorkflows = 50;
    options.Tags = ["payments"];
}, connectionString);
```

The `IxIFlow.Distributed` overload `AddIxIFlowHost(options)` expects you to register the host registry and message bus yourself.

The event store and `IWorkflowVersionRegistry` remain in memory. Every host that may execute or resume an instance must register the same workflow definition version and activity code.

## SQL state and recovery

The SQL repository fences writers with a renewable execution lease and a revision-checked commit. When an owner's lease expires, the SQL host's recovery scan claims the instance and continues it from its last checkpoint. Cancellation requests are applied to idle suspended instances by the same scan.

## Message delivery

The SQL bus claims a message, renews the claim while the handler runs, and marks it processed only after acknowledgement. An unacknowledged delivery is released when its consumer stops; an abandoned claim expires after five minutes by default. Failed deliveries retry and enter dead-letter state after the configured attempt limit. A command for a busy instance is deferred without using up that limit. Acknowledged rows are pruned after the retention period; dead letters are retained.

Queued execute commands carry a stable instance ID and a workflow name and version, resolved on the receiving host. Resume commands carry a delivery ID that is saved with the checkpoint, so a redelivered resume cannot satisfy a later wait with the same key.

## Completion events

A terminal result is written to a completion outbox in the same checkpoint commit. A publisher claims pending results and sends a `WorkflowExecutionCompletedEvent` through the message bus, so hosts do not routinely publish the same completion twice. The SQL bus uses a stable message ID for each instance's started and completed events.

An adapter without durable deduplication can still deliver a completion twice if publication succeeds and the host stops before recording its receipt. Consumers should deduplicate by instance ID. IxIFlow does not include a completion consumer; subscribe to the bus in your application.

## Target design

The coordinator is meant to route a whole workflow execution to a host chosen by health, capacity, current load, tags, and host preferences, over HTTP for immediate execution or through the message bus for queued execution. A resumed instance would prefer the host that ran it, and completion events would reach the caller. See the [roadmap](/docs/roadmap/#coordinator-and-hosts) for the remaining work.

## Current limits

- The coordinator selects a host but does not dispatch work. Publish `ExecuteWorkflowCommand`, `ResumeWorkflowCommand`, or `CancelWorkflowCommand` to the bus with the selected host as the target.
- `HttpWorkflowHostClient` calls host endpoints that IxIFlow does not provide. Without your own endpoints, capacity checks fail and host selection reports no available capacity.
- Host load is not tracked for queued executions, so selection does not balance by current load.
- Resume selection does not prefer the host that ran the instance before.
- Dead letters have no inspection or requeue API.
