using System.Net;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Plugins;
using NuGet.Protocol.Core.Types;

namespace IxIFlow.Authoring;

/// <summary>Connects NuGet's standard discovered authentication plugins for one non-interactive resolution.</summary>
internal sealed class NuGetCredentialScope : ICredentialService, IDisposable
{
    // NuGet exposes the credential service globally. Serialize scopes and restore the owner's service.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private Lazy<ICredentialService>? _previous;
    private Action<Uri, ICredentials>? _previousSuccess;
    private readonly List<PluginDiscoveryResult> _providers = [];
    private readonly List<HttpSource> _httpSources = [];
    private bool _ownsGate;
    private bool _disposed;
    internal List<SourceRepository> Repositories { get; } = [];

    private sealed class ScopedHttpSourceProvider(NuGetCredentialScope owner) : ResourceProvider(typeof(HttpSourceResource), nameof(ScopedHttpSourceProvider))
    {
        public override Task<Tuple<bool, INuGetResource>> TryCreate(SourceRepository source, CancellationToken token)
        {
            if (!source.PackageSource.IsHttp) return Task.FromResult(Tuple.Create(false, (INuGetResource)null!));
            // Do not call HttpSource.Create: the standard provider caches by source across resolutions.
            var http = new HttpSource(source.PackageSource, () => source.GetResourceAsync<HttpHandlerResource>(token), new NullThrottle());
            owner._httpSources.Add(http);
            return Task.FromResult(Tuple.Create(true, (INuGetResource)new HttpSourceResource(http)));
        }
    }
    private readonly Dictionary<Uri, ICredentials> _credentials = [];
    private readonly HashSet<Uri> _attempted = [];

    internal static async Task<NuGetCredentialScope> CreateAsync(IEnumerable<SourceRepository> repositories, CancellationToken token)
    {
        var scope = new NuGetCredentialScope();
        var sources = repositories.ToArray();
        scope.Repositories.AddRange(sources.Select(source => new SourceRepository(source.PackageSource,
            Repository.Provider.GetCoreV3().Where(provider => provider.Value.ResourceType != typeof(HttpSourceResource))
                .Prepend(new Lazy<INuGetResourceProvider>(() => new ScopedHttpSourceProvider(scope))))));
        if (!sources.Any(source => source.PackageSource.IsEnabled && source.PackageSource.IsHttp)) return scope;
        await Gate.WaitAsync(token);
        scope._ownsGate = true;
        scope._previous = HttpHandlerResourceV3.CredentialService;
        scope._previousSuccess = HttpHandlerResourceV3.CredentialsSuccessfullyUsed;
        try
        {
            var manager = PluginManager.Instance;
            // Discovery is cheap. A provider process starts only when a feed challenges,
            // never for local feeds, uploads, or anonymous requests.
            scope._providers.AddRange(await manager.FindAvailablePluginsAsync(token));
            HttpHandlerResourceV3.CredentialService = new(() => scope);
            HttpHandlerResourceV3.CredentialsSuccessfullyUsed = (uri, credentials) =>
            {
                lock (scope._credentials) scope._credentials[uri] = credentials;
            };
            return scope;
        }
        catch (OperationCanceledException) { scope.Dispose(); throw; }
        catch
        {
            scope.Dispose();
            throw new InvalidOperationException("NuGet credential providers could not be initialized");
        }
    }

    public bool HandlesDefaultCredentials => false;

    public async Task<ICredentials?> GetCredentialsAsync(Uri uri, IWebProxy proxy, CredentialRequestType type,
        string message, CancellationToken cancellationToken)
    {
        bool retry;
        lock (_credentials) retry = !_attempted.Add(uri);
        foreach (var provider in _providers)
        {
            var result = await PluginManager.Instance.TryGetSourceAgnosticPluginAsync(provider, OperationClaim.Authentication, cancellationToken);
            if (!result.Item1 || result.Item2.Plugin is not { } plugin) continue;
            var response = await plugin.Connection.SendRequestAndReceiveResponseAsync<GetAuthenticationCredentialsRequest, GetAuthenticationCredentialsResponse>(
                MessageMethod.GetAuthenticationCredentials, new(uri, retry, isNonInteractive: true, canShowDialog: false), cancellationToken);
            if (response.ResponseCode != MessageResponseCode.Success || !response.IsValid()) continue;
            ICredentials credentials = new AuthenticationTypeCredentials(new NetworkCredential(response.Username, response.Password), response.AuthenticationTypes);
            return credentials;
        }
        return null;
    }

    public bool TryGetLastKnownGoodCredentialsFromCache(Uri uri, bool isProxy, out ICredentials credentials)
    {
        lock (_credentials) return _credentials.TryGetValue(uri, out credentials!);
    }

    internal sealed class AuthenticationTypeCredentials(NetworkCredential credential, IEnumerable<string>? types) : ICredentials
    {
        private readonly HashSet<string> _types = new(types ?? [], StringComparer.OrdinalIgnoreCase);
        public NetworkCredential? GetCredential(Uri uri, string authType) => _types.Count == 0 || _types.Contains(authType) ? credential : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Destroy all handlers that captured this service before restoring the global service.
        foreach (var source in _httpSources) source.Dispose();
        _httpSources.Clear(); Repositories.Clear();
        if (_ownsGate)
        {
            HttpHandlerResourceV3.CredentialService = _previous;
            HttpHandlerResourceV3.CredentialsSuccessfullyUsed = _previousSuccess;
        }
        lock (_credentials) { _credentials.Clear(); _attempted.Clear(); }
        _providers.Clear();
        if (_ownsGate) Gate.Release();
    }
}
