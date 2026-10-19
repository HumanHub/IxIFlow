---
title: Coordinators
description: Run a local coordinator, link workflow hosts, and understand polling and recovery.
---

Install `IxIFlow.Coordinator` in the application that runs the matching service and in each application that links to it. The package requires the ASP.NET Core runtime. The engine itself has no gRPC dependency.

The current transport supports explicit trust of plaintext **loopback only**. Run the examples on one machine. Remote links require the transport security work; HTTPS alone is not accepted as an authenticated coordinator identity in this version. `AllowInsecure` defaults to false, and startup refuses plaintext on any other address, including wildcard listeners. SQLite refuses either coordinator role at startup.

## Run a coordinator

Use the same store as the workflow hosts. This example uses PostgreSQL; SQL Server and in-memory applications use the same builder extensions.

<!-- snippet: samples/IxIFlow.Samples/Coordinators.cs#service -->
```csharp
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static IHost CreateCoordinator(string connectionString)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddIxIFlow()
        .UsePostgreSql(connectionString)
        .AddCoordinator(options =>
        {
            options.ListenEndpoint = new Uri("http://127.0.0.1:5088");
            options.AllowInsecure = true;
        });
    return builder.Build();
}
```

Start the returned host with `RunAsync()` or `StartAsync()`.

`AddCoordinator` starts a dedicated HTTP/2 listener inside the application, including an ASP.NET Core application. It does not take over the application's HTTP routes. `AdvertisedEndpoint` overrides the address written to the registry; it must also be a trusted loopback origin. A listen port of zero selects an available port and advertises the actual address, which is useful for tests. Configure an explicit port for normal use.

The matching service keeps connected host capabilities and free slots, subscriptions, and a bounded cache of notice IDs and unmatched hints. It accesses only its own registry row and heartbeat. If the application also runs ordinary workflow hosted services, those services retain their normal store access independently of matching.

## Link workflow hosts

<!-- snippet: samples/IxIFlow.Samples/Coordinators.cs#links -->
```csharp
public static void LinkHost(IServiceCollection services, string connectionString)
{
    services.AddIxIFlow(options => options.Slots = 8)
        .UsePostgreSql(connectionString)
        .UseCoordinators(options =>
        {
            options.AllowInsecure = true;
            // Optional: registry discovery already connects to every live coordinator.
            options.Coordinators.Add(new Uri("http://127.0.0.1:5088"));
        });
}
```

Load each host's workflow definitions before starting it, as described in [hosting](coordinator-and-hosts.md). Hosts publish their actual capabilities and current free slots. Every active demand must match; matching prefers the previous host, then the most free slots, then the host woken least recently. A wake starts one recovery claim pass. The store rechecks eligibility and decides ownership through its fenced lease.

The link service discovers registry members every `DiscoveryInterval` (five seconds). It merges those addresses with the static `Coordinators` list and links to every endpoint. Stale registry entries are dropped; static entries remain candidates for reconnection. Failed links stop contributing to coordinated polling immediately and reconnect with exponential delays from `ReconnectMinDelay` (one second) up to `ReconnectMaxDelay` (30 seconds).

Writers send each committed change through one live link, round-robin, trying the next if its queue is full or the link has failed. Messages carry scheduling identifiers and demand keys and values. Workflow data, events, checkpoints and reply tokens never travel on these streams. Writer-side notification delivery reads the saved demand rows outside the committing transaction.

## Awaits, failure detection and polling

Await subscriptions go to every live link. The coordinator acknowledges installation before the awaiter reads the store. A change notice wakes the awaiter to reread; it never supplies a result. Subscriptions replay on reconnect, and their acknowledgements trigger another read to cover completions missed during the outage.

Both ends send application heartbeats every `LinkHeartbeatInterval` (five seconds), alongside HTTP/2 keepalive. `LinkTimeout` (15 seconds) bounds silence detection and subscription acknowledgement waits, with at most one additional heartbeat interval before the check runs. These timers use the application's monotonic `TimeProvider` timestamps. A protocol mismatch is rejected before Welcome. A stream becomes usable only after a compatible Welcome from an endpoint explicitly trusted by the local connection policy.

While any usable link remains, recovery and awaits use `CoordinatedPollInterval` (ten seconds), and the store wake source is paused. On PostgreSQL this closes the LISTEN connection. When the last link disappears, recovery scans immediately, store listening resumes, and ordinary `PollInterval` recovery and one-second await reads return. Losing one of several links keeps coordinated polling in effect.

Polling always remains finite: a process can commit and crash before sending its notice. Local runnable timers continue to work. Coordinator restart loses only hints; rows and leases remain authoritative in the store.

`SendQueueCapacity` bounds each stream's outgoing queue (256 messages). Full queues drop hints; a control message that cannot be queued closes the link so subscriptions can be replayed safely. Post-commit notification delivery also retains the engine's bounded queue and timeout, so neither slow streams nor full queues delay a commit acknowledgement.

`NoticeCapacity` bounds the coordinator's notice cache (4,096), with `NoticeLifetime` of ten seconds. Duplicate claim hints within that window produce at most one successful wake. Unmatched hints are retried when a host reports capacity and are discarded on expiry. `MaxConnections` and `MaxAwaitedInstancesPerHost` bound host and subscription state. Overflow affects latency and is covered by polling.
