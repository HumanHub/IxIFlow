using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>Finds interrupted running instances for lease-aware recovery.</summary>
public sealed class WorkflowRecoveryService(
    IServiceScopeFactory scopes,
    IWorkflowStateRepository repository,
    ILogger<WorkflowRecoveryService> logger) : BackgroundService
{
    internal async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        var instances = await repository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running);
        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
                    .RecoverWorkflowAsync(instance.InstanceId, cancellationToken);
                if (result.Status == WorkflowExecutionStatus.Faulted)
                    logger.LogWarning("Recovery of workflow {InstanceId} was rejected: {Reason}",
                        instance.InstanceId, result.ErrorMessage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Could not recover workflow {InstanceId}", instance.InstanceId);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
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
