using IxIFlow.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Authoring;

/// <summary>Retries durable pending package work on startup and in bounded periodic passes.</summary>
public sealed class PackageIndexingService(
    IWorkflowCatalogStore catalog,
    IArtifactStore artifacts,
    WorkflowPublisherOptions? options = null,
    ILogger<PackageIndexingService>? logger = null) : BackgroundService
{
    private readonly WorkflowPublisherOptions _options = options ?? new();
    private readonly ILogger<PackageIndexingService> _logger = logger ?? NullLogger<PackageIndexingService>.Instance;
    private readonly SemaphoreSlim _sweep = new(1, 1);
    private string? _afterIdentity;
    private TimeSpan _interval = ValidInterval(options?.PackageIndexingInterval ?? TimeSpan.FromMinutes(1));

    private static TimeSpan ValidInterval(TimeSpan value)
    {
        TimerIntervals.Validate(value, nameof(Interval));

        return value;
    }

    /// <summary>Time between maintenance passes, from one to 4,294,967,294 milliseconds; defaults to one minute.</summary>
    public TimeSpan Interval
    {
        get => _interval;
        init
        {
            TimerIntervals.Validate(value, nameof(Interval));
            _interval = value;
        }
    }

    /// <summary>Maximum pending packages visited by one pass; between one and 1,000, default 256.</summary>
    public int MaxPackagesPerPass { get; init; } = ValidMaximum(options?.MaxPackagesPerIndexingPass ?? 256);

    private static int ValidMaximum(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1000);

        return value;
    }

    /// <summary>Attempts one bounded pass of pending package extraction and returns the number indexed.</summary>
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
                    var page = await catalog.GetPackageExtractionPageAsync(PackageExtraction.Pending, after, Math.Min(64, remaining),
                        cancellationToken: cancellationToken);

                    if (page.Count == 0)
                    {
                        _afterIdentity = null;

                        break;
                    }

                    foreach (var package in page)
                    {
                        remaining--;

                        if (package.Withdrawn)
                            continue;

                        try
                        {
                            var receipt = await new PackageIngestion(catalog, artifacts, _options).RetryAsync(package, cancellationToken);

                            if (receipt.Activities.Extraction == PackageExtraction.Indexed)
                            {
                                completed++;
                                progress = true;
                            }
                        }
                        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                        {
                            _logger.LogDebug(error, "Pending package {Id}@{Version} remains unresolved", package.Id, package.Version);
                        }
                    }

                    after = page[^1].Id + "@" + page[^1].Version;
                    _afterIdentity = after;
                }

                if (progress && remaining > 0)
                    _afterIdentity = null;
            }

            return completed;
        }
        finally
        {
            _sweep.Release();
        }
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
                var page = await catalog.GetPendingPackageDependentsAsync(id, after, Math.Min(64, remaining),
                    cancellationToken: cancellationToken);

                if (page.Count == 0)
                    break;

                foreach (var package in page)
                {
                    remaining--;

                    if (package.Withdrawn)
                        continue;

                    try
                    {
                        var receipt = await new PackageIngestion(catalog, artifacts, _options).RetryAsync(package, cancellationToken);

                        if (receipt.Activities.Extraction == PackageExtraction.Indexed)
                        {
                            completed++;
                            queue.Enqueue(package.Id);
                        }
                    }
                    catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                    {
                        _logger.LogDebug(error, "Dependent package {Id}@{Version} remains unresolved", package.Id, package.Version);
                    }
                }

                after = page[^1].Id + "@" + page[^1].Version;
            }
        }

        return completed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            do
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    _logger.LogError(error, "Could not index pending workflow packages; retrying on the next pass");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}