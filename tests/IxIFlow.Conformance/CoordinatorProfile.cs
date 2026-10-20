using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IxIFlow.Coordinator;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Tests.Conformance;

internal sealed partial class ProfileStore
{
    private readonly List<(ServiceProvider Provider, CoordinatorService Service)> _coordinators = [];
    private X509Certificate2? _certificate;
    private string? _sharedKey;

    internal int CoordinatorCount => _coordinators.Count;

    internal async Task StartCoordinatorsAsync(int count)
    {
        if (count == 0) return;
        _certificate = CreateCertificate();
        _sharedKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        for (var index = 0; index < count; index++)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow().AddCoordinator(options =>
            {
                options.ListenEndpoint = new("https://127.0.0.1:0");
                options.ServerCertificate = _certificate;
                options.SharedKeys.Add(_sharedKey);
            });
            services.AddSingleton(NewRepository());
            var provider = services.BuildServiceProvider();
            var service = provider.GetRequiredService<CoordinatorService>();
            try
            {
                await service.StartAsync(CancellationToken.None);
                _coordinators.Add((provider, service));
            }
            catch { await provider.DisposeAsync(); throw; }
        }
    }

    internal void ConfigureHost(IServiceCollection services)
    {
        if (CoordinatorCount == 0) return;
        new IxIFlowBuilder(services).UseCoordinators(options =>
        {
            options.SharedKey = _sharedKey;
            options.ServerCertificateTrust.Thumbprints.Add(_certificate!.GetCertHashString(HashAlgorithmName.SHA256));
            // Exercise registry discovery: no static endpoint list.
        });
        services.AddSingleton(provider => new ProfileLinks(provider.GetRequiredService<CoordinatorLinks>(), CoordinatorCount));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ProfileLinks>());
    }

    // Most fault scenarios control scans themselves. Start real authenticated links, without
    // introducing an uncontrolled recovery worker beside their injected crash points.
    internal Task StartHostLinksAsync(IServiceProvider provider) => CoordinatorCount == 0
        ? Task.CompletedTask : provider.GetRequiredService<ProfileLinks>().StartManuallyAsync();

    internal Task StopHostLinksAsync(IServiceProvider provider) => provider.GetService<ProfileLinks>()?.StopAsync(CancellationToken.None)
        ?? Task.CompletedTask;

    private async Task StopCoordinatorsAsync()
    {
        foreach (var (provider, service) in _coordinators)
        {
            await service.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
        _coordinators.Clear();
        _certificate?.Dispose();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=conformance-coordinator", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }

    private sealed class ProfileLinks(CoordinatorLinks links, int count) : IHostedService, IAsyncDisposable
    {
        private bool _started;
        private bool _stopped;

        internal async Task StartManuallyAsync()
        {
            if (!_started)
            {
                _started = true;
                await links.StartAsync(CancellationToken.None);
            }
            await WaitForConnectionsAsync(CancellationToken.None);
        }

        // In a generic host CoordinatorLinks is already started by its own hosted-service
        // registration. This service is only the authenticated-startup barrier and lifetime.
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _started = true;
            return WaitForConnectionsAsync(cancellationToken);
        }

        private async Task WaitForConnectionsAsync(CancellationToken cancellationToken)
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            patience.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                while (links.ConnectedCount != count)
                    await Task.Delay(10, patience.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Profile requires {count} authenticated coordinator links; {links.ConnectedCount} connected.");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!_started || _stopped) return;
            _stopped = true;
            await links.StopAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
    }
}
