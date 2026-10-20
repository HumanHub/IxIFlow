using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Coordinator;

internal static class CoordinatorSecurity
{
    internal static bool ValidKey(string? key) => key is { Length: >= 32 and <= 512 } &&
        key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '+' or '/' or '=');

    internal static string EndpointName(Uri endpoint) => $"{endpoint.Scheme}://{endpoint.IdnHost}:{endpoint.Port}";

    internal static bool ValidateCertificate(X509Certificate2? certificate, X509Chain? suppliedChain,
        CoordinatorCertificateTrust trust, bool server, Uri? endpoint = null, IList<string>? names = null)
    {
        if (certificate == null || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
            DateTime.UtcNow > certificate.NotAfter.ToUniversalTime()) return false;
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)) return false;
        var purpose = server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2";
        if (certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension =>
            !extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == purpose))) return false;
        if (server && (endpoint == null || !certificate.MatchesHostname(endpoint.IdnHost, allowWildcards: false, allowCommonName: false))) return false;

        // A pin identifies the leaf itself, including a self-signed leaf. No public-root fallback.
        var hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (trust.Thumbprints.Any(pin => hash.Equals(pin, StringComparison.OrdinalIgnoreCase))) return true;
        if (trust.CertificateAuthorities.Count == 0) return false;
        // Names are configured by the operator, never copied from a discovered row.
        if (server && (names == null || !names.Any(name => certificate.MatchesHostname(name,
            allowWildcards: false, allowCommonName: false)))) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trust.CertificateAuthorities);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(purpose));
        if (suppliedChain != null)
            foreach (var element in suppliedChain.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return chain.Build(certificate);
    }

    internal static SslClientAuthenticationOptions ClientTls(CoordinatorClientOptions options, Uri endpoint,
        Action<bool> validated) => new()
    {
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        AllowRenegotiation = false,
        AllowTlsResume = false,
        CertificateChainPolicy = new X509ChainPolicy
        {
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true
        },
        ClientCertificates = options.AuthenticationMode == CoordinatorAuthenticationMode.MutualTls && options.ClientCertificate != null
            ? new X509CertificateCollection { options.ClientCertificate } : null,
        LocalCertificateSelectionCallback = options.AuthenticationMode == CoordinatorAuthenticationMode.MutualTls
            ? (_, _, _, _, _) => options.ClientCertificate : null,
        RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
        {
            var accepted = certificate is X509Certificate2 peer && ValidateCertificate(peer, chain,
                options.ServerCertificateTrust, server: true, endpoint, options.ServerCertificateNames);
            validated(accepted);
            return accepted;
        }
    };
}

// Authentication runs before Hello is read, the matcher is touched, or Welcome is sent.
internal sealed class CoordinatorAuthenticationInterceptor(CoordinatorOptions options, ILogger logger) : Interceptor
{
    private readonly byte[][] _keys = options.SharedKeys.Select(key => SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToArray();

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var http = context.GetHttpContext();
        string? failure = null;
        if (!http.Request.IsHttps)
        {
            if (!options.AllowInsecure || options.AuthenticationMode == CoordinatorAuthenticationMode.MutualTls ||
                http.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) failure = "transport";
        }
        else if (options.AuthenticationMode == CoordinatorAuthenticationMode.MutualTls)
        {
            // Kestrel requires and validates the whole client chain during the TLS handshake.
            if (http.Connection.ClientCertificate == null) failure = "client-identity";
        }
        else
        {
            var headers = context.RequestHeaders.Where(entry => entry.Key == "authorization").ToArray();
            var value = headers.Length == 1 && !headers[0].IsBinary ? headers[0].Value : "";
            var candidate = value.StartsWith("Bearer ", StringComparison.Ordinal) ? value[7..] : "";
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
            // Fixed-size digests, no early exit, and both rotation slots checked every time.
            var first = CryptographicOperations.FixedTimeEquals(digest, _keys.Length > 0 ? _keys[0] : new byte[32]);
            var second = CryptographicOperations.FixedTimeEquals(digest, _keys.Length > 1 ? _keys[1] : new byte[32]);
            if (!(first | second) || _keys.Length == 0 || !CoordinatorSecurity.ValidKey(candidate)) failure = "shared-key";
        }
        if (failure != null)
        {
            logger.LogWarning("Coordinator link to {Endpoint} refused: {ReasonCategory}",
                CoordinatorSecurity.EndpointName(options.AdvertisedEndpoint ?? options.ListenEndpoint), failure);
            throw new RpcException(new(StatusCode.Unauthenticated, "Coordinator authentication failed"));
        }
        await continuation(requestStream, responseStream, context);
    }
}
