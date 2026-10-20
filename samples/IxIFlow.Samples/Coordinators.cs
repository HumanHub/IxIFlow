using System.Security.Cryptography.X509Certificates;
using IxIFlow.Coordinator;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Samples;

public static class Coordinators
{
    // snippet: service
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
    // end-snippet: service

    // snippet: links
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
    // end-snippet: links

    // snippet: secure-service
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
    // end-snippet: secure-service

    // snippet: secure-links
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
    // end-snippet: secure-links

    // snippet: mutual-tls
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
    // end-snippet: mutual-tls
}
