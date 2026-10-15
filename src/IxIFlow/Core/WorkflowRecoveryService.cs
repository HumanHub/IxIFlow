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
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _lock = new();
    // Instances a pass is recovering now, and instances this host could not run, with the time it
    // tries them again, so the earliest rows cannot keep every slot's worth of the poll to themselves.
    private readonly HashSet<string> _recovering = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _deferred = new(StringComparer.Ordinal);
    private static readonly TimeSpan DeferRejected = TimeSpan.FromMinutes(1);

    internal async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        // A host claims only what it has slots for (D-10); the rest waits for the next pass or another host.
        int limit;
        using (var probe = scopes.CreateScope())
            limit = probe.ServiceProvider.GetService<LocalWorkflowHost>()?.FreeSlots ?? _settings.Slots;
        if (limit == 0)
            return;
        HashSet<string> skipped;
        lock (_lock)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var expired in _deferred.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
                _deferred.Remove(expired);
            skipped = new HashSet<string>(_recovering.Concat(_deferred.Keys), StringComparer.Ordinal);
        }
        var instances = (await repository.GetRunnableInstancesAsync(limit + skipped.Count))
            .Where(instance => !skipped.Contains(instance.InstanceId))
            .Take(limit)
            .ToArray();
        lock (_lock)
            foreach (var instance in instances)
                _recovering.Add(instance.InstanceId);
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
                // A row this host rejected, or left stopped, is likely still due; let others go first.
                if (result.Status is WorkflowExecutionStatus.Faulted or WorkflowExecutionStatus.NeedsResolution)
                    lock (_lock)
                        _deferred[instance.InstanceId] = _time.GetUtcNow().UtcDateTime + DeferRejected;
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
            finally
            {
                lock (_lock)
                    _recovering.Remove(instance.InstanceId);
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A pass runs until the work it handed out is idle or parked; the next tick does not wait for
        // it, and skips the instances it is still recovering.
        using var timer = new PeriodicTimer(_settings.PollInterval, _time);
        var passes = new List<Task>();
        try
        {
            do
            {
                passes.RemoveAll(pass => pass.IsCompleted);
                passes.Add(PassAsync(stoppingToken));
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await Task.WhenAll(passes);
    }

    private async Task PassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Could not scan running workflows for recovery");
        }
    }
}
