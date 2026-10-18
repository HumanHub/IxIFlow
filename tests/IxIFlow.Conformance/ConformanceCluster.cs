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
    private IWorkflowStateRepository? _repository;

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

    /// <summary>
    /// Starts another host on the shared store and returns its engine. <paramref name="wrap"/> puts
    /// faults between the host and its repository.
    /// </summary>
    public IWorkflowClient AddHost(Func<IWorkflowStateRepository, IWorkflowStateRepository>? wrap = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(provider =>
        {
            var repository = _store.NewRepository(provider.GetRequiredService<TimeProvider>());
            _repository ??= repository;
            return wrap?.Invoke(repository) ?? repository;
        }));
        services.Replace(ServiceDescriptor.Singleton(Registry));
        services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
        _configure(services);
        var host = services.BuildServiceProvider();
        try
        {
            host.GetRequiredService<IWorkflowStateRepository>();
            var engine = host.GetRequiredService<IWorkflowClient>();
            _hosts.Add(host);
            return engine;
        }
        catch
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>Runs one recovery scan on the first host, as its background service would.</summary>
    public async Task RecoverOnceAsync()
    {
        var host = _hosts[0];
        using var scanner = new WorkflowRecoveryService(host.GetRequiredService<IServiceScopeFactory>(),
            host.GetRequiredService<IWorkflowStateRepository>(), NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);
    }

    /// <summary>SQLite uses its sole owner's repository; other profiles can open another.</summary>
    public IWorkflowStateRepository NewRepository() => Profile == StoreProfiles.LocalSqlite
        ? _repository ??= _store.NewRepository() : _store.NewRepository();

    /// <summary>Reads an instance directly from the shared store.</summary>
    /// <summary>
    /// Waits until the instance is saved with <paramref name="status"/>: a child runs as an instance of
    /// its own, when a slot takes it (D-17).
    /// </summary>
    public async Task UntilAsync(string instanceId, WorkflowStatus status)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((await GetInstanceAsync(instanceId))?.Status != status)
            await Task.Delay(20, patience.Token);
    }

    public Task<WorkflowInstance?> GetInstanceAsync(string instanceId) =>
        NewRepository().GetWorkflowInstanceAsync(instanceId);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            // Drain admitted runs before deleting a file-backed profile.
            await host.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }
        await _store.DisposeAsync();
    }
}
