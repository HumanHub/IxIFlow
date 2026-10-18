using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>One bounded pass per interval; the store serializes retention writers across hosts.</summary>
internal sealed class WorkflowRetentionService(IWorkflowStateRepository repository, RetentionSettings settings,
    TimeProvider time, ILogger<WorkflowRetentionService> logger) : BackgroundService
{
    internal Task<RetentionResult> PurgeOnceAsync(CancellationToken cancellationToken) =>
        repository.PurgeAsync(settings.FinishedRetention, settings.TombstoneRetention, settings.BatchSize,
            cancellationToken);

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
