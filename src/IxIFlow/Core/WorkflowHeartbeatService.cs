using System.Reflection;
using System.Runtime.InteropServices;
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
        key is "os" or "arch" or "runtime" or "host" || key.StartsWith("workflow:", StringComparison.Ordinal);

    private async Task<IReadOnlyDictionary<string, string>> CapabilitiesAsync()
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
        return capabilities;
    }

    private Task RegisterAsync(IReadOnlyDictionary<string, string> capabilities, CancellationToken token) =>
        repository.RegisterMemberAsync(new MemberRegistration(settings.Id, MemberKind.Host, settings.Name, null,
            settings.Slots, typeof(WorkflowHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? typeof(WorkflowHost).Assembly.GetName().Version!.ToString(),
            settings.HeartbeatInterval, capabilities), token);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RegisterAsync(await CapabilitiesAsync(), cancellationToken);
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
                    var capabilities = await CapabilitiesAsync();
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
