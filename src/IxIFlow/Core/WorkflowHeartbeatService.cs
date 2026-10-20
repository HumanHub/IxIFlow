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
    internal static bool IsAutomaticCapability(string key) =>
        key is "os" or "arch" or "runtime" or "host" or "bundle-host" || key.StartsWith("workflow:", StringComparison.Ordinal);

    internal static async Task<IReadOnlyDictionary<string, string>> CapabilitiesAsync(IWorkflowCatalog registry, HostSettings settings)
    {
        var capabilities = new Dictionary<string, string>(settings.Capabilities, StringComparer.Ordinal)
        {
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux"
                : OperatingSystem.IsMacOS() ? "macos" : RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["host"] = settings.Id.ToString("D")
        };
        foreach (var definition in await registry.GetWorkflowDefinitionsAsync())
            if (await registry.GetWorkflowStructureHashAsync(definition.Name, definition.Version) is { } hash)
                capabilities.Add($"workflow:{definition.Name}@{definition.Version}", hash);
        if (settings.BundleHostingEnabled && !WorkflowSharedAssemblies.IsUsable)
            settings.Compatibility.WarnUnusableProfile(settings.CapabilityLogger);
        else if (settings.BundleHostingEnabled)
        {
            capabilities.Add("bundle-host", "");
            var rows = await registry.GetWorkflowVersionsAsync();
            var retained = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
                if (row.ArtifactDigest != null && settings.Compatibility.Compatible(row.ManifestJson, retained, settings.CapabilityLogger))
                    capabilities[$"workflow:{row.Name}@{row.Version}"] = row.StructureHash;
            settings.Compatibility.Retain(retained);
        }
        return capabilities;
    }

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
        await RegisterAsync(await settings.RefreshCapabilitiesAsync(registry), cancellationToken);
        try { await base.StartAsync(cancellationToken); }
        catch
        {
            await repository.UnregisterMemberAsync(settings.Id, CancellationToken.None);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.HeartbeatInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var capabilities = await settings.RefreshCapabilitiesAsync(registry);
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
