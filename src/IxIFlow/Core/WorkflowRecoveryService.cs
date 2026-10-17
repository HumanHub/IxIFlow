using System.Threading.Channels;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
/// Wakes due waits and deadlines, and recovers interrupted instances. It polls the store and, when the store can wake
/// it, also claims as soon as the store says an instance became claimable (C-13).
/// </summary>
internal sealed class WorkflowRecoveryService(
    IServiceScopeFactory scopes,
    IWorkflowStateRepository repository,
    ILogger<WorkflowRecoveryService> logger,
    TimeProvider? timeProvider = null,
    HostSettings? settings = null,
    IRunnableWakeSource? wakeSource = null) : BackgroundService
{
    private readonly HostSettings _settings = settings ?? HostSettings.Default;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _lock = new();
    // Instances a pass is recovering now, and instances this host could not run, with the time it
    // tries them again, so the earliest rows cannot keep every slot's worth of the poll to themselves.
    private readonly HashSet<string> _recovering = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime Until, int Times)> _deferred = new(StringComparer.Ordinal);

    // A rejection may be a race, such as another pass or the host's timer getting there first, or
    // lasting, such as a definition this host lacks; the wait grows from seconds to minutes.
    private void Defer(string instanceId)
    {
        lock (_lock)
        {
            var times = _deferred.TryGetValue(instanceId, out var previous) ? previous.Times + 1 : 1;
            var wait = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(times - 1, 6))));
            _deferred[instanceId] = (_time.GetUtcNow().UtcDateTime + wait, times);
        }
    }

    internal async Task RecoverOnceAsync(CancellationToken cancellationToken) =>
        await await ClaimAsync(cancellationToken);

    // Lists the instances this host may claim now and reserves them; the task returned recovers them.
    private async Task<Task> ClaimAsync(CancellationToken cancellationToken)
    {
        // A host claims only what it has slots for (D-10); the rest waits for the next pass or another host.
        int limit;
        using (var probe = scopes.CreateScope())
            limit = probe.ServiceProvider.GetService<WorkflowHost>()?.FreeSlots ?? _settings.Slots;
        if (limit == 0)
            return Task.CompletedTask;
        HashSet<string> skipped;
        lock (_lock)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            // Expired entries stay a while, so a row rejected again waits longer; long-expired ones go.
            foreach (var stale in _deferred.Where(item => now - item.Value.Until > TimeSpan.FromMinutes(10))
                         .Select(item => item.Key).ToArray())
                _deferred.Remove(stale);
            skipped = new HashSet<string>(_recovering.Concat(_deferred.Where(item => item.Value.Until > now)
                .Select(item => item.Key)), StringComparer.Ordinal);
        }
        var candidates = await repository.GetRunnableInstancesAsync(limit + skipped.Count);
        // Reserve against the live sets, so a pass running alongside cannot take the same instance.
        var instances = new List<RunnableInstance>();
        lock (_lock)
            foreach (var candidate in candidates)
            {
                if (instances.Count == limit)
                    break;
                if (!skipped.Contains(candidate.InstanceId) && _recovering.Add(candidate.InstanceId))
                    instances.Add(candidate);
            }
        return RecoverReservedAsync(instances, cancellationToken);
    }

    private async Task RecoverReservedAsync(List<RunnableInstance> instances, CancellationToken cancellationToken)
    {
        try
        {
            await RecoverAsync(instances, cancellationToken);
        }
        finally
        {
            // Reservations of instances this pass never reached, for example after cancellation.
            lock (_lock)
                foreach (var instance in instances)
                    _recovering.Remove(instance.InstanceId);
        }
    }

    private async Task RecoverAsync(IReadOnlyList<RunnableInstance> instances, CancellationToken cancellationToken)
    {
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
                // A row this host rejected, or left stopped, may still be due; let others go first.
                if (result.Status is WorkflowExecutionStatus.Faulted or WorkflowExecutionStatus.NeedsResolution)
                    Defer(instance.InstanceId);
                else
                    lock (_lock)
                        _deferred.Remove(instance.InstanceId);
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
                // A row whose recovery throws, such as unreadable data, must not hold the poll.
                Defer(instance.InstanceId);
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
        var passes = new List<Task>();
        var waking = wakeSource == null ? Task.CompletedTask : WakeAsync(wakeSource, passes, stoppingToken);
        // A pass runs until the work it handed out is idle or parked; the next tick does not wait for
        // it, and skips the instances it is still recovering.
        using var timer = new PeriodicTimer(_settings.PollInterval, _time);
        try
        {
            do
            {
                Track(passes, PassAsync(RecoverOnceAsync(stoppingToken), stoppingToken));
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await waking;
        Task[] running;
        lock (passes)
            running = passes.ToArray();
        await Task.WhenAll(running);
    }

    private static void Track(List<Task> passes, Task pass)
    {
        lock (passes)
        {
            passes.RemoveAll(done => done.IsCompleted);
            passes.Add(pass);
        }
    }

    // Claims each time the store wakes the host. Wake-ups that come while a claim is listing are one wake-up, so a
    // burst costs one more claim, not one per instance.
    private async Task WakeAsync(IRunnableWakeSource source, List<Task> passes, CancellationToken stoppingToken)
    {
        var wakes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true
        });
        var listening = ListenAsync(source, () => wakes.Writer.TryWrite(true), stoppingToken);
        try
        {
            await foreach (var _ in wakes.Reader.ReadAllAsync(stoppingToken))
            {
                Task recovering;
                try
                {
                    recovering = await ClaimAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Could not claim the workflows the store woke this host for");
                    continue;
                }
                Track(passes, PassAsync(recovering, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        await listening;
    }

    // A lost connection costs only latency: the poll goes on, and listening starts again a poll interval later.
    private async Task ListenAsync(IRunnableWakeSource source, Action wake, CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                await source.ListenAsync(wake, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Lost the store's wake-ups; polling every {PollInterval} until they return",
                    _settings.PollInterval);
            }
            try
            {
                await Task.Delay(_settings.PollInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PassAsync(Task pass, CancellationToken stoppingToken)
    {
        try
        {
            await pass;
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
