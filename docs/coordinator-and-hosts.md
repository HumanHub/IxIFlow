---
title: Hosting
description: Configure the core host, slots, storage, recovery and graceful drain.
---

`AddIxIFlow()` registers the core host, client and hosted heartbeat, recovery, outbox and retention services. The executor runs activities in that process. Use the .NET generic host to start background services and stop them in order.

## Start the host

With imports `IxIFlow.Extensions`, `Microsoft.Extensions.Hosting` and the standard .NET usings:

<!-- snippet: samples/IxIFlow.Samples/Hosting.cs -->
```csharp
public static IHost CreateHost()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddIxIFlow(options => options.Slots = 8);
    return builder.Build();
}
```

[Hosting.cs](../samples/IxIFlow.Samples/Hosting.cs) compiles this method and shows `StartAsync`, client execution and `StopAsync`. [ReadmeExamples.cs](../tests/IxIFlow.Samples.Tests/ReadmeExamples.cs) exercises that lifecycle.

Resolving a client from a plain service provider permits foreground calls, as in [getting started](getting-started.md). Background recovery, outbox polling and retention require the hosted services to be started.

## Slots and scopes

`IxIFlowOptions.Slots` defaults to 64. Each admitted work item has a DI scope. Nested calls through the slot client can execute inline in their parent's slot, so slots do not impose a separate activity concurrency limit. `MaxActivitiesPerRun` limits in-flight activities within one run.

A run keeps its slot until it is idle or parked. When nothing is in flight and the next retry is further away than `ParkRetriesLongerThan` (one second by default), it saves its retry time and releases the slot and lease. The host picks it up when due. Shorter retry waits stay in the slot.

## Choose a store

Choose a single provider on the builder returned by `AddIxIFlow()`. Memory is the default; `UseSqlite`, `UseSqlServer` and `UsePostgreSql` select durable providers. Their extensions are in `IxIFlow.Extensions`.

The [store guide](stores.md) covers their operational differences. [Hosting.Services](../samples/IxIFlow.Samples/Hosting.cs) compiles all four registrations. Provider schemas initialize on first use.

Load definitions into the in-memory `IWorkflowVersionRegistry` before recovery can need them. Every host sharing a database must have the matching definition versions and activity code. [Review's sample setup](../tests/IxIFlow.Samples.Tests/SampleTests.cs) registers its pinned child before starting the parent.

## Host registry and capabilities

Starting the generic host registers a member in the store before recovery starts. Each host has a fresh GUID identity; `HostName` is a display name and defaults to `pid@machine`. Reusing a display name after restarting does not reuse the old identity. The registry records the member's kind (`host` or `coordinator`), name, optional endpoint, configured slot count, engine version, heartbeat interval and store-stamped start and heartbeat times. Slots advertise configured capacity, rather than a history of current activity or health measurements.

`HeartbeatInterval` defaults to five seconds and accepts one millisecond through twenty minutes. The upper bound ensures a member misses three beats before the one-hour pruning horizon. A member becomes stale after three missed intervals, using the interval it registered and the store's clock. Registry operations remove rows silent for over an hour, including their capability rows. A heartbeat can renew a stale row until pruning; a pruned host registers again on its next heartbeat. On shutdown, heartbeats continue while workflow work drains, then the member unregisters. Registry liveness is independent of execution-lease ownership.

Automatic capabilities are `os` (`windows`, `linux`, `macos`, or an OS description), `arch` (the process architecture in lowercase), `runtime` (the .NET runtime description), `host` (the GUID), and `workflow:<name>@<version>` (the definition's fingerprint) for each registered definition. Set user values through `IxIFlowOptions.Capabilities`, for example `options.Capabilities["region"] = "eu"`. Keys and values are case-sensitive; an empty value can represent a presence-only capability. Configured values cannot replace automatic keys or use the `workflow:` prefix.

Every heartbeat rebuilds the automatic capabilities from `IWorkflowVersionRegistry` and republishes the complete capability snapshot atomically with the heartbeat. A definition registered at runtime appears on the next heartbeat. User values are copied when services are configured. Register only definitions and activity code the host can actually load.

The store exposes `RegisterMemberAsync(MemberRegistration)`, `HeartbeatAsync(id, capabilities)`, `UnregisterMemberAsync(id)` and `GetLiveMembersAsync()`. Live-member reads return immutable capability snapshots with store timestamps. The relational providers persist advertisements in `ixiflow_hosts` and `ixiflow_host_capabilities`; memory provides the same contract. SQL Server and PostgreSQL use database time. Memory and SQLite use the store's injected `TimeProvider`. SQLite advertises one host, replaces any advertisement left by its previous process after exclusive file ownership is acquired, and refuses coordinator registration.

## Recovery and ownership

A host schedules its own saved runnable times locally. The recovery service polls the store every `PollInterval` (one second by default), up to available slots, for due work with no active owner. PostgreSQL `LISTEN`/`NOTIFY` supplies additional wake-ups; polling remains the fallback.

`IRunnableNotifier` is an optional singleton extension beside `IRunnableWakeSource`. Its default does nothing. The host queues `RunnableNotice` hints after successful commits, event appends, cancellation requests and execution lease releases. A notice contains the instance ID, immutable demand-set ID, saved host pin and expiry, last host ID, and `Claimable` and `Idle` flags. Idle and terminal commits also send a change hint for awaiting callers. Notices contain no workflow or event payloads.

Notifier delivery uses a bounded queue of 256 notices and one outstanding call. Queue overflow drops hints; a call taking longer than one second is logged and cancelled. An implementation that ignores cancellation occupies the single call until it returns. Notification failures are logged and never delay a store acknowledgement. Polling, local timers and PostgreSQL wake-ups continue with or without a notifier.

Store acknowledgements capture their notice fields inside the write transaction. `WorkflowCommitResult.Claimable` describes the committing instance; `Notice` carries its change and `MadeRunnable` carries notices for other instances made claimable. Commits retain these notices with their receipts and return them on replay. Idempotent event appends and admissions also return their original notice, including admission lookups through `WorkflowAdmissionRecord.Notice`. Hosts publish these hints again so a lost acknowledgement does not lose a wake; duplicate hints cost only another claim pass. `CancellationRequestResult.Notice` and `ExecutionLeaseReleaseResult.Notice` describe new claimable changes. Cancellation and release return `Accepted` and `Released` respectively. Refused writes have no notice.

An execution lease has a 30-second duration and renewal every 10 seconds in the current implementation. An epoch is never granted twice in a store, and epochs strictly increase for each instance ID, including across deletions and re-creations. Across instances, epochs follow allocation order, with gaps allowed; grants may complete in a different order. Every commit requires the active lease's positive epoch and the expected revision. Every commit after the first also requires the row's incarnation. Losing ownership stops the run; an expired owner's writes cannot overwrite a new owner's progress.

A claim that finds the previous lease expired without release, while the instance was Running, increments the crash count. After three consecutive such claims without saved progress by default, the instance is quarantined at `NeedsResolution`. The quarantine commit resets the crash count; activity-start commits alone do not count as progress. Investigate and use `ReleaseAsync` to clear the operator stop and continue, or `CancelAsync`. `RecoverAsync` leaves operator stops in place.

`RecoverAsync` repeats a terminal result, returns `NeedsResolution` for an operator stop (without loading workflow data), and can return `Scheduled` when a retry parks or `Running` when another owner holds the lease. Unknown/nonrecoverable work or unavailable/incompatible definitions return `Faulted` with a reason. `ReleaseAsync` requires an operator-stop marker; otherwise it returns `Faulted`. It does not supply an uncertain activity's outputs: use `ResolveAsync` or let its recovery contract establish them. Release waits through a parked retry until idle.

## Shutdown

The host stops admitting new steps, lets running activities reach commits, and drains for `DrainTimeout` (20 seconds by default). After that budget, it cancels the run tokens and waits up to ten more seconds for runs to stop. The executor allows five seconds for activity cancellation before abandoning unfinished attempts and releasing ownership. Allow for this in the application's shutdown budget. Activities that ignore cancellation can outlive the run; recovery treats their saved starts as uncertain. Gracefully released leases do not count as crashes.

Outbox workers stop claiming new deliveries and drain those already in flight. Interrupted deliveries can be claimed again after their lease expires.

## Work waiting for a capable host

An instance can be due with no active execution lease and still have no host able to run it. The store compares each saved demand set with persisted capabilities of live hosts. A host is live while its heartbeat is younger than three of its registered heartbeat intervals, measured by the store's clock. Every unexpired demand must match on the same host. A pin also requires that host's advertised identity; an expired pin stops restricting placement. Free slots do not affect this diagnostic: busy hosts still count as capable hosts.

`WhenUnschedulable.Wait` is the default. `WorkflowOptions.WhenUnschedulable = WhenUnschedulable.Fail` refuses a new start when no live host meets the resolved demands. It uses the same store-time liveness and expiry rules. It is an admission-time observation; a host may leave after admission, and a waiting instance can become runnable when a capable host joins.

Resolve `IWorkflowOperations` from DI and call `GetUnschedulableAsync(limit, cursor, cancellationToken)`. `IWorkflowClient` exposes the same read. Both return an `UnschedulablePage`: `Items`, `NextCursor`, and `StoreTimeUtc`. Each item includes `InstanceId`, `WorkflowName`, `WorkflowVersion`, `RunnableAtUtc`, and `UnmetDemands`. Page sizes are 1 through 1,000, defaulting to 100. Continue with `NextCursor` until it is null. Rows are ordered by runnable time, then ordinal instance ID. Each page is a fresh read: completion, leases, heartbeats and demand expiry can change membership between pages. Restart from a null cursor to refresh earlier rows.

`UnmetDemands` lists active demands absent from every live host. If each demand is available somewhere but no host satisfies the combination, it lists the entire active combination. For example, a GPU host in the US and an EU host without a GPU leave a GPU-plus-EU instance waiting. With no live hosts an instance with an empty demand set can also appear, with an empty explanation list.

Every instance requires `workflow:<name>@<version>` with its saved structure hash as the value. If a deployment advertises only a changed definition, the read names the old hash it cannot run. Restore a host with the compatible definition or investigate the saved instance before choosing an operator action.

The recovery service checks every `UnschedulableScanInterval` (30 seconds by default). Startup checks are staggered. Only the host with the lowest live GUID reports a shared store's backlog. Each check reads at most ten pages of 100 items and carries its cursor into the next interval when necessary. A completed scan publishes the count and oldest runnable age grouped by workflow name and version. Long scans describe observations over multiple pages, rather than a transactionally frozen fleet snapshot. Warnings name each blocked instance and its unmet demands, at most once per instance per reporting host in five minutes. Leadership changes can produce another warning.

These reads take no execution slot and expose no HTTP endpoint. Applications decide which callers can resolve and use the operator service.

## Planned hosting work

There is no coordinator yet. Demands, claim filtering, affinity and visibility of unschedulable work run in the stores and host recovery service. Process slots and isolation, the executor message protocol and pipe transport, coordinator discovery and secured links remain planned for 1.0. See the [roadmap](roadmap.md).
