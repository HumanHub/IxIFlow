using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Grpc.Net.Client;
using IxIFlow.Coordinator;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
[Trait("Spec", "C-17")]
public sealed class CoordinatorSecurityTests
{
    private const string CurrentKey = "current-coordinator-key-0123456789abcdef";
    private const string NextKey = "next-coordinator-key-0123456789abcdef0123";
    private const string WrongKey = "wrong-coordinator-key-0123456789abcdef";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutualTlsAcceptsTrustedClientsWithPinsOrPrivateCa(bool pins)
    {
        using var pki = new Certificates();
        var logs = new Logs();
        await using var server = await Server.StartAsync(pki.Server, logs, options =>
        {
            options.AuthenticationMode = CoordinatorAuthenticationMode.MutualTls;
            if (pins) options.ClientCertificateTrust.Thumbprints.Add(Pin(pki.Client));
            else options.ClientCertificateTrust.CertificateAuthorities.Add(pki.Root);
        });
        await using var client = await Client.StartAsync(server.Store, logs, options =>
        {
            options.Coordinators.Add(server.Endpoint);
            options.AuthenticationMode = CoordinatorAuthenticationMode.MutualTls;
            options.ClientCertificate = pki.Client;
            if (pins) options.ServerCertificateTrust.Thumbprints.Add(Pin(pki.Server));
            else
            {
                options.ServerCertificateTrust.CertificateAuthorities.Add(pki.Root);
                options.ServerCertificateNames.Add("coordinator.internal");
            }
        });
        await EventuallyAsync(() => client.State.Connected);
        Assert.Equal(1, client.Links.ConnectedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutualTlsRefusesMissingOrUntrustedClientCertificate(bool untrusted)
    {
        using var pki = new Certificates();
        using var outsider = new Certificates();
        var logs = new Logs();
        await using var server = await Server.StartAsync(pki.Server, logs, options =>
        {
            options.AuthenticationMode = CoordinatorAuthenticationMode.MutualTls;
            options.ClientCertificateTrust.CertificateAuthorities.Add(pki.Root);
        });
        var options = KeyClient(pki.Server);
        options.AuthenticationMode = CoordinatorAuthenticationMode.MutualTls;
        options.ClientCertificate = untrusted ? outsider.Client : null;
        await Assert.ThrowsAnyAsync<Exception>(() => WelcomeAsync(server.Endpoint, options));
    }

    [Theory]
    [InlineData(CurrentKey, true)]
    [InlineData(NextKey, true)]
    [InlineData(WrongKey, false)]
    [InlineData(null, false)]
    public async Task SharedKeyInterceptorAcceptsBothRotationKeysAndRejectsOthers(string? key, bool accepted)
    {
        using var pki = new Certificates();
        var logs = new Logs();
        await using var server = await KeyServerAsync(pki, logs);
        var options = KeyClient(pki.Server);
        if (accepted)
            Assert.Equal(CoordinatorMessageKind.Welcome, (await WelcomeAsync(server.Endpoint, options, key)).Kind);
        else
        {
            var error = await Assert.ThrowsAsync<RpcException>(() => WelcomeAsync(server.Endpoint, options, key));
            Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        }
    }

    [Fact]
    public async Task AuthenticationFailureNeverEntersCoordinatedPollingAndAuthenticatedWelcomeDoes()
    {
        using var pki = new Certificates();
        var logs = new Logs();
        await using var server = await KeyServerAsync(pki, logs);
        await using var denied = await Client.StartAsync(server.Store, logs, options =>
        {
            options.Coordinators.Add(server.Endpoint);
            ConfigureKeyClient(options, pki.Server, WrongKey);
        });
        await EventuallyAsync(() => logs.Entries.Any(entry => entry.Contains("shared-key")));
        Assert.False(denied.State.Connected);
        Assert.False(denied.EverConnected);
        Assert.Equal(0, denied.Links.ConnectedCount);
        await using var accepted = await Client.StartAsync(server.Store, logs, options =>
        {
            options.Coordinators.Add(server.Endpoint);
            ConfigureKeyClient(options, pki.Server, NextKey);
        });
        await EventuallyAsync(() => accepted.State.Connected);
        Assert.True(accepted.EverConnected);
    }

    [Fact]
    public async Task AuthenticatedTlsWithIncompatibleProtocolNeverEntersCoordinatedPolling()
    {
        using var pki = new Certificates();
        var logs = new Logs();
        await using var peer = await ScriptedServer.StartAsync(pki.Server, incompatible: true);
        await using var client = await Client.StartAsync(new InMemoryWorkflowStateRepository(), logs, options =>
        {
            options.Coordinators.Add(peer.Endpoint);
            ConfigureKeyClient(options, pki.Server);
        });
        await EventuallyAsync(() => logs.Entries.Any(entry => entry.Contains("protocol")));
        Assert.Equal(1, peer.Rpc.Requests);
        Assert.Equal("Bearer " + CurrentKey, peer.Rpc.Authorization);
        Assert.False(client.EverConnected);
        Assert.False(client.State.Connected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WrongEndpointIdentityIsRefusedBeforeAnyBearerMetadata(bool discover, bool privateCa)
    {
        using var pki = new Certificates();
        // This is an otherwise valid HTTPS certificate, with the same issuer and endpoint SAN.
        // The locally generated root models public trust without changing the OS store or using a network CA.
        using var attacker = Certificates.Leaf(pki.Root, "another-service.internal", server: true);
        using (var ordinaryTrust = new X509Chain())
        {
            ordinaryTrust.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            ordinaryTrust.ChainPolicy.CustomTrustStore.Add(pki.Root);
            ordinaryTrust.ChainPolicy.DisableCertificateDownloads = true;
            ordinaryTrust.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.True(ordinaryTrust.Build(attacker));
            Assert.True(attacker.MatchesHostname("127.0.0.1", allowCommonName: false));
        }
        var logs = new Logs();
        await using var peer = await ScriptedServer.StartAsync(attacker);
        var store = new InMemoryWorkflowStateRepository();
        if (discover) await store.RegisterMemberAsync(new(Guid.NewGuid(), MemberKind.Coordinator,
            "forged", peer.Endpoint.AbsoluteUri, 0, "1", TimeSpan.FromSeconds(5), new Dictionary<string, string>()));
        await using var client = await Client.StartAsync(store, logs, options =>
        {
            if (!discover) options.Coordinators.Add(peer.Endpoint);
            options.SharedKey = CurrentKey;
            if (privateCa)
            {
                options.ServerCertificateTrust.CertificateAuthorities.Add(pki.Root);
                options.ServerCertificateNames.Add("coordinator.internal");
            }
            else options.ServerCertificateTrust.Thumbprints.Add(Pin(pki.Server));
        });
        await EventuallyAsync(() => logs.Entries.Any(entry => entry.Contains("server-identity")));
        Assert.Equal(0, peer.Rpc.Requests);
        Assert.Null(peer.Rpc.Authorization);
        Assert.False(client.EverConnected);
        Assert.Contains(logs.Entries, entry => entry.Contains(peer.Endpoint.Port.ToString()));
    }

    [Fact]
    public async Task SecurityFailuresLogOnlyEndpointAndReasonCategory()
    {
        using var pki = new Certificates();
        var logs = new Logs();
        await using var server = await KeyServerAsync(pki, logs);
        await using (var client = await Client.StartAsync(server.Store, logs, options =>
        {
            options.Coordinators.Add(server.Endpoint);
            ConfigureKeyClient(options, pki.Server, WrongKey);
        })) await EventuallyAsync(() => logs.Entries.Any(entry => entry.Contains("shared-key")));
        // A hostile peer can put secrets in its gRPC status details. Those must not be logged either.
        await using var peer = await ScriptedServer.StartAsync(pki.Server, error: WrongKey);
        var previousEntries = logs.Entries.Count;
        await using (var client = await Client.StartAsync(new InMemoryWorkflowStateRepository(), logs, options =>
        {
            options.Coordinators.Add(peer.Endpoint);
            ConfigureKeyClient(options, pki.Server);
        })) await EventuallyAsync(() => peer.Rpc.Requests > 0 &&
            logs.Entries.Skip(previousEntries).Any(entry => entry.Contains("authentication")));
        var output = string.Join('\n', logs.Entries);
        foreach (var secret in new[] { CurrentKey, NextKey, WrongKey, Pin(pki.Server), Pin(pki.Client),
            Convert.ToBase64String(pki.Server.RawData), pki.Server.Subject }) Assert.DoesNotContain(secret, output);
        Assert.Contains("https://127.0.0.1:", output);
        Assert.DoesNotContain("System.", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlaintextOffLoopbackIsRefusedForBothRoles(bool server)
    {
        if (server)
            Assert.Throws<InvalidOperationException>(() => new CoordinatorOptions
            { AllowInsecure = true, ListenEndpoint = new("http://0.0.0.0:5088") }.ValidateService());
        else
        {
            var options = new CoordinatorClientOptions { AllowInsecure = true };
            options.Coordinators.Add(new("http://192.0.2.1:5088"));
            Assert.Throws<InvalidOperationException>(options.ValidateClient);
        }
    }

    [Fact]
    public void TlsRequiresAnExplicitIdentityAndPrivateCaRequiresConfiguredNames()
    {
        using var pki = new Certificates();
        var options = new CoordinatorClientOptions { SharedKey = CurrentKey };
        options.Coordinators.Add(new("https://localhost:5088"));
        Assert.Throws<InvalidOperationException>(options.ValidateClient);
        options.ServerCertificateTrust.CertificateAuthorities.Add(pki.Root);
        Assert.Throws<InvalidOperationException>(options.ValidateClient);
        options.ServerCertificateNames.Add("coordinator.internal");
        options.ValidateClient();
    }

    [Fact]
    public void BearerKeysCannotBeConfiguredOnPlaintextAndMoreThanTwoKeysAreRefused()
    {
        using var pki = new Certificates();
        var options = KeyClient(pki.Server);
        options.AllowInsecure = true;
        options.Coordinators.Add(new("http://127.0.0.1:5088"));
        Assert.Throws<InvalidOperationException>(options.ValidateClient);
        Assert.Throws<InvalidOperationException>(() => options.ValidateEndpoint(new("http://127.0.0.1:5088")));
        var service = new CoordinatorOptions { ServerCertificate = pki.Server };
        service.SharedKeys.Add(CurrentKey); service.SharedKeys.Add(NextKey); service.SharedKeys.Add(WrongKey);
        Assert.Throws<ArgumentException>(service.ValidateService);
    }

    [Fact]
    public void PinsDoNotAcceptExpiredWrongPurposeOrWrongHostnameCertificates()
    {
        using var pki = new Certificates();
        using var expired = Certificates.Leaf(pki.Root, "coordinator.internal", true, expired: true);
        foreach (var certificate in new[] { expired, pki.Client })
        {
            var trust = new CoordinatorCertificateTrust(); trust.Thumbprints.Add(Pin(certificate));
            Assert.False(CoordinatorSecurity.ValidateCertificate(certificate, null, trust, server: true, new("https://127.0.0.1")));
        }
        var pinned = new CoordinatorCertificateTrust(); pinned.Thumbprints.Add(Pin(pki.Server));
        Assert.False(CoordinatorSecurity.ValidateCertificate(pki.Server, null, pinned, server: true, new("https://wrong.internal")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CertificatePoliciesAcceptOnlyTheConfiguredPeerIdentity(bool server, bool pins)
    {
        using var pki = new Certificates();
        using var outsider = new Certificates();
        var trust = new CoordinatorCertificateTrust();
        var certificate = server ? pki.Server : pki.Client;
        if (pins) trust.Thumbprints.Add(Pin(certificate));
        else trust.CertificateAuthorities.Add(pki.Root);
        Assert.True(CoordinatorSecurity.ValidateCertificate(certificate, null, trust, server,
            new("https://127.0.0.1"), ["coordinator.internal"]));
        Assert.False(CoordinatorSecurity.ValidateCertificate(server ? outsider.Server : outsider.Client, null, trust, server,
            new("https://127.0.0.1"), ["coordinator.internal"]));
        if (server && !pins)
        {
            using var differentService = Certificates.Leaf(pki.Root, "another-service.internal", true);
            Assert.False(CoordinatorSecurity.ValidateCertificate(differentService, null, trust, true,
                new("https://127.0.0.1"), ["coordinator.internal"]));
        }
    }

    private static string Pin(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);
    private static CoordinatorClientOptions KeyClient(X509Certificate2 server)
    {
        var options = new CoordinatorClientOptions(); ConfigureKeyClient(options, server); return options;
    }
    private static void ConfigureKeyClient(CoordinatorClientOptions options, X509Certificate2 server, string key = CurrentKey)
    { options.SharedKey = key; options.ServerCertificateTrust.Thumbprints.Add(Pin(server)); }
    private static Task<Server> KeyServerAsync(Certificates pki, Logs logs) => Server.StartAsync(pki.Server, logs, options =>
    { options.SharedKeys.Add(CurrentKey); options.SharedKeys.Add(NextKey); });
    private static async Task<CoordinatorMessage> WelcomeAsync(Uri endpoint, CoordinatorClientOptions options, string? key = null)
    {
        using var handler = new SocketsHttpHandler
        { UseProxy = false, AllowAutoRedirect = false, SslOptions = CoordinatorSecurity.ClientTls(options, endpoint, _ => { }) };
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler });
        var headers = new Metadata(); if (key != null) headers.Add("authorization", "Bearer " + key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(LinkProtocol.Link, null,
            new(headers: headers, cancellationToken: timeout.Token));
        await call.RequestStream.WriteAsync(new(HostMessageKind.Hello,
            new(Guid.NewGuid(), "security-test", new(), 1, LinkProtocol.Version)), timeout.Token);
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        return call.ResponseStream.Current;
    }
    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "Condition did not become true"); await Task.Delay(10); }
    }

    private sealed class Certificates : IDisposable
    {
        internal X509Certificate2 Root { get; }
        internal X509Certificate2 Server { get; }
        internal X509Certificate2 Client { get; }
        internal Certificates()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Test private root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
            Server = Leaf(Root, "coordinator.internal", true);
            Client = Leaf(Root, "workflow-host.internal", false);
        }
        internal static X509Certificate2 Leaf(X509Certificate2 issuer, string name, bool server, bool expired = false)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(name); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddHours(expired ? -1 : 24), RandomNumberGenerator.GetBytes(16));
            using var withKey = issued.CopyWithPrivateKey(key);
            // Schannel requires a persisted key. Machine scope also works in runners without a loaded user profile.
            // The import is temporary (no PersistKeySet); disposal removes the key, and no trust store is changed.
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.EphemeralKeySet);
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); Root.Dispose(); }
    }
    private sealed class Logs : ILoggerProvider, ILogger
    {
        internal ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(formatter(state, error) + (error == null ? "" : " " + error));
        public void Dispose() { }
    }
    private sealed class Server(ServiceProvider provider, CoordinatorService service, InMemoryWorkflowStateRepository store) : IAsyncDisposable
    {
        internal Uri Endpoint => service.Endpoint!;
        internal InMemoryWorkflowStateRepository Store => store;
        internal CoordinatorMatcher Matcher => provider.GetRequiredService<CoordinatorMatcher>();
        internal static async Task<Server> StartAsync(X509Certificate2 certificate, Logs logs, Action<CoordinatorOptions> configure)
        {
            var store = new InMemoryWorkflowStateRepository();
            var services = new ServiceCollection().AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            services.AddSingleton<IWorkflowStateRepository>(store);
            services.AddIxIFlow().AddCoordinator(options =>
            { options.ListenEndpoint = new("https://127.0.0.1:0"); options.ServerCertificate = certificate; configure(options); });
            var provider = services.BuildServiceProvider(); var service = provider.GetRequiredService<CoordinatorService>();
            try { await service.StartAsync(CancellationToken.None); return new(provider, service, store); }
            catch { await provider.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await service.StopAsync(CancellationToken.None); await provider.DisposeAsync(); }
    }
    private sealed class Client(ServiceProvider provider, CoordinatorLinks links, CoordinationState state) : IAsyncDisposable
    {
        internal CoordinationState State => state;
        internal CoordinatorLinks Links => links;
        internal bool EverConnected;
        internal static async Task<Client> StartAsync(IWorkflowStateRepository store, Logs logs, Action<CoordinatorClientOptions> configure)
        {
            var services = new ServiceCollection().AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
            services.AddSingleton(store);
            services.AddIxIFlow().UseCoordinators(configure);
            var provider = services.BuildServiceProvider();
            var links = provider.GetRequiredService<CoordinatorLinks>(); var state = provider.GetRequiredService<CoordinationState>();
            var client = new Client(provider, links, state);
            state.Wake += () => { if (state.Connected) client.EverConnected = true; };
            try { await links.StartAsync(CancellationToken.None); return client; }
            catch { await provider.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await links.StopAsync(CancellationToken.None); await provider.DisposeAsync(); }
    }
    private sealed class ScriptedRpc(bool incompatible, string? error)
    {
        internal int Requests;
        internal string? Authorization;
        internal async Task Link(IAsyncStreamReader<HostMessage> input, IServerStreamWriter<CoordinatorMessage> output, ServerCallContext context)
        {
            Interlocked.Increment(ref Requests);
            Authorization = context.RequestHeaders.FirstOrDefault(entry => entry.Key == "authorization")?.Value;
            if (error != null) throw new RpcException(new(StatusCode.Unauthenticated, error));
            await input.MoveNext(context.CancellationToken);
            await output.WriteAsync(new(CoordinatorMessageKind.Welcome, Guid.NewGuid(),
                incompatible ? LinkProtocol.Version + 1 : LinkProtocol.Version));
            while (await input.MoveNext(context.CancellationToken)) { }
        }
    }
    private sealed class ScriptedMethods : IServiceMethodProvider<ScriptedRpc>
    {
        public void OnServiceMethodDiscovery(ServiceMethodProviderContext<ScriptedRpc> context) =>
            context.AddDuplexStreamingMethod(LinkProtocol.Link, [], (service, input, output, call) => service.Link(input, output, call));
    }
    private sealed class ScriptedServer(WebApplication application, Uri endpoint, ScriptedRpc rpc) : IAsyncDisposable
    {
        internal Uri Endpoint => endpoint;
        internal ScriptedRpc Rpc => rpc;
        internal static async Task<ScriptedServer> StartAsync(X509Certificate2 certificate, bool incompatible = false, string? error = null)
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen =>
            { listen.Protocols = HttpProtocols.Http2; listen.UseHttps(certificate); }));
            var rpc = new ScriptedRpc(incompatible, error);
            builder.Services.AddSingleton(rpc); builder.Services.AddGrpc();
            builder.Services.AddSingleton<IServiceMethodProvider<ScriptedRpc>, ScriptedMethods>();
            var app = builder.Build(); app.MapGrpcService<ScriptedRpc>(); await app.StartAsync();
            return new(app, new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), rpc);
        }
        public async ValueTask DisposeAsync() { await application.StopAsync(); await application.DisposeAsync(); }
    }
}
