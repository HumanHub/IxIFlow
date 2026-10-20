---
title: Coordinators
description: Authenticate coordinator links, configure discovery, and understand polling and recovery.
---

Install `IxIFlow.Coordinator` in the application that runs the matching service and in each application that links to it. The package requires the ASP.NET Core runtime. The engine itself has no gRPC dependency.

SQL Server and PostgreSQL deployments can run with zero, one or several coordinators. With zero, hosts claim directly from the store through polling, with PostgreSQL also supplying store notifications. Adding coordinators improves wake and await latency and reduces polling load. Hosts discover and connect to every live coordinator; each coordinator remains stateless, so adding a second requires no shared matching state or leader election.

Links require TLS and either mutual TLS or a shared bearer key. Every HTTPS endpoint also needs a configured coordinator identity: a leaf certificate pin, or a dedicated private CA together with coordinator certificate names. Ordinary public CA validation alone is never sufficient. This policy applies equally to discovered and static endpoints, and prevents a forged registry row or a reused address from receiving credentials. SQLite refuses either coordinator role at startup.

## Shared keys over TLS

Load certificates and keys from your application's secret store and keep the certificates alive until the application stops. Server and client certificates must include their private keys. Use server-authentication certificates with a subject alternative name matching each endpoint's host. The listener accepts TLS 1.2 or TLS 1.3.

<!-- snippet: samples/IxIFlow.Samples/Coordinators.cs#secure-service -->
```csharp
public static IHost CreateSecureCoordinator(string connectionString,
    X509Certificate2 certificate, string currentKey, string nextKey)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddIxIFlow().UsePostgreSql(connectionString)
        .AddCoordinator(options =>
        {
            options.ListenEndpoint = new Uri("https://0.0.0.0:5088");
            options.AdvertisedEndpoint = new Uri("https://coordinator.example.internal:5088");
            options.ServerCertificate = certificate;
            options.AuthenticationMode = CoordinatorAuthenticationMode.SharedKey;
            options.SharedKeys.Add(currentKey);
            options.SharedKeys.Add(nextKey);
        });
    return builder.Build();
}
```

Import `IxIFlow.Coordinator` and `System.Security.Cryptography.X509Certificates` in addition to the builder, hosting and dependency-injection namespaces used below. A wildcard TLS listener needs an `AdvertisedEndpoint` that hosts can reach.

<!-- snippet: samples/IxIFlow.Samples/Coordinators.cs#secure-links -->
```csharp
public static void LinkSecureHost(IServiceCollection services, string connectionString,
    string sharedKey, string coordinatorSha256Thumbprint)
{
    services.AddIxIFlow().UsePostgreSql(connectionString)
        .UseCoordinators(options =>
        {
            options.AuthenticationMode = CoordinatorAuthenticationMode.SharedKey;
            options.SharedKey = sharedKey;
            options.ServerCertificateTrust.Thumbprints.Add(coordinatorSha256Thumbprint);
            // Optional: the same identity policy protects discovered endpoints.
            options.Coordinators.Add(new Uri("https://coordinator.example.internal:5088"));
        });
}
```

The client sends `authorization: Bearer <key>` only after the TLS handshake proves the configured server identity. The server interceptor authenticates before reading protocol messages or admitting the host. Keys are compared using fixed-length SHA-256 digests and constant-time comparisons of both accepted slots. Generate random keys with at least 32 random bytes, encode them as Base64 or Base64url, and supply 32 to 512 characters using letters, digits, `-`, `_`, `+`, `/` or `=`.

For rotation, deploy the server with the current and next keys, restart clients with the next key, then remove the old key and restart the server. One or two keys are accepted; each client sends one. Options are startup configuration, so changing the secret store alone does not change an existing process. Restarting servers closes existing streams and requires new authentication.

## Mutual TLS

Set `AuthenticationMode` on both roles. Kestrel requires a trusted client certificate during the TLS handshake. Hosts authenticate the coordinator through the same mandatory identity policy used for shared keys.

<!-- snippet: samples/IxIFlow.Samples/Coordinators.cs#mutual-tls -->
```csharp
public static void ConfigureMutualTls(CoordinatorOptions server, CoordinatorClientOptions client,
    X509Certificate2 serverCertificate, X509Certificate2 clientCertificate,
    X509Certificate2 coordinatorCa, X509Certificate2 hostCa)
{
    server.AuthenticationMode = CoordinatorAuthenticationMode.MutualTls;
    server.ServerCertificate = serverCertificate;
    server.ClientCertificateTrust.CertificateAuthorities.Add(hostCa);
    client.AuthenticationMode = CoordinatorAuthenticationMode.MutualTls;
    client.ClientCertificate = clientCertificate;
    client.ServerCertificateTrust.CertificateAuthorities.Add(coordinatorCa);
    client.ServerCertificateNames.Add("coordinator.example.internal");
}
```

Apply these settings within the `AddCoordinator` and `UseCoordinators` configuration callbacks. Client certificates must allow client authentication; server certificates must allow server authentication. Use a dedicated private CA with controlled issuance. The server accepts clients issued by its configured host CA. The client requires both a chain to its configured coordinator CA and a SAN matching a configured `ServerCertificateNames` entry, in addition to the endpoint hostname. Registry contents cannot change these names. System public trust roots are never an alternative trust path. Certificate downloads and online revocation checks are disabled; use short-lived certificates and remove compromised pins or trust anchors, then restart affected processes to force reauthentication.

## Certificate pinning and rotation

`ServerCertificateTrust.Thumbprints` and `ClientCertificateTrust.Thumbprints` accept SHA-256 hashes of the complete leaf certificate: 64 hexadecimal characters without separators. Obtain the value with `certificate.GetCertHashString(HashAlgorithmName.SHA256)` from a certificate received through a trusted deployment channel. The usual SHA-1 `certificate.Thumbprint` value is not accepted. Use certificates and private keys dedicated to coordinator identities. A pin can identify a self-signed leaf, but validity dates, certificate purpose and the endpoint SAN are still checked.

You can configure several pins or private trust anchors during rotation. A matching leaf pin or a private CA chain with configured coordinator names authenticates a server. Add the new pin or CA before deploying the new certificate, replace certificates and restart the relevant processes, then remove old pins or CAs and restart again. Configure every intended coordinator identity when running several coordinators. A certificate for another service under the same CA fails unless it also has an explicitly configured coordinator name.

Security failures log the endpoint and a reason category, such as `server-identity`, `client-identity`, `shared-key` or `protocol`. They never log keys, certificates, metadata, or peer-supplied error details.

## Local development over loopback

Plaintext requires explicit `AllowInsecure = true` on both roles and is refused off loopback, including wildcard listeners. This is a local trust mode without credential authentication. Bearer keys and mutual TLS cannot be configured on a plaintext endpoint. The server also checks the actual remote IP. Run the following development examples on one machine.

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

`AddCoordinator` starts a dedicated HTTP/2 listener inside the application, including an ASP.NET Core application. It does not take over the application's HTTP routes. `AdvertisedEndpoint` overrides the address written to the registry and uses the same transport as the listener; a plaintext advertised endpoint must be loopback. A listen port of zero selects an available port and advertises the actual address, which is useful for tests. Configure an explicit port for normal use.

The matching service keeps connected host capabilities and free slots, subscriptions, and a bounded hold of unmatched hints. It removes a hint when it sends a wake, so a later notice for the same instance can wake work again immediately. It accesses only its own registry row and heartbeat. If the application also runs ordinary workflow hosted services, those services retain their normal store access independently of matching.

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

Load each host's workflow definitions before starting it, as described in [hosting](coordinator-and-hosts.md). Hosts publish their actual capabilities and current free slots. Every active demand must match; matching prefers the previous host, then the most free slots, then the host woken least recently. A wake starts one recovery claim pass. Hosts report capacity when a pass completes, including an empty pass. The store rechecks eligibility and decides ownership through its fenced lease. A dedicated termination claim requires a pending Terminate command and ignores placement; lease epochs and incarnation checks still apply because termination writes only saved state and runs no workflow code.

The link service discovers registry members every `DiscoveryInterval` (five seconds). It merges those addresses with the static `Coordinators` list and links to every endpoint. Stale registry entries are dropped; static entries remain candidates for reconnection. Failed links stop contributing to coordinated polling immediately and reconnect with exponential delays from `ReconnectMinDelay` (one second) up to `ReconnectMaxDelay` (30 seconds).

Writers send each committed change through one live link, round-robin, trying the next if its queue is full or the link has failed. Messages carry scheduling identifiers and demand keys and values. Workflow data, events, checkpoints and reply tokens never travel on these streams. Writer-side notification delivery reads the saved demand rows outside the committing transaction.

## Awaits, failure detection and polling

Await subscriptions go to every live link. The coordinator acknowledges installation before the awaiter reads the store. A change notice wakes the awaiter to reread; it never supplies a result. Subscriptions replay on reconnect, and their acknowledgements trigger another read to cover completions missed during the outage.

Both ends send application heartbeats every `LinkHeartbeatInterval` (five seconds), alongside HTTP/2 keepalive. `LinkTimeout` (15 seconds) bounds silence detection and subscription acknowledgement waits, with at most one additional heartbeat interval before the check runs. These timers use the application's monotonic `TimeProvider` timestamps. A protocol mismatch is rejected before Welcome. A TLS stream becomes usable only after server identity validation, client authentication and a compatible Welcome. Opening a socket or completing TLS alone never slows polling. Local plaintext development uses the explicit loopback trust policy.

While any usable link remains, recovery and awaits use `CoordinatedPollInterval` (ten seconds), and the store wake source is paused. On PostgreSQL this closes the LISTEN connection. When the last link disappears, recovery scans immediately, store listening resumes, and ordinary `PollInterval` recovery and one-second await reads return. Losing one of several links keeps coordinated polling in effect.

Polling always remains finite: a process can commit and crash before sending its notice. Local runnable timers continue to work. Coordinator restart loses only hints; rows and leases remain authoritative in the store.

If a woken host dies before claiming, the instance remains runnable and another host's poll can claim it. If a woken host and a polling host try together, the store grants one execution lease. Duplicate hints and concurrent scans use that same claim path. A wake never reserves work or bypasses its demands, runnable time or ownership checks.

`SendQueueCapacity` bounds each stream's outgoing queue (256 messages). Full queues drop hints; a control message that cannot be queued closes the link so subscriptions can be replayed safely. Post-commit notification delivery also retains the engine's bounded queue and timeout, so neither slow streams nor full queues delay a commit acknowledgement.

`NoticeCapacity` bounds the coordinator's notice cache (4,096), with `NoticeLifetime` of ten seconds. Duplicate claim hints within that window produce at most one successful wake. Unmatched hints are retried when a host reports capacity and are discarded on expiry. `MaxConnections` and `MaxAwaitedInstancesPerHost` bound host and subscription state. Overflow affects latency and is covered by polling.
