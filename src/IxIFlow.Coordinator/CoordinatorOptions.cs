using System.Net;

namespace IxIFlow.Coordinator;

public class CoordinatorLinkOptions
{
    /// <summary>Explicit trust of plaintext connections on loopback, for local development only.</summary>
    public bool AllowInsecure { get; set; }
    public TimeSpan LinkHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LinkTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int SendQueueCapacity { get; set; } = 256;

    internal void Validate()
    {
        if (LinkHeartbeatInterval <= TimeSpan.Zero || LinkTimeout < LinkHeartbeatInterval * 2 ||
            LinkTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Link timeout must be at least two heartbeat intervals and at most five minutes");
        if (SendQueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(SendQueueCapacity));
    }

    internal void ValidateEndpoint(Uri endpoint, bool listen = false)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Coordinator endpoints must be absolute origins");
        // Step 6 has no remote identity policy. Do not count an unauthenticated TLS peer as trusted.
        if (!AllowInsecure || endpoint.Scheme != "http" || !IsLoopback(endpoint.Host) ||
            (listen && !IPAddress.TryParse(endpoint.Host, out _)))
            throw new InvalidOperationException("Coordinator links currently require explicit AllowInsecure and an HTTP loopback endpoint. Remote authenticated transport requires link security support.");
    }

    internal static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
}

public sealed class CoordinatorOptions : CoordinatorLinkOptions
{
    public Uri ListenEndpoint { get; set; } = new("http://127.0.0.1:5088");
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
    public IList<Uri> Coordinators { get; } = new List<Uri>();
    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CoordinatedPollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    internal void ValidateClient()
    {
        Validate();
        foreach (var endpoint in Coordinators) ValidateEndpoint(endpoint);
        if (DiscoveryInterval <= TimeSpan.Zero || CoordinatedPollInterval <= TimeSpan.Zero ||
            CoordinatedPollInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Discovery and finite backstop polling intervals must be positive (backstop at most five minutes)");
        if (ReconnectMinDelay <= TimeSpan.Zero || ReconnectMaxDelay < ReconnectMinDelay ||
            ReconnectMaxDelay > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Reconnect delays must be positive and bounded by five minutes");
    }
}
