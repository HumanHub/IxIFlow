---
title: Retention and tombstones
description: Retained terminal results, bounded cleanup and removed instance IDs.
---

A terminal instance row is its retained result. Clients can read it after completion until retention removes it. Removal reserves the ID with a tombstone, allowing callers to distinguish a removed result from an unknown ID.

## Configure retention

In service setup, with `IxIFlow.Extensions` imported:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#retention -->
```csharp
services.AddIxIFlow(options =>
{
    options.FinishedInstanceRetention = TimeSpan.FromDays(30);
    options.TombstoneRetention = TimeSpan.FromDays(90);
    options.RetentionScanInterval = TimeSpan.FromHours(1);
    options.RetentionBatchSize = 100;
});
```

These are the defaults. The snippet compiles in [DocumentationExamples.cs](../samples/IxIFlow.Samples/DocumentationExamples.cs); [DocumentationTests.cs](../tests/IxIFlow.Samples.Tests/DocumentationTests.cs) verifies removal and the client-visible tombstone with fake time.

Finished retention is measured from the first terminal store write and must be at least one minute. Tombstone retention is positive and starts at removal. The host runs a bounded pass at startup and then at each scan interval; each pass can remove up to the batch size of instances and separately that many expired tombstones. Age decisions use store time.

## What keeps an instance

Automatic cleanup skips a terminal instance if any outbox delivery is still pending or any child is nonterminal, including detached children. A delivered or given-up delivery does not block it. A terminal child can be removed while its parent waits because its complete result has already been copied to the parent's durable inbox.

A removed application publisher can leave a durable subscription behind. Explicitly [unregister the publisher](outbox-and-publishers.md#subscription-and-delivery-lifecycle) to give up its pending deliveries. Retention logs and the `ixiflow.retention.skipped_unattempted` metric identify publishers whose never-attempted deliveries keep finished candidates.

## Atomic removal

Cleanup removes instance state, checkpoint parts, commit receipts, trace history, inbox, waits and settled outbox events/deliveries together with creation of the tombstone. Explicit deletion preserves pending outbox deliveries and their immutable events; automatic retention skips such instances. Compact released lease epoch fences remain so that old owners cannot become valid after an ID is reused. Publisher registrations remain until explicitly retired.

`IWorkflowStateRepository.PurgeAsync` exposes a bounded pass and returns removed counts plus never-attempted publisher diagnostics. The hosted retention service uses this same operation. Every provider implements it.

## Client behavior after removal

| Client operation | While the tombstone is active |
| --- | --- |
| `AwaitAsync` | `Removed` result, without workflow data |
| `GetStatusAsync` | `WorkflowExecutionStatus.Removed` |
| `GetSnapshotAsync` | Snapshot with `IsRemoved` |
| `GetTraceAsync`, `GetPendingActivitiesAsync` | Empty collection with `IsRemoved` |
| `GetOpenWaitsAsync` with an instance filter | Empty collection with `IsRemoved` |
| Start/run or another mutating client call under that ID | `WorkflowEngineException` with `InstanceRemoved` |

At the lower store boundary, events addressed to a tombstoned ID are refused as finished. After tombstone expiry, reads use unknown-instance behavior and the ID is available again. Expiry is enforced at admission; an expired row does not need to wait for a scan before reuse.

## Explicit deletion

`DeleteWorkflowInstanceAsync(id, tombstoneRetention)` also leaves a tombstone. Its default is 90 days; pass the same period configured for automatic retention if yours differs. Deleting an unknown/already removed ID does not create or extend a tombstone. If the removed child still has a waiting parent, deletion also sends the terminating child notice atomically.

Retention of unused published bundles, large payload offloading, residency policies and redaction remain [planned](roadmap.md). Instance retention and tombstones are implemented now.
