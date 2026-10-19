---
title: Hosting
description: Configure the core host, slots, storage, recovery and graceful drain.
---

`AddIxIFlow()` registers the core host, client and hosted recovery, outbox and retention services. The executor runs activities in that process. Use the .NET generic host to start background services and stop them in order.

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

## Recovery and ownership

A host schedules its own saved runnable times locally. The recovery service polls the store every `PollInterval` (one second by default), up to available slots, for due work with no active owner. PostgreSQL `LISTEN`/`NOTIFY` supplies additional wake-ups; polling remains the fallback.

An execution lease has a 30-second duration and renewal every 10 seconds in the current implementation. An epoch is never granted twice in a store, and epochs strictly increase for each instance ID, including across deletions and re-creations. Across instances, epochs follow allocation order, with gaps allowed; grants may complete in a different order. Every commit requires the active lease's positive epoch and the expected revision. Every commit after the first also requires the row's incarnation. Losing ownership stops the run; an expired owner's writes cannot overwrite a new owner's progress.

A claim that finds the previous lease expired without release, while the instance was Running, increments the crash count. After three consecutive such claims without saved progress by default, the instance is quarantined at `NeedsResolution`. The quarantine commit resets the crash count; activity-start commits alone do not count as progress. Investigate and use `ReleaseAsync` to clear the operator stop and continue, or `CancelAsync`. `RecoverAsync` leaves operator stops in place.

`RecoverAsync` repeats a terminal result, returns `NeedsResolution` for an operator stop (without loading workflow data), and can return `Scheduled` when a retry parks or `Running` when another owner holds the lease. Unknown/nonrecoverable work or unavailable/incompatible definitions return `Faulted` with a reason. `ReleaseAsync` requires an operator-stop marker; otherwise it returns `Faulted`. It does not supply an uncertain activity's outputs: use `ResolveAsync` or let its recovery contract establish them. Release waits through a parked retry until idle.

## Shutdown

The host stops admitting new steps, lets running activities reach commits, and drains for `DrainTimeout` (20 seconds by default). After that budget, it cancels the run tokens and waits up to ten more seconds for runs to stop. The executor allows five seconds for activity cancellation before abandoning unfinished attempts and releasing ownership. Allow for this in the application's shutdown budget. Activities that ignore cancellation can outlive the run; recovery treats their saved starts as uncertain. Gracefully released leases do not count as crashes.

Outbox workers stop claiming new deliveries and drain those already in flight. Interrupted deliveries can be claimed again after their lease expires.

## Planned hosting work

There is no coordinator yet. Process slots and isolation, the executor message protocol and pipe transport, capabilities and demands, placement and affinity, coordinator discovery and secured links remain planned for 1.0. See the [roadmap](roadmap.md).
