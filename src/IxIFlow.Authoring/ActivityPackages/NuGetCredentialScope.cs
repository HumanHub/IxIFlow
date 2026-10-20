using System.Net;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Plugins;

namespace IxIFlow.Authoring;

/// <summary>Connects NuGet's standard discovered authentication plugins for one non-interactive resolution.</summary>
internal sealed class NuGetCredentialScope : ICredentialService, IDisposable
{
    // NuGet exposes the credential service globally. Serialize scopes and restore the owner's service.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly Lazy<ICredentialService>? _previous = HttpHandlerResourceV3.CredentialService;
    private readonly Action<Uri, ICredentials>? _previousSuccess = HttpHandlerResourceV3.CredentialsSuccessfullyUsed;
    private readonly List<PluginDiscoveryResult> _providers = [];
    private readonly Dictionary<Uri, ICredentials> _credentials = [];
    private readonly HashSet<Uri> _attempted = [];

    internal static async Task<NuGetCredentialScope> CreateAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token);
        var scope = new NuGetCredentialScope();
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
            ICredentials credentials = new NetworkCredential(response.Username, response.Password);
            if (response.AuthenticationTypes is { Count: > 0 })
            {
                var cache = new CredentialCache();
                foreach (var authenticationType in response.AuthenticationTypes)
                    cache.Add(uri, authenticationType, (NetworkCredential)credentials);
                credentials = cache;
            }
            return credentials;
        }
        return null;
    }

    public bool TryGetLastKnownGoodCredentialsFromCache(Uri uri, bool isProxy, out ICredentials credentials)
    {
        lock (_credentials) return _credentials.TryGetValue(uri, out credentials!);
    }

    public void Dispose()
    {
        HttpHandlerResourceV3.CredentialService = _previous;
        HttpHandlerResourceV3.CredentialsSuccessfullyUsed = _previousSuccess;
        lock (_credentials) { _credentials.Clear(); _attempted.Clear(); }
        _providers.Clear();
        Gate.Release();
    }
}
