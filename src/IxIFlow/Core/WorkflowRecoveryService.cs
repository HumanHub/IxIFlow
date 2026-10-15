using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>Wakes due waits and deadlines, and recovers interrupted instances.</summary>
internal sealed class WorkflowRecoveryService(
    IServiceScopeFactory scopes,
    IWorkflowStateRepository repository,
    ILogger<WorkflowRecoveryService> logger,
    TimeProvider? timeProvider = null,
    HostSettings? settings = null) : BackgroundService
{
    private readonly HostSettings _settings = settings ?? HostSettings.Default;

    internal async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        // A host claims only what it has slots for (D-10); the rest waits for the next pass or another host.
        int limit;
        using (var probe = scopes.CreateScope())
            limit = probe.ServiceProvider.GetService<LocalWorkflowHost>()?.FreeSlots ?? _settings.Slots;
        if (limit == 0)
            return;
        var instances = await repository.GetRunnableInstancesAsync(limit);
        await Parallel.ForEachAsync(instances, new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = cancellationToken
        }, async (instance, token) =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                // The host stops before the scanner and drains the work it admitted; the scanner's
                // own token then only interrupts runs still going, which stay recoverable.
                var result = await scope.ServiceProvider.GetRequiredService<IWorkflowClient>()
                    .RecoverAsync(instance.InstanceId, token);
                if (result.Status == WorkflowExecutionStatus.Faulted)
                    logger.LogWarning("Recovery of workflow {InstanceId} was rejected: {Reason}",
                        instance.InstanceId, result.ErrorMessage);
                else if (result.Status == WorkflowExecutionStatus.NeedsResolution && result.ErrorMessage != null)
                    logger.LogWarning("Workflow {InstanceId} needs an operator: {Reason}",
                        instance.InstanceId, result.ErrorMessage);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // The host is stopping; the instance is recovered after the next start.
            }
            catch (Exception error)
            {
                logger.LogError(error, "Could not recover workflow {InstanceId}", instance.InstanceId);
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_settings.PollInterval, timeProvider ?? TimeProvider.System);
        do
        {
            try
            {
                await RecoverOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Could not scan running workflows for recovery");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
