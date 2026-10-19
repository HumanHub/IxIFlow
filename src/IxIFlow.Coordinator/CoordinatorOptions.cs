using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace IxIFlow.Coordinator;

public enum CoordinatorAuthenticationMode { SharedKey, MutualTls }

/// <summary>Explicit peer identities. Thumbprints are SHA-256 certificate hashes, in hexadecimal.</summary>
public sealed class CoordinatorCertificateTrust
{
    public IList<string> Thumbprints { get; } = new List<string>();
    /// <summary>Dedicated private trust anchors. The operating system's public roots are never used.</summary>
    public X509Certificate2Collection CertificateAuthorities { get; } = [];

    internal void Validate()
    {
        if (Thumbprints.Count == 0 && CertificateAuthorities.Count == 0)
            throw new InvalidOperationException("An explicit peer certificate identity is required");
        if (Thumbprints.Any(pin => pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
            throw new ArgumentException("Certificate thumbprints must be SHA-256 hexadecimal hashes");
        if (CertificateAuthorities.Cast<X509Certificate2>().Any(certificate =>
            !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority)))
            throw new ArgumentException("Trust anchors must be private CA certificates");
    }
}

public class CoordinatorLinkOptions
{
    /// <summary>Explicit trust of plaintext connections on loopback, for local development only.</summary>
    public bool AllowInsecure { get; set; }
    public CoordinatorAuthenticationMode AuthenticationMode { get; set; } = CoordinatorAuthenticationMode.SharedKey;
    public TimeSpan LinkHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LinkTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int SendQueueCapacity { get; set; } = 256;

    internal void Validate()
    {
        if (LinkHeartbeatInterval <= TimeSpan.Zero || LinkTimeout < LinkHeartbeatInterval * 2 ||
            LinkTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Link timeout must be at least two heartbeat intervals and at most five minutes");
        if (SendQueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(SendQueueCapacity));
        if (!Enum.IsDefined(AuthenticationMode)) throw new ArgumentException("Invalid coordinator authentication mode");
    }

    internal virtual void ValidateEndpoint(Uri endpoint, bool listen = false)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Coordinator endpoints must be absolute origins");
        if (endpoint.Scheme != "https" &&
            (!AllowInsecure || endpoint.Scheme != "http" || !IsLoopback(endpoint.Host)))
            throw new InvalidOperationException("TLS is required except for explicit AllowInsecure loopback links");
        if (listen && !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out _))
            throw new ArgumentException("Coordinator listeners require a numeric IP address");
        if (endpoint.Scheme == "http" && AuthenticationMode == CoordinatorAuthenticationMode.MutualTls)
            throw new InvalidOperationException("Mutual TLS requires an HTTPS endpoint");
    }

    internal static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
}

public sealed class CoordinatorOptions : CoordinatorLinkOptions
{
    public Uri ListenEndpoint { get; set; } = new("https://127.0.0.1:5088");
    public X509Certificate2? ServerCertificate { get; set; }
    public CoordinatorCertificateTrust ClientCertificateTrust { get; } = new();
    /// <summary>One or two bearer keys, to permit an overlap during rotation. Load from a secret store.</summary>
    public IList<string> SharedKeys { get; } = new List<string>();
    public Uri? AdvertisedEndpoint { get; set; }
    public string Name { get; set; } = "IxIFlow coordinator";
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan NoticeLifetime { get; set; } = TimeSpan.FromSeconds(10);
    public int NoticeCapacity { get; set; } = 4096;
    public int MaxConnections { get; set; } = 1024;
    public int MaxAwaitedInstancesPerHost { get; set; } = 4096;

    internal void ValidateService()
    {
        Validate();
        ValidateEndpoint(ListenEndpoint, listen: true);
        if (AdvertisedEndpoint != null) ValidateEndpoint(AdvertisedEndpoint);
        if (AdvertisedEndpoint != null && AdvertisedEndpoint.Scheme != ListenEndpoint.Scheme)
            throw new ArgumentException("Listen and advertised endpoints must use the same transport");
        if (ListenEndpoint.Scheme == "https")
        {
            if (ServerCertificate?.HasPrivateKey != true) throw new InvalidOperationException("A server certificate with a private key is required");
            if (AuthenticationMode == CoordinatorAuthenticationMode.MutualTls) ClientCertificateTrust.Validate();
            else if (SharedKeys.Count is < 1 or > 2 || SharedKeys.Any(key => !CoordinatorSecurity.ValidKey(key)))
                throw new ArgumentException("Configure one or two printable bearer keys of 32 to 512 characters");
        }
        else if (SharedKeys.Count != 0)
            throw new InvalidOperationException("Bearer credentials require TLS");
        if (ListenEndpoint.Port == 0 && AdvertisedEndpoint != null)
            throw new ArgumentException("An ephemeral listener cannot have an advertised endpoint");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200) throw new ArgumentException("Invalid coordinator name");
        Core.MemberRegistry.ValidateInterval(HeartbeatInterval);
        if (NoticeLifetime <= TimeSpan.Zero || NoticeLifetime > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(NoticeLifetime));
        if (NoticeCapacity < 1 || MaxConnections < 1 || MaxAwaitedInstancesPerHost < 1)
            throw new ArgumentOutOfRangeException(nameof(NoticeCapacity));
    }
}

public sealed class CoordinatorClientOptions : CoordinatorLinkOptions
{
    public X509Certificate2? ClientCertificate { get; set; }
    public CoordinatorCertificateTrust ServerCertificateTrust { get; } = new();
    /// <summary>Coordinator DNS identities required with private CA trust, independent of registry addresses.</summary>
    public IList<string> ServerCertificateNames { get; } = new List<string>();
    public string? SharedKey { get; set; }
    public IList<Uri> Coordinators { get; } = new List<Uri>();
    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CoordinatedPollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    internal override void ValidateEndpoint(Uri endpoint, bool listen = false)
    {
        base.ValidateEndpoint(endpoint, listen);
        if (endpoint.Scheme == "http" && SharedKey != null)
            throw new InvalidOperationException("Bearer credentials require TLS");
    }

    internal void ValidateClient()
    {
        Validate();
        // Discovery can add HTTPS endpoints even when the static list is empty.
        if (!AllowInsecure || Coordinators.Any(endpoint => endpoint.Scheme == "https") ||
            SharedKey != null || AuthenticationMode == CoordinatorAuthenticationMode.MutualTls)
        {
            ServerCertificateTrust.Validate();
            if (ServerCertificateTrust.CertificateAuthorities.Count != 0 &&
                (ServerCertificateNames.Count == 0 || ServerCertificateNames.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidOperationException("Private CA trust requires configured coordinator certificate names");
            if (AuthenticationMode == CoordinatorAuthenticationMode.MutualTls)
            {
                if (ClientCertificate?.HasPrivateKey != true) throw new InvalidOperationException("A client certificate with a private key is required");
            }
            else if (!CoordinatorSecurity.ValidKey(SharedKey))
                throw new ArgumentException("Configure a printable bearer key of 32 to 512 characters");
        }
        foreach (var endpoint in Coordinators) ValidateEndpoint(endpoint);
        if (DiscoveryInterval <= TimeSpan.Zero || CoordinatedPollInterval <= TimeSpan.Zero ||
            CoordinatedPollInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Discovery and finite backstop polling intervals must be positive (backstop at most five minutes)");
        if (ReconnectMinDelay <= TimeSpan.Zero || ReconnectMaxDelay < ReconnectMinDelay ||
            ReconnectMaxDelay > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Reconnect delays must be positive and bounded by five minutes");
    }
}
