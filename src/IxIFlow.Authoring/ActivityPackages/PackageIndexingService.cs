using IxIFlow.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Authoring;

/// <summary>Retries durable pending package work on startup and in bounded periodic passes.</summary>
public sealed class PackageIndexingService(IWorkflowCatalogStore catalog, IArtifactStore artifacts,
    WorkflowPublisherOptions? options = null, ILogger<PackageIndexingService>? logger = null) : BackgroundService
{
    private readonly WorkflowPublisherOptions _options = options ?? new();
    private readonly ILogger<PackageIndexingService> _logger = logger ?? NullLogger<PackageIndexingService>.Instance;
    private readonly SemaphoreSlim _sweep = new(1, 1);
    private string? _afterIdentity;
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
    public int MaxPackagesPerPass { get; init; } = 256;

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPackagesPerPass, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPackagesPerPass, 1000);
        await _sweep.WaitAsync(cancellationToken);
        try
        {
            var completed = 0;
            var remaining = MaxPackagesPerPass;
            var progress = true;
            while (progress && remaining > 0)
            {
                progress = false;
                var after = _afterIdentity;
                while (remaining > 0)
                {
                    var page = await catalog.GetPackageExtractionPageAsync(PackageExtraction.Pending, after, Math.Min(64, remaining));
                    if (page.Count == 0) { _afterIdentity = null; break; }
                    foreach (var package in page)
                    {
                        remaining--;
                        if (package.Withdrawn) continue;
                        try
                        {
                            var receipt = await new PackageIngestion(catalog, artifacts, _options).RetryAsync(package, cancellationToken);
                            if (receipt.Activities.Extraction == PackageExtraction.Indexed) { completed++; progress = true; }
                        }
                        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                        { _logger.LogDebug(error, "Pending package {Id}@{Version} remains unresolved", package.Id, package.Version); }
                    }
                    after = page[^1].Id + "@" + page[^1].Version;
                    _afterIdentity = after;
                }
                if (progress && remaining > 0) _afterIdentity = null;
            }
            return completed;
        }
        finally { _sweep.Release(); }
    }

    /// <summary>Revisits persisted reverse dependency edges after accepting a package closure.</summary>
    public async Task<int> RevisitAsync(IReadOnlyList<string> packageIds, CancellationToken cancellationToken = default)
    {
        var queue = new Queue<string>(packageIds.Select(WorkflowPackageRules.NormalizeId).Distinct(StringComparer.Ordinal));
        var completed = 0;
        var remaining = MaxPackagesPerPass;
        while (queue.TryDequeue(out var id) && remaining > 0)
        {
            string? after = null;
            while (remaining > 0)
            {
                var page = await catalog.GetPendingPackageDependentsAsync(id, after, Math.Min(64, remaining));
                if (page.Count == 0) break;
                foreach (var package in page)
                {
                    remaining--;
                    if (package.Withdrawn) continue;
                    try
                    {
                        var receipt = await new PackageIngestion(catalog, artifacts, _options).RetryAsync(package, cancellationToken);
                        if (receipt.Activities.Extraction == PackageExtraction.Indexed) { completed++; queue.Enqueue(package.Id); }
                    }
                    catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                    { _logger.LogDebug(error, "Dependent package {Id}@{Version} remains unresolved", package.Id, package.Version); }
                }
                after = page[^1].Id + "@" + page[^1].Version;
            }
        }
        return completed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Interval, TimeSpan.Zero);
        using var timer = new PeriodicTimer(Interval);
        do { await SweepAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
