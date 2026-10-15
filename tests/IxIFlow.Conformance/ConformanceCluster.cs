using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// Hosts that share one profile store and one definition registry, as separate processes would.
/// Each host has its own repository instance and in-process gate, so races between hosts are
/// decided by the store, not by an in-process lock.
/// </summary>
internal sealed class ConformanceCluster : IAsyncDisposable
{
    private readonly ProfileStore _store;
    private readonly Action<IServiceCollection> _configure;
    private readonly List<ServiceProvider> _hosts = [];

    private ConformanceCluster(ProfileStore store, Action<IServiceCollection> configure)
    {
        _store = store;
        _configure = configure;
    }

    public IWorkflowVersionRegistry Registry { get; } = new WorkflowVersionRegistry();

    public string Profile => _store.Profile;

    public static async Task<ConformanceCluster> CreateAsync(string profile,
        Action<IServiceCollection>? configure = null) =>
        new(await StoreProfiles.CreateAsync(profile), configure ?? (_ => { }));

    /// <summary>Starts another host on the shared store and returns its engine.</summary>
    public IWorkflowClient AddHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton(_store.NewRepository()));
        services.Replace(ServiceDescriptor.Singleton(Registry));
        services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
        _configure(services);
        var host = services.BuildServiceProvider();
        _hosts.Add(host);
        return host.GetRequiredService<IWorkflowClient>();
    }

    /// <summary>Runs one recovery scan on the first host, as its background service would.</summary>
    public async Task RecoverOnceAsync()
    {
        var host = _hosts[0];
        using var scanner = new WorkflowRecoveryService(host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<IWorkflowStateRepository>(), NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);
    }

    /// <summary>Reads an instance directly from the shared store.</summary>
    public Task<WorkflowInstance?> GetInstanceAsync(string instanceId) =>
        _store.NewRepository().GetWorkflowInstanceAsync(instanceId);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        await _store.DisposeAsync();
    }
}
