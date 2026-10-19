using System.Net;
using Grpc.AspNetCore.Server.Model;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Coordinator;

// Hosting uses only the member registry methods; the matcher has no store reference.
internal sealed class CoordinatorService(CoordinatorOptions options, CoordinatorIdentity identity,
    CoordinatorMatcher matcher, IWorkflowStateRepository store, TimeProvider time,
    ILogger<CoordinatorService> logger) : BackgroundService
{
    private WebApplication? _application;
    internal Uri? Endpoint { get; private set; }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        CoordinatorBuilderExtensions.RefuseSqlite(store);
        options.ValidateService();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(CoordinatorService).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(Math.Max(1, options.LinkHeartbeatInterval.TotalSeconds));
            server.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(Math.Max(1, options.LinkTimeout.TotalSeconds));
            server.Listen(IPAddress.Parse(options.ListenEndpoint.Host.Trim('[', ']')), options.ListenEndpoint.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                if (options.ListenEndpoint.Scheme == "https") listen.UseHttps(https =>
                {
                    https.ServerCertificate = options.ServerCertificate;
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                    https.OnAuthenticate = (_, ssl) =>
                    {
                        ssl.AllowTlsResume = false;
                        ssl.CertificateChainPolicy = new System.Security.Cryptography.X509Certificates.X509ChainPolicy
                        {
                            DisableCertificateDownloads = true,
                            RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                        };
                    };
                    if (options.AuthenticationMode == CoordinatorAuthenticationMode.MutualTls)
                    {
                        https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                        https.ClientCertificateValidation = (certificate, chain, _) =>
                        {
                            var accepted = CoordinatorSecurity.ValidateCertificate(certificate, chain, options.ClientCertificateTrust, server: false);
                            if (!accepted) logger.LogWarning("Coordinator link to {Endpoint} refused: {ReasonCategory}",
                                CoordinatorSecurity.EndpointName(options.ListenEndpoint), "client-identity");
                            return accepted;
                        };
                    }
                });
            });
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(matcher);
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(new CoordinatorAuthenticationInterceptor(options, logger));
        builder.Services.AddGrpc(grpc =>
        {
            grpc.MaxReceiveMessageSize = LinkProtocol.MaxMessageBytes;
            grpc.MaxSendMessageSize = LinkProtocol.MaxMessageBytes;
            grpc.Interceptors.Add<CoordinatorAuthenticationInterceptor>();
        });
        builder.Services.AddSingleton<IServiceMethodProvider<CoordinatorRpcService>, CoordinatorMethods>();
        _application = builder.Build();
        _application.MapGrpcService<CoordinatorRpcService>();
        try
        {
            await _application.StartAsync(cancellationToken);
            Endpoint = options.AdvertisedEndpoint ?? new Uri(_application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            await RegisterAsync(cancellationToken);
            await base.StartAsync(cancellationToken);
        }
        catch { await _application.DisposeAsync(); _application = null; throw; }
    }

    private Task RegisterAsync(CancellationToken token) => store.RegisterMemberAsync(new(identity.Id, MemberKind.Coordinator,
        options.Name, Endpoint!.AbsoluteUri, 0, typeof(CoordinatorService).Assembly.GetName().Version!.ToString(),
        options.HeartbeatInterval, new Dictionary<string, string>()), token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try
                {
                    matcher.ExpireNotices();
                    if (!await store.HeartbeatAsync(identity.Id, new Dictionary<string, string>(), stoppingToken))
                        await RegisterAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning(error, "Could not heartbeat coordinator {CoordinatorId}", identity.Id); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Abort long-lived streams before Kestrel's graceful shutdown wait.
        matcher.DisconnectAll();
        if (_application != null) { await _application.StopAsync(cancellationToken); await _application.DisposeAsync(); _application = null; }
        await store.UnregisterMemberAsync(identity.Id, cancellationToken);
    }
    public override void Dispose()
    {
        matcher.DisconnectAll();
        if (_application != null) _application.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }
}
