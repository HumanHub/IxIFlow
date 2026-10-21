using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>Publishes this process and a fresh capability snapshot for as long as its host runs.</summary>
internal sealed class WorkflowHeartbeatService(IWorkflowStateRepository repository, IWorkflowCatalog registry,
    HostSettings settings, TimeProvider time, ILogger<WorkflowHeartbeatService> logger, ILogger<SensitiveDiagnostics>? sensitiveDiagnostics = null,
    InspectionRedactionPolicy? inspectionPolicy = null) : BackgroundService
{
    private IReadOnlyDictionary<string, string> _registeredCapabilities = null!;

    internal static bool IsAutomaticCapability(string key) =>
        key is "os" or "arch" or "runtime" or "host" or "bundle-host" || key.StartsWith("workflow:", StringComparison.Ordinal);

    internal static Task<IReadOnlyDictionary<string, string>> CapabilitiesAsync(IWorkflowCatalog registry, HostSettings settings) =>
        settings.RefreshCapabilitiesAsync(registry);

    internal static Dictionary<string, string> BaseCapabilities(HostSettings settings) =>
        new(settings.Capabilities, StringComparer.Ordinal)
        {
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux"
                : OperatingSystem.IsMacOS() ? "macos" : RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["host"] = settings.Id.ToString("D")
        };

    internal sealed class CompatibilityMemo
    {
        private readonly ConcurrentDictionary<string, Lazy<bool>> _entries = new(StringComparer.Ordinal);
        private int _unusableProfileWarned;

        // Without a profile this host can load no bundle, so it must not claim published work.
        internal void WarnUnusableProfile(ILogger? logger)
        {
            if (Interlocked.Exchange(ref _unusableProfileWarned, 1) == 0)
                logger?.LogWarning("Bundle hosting is configured but this process has no usable shared compatibility profile " +
                    "(a self-contained single-file process, or unreadable runtime metadata); it does not advertise bundle-host");
        }

        internal bool Compatible(string? json, ISet<string> retained, ILogger? logger)
        {
            if (json == null) return false;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            retained.Add(hash);
            return _entries.GetOrAdd(hash, _ => new Lazy<bool>(() =>
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<WorkflowBundleManifest>(json) is { } manifest &&
                        WorkflowSharedAssemblies.IsCompatible(manifest);
                }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    // Do not log untrusted manifest content or exception details.
                    logger?.LogWarning("Could not validate bundle manifest {ManifestHash}; treating it as incompatible", hash);
                    return false;
                }
            })).Value;
        }

        internal void Retain(IReadOnlySet<string> hashes)
        {
            foreach (var hash in _entries.Keys)
                if (!hashes.Contains(hash)) _entries.TryRemove(hash, out _);
        }
    }

    private Task RegisterAsync(IReadOnlyDictionary<string, string> capabilities, CancellationToken token) =>
        repository.RegisterMemberAsync(new MemberRegistration(settings.Id, MemberKind.Host, settings.Name, null,
            settings.Slots, typeof(WorkflowHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? typeof(WorkflowHost).Assembly.GetName().Version!.ToString(),
            settings.HeartbeatInterval, capabilities), token);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var capabilities = await settings.RefreshCapabilitiesAsync(registry, cancellationToken);
        await RegisterAsync(capabilities, cancellationToken);
        _registeredCapabilities = capabilities;
        try { await base.StartAsync(cancellationToken); }
        catch
        {
            await repository.UnregisterMemberAsync(settings.Id, CancellationToken.None);
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> ObserveRefreshAsync(
        Task<IReadOnlyDictionary<string, string>> refresh, CancellationToken stoppingToken)
    {
        try { return await refresh; }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return null; }
        catch (Exception error)
        {
            // This observer outlives the heartbeat waiter, including during shutdown. Even
            // a failing logger must not leave the abandoned observer with an unobserved fault.
            try
            {
                (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics, LogLevel.Warning, error,
                    "Could not refresh capabilities for host {HostId}; heartbeating with its last snapshot", settings.Id);
            }
            catch { }
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.HeartbeatInterval, time);
        Task<IReadOnlyDictionary<string, string>?>? refresh = null;
        // HostSettings may also serve placement reads for a different catalog object.
        // Only this heartbeat's registration and completed refreshes supply its fallback.
        var capabilities = _registeredCapabilities;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    try
                    {
                        // Dispatch synchronous provider work off the heartbeat loop, keep one
                        // refresh in flight, and bound only the heartbeat's wait for its result.
                        refresh ??= ObserveRefreshAsync(Task.Run(
                            () => settings.RefreshCapabilitiesAsync(registry, stoppingToken), stoppingToken), stoppingToken);
                        capabilities = await refresh.WaitAsync(settings.HeartbeatInterval, time, stoppingToken) ?? capabilities;
                        refresh = null;
                    }
                    catch (TimeoutException) { }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    if (!await repository.HeartbeatAsync(settings.Id, capabilities, stoppingToken))
                        await RegisterAsync(capabilities, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics, LogLevel.Warning, error, "Could not heartbeat host {HostId}", settings.Id); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await repository.UnregisterMemberAsync(settings.Id, CancellationToken.None);
    }
}
