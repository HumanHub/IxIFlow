using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>One bounded pass per interval; the store serializes retention writers across hosts.</summary>
internal sealed class WorkflowRetentionService(IWorkflowStateRepository repository, RetentionSettings settings,
    TimeProvider time, ILogger<WorkflowRetentionService> logger, IArtifactStore? artifacts = null) : BackgroundService
{
    internal async Task<RetentionResult> PurgeOnceAsync(CancellationToken cancellationToken)
    {
        var result = await repository.PurgeAsync(settings.FinishedRetention, settings.TombstoneRetention,
            settings.BatchSize, cancellationToken);
        await repository.PurgeArtifactsAsync(artifacts ?? repository, settings.ArtifactRetention, settings.BatchSize, cancellationToken);
        foreach (var (publisher, count) in result.SkippedUnattemptedByPublisher)
        {
            Telemetry.RetentionSkippedUnattempted.Add(count,
                new KeyValuePair<string, object?>(Telemetry.PublisherName, publisher));
            logger.LogWarning("Retention skipped {InstanceCount} finished candidates because publisher {PublisherName} has pending deliveries that were never attempted",
                count, publisher);
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.ScanInterval, time);
        try
        {
            do
            {
                try
                {
                    await PurgeOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Could not purge retained workflows");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
